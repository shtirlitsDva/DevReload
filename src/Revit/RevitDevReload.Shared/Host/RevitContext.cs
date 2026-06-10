using System;
using System.Threading;

using Autodesk.Revit.UI;

namespace RevitDevReload
{
    // Process-wide Revit handles captured at the only moments Revit hands
    // them out. Static on purpose: the add-in instance is owned by Revit and
    // there is exactly one per process.
    public static class RevitContext
    {
        // Captured in IExternalApplication.OnStartup. Needed to (best-effort)
        // run plugin IExternalApplication hooks.
        public static UIControlledApplication? UiCtrlApp { get; internal set; }

        // Captured each time the DevReload ribbon command runs. Plugin
        // IExternalCommand.Execute is invoked with this (the proven
        // RevitAddInManager pattern). Null until the ribbon button has been
        // clicked once — run_command reports that clearly instead of
        // inventing a fake ExternalCommandData (its ctor is internal).
        public static ExternalCommandData? CapturedCommandData { get; internal set; }

        // Revit UI thread sync context (captured in OnStartup) — used to open
        // and drive the WPF window from the pipe thread.
        public static SynchronizationContext? UiSync { get; internal set; }

        // Marshals work into a valid Revit API context via ExternalEvent.
        public static ApiContextRunner? Runner { get; internal set; }

        public static int RevitVersionYear { get; internal set; }

        public static IntPtr MainWindowHandle { get; internal set; }
    }
}
