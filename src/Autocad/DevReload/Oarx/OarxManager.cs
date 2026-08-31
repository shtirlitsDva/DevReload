using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

using DevReload.Core;
using DevReload.Hud;

using Exception = System.Exception;

namespace DevReload.Oarx
{
    /// <summary>Outcome of one OARX lifecycle operation.</summary>
    public record OarxActionResult(
        string Name,
        bool Success,
        bool Loaded,
        string Message,
        IReadOnlyList<string>? Modules = null,
        string? BuildLog = null);

    /// <summary>One module inside an OARX group, projected for callers outside
    /// the Oarx module.</summary>
    /// <remarks><see cref="TargetPath"/> and <see cref="ModuleFileName"/> are null
    /// until MSBuild has been asked where the module lands. There is no guessed
    /// default — a wrong output directory is the exact wrong-but-plausible failure
    /// this design refuses to have (research F7).</remarks>
    public sealed record OarxModuleInfo(
        string ProjectFilePath,
        string ProjectName,
        string? TargetPath,
        string? ModuleFileName,
        bool Loaded);

    /// <summary>One OARX group's registration and live state.</summary>
    /// <remarks><see cref="Loaded"/> is the state of the group AS A WHOLE. A cycle
    /// that died part-way sets <see cref="PartiallyLoaded"/> instead, which is a
    /// different thing and must not read as loaded.</remarks>
    public sealed record OarxPluginInfo(
        string Name,
        bool Loaded,
        bool PartiallyLoaded,
        string BuildConfiguration,
        string SolutionFilePath,
        string? ActiveWorktreePath,
        bool ConfigPending,
        IReadOnlyList<OarxModuleInfo> Modules);

    /// <summary>
    /// The OARX plugin lifecycle: registry, build, load, unload, reload.
    /// </summary>
    /// <remarks>
    /// Deliberately a sibling of <see cref="PluginManager"/> rather than an
    /// extension of it. The two lifecycles share a shape (Load / Reload / Unload
    /// / BuildOnly over a named registration) and nothing else:
    ///
    /// <para>The .NET path stream-loads assembly BYTES into a collectible ALC, so
    /// the file is never locked and a build can happen while the old plugin is
    /// still running — build first, then swap, and a failed build costs nothing.</para>
    ///
    /// <para>A native module is MAPPED from its file, so the file is locked for as
    /// long as it is loaded. The order is forced the other way — unload, verify,
    /// build, load — and a failed build therefore leaves the session with nothing
    /// loaded. That is not a defect to hide; it is reported loudly instead.</para>
    /// </remarks>
    internal static class OarxManager
    {
        private const string Platform = "x64";

        private static readonly Dictionary<string, OarxRegistration> _plugins =
            new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? Registered;
        public static event Action<string>? Unregistered;
        public static event Action<string>? StateChanged;

        // ── Registry ──────────────────────────────────────────────────

        public static void Add(OarxRegistration reg)
        {
            _plugins[reg.Name] = reg;
            Registered?.Invoke(reg.Name);
        }

        public static bool IsRegistered(string name) => _plugins.ContainsKey(name);

        public static bool IsLoaded(string name) =>
            _plugins.TryGetValue(name, out var reg) && reg.IsLoaded;

        public static IReadOnlyList<string> GetRegisteredNames() => _plugins.Keys.ToList();

        /// <summary>Every registered group with its live state. The single source
        /// of truth for registration + load status, mirroring
        /// <c>PluginManager.ListPluginSnapshots</c>.</summary>
        public static IReadOnlyList<OarxPluginInfo> ListSnapshots() =>
            _plugins.Values.Select(SnapshotOf).ToList();

        private static OarxPluginInfo SnapshotOf(OarxRegistration reg) =>
            new(
                Name: reg.Name,
                Loaded: reg.IsLoaded,
                PartiallyLoaded: reg.IsPartiallyLoaded,
                BuildConfiguration: reg.BuildConfiguration,
                SolutionFilePath: reg.SolutionFilePath,
                ActiveWorktreePath: reg.ActiveWorktreePath,
                ConfigPending: reg.PendingEntry != null,
                Modules: reg.Modules.Select(m => new OarxModuleInfo(
                    ProjectFilePath: m.ProjectFilePath,
                    ProjectName: m.ProjectName,
                    TargetPath: m.TargetPath,
                    ModuleFileName: m.ModuleFileName,
                    Loaded: m.IsLoaded)).ToList());


        internal static bool TryGet(string name, out OarxRegistration reg) =>
            _plugins.TryGetValue(name, out reg!);

        public static bool UnregisterInMemory(string name)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return false;
            try { UnloadModules(reg, NullReloadProgress.Instance); } catch (Exception) { }
            foreach (var (group, cmd, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmd);
            reg.LoaderCommands.Clear();
            _plugins.Remove(name);
            Unregistered?.Invoke(name);
            return true;
        }

        // ── Config edits ──────────────────────────────────────────────

        /// <summary>True when the live registration was built from an entry
        /// equal to <paramref name="entry"/> — the diff a config resync needs.
        /// Serialize-compare: cheap, and it cannot drift from the entry shape.</summary>
        public static bool MatchesSource(string name, OarxPluginEntry entry) =>
            _plugins.TryGetValue(name, out var reg) &&
            System.Text.Json.JsonSerializer.Serialize(reg.Source) ==
            System.Text.Json.JsonSerializer.Serialize(entry);

        public static bool HasPendingConfig(string name) =>
            _plugins.TryGetValue(name, out var reg) && reg.PendingEntry != null;

        /// <summary>Stage a changed on-disk entry against a group whose modules
        /// are currently mapped. Applied by the next Load/Reload; the running
        /// registration is never yanked out from under loaded modules.</summary>
        internal static void StagePendingEntry(string name, OarxPluginEntry entry)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return;
            reg.PendingEntry = entry;
            StateChanged?.Invoke(name);
        }

        /// <summary>
        /// Take a freshly saved config entry live. Everything except the module
        /// list applies immediately regardless of load state — properties are
        /// read at the next build, companions at the next load. A changed module
        /// list on a group with mapped modules is staged instead.
        /// </summary>
        internal static OarxActionResult ApplyEntry(OarxPluginEntry entry, bool prefixChanged)
        {
            if (!_plugins.TryGetValue(entry.Name, out var reg))
                return new OarxActionResult(entry.Name, true, false,
                    "updated plugins.json (group is not registered live)");

            bool modulesChanged = !reg.Modules.Select(m => m.ProjectFilePath)
                .SequenceEqual(entry.ProjectFilePaths, StringComparer.OrdinalIgnoreCase);

            if (modulesChanged && (reg.IsLoaded || reg.IsPartiallyLoaded))
            {
                // Safe fields live now, module list at the next cycle.
                PatchLiveFields(reg, entry, prefixChanged);
                reg.PendingEntry = entry;
                StateChanged?.Invoke(entry.Name);
                return new OarxActionResult(entry.Name, true, reg.IsLoaded,
                    "updated — the changed module list applies at the next load/reload " +
                    "(the current modules stay mapped until then)");
            }

            if (modulesChanged)
            {
                SwapRegistration(reg, entry);
                return new OarxActionResult(entry.Name, true, false, "updated");
            }

            PatchLiveFields(reg, entry, prefixChanged);
            reg.Source = entry;
            reg.PendingEntry = null;
            StateChanged?.Invoke(entry.Name);
            return new OarxActionResult(entry.Name, true, reg.IsLoaded,
                reg.IsLoaded
                    ? "updated — properties apply at the next build, companions at the next load"
                    : "updated");
        }

        private static void PatchLiveFields(
            OarxRegistration reg, OarxPluginEntry entry, bool prefixChanged)
        {
            reg.BuildConfiguration = entry.BuildConfiguration;
            reg.MsBuildProperties.Clear();
            reg.MsBuildProperties.AddRange(entry.MsBuildProperties);
            reg.PreloadNativeModules.Clear();
            reg.PreloadNativeModules.AddRange(entry.PreloadNativeModules);
            reg.PreloadManagedAssemblies.Clear();
            reg.PreloadManagedAssemblies.AddRange(entry.PreloadManagedAssemblies);
            reg.PostloadManagedAssemblies.Clear();
            reg.PostloadManagedAssemblies.AddRange(entry.PostloadManagedAssemblies);

            if (prefixChanged)
            {
                foreach (var (group, cmd, _) in reg.LoaderCommands)
                    Utils.RemoveCommand(group, cmd);
                reg.LoaderCommands.Clear();
                RegisterLoaderCommands(entry.Name, entry.CommandPrefix ?? entry.Name);
            }
        }

        /// <summary>Replace a registration wholesale from its entry. Only legal
        /// with nothing mapped — the callers guarantee that.</summary>
        private static OarxRegistration SwapRegistration(
            OarxRegistration reg, OarxPluginEntry entry)
        {
            foreach (var (group, cmd, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmd);
            reg.LoaderCommands.Clear();

            var fresh = OarxConfigLoader.BuildRegistration(entry);
            _plugins[entry.Name] = fresh;
            RegisterLoaderCommands(entry.Name, entry.CommandPrefix ?? entry.Name);
            StateChanged?.Invoke(entry.Name);
            return fresh;
        }

        /// <summary>Apply a staged config entry once the group's modules are
        /// out. Returns the registration the cycle must continue with.</summary>
        private static OarxRegistration ConsumePending(
            OarxRegistration reg, IReloadProgress ui)
        {
            if (reg.PendingEntry == null) return reg;
            ui.Line("applying the config change that was staged while the group was loaded");
            return SwapRegistration(reg, reg.PendingEntry);
        }

        // ── Lifecycle ─────────────────────────────────────────────────

        /// <summary>Load the group as it currently sits on disk, building only
        /// the modules whose output is missing.</summary>
        public static OarxActionResult Load(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: load", Cycle);

            try
            {
                if (reg.IsLoaded)
                {
                    // The modules may be resident without this group having run —
                    // the dynamic linker keys on FILE NAME, so a demand-loaded
                    // copy from another directory reads as "loaded" here. The
                    // companions are idempotent, so deliver them regardless: a
                    // load command's contract is "the whole stack is up", not
                    // "the native half happened to be".
                    RunPreloads(reg, ui);
                    RunPostloads(reg, ui);
                    ui.Finish("already loaded", true);
                    return Result(reg, true, reg.PendingEntry == null
                        ? "already loaded (companions ensured)"
                        : "already loaded (companions ensured); a staged config " +
                          "change applies at the next reload");
                }

                // A staged config edit applies now — the group is not (fully)
                // loaded, so the module list is free to change. A partial load
                // is emptied first so no old module stays mapped unmanaged.
                if (reg.PendingEntry != null)
                {
                    if (reg.IsPartiallyLoaded)
                    {
                        ui.Step(ReloadStep.Unload);
                        ui.Line("clearing a partially-loaded group");
                        UnloadModules(reg, ui);
                    }
                    reg = ConsumePending(reg, ui);
                }

                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }

                // A partially-loaded group (a previous cycle died mid-way) must be
                // emptied before loading, or the load order is not what it claims.
                if (reg.IsPartiallyLoaded)
                {
                    ui.Step(ReloadStep.Unload);
                    ui.Line("clearing a partially-loaded group");
                    UnloadModules(reg, ui);
                }

                var missing = reg.Modules.Where(m => !File.Exists(m.TargetPath!)).ToList();
                if (missing.Count > 0)
                {
                    ui.Step(ReloadStep.Build);
                    ui.Line($"{missing.Count} module(s) not built yet");
                    var build = BuildModules(reg, ui);
                    if (build != null) { ui.Finish(build.Value.Message, false); return Result(reg, false, build.Value.Message, build.Value.Log); }
                }

                ui.Step(ReloadStep.Load);
                RunPreloads(reg, ui);
                LoadModules(reg, ui);
                RunPostloads(reg, ui);
                ui.Finish("loaded", true);
                return Result(reg, true, "loaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        /// <summary>
        /// The dev loop: unload the whole group, prove every output is writable,
        /// rebuild, load again.
        /// </summary>
        public static OarxActionResult Reload(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: reload", Cycle);

            try
            {
                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }
                WarnAboutOtherHosts(ui);

                ui.Step(ReloadStep.Unload);
                UnloadModules(reg, ui);

                // With the OLD module set out, a staged config edit can land.
                // The new module list needs its own target resolution before
                // the writability check below can speak about it.
                if (reg.PendingEntry != null)
                {
                    reg = ConsumePending(reg, ui);
                    var reResolve = ResolveTargets(reg, ui);
                    if (reResolve != null) { ui.Finish(reResolve, false); return Result(reg, false, reResolve); }
                }

                // The unload said it succeeded. This is where that is checked
                // against the only authority that matters — whether the linker
                // could actually rewrite the file.
                ui.Step(ReloadStep.Verify);
                var locked = reg.Modules
                    .Where(m => !OarxModuleHost.IsFileWritable(m.TargetPath!))
                    .ToList();
                if (locked.Count > 0)
                {
                    string why = string.Join(" ",
                        locked.Select(m => OarxModuleHost.DescribeStillLocked(m.TargetPath!)));
                    string msg =
                        "ABORTED after unload — the build would fail with LNK1168. " + why +
                        " Nothing was rebuilt; the modules are UNLOADED.";
                    ui.Line(msg);
                    ui.Finish("still locked after unload", false);
                    return Result(reg, false, msg);
                }
                ui.Line("all module files are writable");

                ui.Step(ReloadStep.Build);
                var build = BuildModules(reg, ui);
                if (build != null)
                {
                    string msg = build.Value.Message +
                        " The modules remain UNLOADED — a native module is mapped from its " +
                        "file, so it had to be unloaded before the linker could rewrite it.";
                    ui.Finish("build failed", false);
                    return Result(reg, false, msg, build.Value.Log);
                }

                ui.Step(ReloadStep.Load);
                RunPreloads(reg, ui);
                LoadModules(reg, ui);
                RunPostloads(reg, ui);
                ui.Finish("reloaded", true);
                return Result(reg, true, "reloaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        public static OarxActionResult Unload(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            try
            {
                if (!reg.IsLoaded && !reg.IsPartiallyLoaded)
                    return Result(reg, true, "not loaded");

                ui.Begin($"{name}: unload", Cycle);
                ui.Step(ReloadStep.Unload);
                UnloadModules(reg, ui);
                ui.Finish("unloaded", true);
                return Result(reg, true, "unloaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        /// <summary>Build without loading. Only possible while the group is
        /// unloaded — the outputs are locked otherwise.</summary>
        public static OarxActionResult BuildOnly(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: build", Cycle);
            try
            {
                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }

                if (reg.Modules.Any(m => m.IsLoaded))
                {
                    const string msg =
                        "cannot build while the group is loaded — a native module's file is " +
                        "locked while it is mapped. Unload it first, or use Reload.";
                    ui.Finish("loaded; build refused", false);
                    return Result(reg, false, msg);
                }

                ui.Step(ReloadStep.Build);
                var build = BuildModules(reg, ui);
                if (build != null) { ui.Finish("build failed", false); return Result(reg, false, build.Value.Message, build.Value.Log); }

                ui.Finish("built", true);
                return Result(reg, true, "built (not loaded)");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        public static OarxActionResult UnloadAll()
        {
            int n = 0;
            foreach (var reg in _plugins.Values)
            {
                try
                {
                    if (!reg.Modules.Any(m => m.IsLoaded)) continue;
                    UnloadModules(reg, NullReloadProgress.Instance);
                    n++;
                }
                catch (Exception) { /* best-effort during shutdown */ }
            }
            return new OarxActionResult("*", true, false, $"unloaded {n} OARX group(s)");
        }

        // ── Steps ─────────────────────────────────────────────────────

        /// <summary>Ask MSBuild where each module lands. Returns null on success,
        /// or the reason it could not be resolved — never a guessed path.</summary>
        private static string? ResolveTargets(OarxRegistration reg, IReloadProgress ui)
        {
            if (reg.Modules.Count == 0)
                return $"'{reg.Name}' has no modules registered.";

            string solutionDir = reg.SolutionDirectory;
            if (!Directory.Exists(solutionDir))
                return $"solution directory does not exist: {solutionDir}";

            foreach (var m in reg.Modules)
            {
                string proj = reg.EffectiveProjectPath(m);
                if (!File.Exists(proj))
                    return $"project file not found: {proj}";

                string? target = BuildService.QueryMsBuildProperty(
                    proj, "TargetPath", reg.BuildConfiguration, Platform, solutionDir,
                    reg.MsBuildProperties);

                if (string.IsNullOrEmpty(target))
                    return $"MSBuild could not resolve TargetPath for '{m.ProjectName}' " +
                           $"({reg.BuildConfiguration}|{Platform}). " +
                           "Check the configuration exists and the project evaluates.";

                m.TargetPath = target;
                _ = m.Kind; // throws OarxModuleException if the extension is not ObjectARX
                ui.Line($"{m.ProjectName} -> {Path.GetFileName(target)}");
            }
            return null;
        }

        /// <summary>Companions that come BEFORE the modules: full-path native pins
        /// (so base-name references bind to the canonical copies) and managed
        /// assemblies that must be running while a module initialises (a trace UI
        /// listening for a dbx's load-time logs).</summary>
        private static void RunPreloads(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var p in reg.PreloadNativeModules)
                OarxCompanionHost.PinNative(p, ui);
            foreach (var p in reg.PreloadManagedAssemblies)
                OarxCompanionHost.LoadManaged(p, ui);
        }

        /// <summary>Companions that come AFTER the modules: managed assemblies
        /// that import from a module and must not be the thing that maps it
        /// (a mixed-mode interop over the group's dbx).</summary>
        private static void RunPostloads(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var p in reg.PostloadManagedAssemblies)
                OarxCompanionHost.LoadManaged(p, ui);
        }

        private static void LoadModules(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var m in reg.Modules)
            {
                OarxModuleHost.Load(m.TargetPath!);
                ui.Line($"loaded {m.ModuleFileName}");
            }
            StateChanged?.Invoke(reg.Name);
        }

        /// <summary>Unload in REVERSE declaration order: the .arx that uses the
        /// .dbx's classes must go first.</summary>
        private static void UnloadModules(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var m in Enumerable.Reverse(reg.Modules))
            {
                if (m.ModuleFileName == null) continue;
                OarxModuleHost.Unload(m.ModuleFileName);
                ui.Line($"unloaded {m.ModuleFileName}");
            }
            StateChanged?.Invoke(reg.Name);
        }

        private static (string Message, string? Log)? BuildModules(
            OarxRegistration reg, IReloadProgress ui)
        {
            string solutionDir = reg.SolutionDirectory;
            foreach (var m in reg.Modules)
            {
                string proj = reg.EffectiveProjectPath(m);
                ui.Line($"building {m.ProjectName} ({reg.BuildConfiguration}|{Platform})");

                // The HUD's own sink already streams every build line, so the
                // BuildService progress callback is left null here — routing it
                // through ui.Line as well would report each line twice.
                var result = BuildService.BuildProject(
                    proj, reg.BuildConfiguration, Platform, null, solutionDir,
                    new PumpedBuildRunner(ui), reg.MsBuildProperties);

                if (!result.Success)
                    return ($"build FAILED for '{m.ProjectName}' " +
                            $"({result.Errors} error(s)).", result.Log);

                m.TargetPath = result.OutputPath;
            }
            return null;
        }

        // F8: the writability probe is process-global, so a second AutoCAD holding
        // the same modules blocks the build. Cheap to check, and it turns a
        // baffling post-unload abort into an obvious one.
        private static void WarnAboutOtherHosts(IReloadProgress ui)
        {
            try
            {
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                var others = System.Diagnostics.Process.GetProcessesByName("acad")
                    .Select(p => p.Id).Where(id => id != self).ToList();
                if (others.Count > 0)
                    ui.Line($"NOTE: another AutoCAD is running (pid {string.Join(", ", others)}). " +
                            "If it has these modules loaded, the rebuild will be blocked.");
            }
            catch (Exception) { }
        }

        // ── Settings ──────────────────────────────────────────────────

        /// <summary>
        /// Change the configuration a group builds under, in memory and in
        /// plugins.json.
        /// </summary>
        /// <remarks>
        /// Deliberately does NOT invalidate the resolved TargetPaths. The
        /// dynamic linker keys a loaded module on its FILE NAME (research F6),
        /// and that name is the same in every configuration and every worktree —
        /// so the group's loaded state stays readable across the switch, and the
        /// next Load/Reload re-resolves the paths anyway.
        /// </remarks>
        public static bool UpdateBuildConfiguration(string name, string buildConfiguration)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return false;
            reg.BuildConfiguration = buildConfiguration;
            reg.Source.BuildConfiguration = buildConfiguration;
            OarxConfigLoader.UpdateEntry(name, e => e.BuildConfiguration = buildConfiguration);
            StateChanged?.Invoke(name);
            return true;
        }

        /// <summary>Point the group at another git worktree. See the remark on
        /// <see cref="UpdateBuildConfiguration"/> for why load state survives.</summary>
        public static bool UpdateActiveWorktree(string name, string? worktreePath)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return false;
            reg.ActiveWorktreePath = worktreePath;
            reg.Source.ActiveWorktreePath = worktreePath;
            OarxConfigLoader.UpdateEntry(name, e => e.ActiveWorktreePath = worktreePath);
            StateChanged?.Invoke(name);
            return true;
        }

        /// <summary>Module file names in load order, for display. Falls back to
        /// the project name for modules MSBuild has not been asked about yet —
        /// this is a label, not a path, so there is nothing to get wrong.</summary>
        public static IReadOnlyList<string> DescribeModules(string name) =>
            _plugins.TryGetValue(name, out var reg)
                ? reg.Modules.Select(m => m.ModuleFileName ?? m.ProjectName).ToList()
                : Array.Empty<string>();

        // ── Loader commands ───────────────────────────────────────────

        public static void RegisterLoaderCommands(string name, string prefix)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return;
            prefix = prefix.ToUpperInvariant();
            const string group = "DEVRELOAD";

            void Add(string suffix, Action action)
            {
                string cmd = prefix + suffix;
                CommandCallback cb = () => action();
                Utils.AddCommand(group, cmd, cmd, CommandFlags.Modal, cb);
                reg.LoaderCommands.Add((group, cmd, cb));
            }

            Add("LOAD", () => Load(name));
            Add("DEV", () => Reload(name));
            Add("UNLOAD", () => Unload(name));
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static OarxActionResult Result(
            OarxRegistration reg, bool success, string message, string? log = null)
        {
            StateChanged?.Invoke(reg.Name);
            return new OarxActionResult(
                reg.Name, success, reg.IsLoaded, message,
                reg.Modules.Select(m => m.ModuleFileName ?? m.ProjectName).ToList(),
                log);
        }

        /// <summary>
        /// The sink a cycle reports to when the caller does not supply one: the
        /// transient HUD for the person watching it happen, and the command line
        /// for the record they can scroll back to.
        /// </summary>
        /// <remarks>
        /// No editor means no drawing, and both sinks need one — that is the MCP /
        /// startup path, which reads the returned <see cref="OarxActionResult"/>
        /// instead of watching.
        /// </remarks>
        /// <summary>
        /// The OARX cycle, in the order it runs. Unload comes BEFORE build because
        /// a loaded native module locks its own file: the linker cannot write over
        /// an .arx that is still mapped. That ordering is why a failed build leaves
        /// the group unloaded (research F14).
        /// </summary>
        private static readonly ReloadCycle Cycle = new(
            (ReloadStep.Preflight, "resolving module outputs"),
            (ReloadStep.Unload,    "unloading modules"),
            (ReloadStep.Verify,    "checking the files are writable"),
            (ReloadStep.Build,     "building"),
            (ReloadStep.Load,      "loading modules"));

        private static IReloadProgress DefaultProgress()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return NullReloadProgress.Instance;

            void Write(string msg) => ed.WriteMessage("\n" + msg);
            return new CompositeReloadProgress(
                new ReloadHud(warn => Write("[OARX] " + warn)),
                new EditorReloadProgress(Write, "OARX"));
        }
    }
}
