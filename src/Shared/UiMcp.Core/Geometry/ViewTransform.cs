namespace UiMcp.Core.Geometry;

/// <summary>
/// A snapshot of the AutoCAD canvas view, sufficient to map between WCS and
/// device pixels. Captured on the AutoCAD main thread (from the live view +
/// the drawing window's physical-pixel client rect), then used off-thread to
/// place synthetic input precisely while a jig / grip edit is in progress.
/// </summary>
/// <param name="ViewCenterDcs">View center in DCS (drawing) units.</param>
/// <param name="ViewHeight">Visible view height in drawing units. Scale is
/// uniform and derived from this against the device height.</param>
/// <param name="WcsToDcs">Maps a WCS point into the view's DCS plane.</param>
/// <param name="Device">Canvas client area in physical screen pixels.</param>
public readonly record struct ViewDescriptor(
    Pt2 ViewCenterDcs,
    double ViewHeight,
    Matrix2d WcsToDcs,
    PixelRect Device);

/// <summary>Bidirectional WCS ↔ device-pixel mapping for one view snapshot.</summary>
public sealed class ViewTransform
{
    private readonly ViewDescriptor _v;
    private readonly double _scale; // physical px per drawing unit (uniform)

    public ViewTransform(ViewDescriptor v)
    {
        _v = v;
        _scale = v.Device.Height / v.ViewHeight;
    }

    public double Scale => _scale;

    /// <summary>WCS point → device pixel (sub-pixel precision; round at the
    /// SendInput boundary, not here).</summary>
    public Pt2 WcsToDevice(Pt3 wcs)
    {
        Pt2 dcs = _v.WcsToDcs.Apply(wcs.X, wcs.Y);
        double dx = dcs.X - _v.ViewCenterDcs.X;
        double dy = dcs.Y - _v.ViewCenterDcs.Y;
        double sx = _v.Device.CenterX + dx * _scale;
        double sy = _v.Device.CenterY - dy * _scale; // DCS up == screen up; screen Y grows down
        return new Pt2(sx, sy);
    }

    /// <summary>Device pixel → WCS (inverse of <see cref="WcsToDevice"/>).</summary>
    public Pt2 DeviceToWcs(double screenX, double screenY)
    {
        double dx = (screenX - _v.Device.CenterX) / _scale;
        double dy = -(screenY - _v.Device.CenterY) / _scale;
        double qx = _v.ViewCenterDcs.X + dx;
        double qy = _v.ViewCenterDcs.Y + dy;
        return _v.WcsToDcs.Invert().Apply(qx, qy);
    }
}
