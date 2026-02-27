using System;
using System.IO;
using System.Reflection;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

using DevReload;

[assembly: CommandClass(typeof(Example.Loader.ExampleLoaderCommands))]
[assembly: ExtensionApplication(typeof(Example.Loader.ExampleLoaderCommands))]

namespace Example.Loader
{
    /// <summary>
    /// Permanent Loader assembly that lives in AutoCAD's default ALC.
    /// Provides three commands for managing the hot-reloadable plugin:
    /// <list type="bullet">
    ///   <item><c>EXLOAD</c> — Load the pre-built plugin from the Isolated/ subfolder</item>
    ///   <item><c>EXDEV</c>  — Build from VS and hot-reload (the main dev-cycle command)</item>
    ///   <item><c>EXUNLOAD</c> — Unload the plugin and clean up</item>
    /// </list>
    /// </summary>
    public class ExampleLoaderCommands : IExtensionApplication
    {
        private static PluginHost<IPlugin> _host = new();
        private static CommandRegistrar _registrar = new();
        private static PaletteSet? _paletteSet;

        /// <summary>
        /// Called by AutoCAD when this assembly is first loaded via NETLOAD.
        /// </summary>
        public void Initialize()
        {
            Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nDevReload Example Loader initialized. Commands: EXLOAD, EXDEV, EXUNLOAD");
        }

        /// <summary>
        /// Called by AutoCAD on shutdown. Cleans up commands and unloads the plugin.
        /// </summary>
        public void Terminate()
        {
            ClosePaletteSet();
            _registrar.UnregisterAll();
            if (_host.IsLoaded) _host.Unload();
        }

        /// <summary>
        /// Loads the pre-built plugin from the <c>Isolated/</c> subfolder
        /// next to this Loader DLL. Use this for the initial load after
        /// NETLOAD, or to show the palette if it was closed.
        /// </summary>
        [CommandMethod("EXLOAD")]
        public static void LoadPlugin()
        {
            Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            try
            {
                if (_paletteSet != null)
                {
                    _paletteSet.Visible = true;
                    return;
                }

                string loaderDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location)!;
                string pluginPath = Path.Combine(
                    loaderDir, "Isolated", "Example.Plugin.dll");

                Load(pluginPath);
                ed?.WriteMessage(
                    $"\nEXLOAD complete. {_registrar.CommandCount} commands registered.");
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\nError: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// The main dev-cycle command: unloads the current plugin, builds the
        /// project from the running Visual Studio instance, and loads the fresh build.
        /// <para>
        /// The project name passed to <see cref="DevReloadService.FindAndBuild"/>
        /// must match the project name in VS Solution Explorer.
        /// </para>
        /// </summary>
        [CommandMethod("EXDEV")]
        public static void DevReloadPlugin()
        {
            Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            try
            {
                // 1. Clean up old plugin (order matters!)
                ClosePaletteSet();
                _registrar.UnregisterAll();  // Remove commands BEFORE unloading ALC
                if (_host.IsLoaded) _host.Unload();

                // 2. Build from VS
                // "Example.Plugin" must match the project name in VS Solution Explorer
                string? dllPath = DevReloadService.FindAndBuild("Example.Plugin", ed);
                if (dllPath == null) return;

                // 3. Load fresh build
                Load(dllPath);
                ed?.WriteMessage(
                    $"\nEXDEV complete. {_registrar.CommandCount} commands registered.");
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\nError: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Unloads the plugin: closes the palette, unregisters commands,
        /// and releases the isolated ALC for garbage collection.
        /// </summary>
        [CommandMethod("EXUNLOAD")]
        public static void UnloadPlugin()
        {
            Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ClosePaletteSet();
            _registrar.UnregisterAll();
            if (_host.IsLoaded) _host.Unload();
            ed?.WriteMessage("\nExample plugin unloaded.");
        }

        /// <summary>
        /// Shared loading logic. Order of operations is critical:
        /// <list type="number">
        ///   <item>Close old PaletteSet (releases WPF cross-ALC references)</item>
        ///   <item>UnregisterAll (releases AutoCAD's command delegate references)</item>
        ///   <item>Unload old ALC (now collectible — no pinning references)</item>
        ///   <item>Load new assembly from stream (no file lock)</item>
        ///   <item>RegisterFromAssembly (registers commands via Utils.AddCommand)</item>
        ///   <item>Initialize plugin (lifecycle hook)</item>
        ///   <item>Create and show PaletteSet</item>
        /// </list>
        /// </summary>
        private static void Load(string dllPath)
        {
            ClosePaletteSet();
            _registrar.UnregisterAll();
            if (_host.IsLoaded) _host.Unload();

            var plugin = _host.Load(dllPath, "DevReload.Interface");

            // Register [CommandMethod]s via Utils.AddCommand.
            // The Plugin assembly has [assembly: CommandClass(typeof(NoAutoCommands))]
            // to suppress AutoCAD's own ExtensionLoader, so we are the sole owner
            // of these command registrations.
            _registrar.RegisterFromAssembly(_host.LoadedAssembly!);

            plugin.Initialize();

            _paletteSet = (PaletteSet)plugin.CreatePaletteSet();
            _paletteSet.Visible = true;
            _paletteSet.Size = new System.Drawing.Size(400, 300);
            _paletteSet.Dock = DockSides.Right;
        }

        private static void ClosePaletteSet()
        {
            if (_paletteSet != null)
            {
                _paletteSet.Close();
                _paletteSet = null;
            }
        }
    }
}
