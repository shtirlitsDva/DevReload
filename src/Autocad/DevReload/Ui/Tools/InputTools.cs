using System;
using System.ComponentModel;
using Acad.Rpc.Core;
using Autodesk.AutoCAD.ApplicationServices.Core;
using UiMcp.Canvas;
using UiMcp.Core.Geometry;
using UiMcp.Dto;
using UiMcp.Win32;

namespace UiMcp.Tools;

/// <summary>
/// Capability 3 — synthetic mouse input for testing interactive tools (jigs,
/// custom grips, OSNAP, real-time drag previews) that only fire through the
/// real input pipeline. Pixel-space tools take physical screen pixels; canvas
/// tools take WCS and map through the captured view transform. SendInput runs
/// off the main thread on purpose (the main thread is busy in the jig loop).
/// </summary>
[AcadRpcSurface(Group = "ui")]
public static class InputTools
{
    private static MouseButton ParseButton(string b) => b.Trim().ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    [AcadRpcTool, Description("Move the cursor to a physical screen pixel (absolute, multi-monitor aware).")]
    public static ActionResult MouseMove(
        [Description("Physical screen X (px).")] int x,
        [Description("Physical screen Y (px).")] int y)
    { SynthInput.MoveTo(x, y); return new ActionResult(true, $"moved to {x},{y}"); }

    [AcadRpcTool, Description("Click at a physical screen pixel. button: left|right|middle.")]
    public static ActionResult Click(
        [Description("Physical screen X (px).")] int x,
        [Description("Physical screen Y (px).")] int y,
        [Description("left | right | middle (default left).")] string button = "left")
    { SynthInput.Click(x, y, ParseButton(button)); return new ActionResult(true, $"clicked {button} at {x},{y}"); }

    [AcadRpcTool, Description("Drag from one physical pixel to another as button-down, N paced moves, button-up. Paces moves so AutoCAD's input loop samples them (drives grips/window-select/jigs).")]
    public static ActionResult Drag(
        [Description("Start X (px).")] int fromX, [Description("Start Y (px).")] int fromY,
        [Description("End X (px).")] int toX, [Description("End Y (px).")] int toY,
        [Description("Intermediate move samples (default 20).")] int steps = 20,
        [Description("Delay between moves in ms (default 10).")] int stepDelayMs = 10,
        [Description("left | right | middle (default left).")] string button = "left")
    {
        var path = UiMcp.Core.Input.DragPath.Interpolate(new Pt2(fromX, fromY), new Pt2(toX, toY), steps);
        SynthInput.DragPathPx(path, ParseButton(button), stepDelayMs);
        return new ActionResult(true, $"dragged {fromX},{fromY} -> {toX},{toY} ({path.Count} samples)");
    }

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Capture the live canvas view (center, height, twist + drawing-area screen rect) so WCS gesture tools can map WCS->pixel. Call while AutoCAD is quiescent, BEFORE starting a jig. Caches the result and returns it (note records how the device rect was resolved — calibrate live).")]
    public static CanvasViewDto CanvasCaptureView()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        var (view, note) = ViewCapture.Capture(ed);
        CanvasViewCache.Set(view);
        return CanvasViewCache.ToDto(view, note);
    }

    [AcadRpcTool, Description("Click at a WCS point on the canvas using the captured view transform. OSNAP still snaps to real geometry. Requires a prior ui_canvas_capture_view.")]
    public static ActionResult CanvasClick(
        [Description("WCS X.")] double wcsX, [Description("WCS Y.")] double wcsY,
        [Description("left | right | middle (default left).")] string button = "left")
    {
        var t = new ViewTransform(CanvasViewCache.Require());
        var p = t.WcsToDevice(new Pt3(wcsX, wcsY, 0));
        bool fg = Foreground();
        SynthInput.Click((int)Math.Round(p.X), (int)Math.Round(p.Y), ParseButton(button));
        return new ActionResult(true, $"canvas click WCS({wcsX},{wcsY}) -> px({p.X:F0},{p.Y:F0}); foreground={fg}");
    }

    [AcadRpcTool, Description("Drag between two WCS points on the canvas (button-down/paced-moves/button-up) using the captured view transform — for grips, real-time jigs, window-select. Requires a prior ui_canvas_capture_view.")]
    public static ActionResult CanvasDrag(
        [Description("Start WCS X.")] double fromWcsX, [Description("Start WCS Y.")] double fromWcsY,
        [Description("End WCS X.")] double toWcsX, [Description("End WCS Y.")] double toWcsY,
        [Description("Intermediate move samples (default 24).")] int steps = 24,
        [Description("Delay between moves in ms (default 12).")] int stepDelayMs = 12,
        [Description("left | right | middle (default left).")] string button = "left")
    {
        var t = new ViewTransform(CanvasViewCache.Require());
        var a = t.WcsToDevice(new Pt3(fromWcsX, fromWcsY, 0));
        var b = t.WcsToDevice(new Pt3(toWcsX, toWcsY, 0));
        var path = UiMcp.Core.Input.DragPath.Interpolate(a, b, steps);
        bool fg = Foreground();
        SynthInput.DragPathPx(path, ParseButton(button), stepDelayMs);
        return new ActionResult(true, $"canvas drag WCS({fromWcsX},{fromWcsY})->({toWcsX},{toWcsY}), {path.Count} samples; foreground={fg}");
    }

    private static bool Foreground()
        => Win32.Foreground.Ensure(Application.MainWindow?.Handle ?? IntPtr.Zero);
}
