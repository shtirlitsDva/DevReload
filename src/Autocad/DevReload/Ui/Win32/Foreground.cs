using System;
using System.Threading;

namespace UiMcp.Win32;

/// <summary>
/// Brings a window reliably to the foreground despite Windows' foreground-lock
/// (which silently refuses SetForegroundWindow from a process that doesn't
/// already own the foreground). Required before SendInput canvas gestures —
/// without it the synthetic input lands on whatever window is on top (observed
/// live: it hit the agent's own terminal). Synthetic input is session-global,
/// so this necessarily takes the shared cursor; only call it for a deliberate
/// canvas gesture.
/// </summary>
public static class Foreground
{
    public static bool Ensure(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        uint thisThread = NativeMethods.GetCurrentThreadId();

        bool a1 = fgThread != 0 && fgThread != thisThread && NativeMethods.AttachThreadInput(thisThread, fgThread, true);
        bool a2 = targetThread != 0 && targetThread != thisThread && targetThread != fgThread
                  && NativeMethods.AttachThreadInput(thisThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (a1) NativeMethods.AttachThreadInput(thisThread, fgThread, false);
            if (a2) NativeMethods.AttachThreadInput(thisThread, targetThread, false);
        }

        // Give the window manager a beat to settle before input is synthesized.
        for (int i = 0; i < 20 && NativeMethods.GetForegroundWindow() != hwnd; i++)
            Thread.Sleep(15);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
