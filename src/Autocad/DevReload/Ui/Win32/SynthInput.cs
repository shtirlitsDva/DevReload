using System;
using System.Collections.Generic;
using System.Threading;
using UiMcp.Core.Geometry;
using UiMcp.Core.Input;

namespace UiMcp.Win32;

/// <summary>Mouse button selector for synthetic input.</summary>
public enum MouseButton { Left, Right, Middle }

/// <summary>
/// Drives the real OS input pipeline via SendInput, so jigs, grips, OSNAP and
/// transient previews react exactly as they do to a human. Coordinates are
/// physical screen pixels; they are normalized to the 0..65535 virtual-desktop
/// range here (see <see cref="SendInputCoords"/>). Safe to call off the AutoCAD
/// main thread — which is essential, because during a jig / modal loop the main
/// thread is busy and the idle pump is not running.
/// </summary>
public static class SynthInput
{
    private static (int origin, int size) Vx() =>
        (NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));

    private static (int origin, int size) Vy() =>
        (NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    private static NativeMethods.INPUT MoveInput(int x, int y)
    {
        var (vx, cx) = Vx();
        var (vy, cy) = Vy();
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = SendInputCoords.NormalizeAbsolute(x, vx, cx),
                dy = SendInputCoords.NormalizeAbsolute(y, vy, cy),
                dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                          | NativeMethods.MOUSEEVENTF_ABSOLUTE
                          | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            },
        };
    }

    private static NativeMethods.INPUT ButtonInput(MouseButton b, bool down)
    {
        uint flag = b switch
        {
            MouseButton.Right => down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP,
        };
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT { dwFlags = flag },
        };
    }

    private static void Send(params NativeMethods.INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInput delivered {sent}/{inputs.Length} events (last error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
    }

    public static void MoveTo(int x, int y) => Send(MoveInput(x, y));

    public static void Click(int x, int y, MouseButton button)
    {
        Send(MoveInput(x, y));
        Thread.Sleep(15);
        Send(ButtonInput(button, true));
        Thread.Sleep(15);
        Send(ButtonInput(button, false));
    }

    /// <summary>Button-down at the first sample, a paced move at each
    /// subsequent sample, then button-up. <paramref name="stepDelayMs"/> paces
    /// the moves so AutoCAD's input loop samples each one.</summary>
    public static void DragPathPx(IReadOnlyList<Pt2> samples, MouseButton button, int stepDelayMs,
        Action<int>? onSample = null)
    {
        if (samples == null || samples.Count < 2)
            throw new ArgumentException("drag needs at least 2 samples", nameof(samples));

        Send(MoveInput((int)Math.Round(samples[0].X), (int)Math.Round(samples[0].Y)));
        Thread.Sleep(15);
        Send(ButtonInput(button, true));
        onSample?.Invoke(0);

        for (int i = 1; i < samples.Count; i++)
        {
            if (stepDelayMs > 0) Thread.Sleep(stepDelayMs);
            Send(MoveInput((int)Math.Round(samples[i].X), (int)Math.Round(samples[i].Y)));
            onSample?.Invoke(i);
        }

        Thread.Sleep(15);
        Send(ButtonInput(button, false));
    }
}
