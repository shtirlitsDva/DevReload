using DevReload.Oarx;

using Xunit;

namespace DevReload.Oarx.Tests;

public class BuildFolderTests
{
    private const string Folder = @"C:\repo\x64\DevReload";

    [Fact]
    public void NoFolder_PassesTheProfilePropertiesUnchanged()
    {
        var props = new[] { "NdhFastDevLoop=true" };
        Assert.Equal(props, OarxBuildFolder.EffectiveProperties(props, null));
    }

    [Fact]
    public void AFolder_IsAddedAsTheBuildFolderProperty()
    {
        var effective = OarxBuildFolder.EffectiveProperties(new[] { "A=1" }, Folder);
        Assert.Equal(new[] { "A=1", $"DevReloadBuildFolder={Folder}" }, effective);
    }

    [Theory]
    [InlineData(@"C:\repo\x64\DevReload\Mod.dbx")]
    [InlineData(@"C:\REPO\x64\devreload\Mod.dbx")]
    [InlineData(@"C:\repo\x64\DevReload\sub\Mod.arx")]
    public void AModuleInsideTheFolder_IsAccepted(string target) =>
        Assert.Null(OarxBuildFolder.CheckInside(Folder, "Mod", target));

    [Theory]
    [InlineData(@"C:\repo\x64\Debug\Mod.dbx")]
    // A sibling whose name merely STARTS with the folder's is outside it.
    [InlineData(@"C:\repo\x64\DevReload2\Mod.dbx")]
    public void AModuleOutsideTheFolder_IsRefusedByName(string target)
    {
        string? why = OarxBuildFolder.CheckInside(Folder, "Mod", target);
        Assert.NotNull(why);
        Assert.Contains("DevReloadBuildFolder", why);
        Assert.Contains(target, why);
    }

    [Fact]
    public void NoFolder_AcceptsAnyModule() =>
        Assert.Null(OarxBuildFolder.CheckInside(null, "Mod", @"C:\anywhere\Mod.dbx"));

    [Theory]
    [InlineData("DevReloadBuildFolder=x", true)]
    [InlineData(" devreloadbuildfolder = x", true)]
    [InlineData("NdhFastDevLoop=true", false)]
    public void SettingThePropertyByHand_IsDetected(string prop, bool expected) =>
        Assert.Equal(expected, OarxBuildFolder.IsSetByHand(new[] { prop }));
}
