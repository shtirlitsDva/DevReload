using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevReload.Oarx
{
    /// <summary>The three ObjectARX module flavours DevReload can host.</summary>
    public enum OarxModuleKind
    {
        /// <summary>ObjectDBX — custom objects/classes. Loads first, unloads last.</summary>
        Dbx,
        /// <summary>ObjectARX — commands and UI. Loads last, unloads first.</summary>
        Arx,
        /// <summary>accoreconsole module.</summary>
        Crx,
    }

    /// <summary>
    /// One native module inside an OARX plugin group: the project that produces
    /// it and, once MSBuild has been asked, where it lands.
    /// </summary>
    internal sealed class OarxModule
    {
        public required string ProjectFilePath { get; init; }

        /// <summary>Full path to the built module. Null until resolved — there is
        /// no guessed default, because a wrong output directory is the exact
        /// wrong-but-plausible failure this design refuses to have (research F7).</summary>
        public string? TargetPath { get; set; }

        public string ProjectName => Path.GetFileNameWithoutExtension(ProjectFilePath);

        /// <summary>The name the dynamic linker knows this module by: file name
        /// with extension (research F6). Null until <see cref="TargetPath"/> is
        /// resolved.</summary>
        public string? ModuleFileName =>
            TargetPath == null ? null : Path.GetFileName(TargetPath);

        public OarxModuleKind? Kind =>
            TargetPath == null ? null : KindOf(TargetPath);

        public bool IsLoaded =>
            ModuleFileName != null && OarxModuleHost.IsLoaded(ModuleFileName);

        /// <summary>Classify by output extension. Throws rather than guessing:
        /// a project whose TargetExt is not an ObjectARX one is a registration
        /// mistake the user needs to see.</summary>
        public static OarxModuleKind KindOf(string targetPath)
        {
            string ext = Path.GetExtension(targetPath);
            if (ext.Equals(".arx", StringComparison.OrdinalIgnoreCase)) return OarxModuleKind.Arx;
            if (ext.Equals(".dbx", StringComparison.OrdinalIgnoreCase)) return OarxModuleKind.Dbx;
            if (ext.Equals(".crx", StringComparison.OrdinalIgnoreCase)) return OarxModuleKind.Crx;
            throw new OarxModuleException(
                $"'{Path.GetFileName(targetPath)}' is not an ObjectARX module " +
                "(expected .arx, .dbx or .crx). Check the project's ArxAppType/TargetExt.");
        }
    }

    /// <summary>
    /// An OARX plugin: an ORDERED set of native modules built from one solution.
    /// </summary>
    /// <remarks>
    /// Order is the whole reason this is a list rather than a single project.
    /// ObjectARX imposes it — the .dbx owning the custom classes must load before
    /// the .arx that uses them, and must unload after it. The list is in LOAD
    /// order; unload walks it backwards.
    /// </remarks>
    internal sealed class OarxRegistration
    {
        public required string Name { get; init; }

        /// <summary>The solution the modules build under. Not optional and not
        /// inferred: MSBuild resolves a C++ project's output directory through
        /// $(SolutionDir), and evaluating a .vcxproj standalone silently points
        /// TargetPath at a directory the solution build never writes (research F7).</summary>
        public required string SolutionFilePath { get; init; }

        /// <summary>Modules in LOAD order.</summary>
        public required List<OarxModule> Modules { get; init; }

        public string BuildConfiguration { get; set; } = "Debug";
        public string? ActiveWorktreePath { get; set; }

        /// <summary>Extra "Name=Value" MSBuild properties for this group's builds
        /// and property queries (e.g. a repo's fast-dev-loop switch).</summary>
        public List<string> MsBuildProperties { get; init; } = new();

        /// <summary>Native DLLs mapped by FULL PATH before the modules load, so
        /// later base-name references bind to these copies. Never unloaded.</summary>
        public List<string> PreloadNativeModules { get; init; } = new();

        /// <summary>Managed assemblies loaded (NETLOAD-equivalent, default ALC)
        /// BEFORE the modules — e.g. a trace UI that must be listening while a
        /// dbx logs during load. Never unloaded.</summary>
        public List<string> PreloadManagedAssemblies { get; init; } = new();

        /// <summary>Managed assemblies loaded AFTER the modules — e.g. a
        /// mixed-mode interop that statically imports the dbx it wraps and must
        /// not be the thing that maps it. Never unloaded, which also means a
        /// postloaded interop PINS its dbx: a group with one loads fine but can
        /// only be reloaded before the first postload has run.</summary>
        public List<string> PostloadManagedAssemblies { get; init; } = new();

        public List<(string Group, string Name, Autodesk.AutoCAD.Internal.CommandCallback Callback)>
            LoaderCommands { get; } = new();

        /// <summary>The plugins.json entry this registration was built from.
        /// Kept so a config resync can tell an EDITED entry from an unchanged
        /// one — diffing by name alone is how hand-edited companions used to be
        /// silently ignored until restart.</summary>
        public required OarxPluginEntry Source { get; set; }

        /// <summary>A config edit that arrived while the group was LOADED. A
        /// loaded group's registration is never yanked out from under its mapped
        /// modules; the pending entry is applied at the next Load/Reload, when
        /// the modules are out anyway.</summary>
        public OarxPluginEntry? PendingEntry { get; set; }

        /// <summary>The solution directory MSBuild must be told about, worktree-aware.</summary>
        public string SolutionDirectory =>
            Path.GetDirectoryName(EffectiveSolutionPath)!;

        /// <summary>The solution path for the currently selected worktree.</summary>
        public string EffectiveSolutionPath =>
            DevReload.Core.GitWorktreeService.ResolveActiveCsproj(
                SolutionFilePath, ActiveWorktreePath);

        /// <summary>The project path for a module in the currently selected worktree.</summary>
        public string EffectiveProjectPath(OarxModule module) =>
            DevReload.Core.GitWorktreeService.ResolveActiveCsproj(
                module.ProjectFilePath, ActiveWorktreePath);

        /// <summary>True when every module that has been resolved is loaded.
        /// A group is "loaded" only as a whole — a half-loaded group is the
        /// state a failed cycle leaves behind and must not read as loaded.</summary>
        public bool IsLoaded =>
            Modules.Count > 0 && Modules.All(m => m.IsLoaded);

        public bool IsPartiallyLoaded =>
            Modules.Any(m => m.IsLoaded) && !IsLoaded;
    }
}
