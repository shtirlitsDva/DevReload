using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UiMcp.Core.Geometry;

namespace UiMcp.Win32;

public sealed record WindowInfo(
    long Hwnd, string Title, string ClassName,
    PixelRect Bounds, bool Visible, bool Enabled);

/// <summary>
/// Enumerates this process's top-level windows (AutoCAD's main frame plus any
/// modal dialog it raises — e.g. the COGO-point projection dialog that has no
/// .NET API). In-process enumeration walks every thread's windows, so it finds
/// a modal dialog whose own message loop is currently pumping.
/// </summary>
public static class WindowEnum
{
    public static List<WindowInfo> TopLevelWindows()
    {
        var result = new List<WindowInfo>();
        var proc = Process.GetCurrentProcess();
        foreach (ProcessThread t in proc.Threads)
        {
            uint tid = (uint)t.Id;
            NativeMethods.EnumThreadWindows(tid, (hwnd, _) =>
            {
                var info = Describe(hwnd);
                if (info != null) result.Add(info);
                return true;
            }, IntPtr.Zero);
        }
        return result;
    }

    public static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var title = Text(sb => NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity));
        var cls = Text(sb => NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity));
        NativeMethods.GetWindowRect(hwnd, out var r);
        return new WindowInfo(
            Hwnd: hwnd.ToInt64(),
            Title: title,
            ClassName: cls,
            Bounds: new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            Visible: NativeMethods.IsWindowVisible(hwnd),
            Enabled: NativeMethods.IsWindowEnabled(hwnd));
    }

    private static string Text(Func<StringBuilder, int> fill)
    {
        var sb = new StringBuilder(512);
        int n = fill(sb);
        return n > 0 ? sb.ToString() : sb.ToString();
    }
}
