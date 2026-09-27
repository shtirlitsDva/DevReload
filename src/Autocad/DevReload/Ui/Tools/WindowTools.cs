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
    [AcadRpcTool(Effect = ToolEffect.ReadOnly),
     Description("List this AutoCAD process's top-level windows (main frame + any open modal dialog): hwnd, title, class, screen bounds, visible, enabled.")]
    public static List<WindowInfo> ListWindows() => WindowEnum.TopLevelWindows();

    [AcadRpcTool(Effect = ToolEffect.ReadOnly),
     Description("List the push-buttons of a classic dialog (by its hwnd): hwnd, text, screen bounds. Use to discover OK/Cancel/Apply before clicking.")]
    public static List<DialogButton> DialogButtons(
        [Description("Dialog window hwnd from list_windows.")] long hwnd)
        => DialogDriver.Buttons(new IntPtr(hwnd));

    [AcadRpcTool(Effect = ToolEffect.Destructive),
     Description("Click a dialog button by its label (case-insensitive, ignores & mnemonic), e.g. \"OK\". HEADLESS: posts a BM_CLICK message to the button, so the dialog's handler fires as for a user WITHOUT moving the cursor or bringing the dialog foreground — works on a background instance and is safe to run against multiple instances at once.")]
    public static ActionResult DialogClick(
        [Description("Dialog window hwnd from list_windows.")] long hwnd,
        [Description("Button label, e.g. \"OK\", \"Cancel\", \"Apply\".")] string label)
    {
        if (!DialogDriver.ClickButton(new IntPtr(hwnd), label))
            throw new InvalidOperationException(
                $"no button matching '{label}' in dialog {hwnd} — nothing was clicked. " +
                "ui_dialog_buttons lists the labels.");
        return new ActionResult($"clicked '{label}'");
    }

    [AcadRpcTool(Effect = ToolEffect.Destructive),
     Description("Press a global key for a dialog. Pass the dialog's hwnd (from ui_list_windows) so it is brought foreground/focused first — Escape then reliably cancels a modal (incl. native file dialogs). Useful to accept a default button (Enter) or dismiss (Escape).")]
    public static ActionResult PressKey(
        [Description("The key to press.")] DialogKey key,
        [Description("Dialog hwnd to focus before the keystroke (from ui_list_windows). 0 = send to the current foreground window.")] long hwnd = 0)
    {
        byte vk = key switch
        {
            DialogKey.Enter => NativeMethods.VK_RETURN,
            DialogKey.Escape => NativeMethods.VK_ESCAPE,
            DialogKey.Tab => NativeMethods.VK_TAB,
            DialogKey.Space => NativeMethods.VK_SPACE,
            DialogKey.Yes => NativeMethods.VK_Y,
            DialogKey.No => NativeMethods.VK_N,
            // The binder refuses names that are not members, so only a new
            // member without a mapping lands here.
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no virtual-key mapping for this key"),
        };
        bool fg = hwnd != 0 && Foreground.Ensure(new IntPtr(hwnd));
        DialogDriver.PressKey(vk);
        return new ActionResult($"pressed {key}; foreground={fg}");
    }
}

/// <summary>The keys <see cref="WindowTools.PressKey"/> can send to a dialog.
/// Yes / No are the Y / N accelerators of a Yes/No message box.</summary>
public enum DialogKey { Enter, Escape, Tab, Space, Yes, No }
