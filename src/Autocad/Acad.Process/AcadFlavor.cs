namespace Acad.Process;

/// <summary>
/// Identifies an AutoCAD vertical. Used by install discovery and
/// process launch — the launcher passes the right <c>/product</c>
/// argument to <c>acad.exe</c> based on the chosen flavor.
/// </summary>
public enum AcadFlavor
{
    Unknown = 0,
    AutoCAD = 1,
    Civil3D = 2,
    Plant3D = 3,
    Mechanical = 4,
    Electrical = 5,
    Map3D = 6,
    Architecture = 7,
}
