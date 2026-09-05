using System;
using System.Drawing;
using System.Linq;
using System.Threading;

using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

using DevReload.Diagnostics;
using DevReload.Hud;
using DevReload.Oarx;
using DevReload.Rpc;
using DevReload.Views;

[assembly: CommandClass(typeof(DevReload.DevReloaderCommands))]
[assembly: ExtensionApplication(typeof(DevReload.DevReloaderCommands))]

namespace DevReload
{
    /// <summary>
    /// Config-driven loader — reads plugins.json at startup, migrates
    /// old config entries, registers dynamic commands per plugin, and
    /// provides the DEVRELOAD management palette for visual plugin management.
    /// <para>
    /// AutoCAD loads this DLL once via autoload (acad2025.lsp).
    /// If no plugins.json exists, initialization is silent.
    /// Plugins are registered + commands created for all entries,
    /// but only those with <c>loadOnStartup = true</c> are auto-loaded.
    /// Builds run via <c>dotnet build</c> using stored .csproj paths —
    /// no running VS instance required after initial registration.
    /// </para>
    /// </summary>
    public class DevReloaderCommands : IExtensionApplication
    {
        private static PaletteSet? _mgmtPalette;
        private static readonly Guid MgmtPaletteGuid =
            new("fb1be221-4d6f-48ff-a0d3-39dc935bf749");

        private static AcadIdlePumpDispatcher? _dispatcher;

        public void Initialize()
        {
            // Give the diagnostics sink a way onto the command line. Every
            // reported failure in this assembly — the whole plugin lifecycle
            // path — surfaces where the user is already looking, instead of
            // only in %LOCALAPPDATA%\DevReload\devreload.log. Resolved per
            // call because MdiActiveDocument is null this early in startup.
            DevReloadDiagnostics.HostWriter = msg =>
                Application.DocumentManager.MdiActiveDocument?.Editor?.WriteMessage(msg);

            // First-line file log so we can verify autoload at all,
            // independent of whether an editor is attached at Initialize.
            DevReloadDiagnostics.Info("DevReloaderCommands.Initialize entered");

            // Bridge AutoCAD's .NET 8 host runtime to our bundled
            // dependency graph (MCP SDK + Microsoft.Extensions.* 10.x
            // need probing help from a directory the default ALC
            // doesn't know about).
            AssemblyResolver.Install();

            Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;

            // Take AutoCAD's assembly scan off DevReload-loaded plugins before
            // anything can load one. Without this the host registers their
            // commands permanently and builds its own plugin instance, which is
            // what the NoCommands marker used to work around.
            try
            {
                AutoCadScanSuppressor.Install();
                DevReloadDiagnostics.Info("AutoCAD assembly scan suppressed for DevReload ALCs");
            }
            catch (System.Exception ex)
            {
                // Loud, not silent: without suppression every plugin needs the
                // marker back, and PluginManager must not call Initialize.
                DevReloadDiagnostics.Report("AutoCadScanSuppressor.Install", ex);
                ed?.WriteMessage(
                    "\nDevReload: WARNING - could not suppress AutoCAD's assembly scan " +
                    $"({ex.Message}) Plugins on this AutoCAD version still need the " +
                    "NoCommands marker class.");
            }

            // Bring up the Acad.Rpc host before any plugin loads. The
            // host is alive for the whole AutoCAD session; plugins
            // register/unregister their tools into its single
            // ToolCollection on load/unload.
            try
            {
                _dispatcher = new AcadIdlePumpDispatcher();
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var host = AcadRpcHost.Initialize(new AcadRpcHostOptions(
                    PipeName: $"acad-rpc-{pid}",
                    MainThreadDispatcher: _dispatcher,
                    Log: DevReloadDiagnostics.Info));

                // Zero-glue plugin contribution: any assembly in any
                // non-collectible ALC with an [AcadRpcSurface] gets
                // auto-registered. Catches DevReload itself, NETLOAD'd
                // plugins (default ALC), NSLOAD'd plugins (isolated
                // non-collectible ALCs). Plugins loaded into a
                // collectible ALC via DevReload are registered
                // explicitly by PluginManager.LoadCore, because the
                // hot-reload lifecycle owns register/unregister.
                host.EnableAutoDiscovery();

                _ = host.StartAsync(CancellationToken.None);
                DevReloadDiagnostics.Info($"RPC pipe opening at \\\\.\\pipe\\acad-rpc-{pid}");
                ed?.WriteMessage(
                    $"\nDevReload: RPC pipe opened at \\\\.\\pipe\\acad-rpc-{pid}");
            }
            catch (System.Exception ex)
            {
                DevReloadDiagnostics.Report("AcadRpcHost.StartAsync", ex);
            }

            var config = PluginConfigLoader.Load();
            if (config == null || (config.Plugins.Count == 0 && config.OarxPlugins.Count == 0))
            {
                ed?.WriteMessage("\nDevReload initialized (no plugins configured).");
                return;
            }

            PluginConfigLoader.MigrateIfNeeded(config);

            // Register all plugins + their LOAD/DEV/UNLOAD commands
            foreach (var entry in config.Plugins)
                RegisterFromConfig(entry);

            // ObjectARX groups run their own lifecycle (Oarx/). A bad OARX entry
            // must not take the whole startup down with it — the .NET plugins
            // registered above are unrelated to it.
            foreach (var oarx in config.OarxPlugins)
            {
                try { OarxConfigLoader.RegisterFromConfig(oarx); }
                catch (System.Exception ex)
                {
                    ed?.WriteMessage(
                        $"\nDevReload: OARX '{oarx.Name}' failed to register: {ex.Message}");
                }
            }

            // Auto-load only plugins with loadOnStartup = true.
            //
            // Explicitly headless: the HUD holds a closing frame for three seconds,
            // so letting these use the default sink would put one HUD per
            // auto-loaded plugin between the user and their session. There is also
            // no document to hang a transient on this early.
            int loaded = 0;
            foreach (var entry in config.Plugins.Where(e => e.LoadOnStartup))
            {
                PluginManager.Load(entry.Name, NullReloadProgress.Instance);
                loaded++;
            }
            foreach (var oarx in config.OarxPlugins.Where(e => e.LoadOnStartup))
            {
                OarxManager.Load(oarx.Name, NullReloadProgress.Instance);
                loaded++;
            }

            var names = PluginManager.GetRegisteredPluginNames();
            int oarxCount = OarxManager.GetRegisteredNames().Count;
            ed?.WriteMessage(
                $"\nDevReload: {names.Count} .NET plugin(s), {oarxCount} OARX group(s), " +
                $"{loaded} auto-loaded.");
        }

        public void Terminate()
        {
            // Collect-then-aggregate: every step runs and every failure is
            // reported as it happens, then the lot is rethrown. Throwing on the
            // first failure would skip the steps below it and leak more than the
            // old silent version did.
            //
            // Consequence worth knowing: this rethrow lands in AutoCAD's own
            // shutdown, so a genuinely broken teardown now surfaces as a noisy
            // exit rather than a silent one. That is the intended trade — a
            // failed teardown is a real defect and should not be invisible.
            var failures = new System.Collections.Generic.List<System.Exception>();

            DevReloadDiagnostics.Step(failures, "AcadRpcHost.Shutdown",
                () => AcadRpcHost.Current.ShutdownAsync().GetAwaiter().GetResult());
            DevReloadDiagnostics.Step(failures, "AcadIdlePumpDispatcher.Dispose",
                () => _dispatcher?.Dispose());
            DevReloadDiagnostics.Step(failures, "PluginManager.UnloadAll",
                () => PluginManager.UnloadAll());
            // Native modules must leave with the session too; a mapped .arx would
            // otherwise keep its file locked for whatever runs next.
            DevReloadDiagnostics.Step(failures, "OarxManager.UnloadAll",
                () => OarxManager.UnloadAll());
            DevReloadDiagnostics.Step(failures, "AutoCadScanSuppressor.Restore",
                () => AutoCadScanSuppressor.Restore());

            DevReloadDiagnostics.ThrowIfAny("DevReloaderCommands.Terminate", failures);
        }

        // ── Management palette ────────────────────────────────────────

        [CommandMethod("DEVRELOAD")]
        public static void OpenManager()
        {
            if (_mgmtPalette == null)
            {
                _mgmtPalette = new PaletteSet(
                    "DevReload Manager", MgmtPaletteGuid)
                {
                    Size = new Size(400, 500),
                    MinimumSize = new Size(300, 200),
                    DockEnabled = DockSides.Left | DockSides.Right,
                };
                // Two AddVisuals = two AutoCAD-native palette tabs. The tab
                // chrome is the host's, not ours. Both visuals share ONE
                // view-model: it subscribes to both plugin registries, so a
                // second instance would double every registry event.
                var vm = new ViewModels.DevReloadViewModel();
                _mgmtPalette.AddVisual(".NET", new DevReloadPanel(vm));
                _mgmtPalette.AddVisual("OARX", new OarxPanel(vm));
            }
            _mgmtPalette.Visible = true;
        }

        // ── Config → PluginManager bridge ─────────────────────────────

        /// <summary>
        /// Register a single plugin from a <see cref="PluginEntry"/> config
        /// entry. Creates the PluginManager registration and the 3 loader
        /// commands ({prefix}LOAD/DEV/UNLOAD). Same call site for both
        /// the palette UI (ConfirmAddPlugin) and the RPC tool surface
        /// (via PluginConfigLoader.RegisterNewPlugin).
        /// </summary>
        internal static void RegisterFromConfig(PluginEntry entry)
        {
            var builder = PluginManager.Register(entry.Name);

            if (entry.DllPath != null) builder.WithDllPath(entry.DllPath);
            if (entry.ProjectFilePath != null) builder.WithProjectFilePath(entry.ProjectFilePath);
            builder.WithBuildConfiguration(entry.BuildConfiguration);
            builder.WithActiveWorktreePath(entry.ActiveWorktreePath);
            builder.WithCommands();
            // Shared / mixed-mode assembly choice is no longer held in
            // PluginEntry — it lives in <buildDir>/SharedAssemblies.Config.json
            // and is read fresh by PluginManager.LoadCore on every load.

            builder.Commit();

            string prefix = entry.CommandPrefix ?? entry.Name;
            PluginManager.RegisterLoaderCommands(entry.Name, prefix);
        }
    }
}
