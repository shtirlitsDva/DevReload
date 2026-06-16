using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using UiMcp.Core.Geometry;
using UiMcp.Win32;

namespace UiMcp.Canvas;

/// <summary>
/// Captures the live AutoCAD canvas view into a pure <see cref="ViewDescriptor"/>
/// (consumed by the tested <see cref="ViewTransform"/>) plus the drawing area's
/// physical-pixel screen rect. Read on the main thread while quiescent, then the
/// resulting transform is used off-thread to place synthetic input during a jig
/// — the main thread being busy in the jig loop is exactly why we snapshot first.
///
/// Covers plan / top-down views (plan drawings, Civil 3D profile views) with
/// optional view-twist. Full 3D oblique views are out of scope for v1.
/// </summary>
public static class ViewCapture
{
    /// <summary>The device-rect source is the brittle part (per the research:
    /// sniffing AutoCAD's window tree by class is version-fragile). The note
    /// records how the rect was resolved so live calibration is explicit.</summary>
    public static (ViewDescriptor View, string Note) Capture(Editor ed)
    {
        using var view = ed.GetCurrentView();
        double twist = view.ViewTwist; // radians, CCW
        // Plan/profile: DCS axes align with WCS XY apart from view twist about
        // the view center. Build WcsToDcs as a rotation by -twist (identity at 0).
        double c = Math.Cos(-twist), s = Math.Sin(-twist);
        var wcsToDcs = new Matrix2d(c, -s, s, c, 0, 0);

        var (rect, note) = ResolveDeviceRect();

        var vd = new ViewDescriptor(
            ViewCenterDcs: new Pt2(view.CenterPoint.X, view.CenterPoint.Y),
            ViewHeight: view.Height,
            WcsToDcs: wcsToDcs,
            Device: rect);
        return (vd, note);
    }

    /// <summary>
    /// Physical-pixel screen rect of the drawing canvas. Calibrated against live
    /// ground truth (AC2025): the model graphics surface is the descendant window
    /// of class <c>ACADDM_CHILD_DXGI_FLIP_MODE_VIEW_CLASS</c> (the DirectX flip
    /// view); its rect aspect matches GetCurrentView's Width/Height. We must NOT
    /// pick "largest child" — that grabs AutoCAD's Chromium overlay (Start tab /
    /// web content, a wider window) and yields a wrong, too-tall rect.
    /// Resolution order: DXGI flip view → MDIClient → largest visible child.
    /// </summary>
    public static (PixelRect Rect, string Note) ResolveDeviceRect()
    {
        IntPtr main = Application.MainWindow?.Handle ?? IntPtr.Zero;
        if (main == IntPtr.Zero)
            return (new PixelRect(0, 0, 0, 0), "no main window handle");

        IntPtr dxgi = IntPtr.Zero, mdi = IntPtr.Zero, largest = IntPtr.Zero;
        long largestArea = 0;
        NativeMethods.EnumChildWindows(main, (h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (!NativeMethods.GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area <= 0) return true;
            string cls = ClassOf(h);
            if (dxgi == IntPtr.Zero && cls.IndexOf("DXGI_FLIP_MODE_VIEW", StringComparison.OrdinalIgnoreCase) >= 0)
                dxgi = h;
            else if (mdi == IntPtr.Zero && cls.Equals("MDIClient", StringComparison.OrdinalIgnoreCase))
                mdi = h;
            if (area > largestArea) { largestArea = area; largest = h; }
            return true;
        }, IntPtr.Zero);

        IntPtr target; string how;
        if (dxgi != IntPtr.Zero) { target = dxgi; how = "DXGI flip-mode view"; }
        else if (mdi != IntPtr.Zero) { target = mdi; how = "MDIClient"; }
        else if (largest != IntPtr.Zero) { target = largest; how = "largest visible child (fallback)"; }
        else { target = main; how = "main frame (fallback)"; }

        NativeMethods.GetWindowRect(target, out var wr);
        var rect = new PixelRect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
        return (rect, $"device rect from {how} hwnd 0x{target.ToInt64():X}");
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(h, sb, 256);
        return sb.ToString();
    }
}
