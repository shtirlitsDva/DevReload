using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Acad.Process;

/// <summary>
/// Discovers AutoCAD/vertical installs from the Windows registry.
/// Reads <c>HKLM\SOFTWARE\Autodesk\AutoCAD\R&lt;n&gt;.0\&lt;productCode&gt;</c>
/// (and 32-on-64 view if needed) for the install location and product
/// identity. Flavor is detected from <c>ProductName</c> rather than
/// product-code numerology — the codes change each major release and
/// product-name strings are stable.
/// </summary>
public static class AcadInstallRegistry
{
    private const string RootKey = @"SOFTWARE\Autodesk\AutoCAD";

    /// <summary>
    /// Enumerate every installed flavor on the machine. Result is
    /// stable-ordered by (release-desc, flavor-asc, productCode-asc) so
    /// the newest release of the highest-priority flavor sits first —
    /// useful for "pick a default" callers.
    /// </summary>
    public static IReadOnlyList<AcadInstall> Discover()
    {
        var results = new List<AcadInstall>();
        DiscoverFromHive(RegistryView.Registry64, results);
        DiscoverFromHive(RegistryView.Registry32, results);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedup = new List<AcadInstall>();
        foreach (var i in results)
        {
            var key = i.ExePath + "|" + i.ProductCode;
            if (seen.Add(key)) dedup.Add(i);
        }

        dedup.Sort((a, b) =>
        {
            int cmp = string.CompareOrdinal(b.ReleaseKey, a.ReleaseKey);
            if (cmp != 0) return cmp;
            cmp = ((int)a.Flavor).CompareTo((int)b.Flavor);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.ProductCode, b.ProductCode);
        });
        return dedup;
    }

    /// <summary>
    /// Pick a sensible default install for the requested flavor.
    /// Returns null if no matching install is found.
    /// </summary>
    public static AcadInstall? PickDefault(AcadFlavor flavor, IReadOnlyList<AcadInstall>? known = null)
    {
        known ??= Discover();
        foreach (var i in known)
            if (i.Flavor == flavor) return i;
        return null;
    }

    private static void DiscoverFromHive(RegistryView view, List<AcadInstall> sink)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var rootKey = baseKey.OpenSubKey(RootKey);
        if (rootKey == null) return;

        foreach (var releaseName in rootKey.GetSubKeyNames())
        {
            if (releaseName.Length < 2 || releaseName[0] != 'R') continue;
            using var releaseKey = rootKey.OpenSubKey(releaseName);
            if (releaseKey == null) continue;

            foreach (var productSubkey in releaseKey.GetSubKeyNames())
            {
                using var productKey = releaseKey.OpenSubKey(productSubkey);
                if (productKey == null) continue;

                string productCode = ExtractProductCode(productSubkey);
                if (string.IsNullOrEmpty(productCode)) continue;

                string? installPath = productKey.GetValue("AcadLocation") as string;
                if (string.IsNullOrEmpty(installPath)) continue;

                string exePath = Path.Combine(installPath, "acad.exe");
                if (!File.Exists(exePath)) continue;

                string productName = (productKey.GetValue("ProductName") as string)
                    ?? string.Empty;
                if (string.IsNullOrEmpty(productName)) productName = "AutoCAD";

                var flavor = FlavorTable.FlavorForProductName(productName);
                var cmdLineArg = FlavorTable.CmdLineArgForFlavor(flavor);

                sink.Add(new AcadInstall(
                    Flavor: flavor,
                    ProductName: productName,
                    ReleaseKey: releaseName,
                    ProductCode: productCode,
                    InstallPath: installPath,
                    ExePath: exePath,
                    ProductCmdLineArg: cmdLineArg));
            }
        }
    }

    private static string ExtractProductCode(string registrySubkey)
    {
        const string prefix = "ACAD-";
        if (!registrySubkey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        int dash = prefix.Length;
        int colon = registrySubkey.IndexOf(':', dash);
        if (colon < 0) colon = registrySubkey.Length;
        return registrySubkey.Substring(dash, colon - dash);
    }
}

internal static class FlavorTable
{
    /// <summary>
    /// Detect flavor from the registry's <c>ProductName</c> string.
    /// Robust across releases — Autodesk renumbers product codes each
    /// major release (e.g. 7100→8100 for Civil 3D) but the human
    /// product-name text is stable.
    /// </summary>
    public static AcadFlavor FlavorForProductName(string? productName)
    {
        if (string.IsNullOrEmpty(productName)) return AcadFlavor.Unknown;
        var name = productName.ToLowerInvariant();
        if (name.Contains("civil 3d")) return AcadFlavor.Civil3D;
        if (name.Contains("plant 3d")) return AcadFlavor.Plant3D;
        if (name.Contains("mechanical")) return AcadFlavor.Mechanical;
        if (name.Contains("electrical")) return AcadFlavor.Electrical;
        if (name.Contains("map 3d")) return AcadFlavor.Map3D;
        if (name.Contains("architecture")) return AcadFlavor.Architecture;
        if (name.Contains("autocad")) return AcadFlavor.AutoCAD;
        return AcadFlavor.Unknown;
    }

    public static string CmdLineArgForFlavor(AcadFlavor flavor) => flavor switch
    {
        AcadFlavor.AutoCAD => string.Empty,
        AcadFlavor.Civil3D => "/product C3D",
        AcadFlavor.Plant3D => "/product P3D",
        AcadFlavor.Mechanical => "/product MCD",
        AcadFlavor.Electrical => "/product EST",
        AcadFlavor.Map3D => "/product MAP",
        AcadFlavor.Architecture => "/product ARCHDESK",
        _ => string.Empty,
    };
}
