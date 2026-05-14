using Acad.Process;
using Xunit;

namespace Acad.Rpc.Core.Tests;

/// <summary>
/// Pure data tests for the product-name → flavor / cmd-line mapping.
/// Catches a typo when adding a vertical without requiring an actual
/// install of that vertical.
/// </summary>
public class AcadFlavorTableTests
{
    [Theory]
    [InlineData("AutoCAD 2025", AcadFlavor.AutoCAD)]
    [InlineData("Autodesk AutoCAD 2025 - English", AcadFlavor.AutoCAD)]
    [InlineData("Autodesk Civil 3D 2025 - English", AcadFlavor.Civil3D)]
    [InlineData("Civil 3D 2026", AcadFlavor.Civil3D)]
    [InlineData("Autodesk Plant 3D 2025", AcadFlavor.Plant3D)]
    [InlineData("AutoCAD Mechanical 2025", AcadFlavor.Mechanical)]
    [InlineData("AutoCAD Electrical 2025", AcadFlavor.Electrical)]
    [InlineData("AutoCAD Map 3D 2025", AcadFlavor.Map3D)]
    [InlineData("AutoCAD Architecture 2025", AcadFlavor.Architecture)]
    [InlineData("Definitely Not Autodesk", AcadFlavor.Unknown)]
    [InlineData("", AcadFlavor.Unknown)]
    [InlineData(null, AcadFlavor.Unknown)]
    public void FlavorForProductName_DetectsExpectedFlavor(string? productName, AcadFlavor expected)
    {
        var type = typeof(AcadInstall).Assembly.GetType("Acad.Process.FlavorTable");
        Assert.NotNull(type);
        var method = type!.GetMethod(
            "FlavorForProductName",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var actual = (AcadFlavor)method!.Invoke(null, new object?[] { productName })!;
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(AcadFlavor.AutoCAD, "")]
    [InlineData(AcadFlavor.Civil3D, "/product C3D")]
    [InlineData(AcadFlavor.Plant3D, "/product P3D")]
    [InlineData(AcadFlavor.Mechanical, "/product MCD")]
    [InlineData(AcadFlavor.Electrical, "/product EST")]
    [InlineData(AcadFlavor.Map3D, "/product MAP")]
    [InlineData(AcadFlavor.Architecture, "/product ARCHDESK")]
    [InlineData(AcadFlavor.Unknown, "")]
    public void CmdLineArgForFlavor_MatchesTable(AcadFlavor flavor, string expected)
    {
        var type = typeof(AcadInstall).Assembly.GetType("Acad.Process.FlavorTable");
        var method = type!.GetMethod(
            "CmdLineArgForFlavor",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var actual = (string)method!.Invoke(null, new object?[] { flavor })!;
        Assert.Equal(expected, actual);
    }
}
