using System;
using System.Collections.Generic;
using UiMcp.Core.Geometry;

namespace UiMcp.Core.Input;

/// <summary>
/// Turns a drag (or polyline gesture) into evenly-spaced mouse-move samples.
/// AutoCAD's jig / grip / window-select loops react to WM_MOUSEMOVE, so the
/// path must be delivered as paced moves between button-down and button-up.
/// </summary>
public static class DragPath
{
    public static IReadOnlyList<Pt2> Interpolate(Pt2 from, Pt2 to, int steps)
    {
        if (steps < 1) steps = 1;
        var pts = new List<Pt2>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double f = (double)i / steps;
            pts.Add(new Pt2(from.X + (to.X - from.X) * f, from.Y + (to.Y - from.Y) * f));
        }
        return pts;
    }

    /// <summary>Chain a polyline of waypoints; shared joints appear once.</summary>
    public static IReadOnlyList<Pt2> InterpolatePolyline(IReadOnlyList<Pt2> waypoints, int stepsPerSegment)
    {
        if (waypoints == null) throw new ArgumentNullException(nameof(waypoints));
        if (waypoints.Count == 0) return Array.Empty<Pt2>();
        if (waypoints.Count == 1) return new[] { waypoints[0] };

        var pts = new List<Pt2> { waypoints[0] };
        for (int s = 0; s < waypoints.Count - 1; s++)
        {
            var seg = Interpolate(waypoints[s], waypoints[s + 1], stepsPerSegment);
            // skip seg[0] — it's the previous segment's last point
            for (int i = 1; i < seg.Count; i++) pts.Add(seg[i]);
        }
        return pts;
    }
}
