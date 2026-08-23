using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

using DevReload.Core;
using DevReload.Hud;
using DevReload.Rpc;

using Exception = System.Exception;

namespace DevReload
{
    public static class PluginManager
    {
        private static readonly Dictionary<string, PluginRegistration> _plugins = new();

        /// <summary>
        /// The .NET cycle, in the order it runs. Build comes FIRST — the opposite
        /// of OARX, and deliberately so: a plugin assembly is stream-loaded, so it
        /// locks nothing on disk and can be rebuilt while the old one is still
        /// running. Build-first-then-swap means a failed build changes nothing and
        /// the previous plugin keeps working, where a failed OARX build leaves the
        /// group unloaded (research F14).
        /// </summary>
        private static readonly ReloadCycle Cycle = new(
            (ReloadStep.Build,  "building"),
            (ReloadStep.Unload, "unloading the previous ALC"),
            (ReloadStep.Load,   "loading"));

        /// <summary>
        /// Where an interactive cycle reports itself: the transient HUD.
        /// </summary>
        /// <remarks>
        /// HUD only, no editor sink — unlike OARX, this lifecycle already writes
        /// its own command-line messages inline, and adding one would print
        /// everything twice.
        ///
        /// <para>No editor means no drawing to hang a transient on. That is the
        /// startup path, which is headless by construction and reads the returned
        /// <see cref="PluginActionResult"/> instead of watching.</para>
        /// </remarks>
        private static IReloadProgress DefaultProgress()
        {
            var ed = GetEditor();
            if (ed == null) return NullReloadProgress.Instance;

            return new CompositeReloadProgress(
                new ReloadHud(warn => ed.WriteMessage("\n[DevReload] " + warn)));
        }

        /// <summary>Raised after a plugin enters the in-memory registry, from
        /// EVERY registration path (startup, palette add, MCP register, config
        /// reload). The palette ViewModel subscribes so a card appears even when
        /// the registration was triggered out-of-band (e.g. the MCP tool) while
        /// the palette is already open. The registry is the single source of
        /// truth; the UI is a projection of it.</summary>
        public static event Action<string>? PluginRegistered;

        /// <summary>Raised after a plugin leaves the in-memory registry, from
        /// both Unregister and UnregisterInMemory. Lets the palette drop the
        /// card reactively.</summary>
        public static event Action<string>? PluginUnregistered;

        /// <summary>Raised after a plugin's load state may have changed — load,
        /// reload, or unload — regardless of who triggered it (the palette's own
        /// buttons or the out-of-band MCP tool surface an agent drives). The
        /// palette refreshes the matching card so its loaded indicator reflects
        /// the change even when it didn't initiate it. Same projection principle
        /// as <see cref="PluginRegistered"/>: the registry is the single source
        /// of truth, the UI follows it.</summary>
        public static event Action<string>? PluginStateChanged;

        public static PluginRegistrationBuilder Register(string pluginName)
        {
            return new PluginRegistrationBuilder(pluginName);
        }

        public static PluginActionResult Load(
            string pluginName, IReloadProgress? progress = null)
        {
            var ed = GetEditor();
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return new PluginActionResult(pluginName, false, 0, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{pluginName}: load", Cycle);

            if (reg.Host.IsLoaded)
            {
                ed?.WriteMessage($"\n{pluginName} is already loaded.");
                ui.Finish("already loaded", true);
                return Result(reg, success: true, "already loaded");
            }

            BuildResult? build = null;
            try
            {
                string dllPath = reg.DllPath;

                if (!File.Exists(dllPath))
                {
                    string csprojPath = GetEffectiveCsprojPath(reg);
                    ed?.WriteMessage($"\n{pluginName} DLL not found, building...");
                    ui.Step(ReloadStep.Build);
                    build = AcadBuild.Build(
                        csprojPath, reg.BuildConfiguration, ed, ui);
                    if (!build.Success || build.OutputPath == null)
                    {
                        ui.Finish("build failed", false);
                        return Result(reg, success: false, "build failed", build);
                    }
                    reg.DllPath = build.OutputPath;
                    dllPath = build.OutputPath;
                }

                try
                {
                    LoadCore(reg, dllPath, ui);
                }
                catch (StalePluginException)
                {
                    ed?.WriteMessage($"\nStale plugin detected, rebuilding...");
                    string csprojPath = GetEffectiveCsprojPath(reg);
                    ui.Step(ReloadStep.Build);
                    build = AcadBuild.Build(
                        csprojPath, reg.BuildConfiguration, ed, ui);
                    if (!build.Success || build.OutputPath == null)
                    {
                        ui.Finish("rebuild failed", false);
                        return Result(reg, success: false, "rebuild failed", build);
                    }
                    reg.DllPath = build.OutputPath;
                    dllPath = build.OutputPath;
                    LoadCore(reg, dllPath, ui);
                }

                ed?.WriteMessage($"\n{pluginName} loaded.{CommandSuffix(reg)}");
                ui.Finish("loaded", true);
                return Result(reg, success: true, "loaded", build);
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} load error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
                ui.Finish($"{ex.GetType().Name}: {ex.Message}", false);
                return Result(reg, success: false,
                    $"load error: {ex.GetType().Name}: {ex.Message}", build);
            }
        }

        public static PluginActionResult DevReload(
            string pluginName, IReloadProgress? progress = null)
        {
            var ed = GetEditor();
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return new PluginActionResult(pluginName, false, 0, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{pluginName}: reload", Cycle);

            BuildResult? build = null;
            try
            {
                string csprojPath = GetEffectiveCsprojPath(reg);

                ui.Step(ReloadStep.Build);
                build = AcadBuild.Build(
                    csprojPath, reg.BuildConfiguration, ed, ui);
                if (!build.Success || build.OutputPath == null)
                {
                    // Nothing was torn down, so whatever was running before this
                    // call is still running. The OARX verdict in the same red means
                    // the opposite — there, a failed build leaves nothing loaded —
                    // so this one says which it is.
                    ui.Finish(reg.Host.IsLoaded
                        ? "build failed — still on the previous build"
                        : "build failed", false);
                    return Result(reg, success: false, "build failed", build);
                }
                reg.DllPath = build.OutputPath;
                string dllPath = build.OutputPath;

                try
                {
                    LoadCore(reg, dllPath, ui);
                }
                catch (StalePluginException)
                {
                    string msg = $"{pluginName}: IExtensionApplication version mismatch. Restart AutoCAD.";
                    ed?.WriteMessage("\n" + msg);
                    ui.Finish("version mismatch — restart AutoCAD", false);
                    return Result(reg, success: false, msg, build);
                }

                ed?.WriteMessage($"\n{pluginName} dev-reloaded.{CommandSuffix(reg)}");
                ui.Finish("reloaded", true);
                return Result(reg, success: true, "dev-reloaded", build);
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} dev-reload error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
                ui.Finish($"{ex.GetType().Name}: {ex.Message}", false);
                return Result(reg, success: false,
                    $"dev-reload error: {ex.GetType().Name}: {ex.Message}", build);
            }
        }

        // Build the plugin's current selection (worktree + config) WITHOUT loading
        // it. Used by the "Build only" flyout so a freshly-selected worktree can be
        // built — producing its DLLs — before the user configures shared assemblies
        // and reloads. Loading is intentionally skipped: a wrong/empty shared config
        // would otherwise wedge the session.
        public static PluginActionResult BuildOnly(
            string pluginName, IReloadProgress? progress = null)
        {
            var ed = GetEditor();
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return new PluginActionResult(pluginName, false, 0, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{pluginName}: build", Cycle);

            try
            {
                string csprojPath = GetEffectiveCsprojPath(reg);
                ui.Step(ReloadStep.Build);
                var build = AcadBuild.Build(
                    csprojPath, reg.BuildConfiguration, ed, ui);
                if (!build.Success || build.OutputPath == null)
                {
                    ui.Finish("build failed", false);
                    return Result(reg, success: false, "build failed", build);
                }

                reg.DllPath = build.OutputPath;
                ed?.WriteMessage($"\n{pluginName} built (not loaded).");
                ui.Finish("built (not loaded)", true);
                return Result(reg, success: true, "built", build);
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} build error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
                ui.Finish($"{ex.GetType().Name}: {ex.Message}", false);
                return Result(reg, success: false,
                    $"build error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static PluginActionResult Unload(
            string pluginName, IReloadProgress? progress = null)
        {
            var ed = GetEditor();
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return new PluginActionResult(pluginName, false, 0, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{pluginName}: unload", Cycle);

            if (!reg.Host.IsLoaded)
            {
                ed?.WriteMessage($"\n{pluginName} is not loaded.");
                ui.Finish("not loaded", true);
                return Result(reg, success: true, "not loaded");
            }

            try
            {
                ui.Step(ReloadStep.Unload);
                TearDown(reg);
                ed?.WriteMessage($"\n{pluginName} unloaded.");
                ui.Finish("unloaded", true);
                return Result(reg, success: true, "unloaded");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} unload error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
                ui.Finish($"{ex.GetType().Name}: {ex.Message}", false);
                return Result(reg, success: false,
                    $"unload error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static UnloadAllResult UnloadAll()
        {
            var loadedBefore = _plugins.Values
                .Where(r => r.Host.IsLoaded)
                .Select(r => r.PluginName)
                .ToList();
            foreach (var reg in _plugins.Values)
            {
                try { TearDown(reg); }
                catch { /* best-effort during shutdown */ }
            }
            // UnloadAll doesn't route through Result(), so notify the palette
            // here for every card that actually flipped to unloaded.
            foreach (var name in loadedBefore)
                PluginStateChanged?.Invoke(name);
            return new UnloadAllResult(
                Total: _plugins.Count,
                UnloadedNow: loadedBefore.Count,
                PluginNames: loadedBefore);
        }

        private static string CommandSuffix(PluginRegistration reg) =>
            reg.Registrar != null
                ? $" {reg.Registrar.CommandCount} commands registered."
                : "";

        private static PluginActionResult Result(
            PluginRegistration reg, bool success, string message, BuildResult? build = null)
        {
            // Every lifecycle op (Load/DevReload/Unload/BuildOnly) returns
            // through here, so this is the single funnel where the palette must
            // be told the load state may have moved — including when an agent
            // triggered it via the MCP tool surface rather than a palette button.
            // RefreshState on the card reads the live registry and is idempotent,
            // so firing on no-op returns (e.g. "already loaded") is harmless.
            PluginStateChanged?.Invoke(reg.PluginName);

            // A successful build's log is tens of KB of compiler noise that can
            // blow the MCP result-size cap; the agent only needs it to read
            // errors. Keep the full log on failure, summarize it on success.
            BuildResult? buildForResult = build;
            if (build is { Success: true })
                buildForResult = build with
                {
                    Log = $"build succeeded ({build.Warnings} warning(s), {build.Errors} error(s)); log omitted"
                };

            return new(
                PluginName: reg.PluginName,
                Success: success,
                CommandCount: reg.Registrar?.CommandCount ?? 0,
                Loaded: reg.Host.IsLoaded,
                Message: message,
                Build: buildForResult);
        }

        // ── Public query + management API ─────────────────────────────

        public static IReadOnlyList<string> GetRegisteredPluginNames()
            => _plugins.Keys.ToList();

        public static bool IsRegistered(string pluginName)
            => _plugins.ContainsKey(pluginName);

        public static bool IsLoaded(string pluginName)
            => _plugins.TryGetValue(pluginName, out var reg) && reg.Host.IsLoaded;

        /// <summary>Project the registration into a serializable snapshot
        /// for the RPC tool surface. Returns null if not registered.</summary>
        public static PluginInfo? SnapshotRegistration(string pluginName)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg)) return null;
            return SnapshotOf(reg);
        }

        /// <summary>Snapshot every registered plugin. Same shape as
        /// <see cref="SnapshotRegistration"/>; for callers (palette + RPC)
        /// that want the whole list.</summary>
        public static IReadOnlyList<PluginInfo> ListPluginSnapshots() =>
            _plugins.Values.Select(SnapshotOf).ToList();

        private static PluginInfo SnapshotOf(PluginRegistration reg) =>
            new(
                Name: reg.PluginName,
                Loaded: reg.Host.IsLoaded,
                BuildConfiguration: reg.BuildConfiguration,
                DllPath: reg.DllPath,
                ProjectFilePath: reg.ProjectFilePath,
                ActiveWorktreePath: reg.ActiveWorktreePath,
                CommandCount: reg.Registrar?.CommandCount ?? 0);

        /// <summary>Project the loaded assembly's metadata into a serializable
        /// shape. Returns a record with Loaded=false if the plugin is not
        /// loaded (or not registered).</summary>
        public static PluginAssemblyInfo GetAssemblyInfo(string pluginName)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg) ||
                !reg.Host.IsLoaded ||
                reg.Host.LoadedAssembly == null)
            {
                return new PluginAssemblyInfo(
                    PluginName: pluginName,
                    Loaded: false,
                    AssemblyName: null,
                    Version: null,
                    Location: null,
                    LastWriteUtc: null);
            }
            var asm = reg.Host.LoadedAssembly;
            var name = asm.GetName();
            string? loc = string.IsNullOrEmpty(asm.Location) ? null : asm.Location;
            string? when = loc != null && File.Exists(loc)
                ? File.GetLastWriteTimeUtc(loc).ToString("O")
                : null;
            return new PluginAssemblyInfo(
                PluginName: pluginName,
                Loaded: true,
                AssemblyName: name.Name,
                Version: name.Version?.ToString(),
                Location: loc,
                LastWriteUtc: when);
        }

        public static PluginActionResult Unregister(string pluginName)
        {
            bool wasRegistered = UnregisterInMemory(pluginName);
            bool fileRemoved = PluginConfigLoader.RemovePluginEntry(pluginName);
            string message = wasRegistered
                ? (fileRemoved ? "unregistered and removed from plugins.json" : "unregistered")
                : (fileRemoved ? "removed from plugins.json only" : "was not registered");
            return new PluginActionResult(
                PluginName: pluginName,
                Success: wasRegistered || fileRemoved,
                CommandCount: 0,
                Loaded: false,
                Message: message);
        }

        /// <summary>Tear down a plugin's runtime state and drop it from the
        /// in-memory registry WITHOUT touching plugins.json. Raises
        /// PluginUnregistered. Returns whether it was registered.
        /// Used by the palette's "Reload Config" to resync the registry to the
        /// file: that path must NOT delete file entries — calling the public
        /// Unregister in a loop there previously wiped the entire config.</summary>
        internal static bool UnregisterInMemory(string pluginName)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return false;
            TearDown(reg);
            UnregisterLoaderCommands(reg);
            _plugins.Remove(pluginName);
            PluginUnregistered?.Invoke(pluginName);
            return true;
        }

        // ── Loader-level command registration ─────────────────────────

        public static void RegisterLoaderCommands(string pluginName, string prefix)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return;

            prefix = prefix.ToUpperInvariant();
            string group = "DEVRELOAD";
            string name = pluginName;

            void Register(string suffix, Action action)
            {
                string cmdName = prefix + suffix;
                CommandCallback cb = () => action();
                Utils.AddCommand(group, cmdName, cmdName, CommandFlags.Modal, cb);
                reg.LoaderCommands.Add((group, cmdName, cb));
            }

            Register("LOAD", () => Load(name));
            Register("DEV", () => DevReload(name));
            Register("UNLOAD", () => Unload(name));
        }

        private static void UnregisterLoaderCommands(PluginRegistration reg)
        {
            foreach (var (group, cmdName, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmdName);
            reg.LoaderCommands.Clear();
        }

        // ── Private helpers ────────────────────────────────────────────

        /// <summary>
        /// Core load sequence: tear down old → load new from stream →
        /// initialize → register commands.
        /// </summary>
        /// <remarks>
        /// DevReload owns the whole lifecycle now. It used to be split: AutoCAD's
        /// assembly scan built its own instance of the plugin and called
        /// Initialize on that one, while DevReload built a second instance and
        /// called Terminate on it, so instance state set up in Initialize was
        /// invisible to Terminate. AutoCadScanSuppressor removes AutoCAD from the
        /// picture, leaving one instance that gets both halves.
        /// </remarks>
        private static void LoadCore(
            PluginRegistration reg, string dllPath, IReloadProgress ui)
        {
            ui.Step(ReloadStep.Unload);
            TearDown(reg);

            ui.Step(ReloadStep.Load);

            // Single source of truth for shared-assembly config: the file in
            // the build directory we're loading from. No cached state on the
            // registration, no fallback to plugins.json — if the file isn't
            // there, this build has no shared assemblies, period.
            string pluginDir = Path.GetDirectoryName(dllPath)!;
            var sharedConfig = SharedAssembliesFile.Read(pluginDir);

            // Auto-inject Acad.Rpc.Core into every plugin's effective shared
            // list. Required for plugins that contribute MCP tools: their
            // [AcadRpcSurface] / [AcadRpcTool] attribute references must
            // resolve to the SAME loaded Acad.Rpc.Core instance DevReload uses,
            // otherwise the singleton host's registry never sees them.
            // Plugin authors don't need to remember to add it.
            const string AcadRpcCore = "Acad.Rpc.Core";
            if (!sharedConfig.SharedAssemblies.Contains(AcadRpcCore, StringComparer.OrdinalIgnoreCase))
                sharedConfig.SharedAssemblies.Add(AcadRpcCore);
            if (!sharedConfig.StreamedAssemblies.Contains(AcadRpcCore, StringComparer.OrdinalIgnoreCase))
                sharedConfig.StreamedAssemblies.Add(AcadRpcCore);

            string[] sharedNames = sharedConfig.SharedAssemblies.ToArray();

            // Pre-load shared assemblies into the default ALC (shared with the
            // Revit host via DevReload.BuildCore). Editor may be null on a fresh
            // start, so resolve it per message.
            SharedAssemblyPreloader.Preload(
                pluginDir, sharedConfig,
                msg => GetEditor()?.WriteMessage("\n[DevReload] " + msg));

            var plugin = reg.Host.Load(dllPath, sharedNames);

            // Before the commands, matching the order AutoCAD's own scan used:
            // it initialized the plugin during LoadFromStream, and DevReload
            // registered commands afterwards. Guarded, because when suppression
            // is off the host has already called this and a second call would
            // initialize the plugin twice.
            if (AutoCadScanSuppressor.IsActive)
                plugin.Initialize();

            if (reg.Registrar != null)
            {
                reg.Registrar.RegisterFromAssembly(reg.Host.LoadedAssembly!);
            }

            // Register the freshly-loaded assembly's tools into the
            // unified MCP surface. This runs after Initialize, so a plugin
            // that sets up state its tools need has already done so by the
            // time the agent can see them.
            try
            {
                if (AcadRpcHost.IsInitialized && reg.Host.LoadedAssembly != null)
                {
                    AcadRpcHost.Current.RegisterAssembly(reg.Host.LoadedAssembly);
                }
            }
            catch (Exception ex)
            {
                GetEditor()?.WriteMessage(
                    $"\n[DevReload] RPC RegisterAssembly failed: {ex.Message}");
            }
        }

        private static void TearDown(PluginRegistration reg)
        {
            // RPC unregister fires FIRST so any inbound agent call lands
            // after the SDK has already removed the tool. The plugin's
            // Terminate then runs with no inbound RPC traffic possible.
            try
            {
                if (AcadRpcHost.IsInitialized && reg.Host.LoadedAssembly != null)
                {
                    AcadRpcHost.Current.UnregisterAssembly(reg.Host.LoadedAssembly);
                }
            }
            catch { /* best-effort during teardown */ }

            reg.Registrar?.UnregisterAll();

            if (reg.Host.IsLoaded)
            {
                try { reg.Host.Plugin?.Terminate(); }
                catch { /* best-effort */ }

                reg.Host.Unload();
            }
        }

        private static string GetEffectiveCsprojPath(PluginRegistration reg)
            => GitWorktreeService.ResolveActiveCsproj(
                reg.ProjectFilePath, reg.ActiveWorktreePath);

        private static PluginRegistration GetRegistration(string pluginName)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg))
                throw new InvalidOperationException(
                    $"Plugin '{pluginName}' is not registered. " +
                    $"Call PluginManager.Register(\"{pluginName}\")...Commit() first.");
            return reg;
        }

        private static Editor? GetEditor()
        {
            return Application.DocumentManager.MdiActiveDocument?.Editor;
        }

        // ── Mutations: single funnel for UI + RPC ─────────────────────
        //
        // Both the palette ViewModel and the MCP tool surface call these
        // methods. They update the live registration AND persist to
        // plugins.json so the change survives an AutoCAD restart. There
        // is no separate "RPC persistence path"; the UI got a second
        // path before this refactor, which was the bug.

        public static PluginActionResult UpdateBuildConfiguration(
            string pluginName, string buildConfiguration)
        {
            bool inMemory = false;
            if (_plugins.TryGetValue(pluginName, out var reg))
            {
                reg.BuildConfiguration = buildConfiguration;
                inMemory = true;
            }
            bool persisted = PluginConfigLoader.UpdatePluginEntry(
                pluginName, e => e.BuildConfiguration = buildConfiguration);
            return ResultForUpdate(pluginName, inMemory, persisted,
                $"build config -> {buildConfiguration}");
        }

        public static PluginActionResult UpdateActiveWorktree(
            string pluginName, string? worktreePath)
        {
            bool inMemory = false;
            if (_plugins.TryGetValue(pluginName, out var reg))
            {
                reg.ActiveWorktreePath = worktreePath;
                inMemory = true;
            }
            bool persisted = PluginConfigLoader.UpdatePluginEntry(
                pluginName, e => e.ActiveWorktreePath = worktreePath);
            return ResultForUpdate(pluginName, inMemory, persisted,
                $"worktree -> {worktreePath ?? "(main)"}");
        }

        /// <summary>List the configurations the plugin's project declares
        /// (Available) plus the currently-selected one (Current). Sourced from
        /// plugins.json — the durable truth every mutation persists to — so it
        /// can run off the AutoCAD main thread without racing the in-memory
        /// registry (which is a plain Dictionary). The MSBuild query is the
        /// reason it must stay off the main thread: it spawns dotnet and blocks
        /// for a second or two. Throws (no fabricated list) when the plugin, its
        /// project, or MSBuild can't be resolved.</summary>
        public static PluginConfigurationsResult GetConfigurations(string pluginName)
        {
            var entry = PluginConfigLoader.Load()?.Plugins
                .FirstOrDefault(p => p.Name.Equals(
                    pluginName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"plugin '{pluginName}' is not in plugins.json");

            if (string.IsNullOrEmpty(entry.ProjectFilePath))
                throw new InvalidOperationException(
                    $"plugin '{pluginName}' has no project file recorded; " +
                    "its configurations can't be resolved");

            var configs = BuildService.GetConfigurations(
                entry.ProjectFilePath, entry.ActiveWorktreePath, AcadBuild.Platform);
            if (configs.Count == 0)
                throw new InvalidOperationException(
                    $"could not resolve configurations for '{pluginName}'. " +
                    "Restore/build the project at least once and try again.");

            return new PluginConfigurationsResult(
                pluginName, entry.BuildConfiguration, configs);
        }

        private static PluginActionResult ResultForUpdate(
            string pluginName, bool inMemory, bool persisted, string action)
        {
            var snap = SnapshotRegistration(pluginName);
            string suffix = (inMemory, persisted) switch
            {
                (true, true)   => " (live + persisted)",
                (true, false)  => " (live only; no plugins.json entry)",
                (false, true)  => " (persisted; not registered live)",
                (false, false) => " (no live registration, no plugins.json entry)",
            };
            return new PluginActionResult(
                PluginName: pluginName,
                Success: inMemory || persisted,
                CommandCount: snap?.CommandCount ?? 0,
                Loaded: snap?.Loaded ?? false,
                Message: action + suffix);
        }

        internal static void AddRegistration(PluginRegistration reg)
        {
            _plugins[reg.PluginName] = reg;
            PluginRegistered?.Invoke(reg.PluginName);
        }
    }

    internal class PluginRegistration
    {
        public required string PluginName { get; init; }
        public required string DllPath { get; set; }
        public required string ProjectFilePath { get; init; }
        public required string BuildConfiguration { get; set; }
        public string? ActiveWorktreePath { get; set; }

        public PluginHost<IExtensionApplication> Host { get; } = new();
        public CommandRegistrar? Registrar { get; init; }

        public List<(string Group, string Name, CommandCallback Callback)> LoaderCommands { get; }
            = new();
    }

    public class PluginRegistrationBuilder
    {
        private readonly string _pluginName;
        private string? _dllPath;
        private string? _projectFilePath;
        private string _buildConfiguration = "Debug";
        private string? _activeWorktreePath;
        private bool _useCommands;

        internal PluginRegistrationBuilder(string pluginName)
        {
            _pluginName = pluginName;
        }

        public PluginRegistrationBuilder WithDllPath(string dllPath)
        {
            _dllPath = dllPath;
            return this;
        }

        public PluginRegistrationBuilder WithProjectFilePath(string path)
        {
            _projectFilePath = path;
            return this;
        }

        public PluginRegistrationBuilder WithBuildConfiguration(string buildConfiguration)
        {
            _buildConfiguration = buildConfiguration;
            return this;
        }

        public PluginRegistrationBuilder WithActiveWorktreePath(string? path)
        {
            _activeWorktreePath = path;
            return this;
        }

        public PluginRegistrationBuilder WithCommands()
        {
            _useCommands = true;
            return this;
        }

        public void Commit()
        {
            var reg = new PluginRegistration
            {
                PluginName = _pluginName,
                DllPath = _dllPath ?? "",
                ProjectFilePath = _projectFilePath ?? "",
                BuildConfiguration = _buildConfiguration,
                ActiveWorktreePath = _activeWorktreePath,
                Registrar = _useCommands ? new CommandRegistrar() : null,
            };

            PluginManager.AddRegistration(reg);
        }
    }
}
