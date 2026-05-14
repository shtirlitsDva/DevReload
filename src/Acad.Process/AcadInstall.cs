namespace Acad.Process;

/// <summary>
/// One installed AutoCAD/vertical product. Discovered via registry.
/// <see cref="ExePath"/> is the launchable acad.exe.
/// <see cref="ProductCmdLineArg"/> is the <c>/product</c> selector passed
/// to acad.exe to start in the right vertical mode — empty for vanilla
/// AutoCAD.
/// </summary>
public sealed record AcadInstall(
    AcadFlavor Flavor,
    string ProductName,
    string ReleaseKey,
    string ProductCode,
    string InstallPath,
    string ExePath,
    string ProductCmdLineArg);
