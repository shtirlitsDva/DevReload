using UiMcp.Core.Geometry;

namespace UiMcp.Core.Tests;

/// <summary>
/// WCS → device-pixel mapping for the AutoCAD canvas. The AutoCAD-specific
/// part (deriving WcsToDcs from the live view) lives in the plugin; this is
/// the pure, deterministic math: centering, uniform scale, and the DCS→screen
/// Y-flip. Tests use an identity WcsToDcs (the plan/top-down case that covers
/// plan drawings and Civil 3D profile views).
/// </summary>
public class ViewTransformTests
{
    // A 800x600 px canvas at screen origin (0,0), showing a view 60 drawing
    // units high centered on WCS (100,100). Uniform scale = 600/60 = 10 px/unit.
    private static ViewDescriptor Plan() => new(
        ViewCenterDcs: new Pt2(100, 100),
        ViewHeight: 60,
        WcsToDcs: Matrix2d.Identity,
        Device: new PixelRect(0, 0, 800, 600));

    [Fact]
    public void Center_MapsToDeviceCenter()
    {
        var t = new ViewTransform(Plan());
        var p = t.WcsToDevice(new Pt3(100, 100, 0));
        Assert.Equal(400.0, p.X, 6);   // 0 + 800/2
        Assert.Equal(300.0, p.Y, 6);   // 0 + 600/2
    }

    [Fact]
    public void PointAbove_MapsHigherOnScreen_YFlips()
    {
        var t = new ViewTransform(Plan());
        // +30 units north of center == top edge of the view (30*10 = 300 px up).
        var p = t.WcsToDevice(new Pt3(100, 130, 0));
        Assert.Equal(400.0, p.X, 6);
        Assert.Equal(0.0, p.Y, 6);     // top of canvas (screen Y decreases upward)
    }

    [Fact]
    public void PointEastAndSouth_ScalesUniformly()
    {
        var t = new ViewTransform(Plan());
        var p = t.WcsToDevice(new Pt3(120, 70, 0)); // +20 east, -30 north
        Assert.Equal(400.0 + 200.0, p.X, 6); // 20*10 right
        Assert.Equal(300.0 + 300.0, p.Y, 6); // 30*10 down
    }

    [Fact]
    public void RespectsDeviceOffset()
    {
        var d = new ViewDescriptor(new Pt2(0, 0), 100, Matrix2d.Identity,
            new PixelRect(1920, 200, 1000, 1000)); // canvas on a 2nd monitor
        var t = new ViewTransform(d);
        var p = t.WcsToDevice(new Pt3(0, 0, 0));
        Assert.Equal(1920 + 500.0, p.X, 6);
        Assert.Equal(200 + 500.0, p.Y, 6);
    }

    [Fact]
    public void Roundtrips_DeviceToWcs()
    {
        var t = new ViewTransform(Plan());
        var wcs = new Pt3(137.5, 88.25, 0);
        var dev = t.WcsToDevice(wcs);
        var back = t.DeviceToWcs(dev.X, dev.Y);
        Assert.Equal(wcs.X, back.X, 6);
        Assert.Equal(wcs.Y, back.Y, 6);
    }
}
