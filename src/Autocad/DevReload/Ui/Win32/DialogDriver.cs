using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UiMcp.Core.Geometry;

namespace UiMcp.Win32;

public sealed record DialogButton(long Hwnd, string Text, PixelRect Bounds);

/// <summary>
/// Drives classic (Win32/MFC) AutoCAD &amp; Civil 3D dialogs that have no .NET
/// API — e.g. the "Project Objects to Profile View" dialog raised by the COGO
/// point projection command. These dialogs run their own modal message loop, so
/// these methods work off the AutoCAD main thread (whose idle pump is stalled
/// during the modal loop) by talking to the dialog's own HWNDs.
///
/// WPF-based dialogs hosted in-process are instead reachable via the WPF tools
/// (they appear as PresentationSources) targeted by their hwnd.
/// </summary>
public static class DialogDriver
{
    /// <summary>Enumerate the child push-buttons of a dialog with their text.</summary>
    public static List<DialogButton> Buttons(IntPtr dialog)
    {
        var list = new List<DialogButton>();
        NativeMethods.EnumChildWindows(dialog, (h, _) =>
        {
            var cls = ClassOf(h);
            if (cls.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var text = TextOf(h);
                NativeMethods.GetWindowRect(h, out var r);
                list.Add(new DialogButton(h.ToInt64(), text,
                    new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Click a button whose text matches <paramref name="label"/>
    /// (ignoring case and the &amp; mnemonic marker) via a real synthetic click
    /// at its center, so the dialog's command handler fires as for a user.</summary>
    public static bool ClickButton(IntPtr dialog, string label)
    {
        foreach (var b in Buttons(dialog))
        {
            if (Matches(b.Text, label))
            {
                // A synthetic click lands on whatever owns those pixels and only
                // registers on the active window — so bring the dialog foreground
                // first (AttachThreadInput-based, beats the foreground lock).
                Foreground.Ensure(dialog);
                SynthInput.Click(b.Bounds.X + b.Bounds.Width / 2,
                                 b.Bounds.Y + b.Bounds.Height / 2, MouseButton.Left);
                return true;
            }
        }
        return false;
    }

    public static void PressKey(byte vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool Matches(string text, string label)
    {
        string Norm(string s) => s.Replace("&", "").Trim();
        return string.Equals(Norm(text), Norm(label), StringComparison.OrdinalIgnoreCase);
    }

    private static string TextOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
