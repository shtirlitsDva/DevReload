using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

using Autodesk.Revit.UI;

using RevitDevReload.Core;
using RevitDevReload.Ui;

namespace RevitDevReload
{
    // Host entry point — the only .addin-registered application. Owns the
    // ribbon button, the API-context runner, the pipe server and the manager
    // window. Plugins never get their own manifests; they live inside this
    // host's loaders.
    public sealed class RevitDevReloadApp : IExternalApplication
    {
        private static PipeServer? _pipeServer;
        private static ManagerWindow? _window;

        public Result OnStartup(UIControlledApplication application)
        {
            RevitContext.UiCtrlApp = application;
            RevitContext.UiSync = SynchronizationContext.Current;
            RevitContext.MainWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            RevitContext.RevitVersionYear = int.Parse(
                application.ControlledApplication.VersionNumber);

            var runner = new ApiContextRunner();
            runner.Attach();
            RevitContext.Runner = runner;

            CreateRibbonButton(application);

            RevitPluginManager.LoadFromConfig();

            _pipeServer = new PipeServer(
                $"RevitDevReload.{Process.GetCurrentProcess().Id}",
                PipeDispatcher.Dispatch);
            _pipeServer.Error += ex =>
                DevReloadLogBuffer.Add("pipe error: " + ex.Message);
            _pipeServer.Start();

            // Autoload marked plugins once the UI is idle (builds may take a
            // while; doing it inside OnStartup would stall Revit's launch).
            application.Idling += AutoloadOnFirstIdle;

            DevReloadLogBuffer.Add(
                $"RevitDevReload started (Revit {RevitContext.RevitVersionYear}, " +
                $"pipe RevitDevReload.{Process.GetCurrentProcess().Id})");
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            foreach (var reg in RevitPluginManager.All)
            {
                if (reg.IsLoaded)
                {
                    try { RevitPluginManager.Unload(reg.Entry.Name); } catch { }
                }
            }
            _pipeServer?.Dispose();
            _pipeServer = null;
            return Result.Succeeded;
        }

        private bool _autoloadDone;

        private void AutoloadOnFirstIdle(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_autoloadDone) return;
            _autoloadDone = true;
            if (sender is UIApplication app)
                app.Idling -= AutoloadOnFirstIdle;

            foreach (var reg in RevitPluginManager.All)
            {
                if (reg.Entry.LoadOnStartup)
                    RevitPluginManager.Load(reg.Entry.Name);
            }
        }

        private static void CreateRibbonButton(UIControlledApplication application)
        {
            try
            {
                RibbonPanel panel = application.CreateRibbonPanel("DevReload");
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                var buttonData = new PushButtonData(
                    "RevitDevReloadOpen",
                    "DevReload",
                    assemblyPath,
                    typeof(OpenManagerCommand).FullName);
                buttonData.ToolTip =
                    "Open the DevReload plugin manager (hot-reload plugins without restarting Revit)";
                panel.AddItem(buttonData);
            }
            catch (Exception ex)
            {
                DevReloadLogBuffer.Add("ribbon creation failed: " + ex.Message);
            }
        }

        // UI-thread-safe window opener shared by the ribbon command and the
        // pipe's open_window.
        public static void ShowManagerWindowFromAnyThread()
        {
            var sync = RevitContext.UiSync;
            if (sync == null)
                throw new InvalidOperationException("UI sync context not captured");
            sync.Post(_ => ShowManagerWindow(), null);
        }

        public static void ShowManagerWindow()
        {
            if (_window != null)
            {
                _window.Activate();
                return;
            }

            _window = new ManagerWindow();
            _window.Closed += (_, _) => _window = null;

            // Owned by Revit's main window so it stays on top of Revit and
            // minimises with it.
            var helper = new System.Windows.Interop.WindowInteropHelper(_window)
            {
                Owner = RevitContext.MainWindowHandle,
            };
            _window.Show();
        }
    }
}
