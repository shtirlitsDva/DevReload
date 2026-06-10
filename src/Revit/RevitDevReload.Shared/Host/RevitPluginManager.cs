using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using DevReload.Core;

using RevitDevReload.Core;

namespace RevitDevReload
{
    public sealed class RevitActionResult
    {
        public RevitActionResult(string pluginName, bool success, string message,
            int commandCount = 0, bool loaded = false, BuildResult? build = null)
        {
            PluginName = pluginName;
            Success = success;
            Message = message;
            CommandCount = commandCount;
            Loaded = loaded;
            Build = build;
        }

        public string PluginName { get; }
        public bool Success { get; }
        public string Message { get; }
        public int CommandCount { get; }
        public bool Loaded { get; }
        public BuildResult? Build { get; }
    }

    public sealed class RevitPluginRegistration
    {
        public RevitPluginRegistration(RevitPluginEntry entry)
        {
            Entry = entry;
        }

        public RevitPluginEntry Entry { get; }
        public LoadedPluginHandle? Handle { get; internal set; }
        public IReadOnlyList<DiscoveredCommand> Commands { get; internal set; }
            = Array.Empty<DiscoveredCommand>();

        // Plugin's own IExternalApplication instance when it has one —
        // OnShutdown is invoked before unload so the plugin can release
        // events/updaters that would otherwise pin the ALC.
        public object? PluginApp { get; internal set; }

        public bool IsLoaded => Handle != null;
    }

    // Same orchestration shape as the AutoCAD PluginManager: registry is the
    // single source of truth, build-first-then-swap, events let the UI follow
    // out-of-band (pipe-driven) changes.
    public static class RevitPluginManager
    {
        private static readonly Dictionary<string, RevitPluginRegistration> _plugins =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

#if NET8_0_OR_GREATER
        private static readonly IPluginLoader _loader = new AlcPluginLoader();
#else
        private static readonly IPluginLoader _loader = new LegacyPluginLoader();
#endif

        public static event Action? PluginsChanged;

        public static bool SupportsTrueUnload => _loader.SupportsTrueUnload;

        public static IReadOnlyList<RevitPluginRegistration> All
        {
            get { lock (_lock) return _plugins.Values.ToList(); }
        }

        public static RevitPluginRegistration? Get(string name)
        {
            lock (_lock) return _plugins.TryGetValue(name, out var r) ? r : null;
        }

        public static RevitActionResult Register(RevitPluginEntry entry, bool persist = true)
        {
            lock (_lock)
            {
                if (_plugins.ContainsKey(entry.Name))
                    return new RevitActionResult(entry.Name, false, "already registered");
                _plugins[entry.Name] = new RevitPluginRegistration(entry);
            }
            if (persist) PersistConfig();
            DevReloadLogBuffer.Add($"registered {entry.Name}");
            PluginsChanged?.Invoke();
            return new RevitActionResult(entry.Name, true, "registered");
        }

        public static RevitActionResult Unregister(string name)
        {
            var reg = Get(name);
            if (reg == null)
                return new RevitActionResult(name, false, "not registered");
            if (reg.IsLoaded)
            {
                var unload = Unload(name);
                if (!unload.Success) return unload;
            }
            lock (_lock) _plugins.Remove(name);
            PersistConfig();
            DevReloadLogBuffer.Add($"unregistered {name}");
            PluginsChanged?.Invoke();
            return new RevitActionResult(name, true, "unregistered");
        }

        // Build (when needed) on the calling thread, swap inside API context.
        public static RevitActionResult Load(string name) => LoadCore(name, forceBuild: false);

        public static RevitActionResult Reload(string name) => LoadCore(name, forceBuild: true);

        private static RevitActionResult LoadCore(string name, bool forceBuild)
        {
            var reg = Get(name);
            if (reg == null)
                return new RevitActionResult(name, false, "not registered");

            BuildResult? build = null;
            try
            {
                string? dllPath = reg.Entry.DllPath;

                if (forceBuild || string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    if (string.IsNullOrEmpty(reg.Entry.ProjectFilePath))
                        return new RevitActionResult(name, false,
                            "no csproj recorded and DLL missing");

                    string csproj = GitWorktreeService.ResolveActiveCsproj(
                        reg.Entry.ProjectFilePath!, reg.Entry.ActiveWorktreePath);

                    // Old-style csprojs (the pre-2025 norm in this user's
                    // repos) are AnyCPU — only force a platform for SDK-style.
                    string? platform = BuildService.IsSdkStyle(csproj) ? "x64" : null;
                    build = BuildService.BuildProject(
                        csproj, reg.Entry.BuildConfiguration, platform,
                        DevReloadLogBuffer.Add);
                    if (!build.Success || build.OutputPath == null)
                        return new RevitActionResult(name, false, "build failed",
                            build: build);
                    reg.Entry.DllPath = build.OutputPath;
                    dllPath = build.OutputPath;
                    PersistConfig();
                }

                // Build succeeded — NOW swap, inside API context so plugin
                // shutdown/startup hooks are legal. Old plugin keeps running
                // if the build failed above (we never get here).
                var runner = RevitContext.Runner
                    ?? throw new InvalidOperationException("API runner not attached");

                runner.Run(app =>
                {
                    if (reg.IsLoaded) UnloadInApiContext(reg);

                    string buildDir = Path.GetDirectoryName(dllPath!)!;
                    var sharedCfg = SharedAssembliesFile.Read(buildDir);
                    var handle = _loader.Load(dllPath!, sharedCfg.SharedAssemblies);

                    reg.Handle = handle;
                    reg.Commands = CommandScanner.FindExternalCommands(handle.Assembly);
                    StartPluginApp(reg, app);
                });

                DevReloadLogBuffer.Add(
                    $"{name} loaded — {reg.Commands.Count} command(s)");
                PluginsChanged?.Invoke();
                return new RevitActionResult(name, true,
                    forceBuild ? "reloaded" : "loaded",
                    reg.Commands.Count, loaded: true, build: build);
            }
            catch (Exception ex)
            {
                DevReloadLogBuffer.Add($"{name} load error: {ex.GetType().Name}: {ex.Message}");
                DevReloadLogBuffer.Add(ex.ToString());
                return new RevitActionResult(name, false,
                    $"load error: {ex.GetType().Name}: {ex.Message}", build: build);
            }
        }

        public static RevitActionResult Unload(string name)
        {
            var reg = Get(name);
            if (reg == null)
                return new RevitActionResult(name, false, "not registered");
            if (!reg.IsLoaded)
                return new RevitActionResult(name, true, "already unloaded");

            try
            {
                var runner = RevitContext.Runner
                    ?? throw new InvalidOperationException("API runner not attached");
                runner.Run(_ => UnloadInApiContext(reg));

                string note = _loader.SupportsTrueUnload
                    ? "unloaded"
                    : "unloaded (assembly stays resident until Revit restarts — .NET Framework)";
                DevReloadLogBuffer.Add($"{name} {note}");
                PluginsChanged?.Invoke();
                return new RevitActionResult(name, true, note);
            }
            catch (Exception ex)
            {
                DevReloadLogBuffer.Add($"{name} unload error: {ex.Message}");
                return new RevitActionResult(name, false,
                    $"unload error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static RevitActionResult RunCommand(string pluginName, string fullClassName)
        {
            var reg = Get(pluginName);
            if (reg == null)
                return new RevitActionResult(pluginName, false, "not registered");
            if (!reg.IsLoaded || reg.Handle == null)
                return new RevitActionResult(pluginName, false, "not loaded");

            var commandData = RevitContext.CapturedCommandData;
            if (commandData == null)
                return new RevitActionResult(pluginName, false,
                    "no ExternalCommandData captured yet — open the DevReload " +
                    "window (ribbon button) once, then retry");

            try
            {
                var runner = RevitContext.Runner
                    ?? throw new InvalidOperationException("API runner not attached");

                string resultText = runner.Run(app =>
                {
                    Type type = reg.Handle!.Assembly.GetType(fullClassName)
                        ?? throw new InvalidOperationException(
                            $"type {fullClassName} not found in {pluginName}");
                    object instance = CreateInstanceUnwrapped(type);
                    if (instance is not IExternalCommand command)
                        throw new InvalidOperationException(
                            $"{fullClassName} does not implement IExternalCommand");

                    string message = "";
                    var elements = new ElementSet();
                    Result result = command.Execute(commandData, ref message, elements);
                    return string.IsNullOrEmpty(message)
                        ? result.ToString()
                        : $"{result}: {message}";
                });

                DevReloadLogBuffer.Add($"{pluginName}.{fullClassName} → {resultText}");
                return new RevitActionResult(pluginName, true, resultText,
                    reg.Commands.Count, loaded: true);
            }
            catch (Exception ex)
            {
                DevReloadLogBuffer.Add(
                    $"{pluginName}.{fullClassName} error: {ex.Message}");
                return new RevitActionResult(pluginName, false,
                    $"command error: {ex.Message}");
            }
        }

        // ── internals ────────────────────────────────────────────────

        // Activator.CreateInstance and MethodInfo.Invoke wrap any exception the
        // plugin throws in a TargetInvocationException whose Message is the
        // useless "Exception has been thrown by the target of an invocation."
        // Rethrow the inner exception with its original stack preserved so the
        // log shows the plugin's actual failure, not the reflection wrapper.
        private static object CreateInstanceUnwrapped(Type type)
        {
            try { return Activator.CreateInstance(type)!; }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable; satisfies the compiler
            }
        }

        private static void InvokeUnwrapped(MethodInfo method, object instance, params object[] args)
        {
            try { method.Invoke(instance, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            }
        }

        private static void UnloadInApiContext(RevitPluginRegistration reg)
        {
            ShutdownPluginApp(reg);
            reg.Commands = Array.Empty<DiscoveredCommand>();
            var handle = reg.Handle;
            reg.Handle = null;
            if (handle != null) _loader.Unload(handle);
        }

        // Best-effort lifecycle hooks: a plugin exposing IExternalApplication
        // gets OnStartup/OnShutdown with the host's UIControlledApplication.
        // Revit's startup-only registrations (ribbon, dockable panes) will
        // throw inside the plugin when loaded mid-session — that error is the
        // plugin author's signal, not something to swallow here.
        private static void StartPluginApp(RevitPluginRegistration reg, UIApplication app)
        {
            var appTypes = CommandScanner.FindExternalApplications(reg.Handle!.Assembly);
            if (appTypes.Count == 0) return;

            Type appType = appTypes[0];
            object instance = CreateInstanceUnwrapped(appType);
            reg.PluginApp = instance;

            var uiCtrlApp = RevitContext.UiCtrlApp;
            if (uiCtrlApp == null) return;

            MethodInfo? onStartup = appType.GetMethod("OnStartup");
            if (onStartup != null)
                InvokeUnwrapped(onStartup, instance, uiCtrlApp);
            DevReloadLogBuffer.Add(
                $"{reg.Entry.Name}: ran {appType.Name}.OnStartup");
        }

        private static void ShutdownPluginApp(RevitPluginRegistration reg)
        {
            if (reg.PluginApp == null) return;
            try
            {
                MethodInfo? onShutdown = reg.PluginApp.GetType().GetMethod("OnShutdown");
                var uiCtrlApp = RevitContext.UiCtrlApp;
                if (onShutdown != null && uiCtrlApp != null)
                {
                    InvokeUnwrapped(onShutdown, reg.PluginApp, uiCtrlApp);
                    DevReloadLogBuffer.Add(
                        $"{reg.Entry.Name}: ran {reg.PluginApp.GetType().Name}.OnShutdown");
                }
            }
            finally
            {
                reg.PluginApp = null;
            }
        }

        public static void PersistConfig()
        {
            var config = new RevitPluginConfig();
            lock (_lock)
            {
                config.Plugins.AddRange(_plugins.Values.Select(r => r.Entry));
            }
            RevitPluginConfigLoader.Save(RevitContext.RevitVersionYear, config);
        }

        public static void LoadFromConfig()
        {
            var config = RevitPluginConfigLoader.Load(RevitContext.RevitVersionYear);
            if (config == null) return;

            foreach (var entry in config.Plugins)
                Register(entry, persist: false);
        }
    }
}
