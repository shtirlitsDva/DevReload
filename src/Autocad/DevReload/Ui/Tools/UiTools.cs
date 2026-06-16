using System.ComponentModel;
using Acad.Rpc.Core;
using UiMcp.Dto;
using UiMcp.Wpf;

namespace UiMcp.Tools;

/// <summary>
/// Capability 1 — see &amp; interact with WPF plugin UI hosted in AutoCAD,
/// in-process. Reads the live visual tree AND the bound ViewModel; drives
/// controls through their automation peers (coordinate-free, DPI-proof).
/// These touch WPF objects so they run on AutoCAD's main thread.
/// </summary>
[AcadRpcSurface(Group = "ui")]
public static class UiTools
{
    [AcadRpcTool, RunOnAcadMainThread,
     Description("List WPF surfaces (palettes / windows) hosted in this AutoCAD process: hwnd, title, root type, size.")]
    public static System.Collections.Generic.List<SurfaceInfo> ListSurfaces()
        => WpfInspector.ListSurfaces();

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Snapshot a WPF surface: the element tree (type, x:Name, AutomationId, text, value, enabled, visible, screen-pixel bounds) plus a reflection dump of the bound ViewModel. Re-read after every action — the live tree mutates. Each node's 'id' (e.g. \"0/3/1\") is the element reference for the action tools.")]
    public static SurfaceSnapshot Snapshot(
        [Description("Target surface hwnd from list_surfaces; 0 = first/only surface.")] long hwnd = 0,
        [Description("Max visual-tree depth to serialize (default 40).")] int maxDepth = 40,
        [Description("Include the bound ViewModel (DataContext) property dump (default true).")] bool includeViewModel = true)
        => WpfInspector.Snapshot(hwnd, maxDepth, includeViewModel);

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Invoke a control (click a Button etc.) via its automation peer's Invoke pattern. elementRef is a tree-path id, x:Name, or AutomationId.")]
    public static ActionResult Invoke(
        [Description("Element reference: tree-path id (\"0/3/1\"), x:Name, or AutomationId.")] string elementRef,
        [Description("Target surface hwnd; 0 = first/only surface.")] long hwnd = 0)
        => WpfInspector.Invoke(hwnd, elementRef);

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Set a control's value via its Value pattern (e.g. type into a TextBox).")]
    public static ActionResult SetValue(
        [Description("Element reference: tree-path id, x:Name, or AutomationId.")] string elementRef,
        [Description("New value.")] string value,
        [Description("Target surface hwnd; 0 = first/only surface.")] long hwnd = 0)
        => WpfInspector.SetValue(hwnd, elementRef, value);

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Toggle a control (CheckBox/ToggleButton) via its Toggle pattern.")]
    public static ActionResult Toggle(
        [Description("Element reference: tree-path id, x:Name, or AutomationId.")] string elementRef,
        [Description("Target surface hwnd; 0 = first/only surface.")] long hwnd = 0)
        => WpfInspector.Toggle(hwnd, elementRef);

    [AcadRpcTool, RunOnAcadMainThread,
     Description("Select an item (ListBoxItem/RadioButton/Tab) via its SelectionItem pattern.")]
    public static ActionResult Select(
        [Description("Element reference: tree-path id, x:Name, or AutomationId.")] string elementRef,
        [Description("Target surface hwnd; 0 = first/only surface.")] long hwnd = 0)
        => WpfInspector.Select(hwnd, elementRef);
}
