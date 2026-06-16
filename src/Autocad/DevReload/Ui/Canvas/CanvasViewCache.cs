using UiMcp.Core.Geometry;

namespace UiMcp.Canvas;

/// <summary>JSON-friendly projection of a captured canvas view.</summary>
public sealed record CanvasViewDto(
    double ViewCenterX, double ViewCenterY, double ViewHeight,
    int DeviceX, int DeviceY, int DeviceWidth, int DeviceHeight,
    double PxPerUnit, string Note);

/// <summary>
/// Holds the most recently captured canvas <see cref="ViewDescriptor"/>. The
/// agent captures it (ui_canvas_capture_view) while AutoCAD is quiescent, then
/// the WCS gesture tools reuse it off-thread — which is what lets a drag drive a
/// jig whose pick loop has stalled the main thread (so a fresh capture would
/// hang). Cleared implicitly by capturing again.
/// </summary>
public static class CanvasViewCache
{
    private static ViewDescriptor? _last;

    public static void Set(ViewDescriptor v) => _last = v;

    public static ViewDescriptor Require() =>
        _last ?? throw new System.InvalidOperationException(
            "no captured view; call ui_canvas_capture_view first (while AutoCAD is quiescent).");

    public static CanvasViewDto ToDto(ViewDescriptor v, string note)
    {
        var t = new ViewTransform(v);
        return new CanvasViewDto(
            v.ViewCenterDcs.X, v.ViewCenterDcs.Y, v.ViewHeight,
            v.Device.X, v.Device.Y, v.Device.Width, v.Device.Height,
            t.Scale, note);
    }
}
