using System;
using UiMcp.Core.Geometry;

namespace UiMcp.Core.Vision;

/// <summary>
/// Resolves the physical-pixel rectangle a screenshot should capture, from
/// either a UI element's bounds or a WCS bounding box projected through the
/// live view transform. Always clamped to the supplied screen bounds.
/// </summary>
public static class ScreenRegion
{
    public static PixelRect FromElement(PixelRect bounds, int padding, PixelRect screen)
    {
        int x = bounds.X - padding;
        int y = bounds.Y - padding;
        int right = bounds.Right + padding;
        int bottom = bounds.Bottom + padding;
        return Clamp(x, y, right, bottom, screen);
    }

    public static PixelRect FromWcsBox(Pt3 cornerA, Pt3 cornerB, ViewTransform t, int padding, PixelRect screen)
    {
        Pt2 a = t.WcsToDevice(cornerA);
        Pt2 b = t.WcsToDevice(cornerB);
        // The Y-flip means the WCS corner ordering does not survive to screen;
        // take min/max on each axis after projection.
        int x = (int)Math.Floor(Math.Min(a.X, b.X)) - padding;
        int y = (int)Math.Floor(Math.Min(a.Y, b.Y)) - padding;
        int right = (int)Math.Ceiling(Math.Max(a.X, b.X)) + padding;
        int bottom = (int)Math.Ceiling(Math.Max(a.Y, b.Y)) + padding;
        return Clamp(x, y, right, bottom, screen);
    }

    private static PixelRect Clamp(int x, int y, int right, int bottom, PixelRect screen)
    {
        x = Math.Max(x, screen.X);
        y = Math.Max(y, screen.Y);
        right = Math.Min(right, screen.Right);
        bottom = Math.Min(bottom, screen.Bottom);
        int w = Math.Max(0, right - x);
        int h = Math.Max(0, bottom - y);
        return new PixelRect(x, y, w, h);
    }
}
