using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace DevReload.Core
{
    // Discovers assemblies a csproj references from OUTSIDE its own build dir — i.e.
    // <Reference> items with a <HintPath> pointing elsewhere (e.g. a central deploy
    // folder like Appload). These are candidates the shared-ALC loader can load from
    // their referenced location, so a plugin need not keep a private copy.
    //
    // XML parse, NOT MSBuild evaluation: it needs no restore and works even when the
    // project can't evaluate (broken Import, un-restored worktree). HintPaths that are
    // literal paths — the only kind that resolve to a real external dir — parse fine.
    // Namespace-agnostic (LocalName) so SDK-style (no xmlns) and old-style (2003
    // xmlns) csprojs both work.
    public static class CsprojReferenceScanner
    {
        // One referenced assembly that lives outside the build dir.
        // Name is the DLL simple name (matches the SharedAssemblies list + the
        // Directory.GetFiles(pluginDir,"*.dll") naming the dialog already uses).
        public readonly record struct ExternalReference(string Name, string Directory);

        // External references for a csproj, keyed off <HintPath>. Excludes references
        // whose HintPath resolves into buildDir (those are effectively local) and any
        // whose target file does not exist. Returns empty on any parse failure —
        // discovery is best-effort and must never break the dialog.
        public static IReadOnlyList<ExternalReference> ScanExternalReferences(
            string csprojPath, string buildDir)
        {
            var result = new List<ExternalReference>();
            if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
                return result;

            string projectDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            string normBuildDir = NormalizeDir(buildDir);

            XDocument doc;
            try { doc = XDocument.Load(csprojPath); }
            catch { return result; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reference in doc.Descendants()
                         .Where(e => e.Name.LocalName == "Reference"))
            {
                var hintPathElem = reference.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "HintPath");
                string? hintPath = hintPathElem?.Value?.Trim();
                if (string.IsNullOrEmpty(hintPath)) continue;

                // Resolve relative HintPaths against the csproj directory.
                string fullPath = Path.GetFullPath(
                    Path.IsPathRooted(hintPath)
                        ? hintPath
                        : Path.Combine(projectDir, hintPath));

                if (!File.Exists(fullPath)) continue;

                string dir = NormalizeDir(Path.GetDirectoryName(fullPath)!);
                // Skip references that live in the build dir — those are local DLLs the
                // dialog already lists via Directory.GetFiles.
                if (string.Equals(dir, normBuildDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileNameWithoutExtension(fullPath);
                if (name.Length == 0 || !seen.Add(name)) continue;

                result.Add(new ExternalReference(name, dir));
            }

            return result;
        }

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            string full = Path.GetFullPath(dir);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
