using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

using Exception = System.Exception;

namespace DevReload
{
    [AcadRpcSurface]
    public static class PluginManager
    {
        private static readonly Dictionary<string, PluginRegistration> _plugins = new();

        public static PluginRegistrationBuilder Register(string pluginName)
        {
            return new PluginRegistrationBuilder(pluginName);
        }

        public static void Load(string pluginName)
        {
            var ed = GetEditor();
            try
            {
                var reg = GetRegistration(pluginName);

                if (reg.Host.IsLoaded)
                {
                    ed?.WriteMessage($"\n{pluginName} is already loaded.");
                    return;
                }

                string dllPath = reg.DllPath;

                if (!File.Exists(dllPath))
                {
                    string csprojPath = GetEffectiveCsprojPath(reg);
                    ed?.WriteMessage($"\n{pluginName} DLL not found, building...");
                    string? builtPath = DevReloadService.BuildProject(
                        csprojPath, reg.BuildConfiguration, ed);
                    if (builtPath == null) return;
                    reg.DllPath = builtPath;
                    dllPath = builtPath;
                }

                try
                {
                    LoadCore(reg, dllPath);
                }
                catch (StalePluginException)
                {
                    ed?.WriteMessage($"\nStale plugin detected, rebuilding...");
                    string csprojPath = GetEffectiveCsprojPath(reg);
                    string? rebuilt = DevReloadService.BuildProject(
                        csprojPath, reg.BuildConfiguration, ed);
                    if (rebuilt == null) return;
                    reg.DllPath = rebuilt;
                    dllPath = rebuilt;
                    LoadCore(reg, dllPath);
                }

                string cmdMsg = reg.Registrar != null
                    ? $" {reg.Registrar.CommandCount} commands registered."
                    : "";
                ed?.WriteMessage($"\n{pluginName} loaded.{cmdMsg}");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} load error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
            }
        }

        public static void DevReload(string pluginName)
        {
            var ed = GetEditor();
            try
            {
                var reg = GetRegistration(pluginName);

                string csprojPath = GetEffectiveCsprojPath(reg);
                string? dllPath = DevReloadService.BuildProject(
                    csprojPath, reg.BuildConfiguration, ed);
                if (dllPath == null) return;
                reg.DllPath = dllPath;

                try
                {
                    LoadCore(reg, dllPath);
                }
                catch (StalePluginException)
                {
                    ed?.WriteMessage(
                        $"\n{pluginName}: IExtensionApplication version mismatch. Restart AutoCAD.");
                    return;
                }

                string cmdMsg = reg.Registrar != null
                    ? $" {reg.Registrar.CommandCount} commands registered."
                    : "";
                ed?.WriteMessage($"\n{pluginName} dev-reloaded.{cmdMsg}");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} dev-reload error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
            }
        }

        public static void Unload(string pluginName)
        {
            var ed = GetEditor();
            try
            {
                var reg = GetRegistration(pluginName);

                if (!reg.Host.IsLoaded)
                {
                    ed?.WriteMessage($"\n{pluginName} is not loaded.");
                    return;
                }

                TearDown(reg);
                ed?.WriteMessage($"\n{pluginName} unloaded.");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n{pluginName} unload error: {ex.Message}");
                ed?.WriteMessage($"\n{ex}");
            }
        }

        public static void UnloadAll()
        {
            foreach (var reg in _plugins.Values)
            {
                try { TearDown(reg); }
                catch { /* best-effort during shutdown */ }
            }
        }

        // ── Public query + management API ─────────────────────────────

        public static IReadOnlyList<string> GetRegisteredPluginNames()
            => _plugins.Keys.ToList();

        public static bool IsRegistered(string pluginName)
            => _plugins.ContainsKey(pluginName);

        public static bool IsLoaded(string pluginName)
            => _plugins.TryGetValue(pluginName, out var reg) && reg.Host.IsLoaded;

        public static void Unregister(string pluginName)
        {
            if (!_plugins.TryGetValue(pluginName, out var reg))
                return;

            TearDown(reg);
            UnregisterLoaderCommands(reg);
            _plugins.Remove(pluginName);
        }

        // ── MCP tool surface ──────────────────────────────────────────
        // Each tool is an annotated method sitting next to the verb it
        // wraps. No central registry, no Verbs/ folder. AcadRpcHost
        // scans this assembly on Initialize and surfaces these as MCP
        // tools. Methods marshal to the AutoCAD main thread via
        // AcadRpc.OnMainThread so the SDK worker never touches AutoCAD
        // APIs directly.

        [AcadRpcTool, Description("Build the plugin from source and load (or reload) it into a fresh isolated ALC.")]
        public static Task<string> Reload(
            [Description("Registered plugin name as in plugins.json (e.g. \"DevReloadTest\")")] string name,
            CancellationToken ct = default)
            => AcadRpc.OnMainThread<string>(() =>
            {
                try { DevReload(name); return $"reload ok: {name}"; }
                catch (Exception ex) { return $"reload failed: {ex.Message}"; }
            }, ct);

        [AcadRpcTool, Description("Load a registered plugin if not already loaded.")]
        public static Task<string> LoadPlugin(
            [Description("Registered plugin name")] string name,
            CancellationToken ct = default)
            => AcadRpc.OnMainThread<string>(() =>
            {
                try { Load(name); return $"load ok: {name}"; }
                catch (Exception ex) { return $"load failed: {ex.Message}"; }
            }, ct);

        [AcadRpcTool, Description("Unload a loaded plugin (tear down the isolated ALC).")]
        public static Task<string> UnloadPlugin(
            [Description("Registered plugin name")] string name,
            CancellationToken ct = default)
            => AcadRpc.OnMainThread<string>(() =>
            {
                try { Unload(name); return $"unload ok: {name}"; }
                catch (Exception ex) { return $"unload failed: {ex.Message}"; }
            }, ct);

        [AcadRpcTool, Description("List all registered plugins with their loaded/unloaded state.")]
        public static Task<string> ListPlugins(CancellationToken ct = default)
            => AcadRpc.OnMainThread<string>(() =>
            {
                var lines = _plugins.Values.Select(reg =>
                    $"{reg.PluginName} | loaded={reg.Host.IsLoaded} | config={reg.BuildConfiguration} | dll={reg.DllPath}");
                return string.Join("\n", lines);
            }, ct);

        [AcadRpcTool, Description("Check whether a plugin is currently loaded.")]
        public static Task<bool> IsPluginLoaded(
            [Description("Registered plugin name")] string name,
            CancellationToken ct = default)
            => AcadRpc.OnMainThread<bool>(() => IsLoaded(name), ct);

        [AcadRpcTool, Description("Get info about a loaded plugin's assembly (name, location, last-write timestamp).")]
        public static Task<string> GetAssemblyInfo(
            [Description("Registered plugin name")] string name,
            CancellationToken ct = default)
            => AcadRpc.OnMainThread<string>(() =>
            {
                if (!_plugins.TryGetValue(name, out var reg)) return $"not registered: {name}";
                if (!reg.Host.IsLoaded || reg.Host.LoadedAssembly == null) return $"not loaded: {name}";
                var asm = reg.Host.LoadedAssembly;
                var asmName = asm.GetName();
                var loc = asm.Location;
                var when = string.IsNullOrEmpty(loc) || !File.Exists(loc)
                    ? "(streamed; no file)"
                    : File.GetLastWriteTimeUtc(loc).ToString("O");
                return $"{asmName.Name} v{asmName.Version} loaded={loc} lastWriteUtc={when}";
            }, ct);

        [AcadRpcTool, Description("List every MCP tool currently registered with the Acad.Rpc host, grouped by source assembly.")]
        public static Task<string> ListTools(CancellationToken ct = default)
            => Task.FromResult(string.Join("\n",
                AcadRpcHost.Current.ListRegisteredTools()
                    .OrderBy(t => t.SourceAssembly).ThenBy(t => t.ToolName)
                    .Select(t => $"{t.SourceAssembly}::{t.ToolName} — {t.Description ?? ""}")));

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
        /// register commands.
        /// AutoCAD calls IExtensionApplication.Initialize() automatically.
        /// DevReload does NOT call Initialize().
        /// </summary>
        private static void LoadCore(PluginRegistration reg, string dllPath)
        {
            TearDown(reg);

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

            if (sharedNames.Length > 0)
            {
                var ed = GetEditor();
                var mixedSet = new HashSet<string>(
                    sharedConfig.MixedModeAssemblies, StringComparer.OrdinalIgnoreCase);
                var streamedSet = new HashSet<string>(
                    sharedConfig.StreamedAssemblies, StringComparer.OrdinalIgnoreCase);

                foreach (string asmName in sharedNames)
                {
                    string asmPath = Path.Combine(pluginDir, asmName + ".dll");
                    if (!File.Exists(asmPath)) continue;

                    if (mixedSet.Contains(asmName))
                    {
                        EnsureRuntimeConfig(asmPath, asmName, ed);
                        Assembly.LoadFrom(asmPath);
                    }
                    else if (streamedSet.Contains(asmName))
                    {
                        LoadSharedFromStream(asmPath);
                    }
                    else
                    {
                        Assembly.LoadFrom(asmPath);
                    }
                }
            }

            var plugin = reg.Host.Load(dllPath, sharedNames);

            if (reg.Registrar != null)
            {
                reg.Registrar.RegisterFromAssembly(reg.Host.LoadedAssembly!);
            }

            // Register the freshly-loaded assembly's tools into the
            // unified MCP surface. AutoCAD has already invoked the
            // plugin's IExtensionApplication.Initialize() via its
            // AssemblyLoad-event-driven scan; tool registration happens
            // immediately after so the new tools become visible to the
            // agent as soon as Initialize completes its work.
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

        // Stream-loads a shared assembly INTO the default ALC.
        //
        // We must call AssemblyLoadContext.Default.LoadFromStream(...) explicitly.
        // Assembly.Load(byte[]) looks like the "right" API but it loads into a
        // brand-new anonymous AssemblyLoadContext (one per call) per the
        // documented .NET algorithm — the assembly never ends up in
        // AssemblyLoadContext.Default and is invisible to name-based binding
        // from the isolated plugin ALC.
        //
        // Default.LoadFromStream behaves like LoadFrom for binding purposes
        // (assembly is in Default.Assemblies, findable by name), but without
        // the file lock on the DLL on disk.
        //
        // The default ALC is non-collectible. The streamed image lives until
        // AutoCAD exits — rebuilding the DLL on disk is fine, but the running
        // plugin keeps seeing the old surface until AutoCAD restarts.
        //
        // Idempotency guard: on the second DevReload cycle, this method is
        // called again with the same simple name. Default.LoadFromStream is
        // NOT idempotent by name — it throws "Assembly with same name is
        // already loaded". Since the existing image is permanent (default ALC
        // is non-collectible), there's nothing to do on a re-load anyway —
        // skip silently. (Compare Assembly.LoadFrom, which is already
        // idempotent by name and is why the LoadFrom branches of LoadCore
        // never hit this problem.)
        private static void LoadSharedFromStream(string asmPath)
        {
            string asmName = Path.GetFileNameWithoutExtension(asmPath);
            foreach (var existing in AssemblyLoadContext.Default.Assemblies)
            {
                if (string.Equals(
                        existing.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            byte[] asmBytes = File.ReadAllBytes(asmPath);
            string pdbPath = Path.ChangeExtension(asmPath, ".pdb");
            using var asmStream = new MemoryStream(asmBytes);
            if (File.Exists(pdbPath))
            {
                byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);
                AssemblyLoadContext.Default.LoadFromStream(asmStream, pdbStream);
            }
            else
            {
                AssemblyLoadContext.Default.LoadFromStream(asmStream);
            }
        }

        private static void EnsureRuntimeConfig(string asmPath, string asmName, Editor? ed)
        {
            string asmDir = Path.GetDirectoryName(asmPath)!;
            string rcPath = Path.Combine(asmDir, asmName + ".runtimeconfig.json");
            if (!File.Exists(rcPath))
            {
                ed?.WriteMessage($"\n[DevReload] Creating runtimeconfig.json for mixed-mode: {asmName}");
                File.WriteAllText(rcPath,
                    """
                    {
                      "runtimeOptions": {
                        "tfm": "net8.0",
                        "framework": {
                          "name": "Microsoft.NETCore.App",
                          "version": "8.0.0"
                        }
                      }
                    }
                    """);
            }

            string ijwPath = Path.Combine(asmDir, "Ijwhost.dll");
            if (!File.Exists(ijwPath))
                ed?.WriteMessage($"\n[DevReload] WARNING: Ijwhost.dll not found in {asmDir}");
        }

        private static string GetEffectiveCsprojPath(PluginRegistration reg)
        {
            if (string.IsNullOrEmpty(reg.ActiveWorktreePath))
                return reg.ProjectFilePath;

            string? repoRoot = GitWorktreeService.GetRepoRoot(
                Path.GetDirectoryName(reg.ProjectFilePath)!);
            if (repoRoot == null)
                return reg.ProjectFilePath;

            return GitWorktreeService.RemapToWorktree(
                reg.ProjectFilePath, repoRoot, reg.ActiveWorktreePath);
        }

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

        public static void UpdateBuildConfiguration(string pluginName, string buildConfiguration)
        {
            if (_plugins.TryGetValue(pluginName, out var reg))
                reg.BuildConfiguration = buildConfiguration;
        }

        public static void UpdateActiveWorktree(string pluginName, string? worktreePath)
        {
            if (_plugins.TryGetValue(pluginName, out var reg))
                reg.ActiveWorktreePath = worktreePath;
        }

        internal static void AddRegistration(PluginRegistration reg)
        {
            _plugins[reg.PluginName] = reg;
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
