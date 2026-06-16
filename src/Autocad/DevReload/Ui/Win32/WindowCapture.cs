using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using UiMcp.Core.Geometry;

namespace UiMcp.Win32;

/// <summary>
/// Per-window and per-region screen capture. <see cref="CaptureWindow"/> uses
/// PrintWindow(PW_RENDERFULLCONTENT) which renders DWM-composited content even
/// when the window is occluded — the right tool for a docked palette or a modal
/// dialog. <see cref="CaptureRegion"/> grabs an arbitrary physical-pixel rect
/// off the live screen (used for canvas / entity / element captures).
///
/// All methods return the PNG bytes in memory; nothing is written to disk. The
/// tool layer base64-encodes them into an MCP image content block so the agent
/// sees the screenshot inline (no temp-file round-trip).
/// </summary>
public static class WindowCapture
{
    public sealed record Shot(byte[] Png, int Width, int Height);

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static Shot CaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            throw new InvalidOperationException("GetWindowRect failed.");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"window has empty rect ({w}x{h}).");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                bool ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                if (!ok) throw new InvalidOperationException("PrintWindow returned false.");
            }
            finally { g.ReleaseHdc(hdc); }
        }
        return new Shot(EncodePng(bmp), w, h);
    }

    /// <summary>
    /// Occlusion-proof region capture: PrintWindow the whole window, then crop to
    /// the requested screen rect (translated to window-relative). Use this for the
    /// AutoCAD canvas — CopyFromScreen would grab whatever window is on top, and
    /// PrintWindow(PW_RENDERFULLCONTENT) renders AutoCAD's GPU canvas correctly
    /// (verified live: the model viewport, ViewCube and WCS icon all come through).
    /// </summary>
    public static Shot CaptureWindowRegion(IntPtr hwnd, PixelRect screenRegion)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            throw new InvalidOperationException("GetWindowRect failed.");
        int ww = r.Right - r.Left, wh = r.Bottom - r.Top;
        if (ww <= 0 || wh <= 0) throw new InvalidOperationException($"window has empty rect ({ww}x{wh}).");

        using var full = new Bitmap(ww, wh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT))
                    throw new InvalidOperationException("PrintWindow returned false.");
            }
            finally { g.ReleaseHdc(hdc); }
        }

        var want = new Rectangle(screenRegion.X - r.Left, screenRegion.Y - r.Top, screenRegion.Width, screenRegion.Height);
        var crop = Rectangle.Intersect(want, new Rectangle(0, 0, ww, wh));
        if (crop.Width <= 0 || crop.Height <= 0)
            throw new InvalidOperationException("requested region does not intersect the window.");
        using var sub = full.Clone(crop, full.PixelFormat);
        return new Shot(EncodePng(sub), crop.Width, crop.Height);
    }

    public static Shot CaptureRegion(PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException($"region is empty ({region.Width}x{region.Height}).");
        using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        return new Shot(EncodePng(bmp), region.Width, region.Height);
    }
}
