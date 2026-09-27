using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevReload.Oarx
{
    /// <summary>
    /// A profile's build folder: the folder DevReload builds a group INTO and
    /// loads its modules FROM. Pure path logic, no AutoCAD, so it is tested
    /// directly.
    /// </summary>
    /// <remarks>
    /// The folder reaches MSBuild as one property, <see cref="Property"/>, and
    /// the repo's MSBuild files route OutDir/IntDir from it. DevReload cannot
    /// force a project to honour it, so it checks the answer instead: a module
    /// that resolves or lands outside the folder is refused, loudly, rather
    /// than loaded from wherever the project happened to put it.
    /// </remarks>
    public static class OarxBuildFolder
    {
        /// <summary>The MSBuild property the folder is passed through.</summary>
        public const string Property = "DevReloadBuildFolder";

        /// <summary>The properties every build and TargetPath query passes: the
        /// profile's own plus the folder. One list for both, because resolving
        /// with one set and building with another is the wrong-but-plausible
        /// split DevReload refuses.</summary>
        public static IReadOnlyList<string> EffectiveProperties(
            IReadOnlyList<string> properties, string? folder) =>
            folder == null ? properties : properties.Append($"{Property}={folder}").ToList();

        /// <summary>True when the list sets <see cref="Property"/> by hand.</summary>
        public static bool IsSetByHand(IEnumerable<string> properties) =>
            properties.Any(p => p.Split('=')[0].Trim().Equals(
                Property, StringComparison.OrdinalIgnoreCase));

        /// <summary>Null when <paramref name="targetPath"/> is inside
        /// <paramref name="folder"/> (or there is no folder), otherwise why the
        /// module is refused.</summary>
        public static string? CheckInside(string? folder, string projectName, string targetPath)
        {
            if (folder == null) return null;
            string root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(targetPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;
            return $"'{projectName}' lands at {targetPath}, outside the profile's build folder " +
                   $"{folder}. The project does not honour {Property}: route its OutDir/IntDir " +
                   "from that property, or clear the profile's buildFolder.";
        }
    }
}
