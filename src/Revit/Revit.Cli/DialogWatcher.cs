using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Revit.Cli
{
    // Auto-confirms Revit's add-in security prompts during automated runs:
    // discovers dialog buttons through UI Automation (element tree + names —
    // the AutoHotkey approach), never via pixels. Clicks are logged so a test
    // transcript shows exactly which dialogs were dismissed.
    public sealed class DialogWatcher : IDisposable
    {
        // Button labels that mean "allow the add-in", in preference order.
        // EN labels; extend here if Revit runs localized.
        private static readonly string[] _allowButtons =
        {
            "Always Load", "Load Once", "Load",
        };

        // Only dialogs whose title matches one of these get auto-confirmed —
        // a generic "click anything" watcher would happily dismiss error
        // dialogs and hide real failures.
        private static readonly string[] _dialogTitleParts =
        {
            "Security", "Unsigned", "Add-In", "Addin",
        };

        private readonly int _rootPid;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new();

        public event Action<string>? Clicked;

        public DialogWatcher(int rootPid)
        {
            _rootPid = rootPid;
            _thread = new Thread(WatchLoop) { IsBackground = true, Name = "DialogWatcher" };
        }

        public void Start() => _thread.Start();

        private void WatchLoop()
        {
            using var automation = new UIA3Automation();
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    foreach (int pid in PidFamily(_rootPid))
                        ScanProcessWindows(automation, pid);
                }
                catch
                {
                    // UIA hiccups while windows appear/vanish are routine.
                }

                if (_cts.Token.WaitHandle.WaitOne(500)) return;
            }
        }

        private void ScanProcessWindows(UIA3Automation automation, int pid)
        {
            var desktop = automation.GetDesktop();
            var windows = desktop.FindAllChildren(cf => cf.ByProcessId(pid));
            foreach (var window in windows)
            {
                string title = SafeName(window);
                if (!_dialogTitleParts.Any(part =>
                        title.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                var buttons = window.FindAllDescendants(
                    cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));

                foreach (string allow in _allowButtons)
                {
                    var button = buttons.FirstOrDefault(b =>
                        SafeName(b).IndexOf(allow, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (button == null) continue;

                    try
                    {
                        button.AsButton().Invoke();
                        Clicked?.Invoke($"dialog '{title}' → clicked '{SafeName(button)}'");
                    }
                    catch (Exception ex)
                    {
                        Clicked?.Invoke($"dialog '{title}' → click failed: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private static string SafeName(AutomationElement element)
        {
            try { return element.Name ?? ""; }
            catch { return ""; }
        }

        // Revit may show the security dialog from a child process; include
        // direct children of the root pid.
        private static IEnumerable<int> PidFamily(int rootPid)
        {
            yield return rootPid;
            foreach (var proc in Process.GetProcessesByName("Revit"))
                if (proc.Id != rootPid)
                    yield return proc.Id;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join(2000);
            _cts.Dispose();
        }
    }
}
