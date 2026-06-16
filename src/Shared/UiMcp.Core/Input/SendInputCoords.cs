using System;

namespace UiMcp.Core.Input;

/// <summary>
/// Coordinate normalization for SendInput mouse events. With
/// MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK the dx/dy fields are not
/// pixels but a 0..65535 fraction of the virtual screen. Getting the divisor
/// and origin right is what keeps a synthetic click on the intended pixel
/// across multi-monitor layouts (including monitors left of / above primary,
/// which give a negative virtual-screen origin).
/// </summary>
public static class SendInputCoords
{
    public const int AbsoluteMax = 65535;

    /// <summary>
    /// Map a physical-pixel coordinate on one axis to the 0..65535 SendInput
    /// absolute range. <paramref name="virtualOrigin"/> /
    /// <paramref name="virtualSize"/> are SM_XVIRTUALSCREEN/SM_CXVIRTUALSCREEN
    /// (or the Y pair). Result is clamped to [0, 65535].
    /// </summary>
    public static int NormalizeAbsolute(int coord, int virtualOrigin, int virtualSize)
    {
        if (virtualSize <= 1) return 0;
        double n = (coord - virtualOrigin) * (double)AbsoluteMax / (virtualSize - 1);
        int r = (int)Math.Round(n, MidpointRounding.AwayFromZero);
        if (r < 0) return 0;
        if (r > AbsoluteMax) return AbsoluteMax;
        return r;
    }
}
