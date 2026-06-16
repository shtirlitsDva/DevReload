using UiMcp.Core.Input;

namespace UiMcp.Core.Tests;

/// <summary>
/// SendInput with MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK expects
/// coordinates normalized to 0..65535 across the whole virtual screen, in
/// physical pixels. Off-by-one here puts every synthetic click a pixel or
/// two off — exactly the canvas-precision risk flagged in the research.
/// </summary>
public class SendInputCoordsTests
{
    // Virtual screen: left=0, top=0, width=1920, height=1080.
    [Fact]
    public void LeftEdge_MapsToZero()
        => Assert.Equal(0, SendInputCoords.NormalizeAbsolute(0, 0, 1920));

    [Fact]
    public void RightEdge_MapsToMax()
        => Assert.Equal(65535, SendInputCoords.NormalizeAbsolute(1919, 0, 1920));

    [Fact]
    public void Center_MapsToHalf()
    {
        int n = SendInputCoords.NormalizeAbsolute(960, 0, 1920);
        Assert.InRange(n, 32750, 32790); // ~32767
    }

    [Fact]
    public void NegativeVirtualOrigin_SecondMonitorLeftOfPrimary()
    {
        // Virtual screen starts at -1920 (a monitor to the left of primary).
        Assert.Equal(0, SendInputCoords.NormalizeAbsolute(-1920, -1920, 3840));
        Assert.Equal(65535, SendInputCoords.NormalizeAbsolute(1919, -1920, 3840));
    }

    [Fact]
    public void ClampsOutOfRange()
    {
        Assert.Equal(0, SendInputCoords.NormalizeAbsolute(-50, 0, 1920));
        Assert.Equal(65535, SendInputCoords.NormalizeAbsolute(99999, 0, 1920));
    }
}
