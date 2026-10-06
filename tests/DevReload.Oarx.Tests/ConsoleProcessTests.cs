using System;

using Xunit;

namespace DevReload.Oarx.Tests;

/// <summary>DevReload stays out of a UI-less console, decided from the image
/// and the command line alone.</summary>
public class ConsoleProcessTests
{
    private const string Bricscad = @"C:\Program Files\Bricsys\BricsCAD V26 en_US\bricscad.exe";
    private const string Acad = @"C:\Program Files\Autodesk\AutoCAD 2025\acad.exe";
    private const string CoreConsole = @"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe";

    /// <summary>NSSM's BricsCAD plot console, as BricsCadConsole.Launch builds it.</summary>
    [Fact]
    public void The_nssm_bricscad_plot_console_is_a_console()
    {
        var args = new[] { "/b", @"H:\t\job\plot.scr", "/automation", "/nologo", "/p", "NSSM-console-1" };

        string? why = ConsoleProcess.Reason(CadHost.BricsCad, Bricscad, args);

        Assert.NotNull(why);
        Assert.Contains("/automation", why);
    }

    [Theory]
    [InlineData("/Automation")]
    [InlineData("-automation")]
    [InlineData("/AUTOMATION")]
    public void The_switch_is_read_in_any_case_and_with_either_prefix(string sw)
    {
        Assert.NotNull(ConsoleProcess.Reason(CadHost.BricsCad, Bricscad, new[] { sw }));
    }

    [Theory]
    [InlineData]
    [InlineData("/p", "Default")]
    [InlineData(@"C:\work\automation.dwg")]
    [InlineData("/automationx")]
    [InlineData("/b", @"C:\scripts\automation")]
    public void An_interactive_bricscad_is_not_a_console(params string[] args)
    {
        Assert.Null(ConsoleProcess.Reason(CadHost.BricsCad, Bricscad, args));
    }

    [Fact]
    public void Accoreconsole_is_a_console_whatever_its_arguments()
    {
        var args = new[] { "/i", @"C:\s\A.dwg", "/s", @"C:\s\plot.scr", "/readonly", "/product", "C3D" };

        Assert.NotNull(ConsoleProcess.Reason(CadHost.AutoCad, CoreConsole, args));
        Assert.NotNull(ConsoleProcess.Reason(CadHost.AutoCad, "ACCORECONSOLE.EXE", Array.Empty<string>()));
    }

    /// <summary>A COM-started AutoCAD (acad.exe /Automation) has its full UI
    /// and may be shown: it is not a console.</summary>
    [Fact]
    public void A_com_started_autocad_is_not_a_console()
    {
        Assert.Null(ConsoleProcess.Reason(CadHost.AutoCad, Acad, new[] { "/Automation", "-Embedding" }));
        Assert.Null(ConsoleProcess.Reason(CadHost.AutoCad, Acad, new[] { "/product", "C3D" }));
    }

    [Fact]
    public void An_unknown_image_is_not_a_console()
    {
        Assert.Null(ConsoleProcess.Reason(CadHost.BricsCad, null, Array.Empty<string>()));
    }
}
