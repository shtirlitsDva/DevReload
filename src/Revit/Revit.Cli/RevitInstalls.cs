using System;
using System.Collections.Generic;
using System.IO;

namespace Revit.Cli
{
    public sealed record RevitInstall(int Year, string InstallDir, string ExePath);

    public static class RevitInstalls
    {
        // Probe the conventional install roots. Registry-free on purpose:
        // every supported install (2022-2025 on this machine) lives at the
        // default path, and a probe can't go stale the way registry keys do.
        public static IReadOnlyList<RevitInstall> Discover()
        {
            var result = new List<RevitInstall>();
            for (int year = 2022; year <= 2030; year++)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Autodesk", $"Revit {year}");
                string exe = Path.Combine(dir, "Revit.exe");
                if (File.Exists(exe))
                    result.Add(new RevitInstall(year, dir, exe));
            }
            return result;
        }

        public static RevitInstall Require(int year)
        {
            foreach (var install in Discover())
                if (install.Year == year)
                    return install;
            throw new InvalidOperationException(
                $"Revit {year} not found under Program Files\\Autodesk.");
        }
    }
}
