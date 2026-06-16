using UiMcp.Core.Geometry;
using UiMcp.Core.Vision;

namespace UiMcp.Core.Tests;

/// <summary>
/// Resolving the rectangle to screenshot. Two sources: a UI element's bounds
/// (+ padding) and a WCS bounding box mapped through the view transform. Both
/// are clamped to the virtual screen so a capture never reads off-bounds.
/// </summary>
public class ScreenRegionTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void ElementBounds_AddsPadding()
    {
        var r = ScreenRegion.FromElement(new PixelRect(100, 100, 50, 40), padding: 10, Screen);
        Assert.Equal(new PixelRect(90, 90, 70, 60), r);
    }

    [Fact]
    public void ElementBounds_ClampsToScreen()
    {
        var r = ScreenRegion.FromElement(new PixelRect(0, 0, 50, 40), padding: 10, Screen);
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        // padding that would push left/top negative is absorbed, not negative origin
        Assert.True(r.Right <= Screen.Right);
        Assert.True(r.Bottom <= Screen.Bottom);
    }

    [Fact]
    public void WcsBox_MapsThroughTransform_AndOrdersCorners()
    {
        // Plan view: center (0,0), 100 units high over an 800x800 canvas → 8 px/unit.
        var t = new ViewTransform(new ViewDescriptor(
            new Pt2(0, 0), 100, Matrix2d.Identity, new PixelRect(0, 0, 800, 800)));

        // WCS box from (-10,-10) to (10,10): 20x20 units → 160x160 px, centered.
        var r = ScreenRegion.FromWcsBox(new Pt3(-10, -10, 0), new Pt3(10, 10, 0), t, padding: 0, Screen);
        Assert.Equal(400 - 80, r.X);
        Assert.Equal(400 - 80, r.Y);
        Assert.Equal(160, r.Width);
        Assert.Equal(160, r.Height);
    }
}
