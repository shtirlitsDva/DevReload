using System.Collections.Generic;

namespace UiMcp.Dto;

/// <summary>A WPF root surface hosted in the AutoCAD process (a palette's
/// HwndSource root visual, or any other WPF top-level content).</summary>
public sealed record SurfaceInfo(
    long Hwnd, string Title, string RootType, int Width, int Height);

/// <summary>One node in a serialized WPF visual/logical tree. <see cref="Id"/>
/// is a tree path (e.g. "0/3/1") usable as an element reference in the action
/// tools; <see cref="Name"/> is x:Name; <see cref="AutomationId"/> is the
/// AutomationProperties.AutomationId when set.</summary>
public sealed record ElementNode(
    string Id,
    string Type,
    string? Name,
    string? AutomationId,
    string? Text,
    string? Value,
    bool IsEnabled,
    bool IsVisible,
    int X, int Y, int Width, int Height,
    List<ElementNode> Children);

/// <summary>A full palette snapshot: the element tree plus a reflection dump
/// of the bound ViewModel (DataContext public properties), so the agent can
/// assert on the ViewModel — the thing actually under test — not just pixels.</summary>
public sealed record SurfaceSnapshot(
    SurfaceInfo Surface,
    ElementNode Root,
    Dictionary<string, string?> ViewModel);

public sealed record ActionResult(bool Ok, string Message);

/// <summary>Structured metadata for a screenshot tool. The PNG itself travels
/// as an inline MCP image content block (see <see cref="Acad.Rpc.Core.ToolResult"/>),
/// so there is no file path — the agent sees the image directly.</summary>
public sealed record ScreenshotResult(int Width, int Height, string Note);

/// <summary>Structured metadata for a drag-with-burst capture. The ordered
/// frames travel as inline MCP image content blocks; this carries just the count
/// and frame size.</summary>
public sealed record CaptureBurstResult(int FrameCount, int Width, int Height, string Note);
