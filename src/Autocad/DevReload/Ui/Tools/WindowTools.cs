using System;
using System.Collections.Generic;
using System.ComponentModel;
using Acad.Rpc.Core;
using UiMcp.Dto;
using UiMcp.Win32;

namespace UiMcp.Tools;

/// <summary>
/// Capability 2 — drive native (Win32/MFC) AutoCAD &amp; Civil 3D dialogs that
/// have no .NET API (e.g. the COGO-point "Project Objects to Profile View"
/// dialog). Deliberately NOT marshaled to the main thread: a modal dialog runs
/// its own message loop while AutoCAD's idle pump is stalled, so these talk to
/// the dialog's HWNDs directly. (WPF dialogs hosted in-process appear as
/// surfaces in ui_list_surfaces and are driven by the ui_* tools instead.)
/// </summary>
[AcadRpcSurface(Group = "ui")]
public static class WindowTools
{
    [AcadRpcTool,
     Description("List this AutoCAD process's top-level windows (main frame + any open modal dialog): hwnd, title, class, screen bounds, visible, enabled.")]
    public static List<WindowInfo> ListWindows() => WindowEnum.TopLevelWindows();

    [AcadRpcTool,
     Description("List the push-buttons of a classic dialog (by its hwnd): hwnd, text, screen bounds. Use to discover OK/Cancel/Apply before clicking.")]
    public static List<DialogButton> DialogButtons(
        [Description("Dialog window hwnd from list_windows.")] long hwnd)
        => DialogDriver.Buttons(new IntPtr(hwnd));

    [AcadRpcTool,
     Description("Click a dialog button by its label (case-insensitive, ignores & mnemonic), e.g. \"OK\". Delivers a real synthetic click so the dialog's handler fires as for a user.")]
    public static ActionResult DialogClick(
        [Description("Dialog window hwnd from list_windows.")] long hwnd,
        [Description("Button label, e.g. \"OK\", \"Cancel\", \"Apply\".")] string label)
        => DialogDriver.ClickButton(new IntPtr(hwnd), label)
            ? new ActionResult(true, $"clicked '{label}'")
            : new ActionResult(false, $"no button matching '{label}' found");

    [AcadRpcTool,
     Description("Press a global key for a dialog: one of enter, escape, tab, space, yes, no. Pass the dialog's hwnd (from ui_list_windows) so it is brought foreground/focused first — escape then reliably cancels a modal (incl. native file dialogs). Useful to accept a default button (enter) or dismiss (escape).")]
    public static ActionResult PressKey(
        [Description("Key name: enter | escape | tab | space | yes | no.")] string key,
        [Description("Dialog hwnd to focus before the keystroke (from ui_list_windows). 0 = send to the current foreground window.")] long hwnd = 0)
    {
        byte vk = key.Trim().ToLowerInvariant() switch
        {
            "enter" or "return" => NativeMethods.VK_RETURN,
            "escape" or "esc" => NativeMethods.VK_ESCAPE,
            "tab" => NativeMethods.VK_TAB,
            "space" => NativeMethods.VK_SPACE,
            "yes" or "y" => NativeMethods.VK_Y,
            "no" or "n" => NativeMethods.VK_N,
            _ => 0,
        };
        if (vk == 0) return new ActionResult(false, $"unknown key '{key}'");
        bool fg = hwnd != 0 && Foreground.Ensure(new IntPtr(hwnd));
        DialogDriver.PressKey(vk);
        return new ActionResult(true, $"pressed {key}; foreground={fg}");
    }
}
