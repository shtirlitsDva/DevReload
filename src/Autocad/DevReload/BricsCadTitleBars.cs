#if BRICSCAD
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

using DevReload.Diagnostics;

namespace DevReload
{
    /// <summary>
    /// Dark title bars on every window BricsCAD opens: the main frame, dialogs
    /// (Options, …), floating panels, message boxes. BricsCAD is dark inside,
    /// but Windows draws their title bars white.
    /// </summary>
    /// <remarks>
    /// A WinEvent hook scoped to this process reports each window as it is
    /// created and shown; every top-level window with a caption gets
    /// <see cref="WpfSHARED.DarkTitleBar.ApplyToHostWindow"/>. Out-of-context
    /// events arrive through the installing (main) thread's message loop, so
    /// the callback runs on the main thread. They arrive after a window's own
    /// setup, so this colour also wins over DevReload's own themed windows:
    /// in BricsCAD every title bar looks the same.
    /// </remarks>
    internal static class BricsCadTitleBars
    {
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;
        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_CAPTION = 0x00C00000L; // WS_BORDER | WS_DLGFRAME

        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
            WinEventProc proc, uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        // Held in a field: native code calls it, and a collected delegate
        // would crash the process on the next event.
        private static WinEventProc? _proc;
        private static IntPtr _hook;

        /// <summary>Themes the windows already open and every one opened
        /// later. Main thread only (its message loop delivers the events).</summary>
        public static void Install()
        {
            if (_hook != IntPtr.Zero) return;
            uint pid = (uint)Environment.ProcessId;

            _proc = OnWinEvent;
            // CREATE..SHOW also spans DESTROY (0x8001), which OnWinEvent ignores.
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero,
                _proc, pid, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero)
            {
                _proc = null;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed");
            }

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == pid) TryTheme(hwnd);
                return true;
            }, IntPtr.Zero);
        }

        public static void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _proc = null;
        }

        private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (evt == EVENT_OBJECT_CREATE || evt == EVENT_OBJECT_SHOW)
                if (idObject == OBJID_WINDOW && idChild == 0) TryTheme(hwnd);
        }

        private static void TryTheme(IntPtr hwnd)
        {
            try
            {
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                if ((style & WS_CHILD) != 0 || (style & WS_CAPTION) != WS_CAPTION) return;
                WpfSHARED.DarkTitleBar.ApplyToHostWindow(hwnd);
            }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. This runs from a native
                // callback; an escaping exception would take BricsCAD down, and
                // a window left with a white title bar harms nothing.
                DevReloadDiagnostics.Report("BricsCadTitleBars.TryTheme", ex);
            }
        }
    }
}
#endif
