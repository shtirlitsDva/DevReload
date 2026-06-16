using UiMcp.Core.Geometry;
using UiMcp.Core.Input;

namespace UiMcp.Core.Tests;

/// <summary>
/// Interpolating a drag into discrete mouse-move samples. AutoCAD's jig /
/// grip loop samples WM_MOUSEMOVE, so a drag must be delivered as several
/// paced moves between button-down and button-up, not one jump.
/// </summary>
public class DragPathTests
{
    [Fact]
    public void Interpolate_IncludesEndpoints()
    {
        var pts = DragPath.Interpolate(new Pt2(0, 0), new Pt2(100, 0), steps: 4);
        Assert.Equal(new Pt2(0, 0), pts[0]);
        Assert.Equal(new Pt2(100, 0), pts[^1]);
    }

    [Fact]
    public void Interpolate_ProducesStepsPlusOnePoints_EvenlySpaced()
    {
        var pts = DragPath.Interpolate(new Pt2(0, 0), new Pt2(100, 0), steps: 4);
        Assert.Equal(5, pts.Count); // 4 segments => 5 points
        Assert.Equal(25, pts[1].X, 6);
        Assert.Equal(50, pts[2].X, 6);
        Assert.Equal(75, pts[3].X, 6);
    }

    [Fact]
    public void Interpolate_StepsClampedToAtLeastOne()
    {
        var pts = DragPath.Interpolate(new Pt2(0, 0), new Pt2(10, 10), steps: 0);
        Assert.Equal(2, pts.Count); // just endpoints
    }

    [Fact]
    public void Polyline_ChainsSegments_NoDuplicateJoints()
    {
        var poly = new[] { new Pt2(0, 0), new Pt2(10, 0), new Pt2(10, 10) };
        var pts = DragPath.InterpolatePolyline(poly, stepsPerSegment: 2);
        // 2 segments * 2 steps = 4, +1 start = 5 points, joints not doubled
        Assert.Equal(5, pts.Count);
        Assert.Equal(new Pt2(0, 0), pts[0]);
        Assert.Equal(new Pt2(10, 0), pts[2]);
        Assert.Equal(new Pt2(10, 10), pts[^1]);
    }
}
