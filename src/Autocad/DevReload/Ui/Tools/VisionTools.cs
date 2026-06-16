using System;
using System.Collections.Generic;
using System.ComponentModel;
using Acad.Rpc.Core;
using UiMcp.Canvas;
using UiMcp.Core.Geometry;
using UiMcp.Core.Input;
using UiMcp.Core.Vision;
using UiMcp.Dto;
using UiMcp.Win32;
using UiMcp.Wpf;

namespace UiMcp.Tools;

/// <summary>
/// Capability 4 — granular vision. Screenshots scoped to a window, an arbitrary
/// region, a named WPF element's bounds, or a WCS bounding box on the canvas;
/// plus a drag-with-burst that interleaves a synthetic drag with timed frames so
/// the agent can see how a jig animates. Each tool returns the PNG(s) as inline
/// MCP image content blocks (no temp files) alongside structured size metadata.
/// Window capture uses PrintWindow(PW_RENDERFULLCONTENT) so a docked/occluded
/// palette still renders.
/// </summary>
[AcadRpcSurface(Group = "ui")]
public static class VisionTools
{
    // The AutoCAD frame whose PrintWindow render includes the GPU canvas; canvas
    // captures crop this so occlusion (e.g. the agent's own terminal on top)
    // never corrupts the image.
    private static IntPtr FrameHwnd() =>
        Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow?.Handle ?? IntPtr.Zero;

    private static PixelRect VirtualScreen() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    // Wrap a single capture as an MCP result: the PNG rides back inline as an
    // image content block, the size/note as structuredContent.
    private static ToolResult Shot(WindowCapture.Shot shot, string note) => new()
    {
        Structured = new ScreenshotResult(shot.Width, shot.Height, note),
        Images = new[] { new ToolImage(Convert.ToBase64String(shot.Png)) },
    };

    [AcadRpcTool, Description("Screenshot a window by hwnd via PrintWindow (renders even when occluded). Returns the PNG inline + its size.")]
    public static ToolResult ScreenshotWindow(
        [Description("Window hwnd from ui_list_windows / ui_list_surfaces.")] long hwnd)
        => Shot(WindowCapture.CaptureWindow(new IntPtr(hwnd)), "PrintWindow");

    [AcadRpcTool, Description("Screenshot an arbitrary physical-pixel screen region. Returns the PNG inline.")]
    public static ToolResult ScreenshotRegion(
        [Description("Region X (px).")] int x, [Description("Region Y (px).")] int y,
        [Description("Width (px).")] int width, [Description("Height (px).")] int height)
    {
        var region = ScreenRegion.FromElement(new PixelRect(x, y, width, height), 0, VirtualScreen());
        return Shot(WindowCapture.CaptureRegion(region), "region");
    }

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Screenshot a WPF element (by tree-path id / x:Name / AutomationId) plus optional padding — for inspecting one control's rendering. Bounds read on the UI thread; capture follows. Returns the PNG inline.")]
    public static ToolResult ScreenshotElement(
        [Description("Element reference: tree-path id, x:Name, or AutomationId.")] string elementRef,
        [Description("Padding around the element (px, default 6).")] int padding = 6,
        [Description("Target surface hwnd; 0 = first/only surface.")] long hwnd = 0)
    {
        var bounds = WpfInspector.ElementScreenBounds(hwnd, elementRef);
        var region = ScreenRegion.FromElement(bounds, padding, VirtualScreen());
        return Shot(WindowCapture.CaptureRegion(region), $"element '{elementRef}'");
    }

    [AcadRpcTool, Description("Screenshot the canvas region covering a WCS bounding box (+ padding px), using the captured view transform. Requires a prior ui_canvas_capture_view. Great for focusing on specific entities. Returns the PNG inline.")]
    public static ToolResult ScreenshotWcsBox(
        [Description("Min WCS X.")] double minX, [Description("Min WCS Y.")] double minY,
        [Description("Max WCS X.")] double maxX, [Description("Max WCS Y.")] double maxY,
        [Description("Padding around the box (px, default 10).")] int padding = 10)
    {
        var t = new ViewTransform(CanvasViewCache.Require());
        var region = ScreenRegion.FromWcsBox(new Pt3(minX, minY, 0), new Pt3(maxX, maxY, 0), t, padding, VirtualScreen());
        return Shot(WindowCapture.CaptureWindowRegion(FrameHwnd(), region), "wcs box (PrintWindow+crop)");
    }

    [AcadRpcTool, Description("Drive a canvas drag (WCS) while capturing the drawing area every 'captureStride' move samples — so the agent can see how a jig / grip animates frame by frame. Requires a prior ui_canvas_capture_view. Returns the ordered frames inline.")]
    public static ToolResult CanvasDragCapture(
        [Description("Start WCS X.")] double fromWcsX, [Description("Start WCS Y.")] double fromWcsY,
        [Description("End WCS X.")] double toWcsX, [Description("End WCS Y.")] double toWcsY,
        [Description("Intermediate move samples (default 24).")] int steps = 24,
        [Description("Delay between moves in ms (default 30).")] int stepDelayMs = 30,
        [Description("Capture a frame every Nth sample (default 4).")] int captureStride = 4,
        [Description("left | right | middle (default left).")] string button = "left")
    {
        var view = CanvasViewCache.Require();
        var t = new ViewTransform(view);
        var a = t.WcsToDevice(new Pt3(fromWcsX, fromWcsY, 0));
        var b = t.WcsToDevice(new Pt3(toWcsX, toWcsY, 0));
        var path = DragPath.Interpolate(a, b, steps);

        var images = new List<ToolImage>();
        var region = view.Device;
        var frame = FrameHwnd();
        if (captureStride < 1) captureStride = 1;
        int w = region.Width, h = region.Height;

        var mb = button.Trim().ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        Foreground.Ensure(Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow?.Handle ?? IntPtr.Zero);

        SynthInput.DragPathPx(path, mb, stepDelayMs, onSample: i =>
        {
            if (i % captureStride != 0 && i != path.Count - 1) return;
            try
            {
                var s = WindowCapture.CaptureWindowRegion(frame, region);
                images.Add(new ToolImage(Convert.ToBase64String(s.Png)));
                w = s.Width; h = s.Height;
            }
            catch { /* a dropped frame must not abort the gesture */ }
        });

        return new ToolResult
        {
            Structured = new CaptureBurstResult(images.Count, w, h,
                $"{images.Count} frames over {path.Count} samples"),
            Images = images,
        };
    }
}
