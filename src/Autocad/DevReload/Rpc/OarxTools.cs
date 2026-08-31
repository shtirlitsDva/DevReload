using System.Collections.Generic;
using System.ComponentModel;

using Acad.Rpc.Core;

using DevReload.Oarx;

namespace DevReload.Rpc
{
    /// <summary>
    /// The MCP tool surface for OARX plugin groups: the native-module sibling of
    /// <see cref="DevReloadTools"/>.
    /// </summary>
    /// <remarks>
    /// A separate surface rather than extra tools on the devreload one, for the
    /// same reason <c>OarxManager</c> is a sibling of <c>PluginManager</c>: the two
    /// lifecycles share a shape and nothing else. A group is an ORDERED list of
    /// native modules built from one solution, and because a loaded module locks
    /// its own file the order is unload -> build -> load — so a FAILED BUILD LEAVES
    /// THE GROUP UNLOADED (research F14), where the .NET path would have kept the
    /// old plugin running. Merging the surfaces would invite an agent to read a
    /// native failure with .NET expectations.
    ///
    /// <para>Every lifecycle tool is <see cref="RunOnAcadMainThreadAttribute"/>.
    /// Mapping and unmapping a native module is a main-thread operation, and the
    /// cycle drives a transient-graphics HUD — so unlike <c>devreload_build_project</c>
    /// there is no off-thread variant. A reload therefore blocks the caller for the
    /// length of the compile.</para>
    ///
    /// <para>No try/catch-to-string: if a wrapper throws, the host turns it into an
    /// MCP error response with the type and message preserved.</para>
    /// </remarks>
    [AcadRpcSurface(Group = "oarx")]
    public static class OarxTools
    {
        // ── Query ────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("List every registered OARX group with its live state and its modules in LOAD order. The single source of truth for OARX registration + load status. 'loaded' is the state of the group as a WHOLE; 'partiallyLoaded' means a previous cycle died part-way and the group is neither loaded nor clean. A module's targetPath/moduleFileName are null until MSBuild has been asked where it lands.")]
        public static IReadOnlyList<OarxPluginInfo> ListPlugins() =>
            OarxManager.ListSnapshots();

        // ── Lifecycle ────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("The OARX dev loop: unload the whole group, prove every module output is writable, rebuild from source, load again in order. Equivalent to the generated {PREFIX}DEV command. BLOCKS for the length of the compile. If the build fails the group is left UNLOADED (a loaded module locks its file, so it must come out before the linker can write it) and the response carries the build log.")]
        public static OarxActionResult Reload(
            [Description("Registered OARX group name as in plugins.json (e.g. \"VectorArx\")")] string name) =>
            OarxManager.Reload(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Load the group as it currently sits on disk, in registration order, building only the modules whose output is missing. Equivalent to the generated {PREFIX}LOAD command. No-op if the group is already fully loaded.")]
        public static OarxActionResult LoadPlugin(
            [Description("Registered OARX group name")] string name) =>
            OarxManager.Load(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Unload every module in the group, walking the registration order BACKWARDS (the .arx comes out before the .dbx whose classes it uses). Equivalent to the generated {PREFIX}UNLOAD command. No-op if nothing is loaded.")]
        public static OarxActionResult UnloadPlugin(
            [Description("Registered OARX group name")] string name) =>
            OarxManager.Unload(name);

        // ── Registration ─────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Register a new OARX group and persist it to plugins.json. projectFilePaths is ORDERED and the order is load order: a .dbx owning custom classes must come before the .arx that uses them. solutionFilePath is required and is not inferred — MSBuild resolves a C++ project's output through $(SolutionDir), and evaluating a .vcxproj standalone silently points TargetPath at a directory the solution build never writes to. After this call the group is available via this surface's load_plugin/reload tools and the generated {PREFIX}LOAD/DEV/UNLOAD AutoCAD commands.")]
        public static RegisterOarxResult RegisterNewPlugin(
            [Description("Absolute path to the .sln the modules build under")] string solutionFilePath,
            [Description("Absolute paths to the module .vcxproj files, in LOAD order (.dbx before .arx)")] string[] projectFilePaths,
            [Description("Any configuration the solution declares (default 'Debug')")] string buildConfiguration = "Debug",
            [Description("Group name. Defaults to the LAST module project's file name, which for a dbx+arx pair is the .arx — what the user calls the plugin.")] string? name = null,
            [Description("Optional command prefix for the generated {prefix}LOAD/DEV/UNLOAD commands. Defaults to the group name.")] string? commandPrefix = null,
            [Description("Auto-load at AutoCAD startup")] bool loadOnStartup = false,
            [Description("Extra 'Name=Value' MSBuild properties applied to this group's builds AND its TargetPath queries (e.g. a repo's fast-dev-loop switch).")] string[]? msbuildProperties = null,
            [Description("Native DLLs mapped by FULL PATH before the modules load, so later base-name references bind to these canonical copies (a shared logging hub). Never unloaded; a same-named module already mapped from elsewhere is warned about loudly.")] string[]? preloadNativeModules = null,
            [Description("Managed assemblies loaded (NETLOAD-equivalent, default ALC) BEFORE the modules — e.g. a trace UI that must be listening while a dbx logs during load. Never unloaded; idempotent per assembly.")] string[]? preloadManagedAssemblies = null,
            [Description("Managed assemblies loaded AFTER the modules — e.g. a mixed-mode interop that statically imports the group's dbx. Never unloaded, so such an interop PINS the dbx: the group loads fine but stops being reloadable once the postload has run.")] string[]? postloadManagedAssemblies = null) =>
            OarxConfigLoader.RegisterNewPlugin(
                solutionFilePath, projectFilePaths, buildConfiguration,
                name, commandPrefix, loadOnStartup,
                msbuildProperties, preloadNativeModules,
                preloadManagedAssemblies, postloadManagedAssemblies);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Patch an existing OARX group in plugins.json AND live — the edit counterpart of register_new_plugin, so companions or properties can change without an unregister/re-register round-trip. Every parameter except name is optional: omitted = keep the current value, an EMPTY array = clear. The group's name and solution are its identity and cannot be patched (rename = unregister + register). No unload is needed: msbuildProperties apply at the group's next BUILD and the companion lists at its next LOAD. The one exception is projectFilePaths on a group whose modules are mapped — that change is STAGED and applied automatically at the next load/reload (the response says so, and list_plugins shows configPending until then).")]
        public static OarxActionResult UpdatePlugin(
            [Description("Registered OARX group name as in plugins.json")] string name,
            [Description("New command prefix for the generated {prefix}LOAD/DEV/UNLOAD commands; the old commands are replaced immediately")] string? commandPrefix = null,
            [Description("Auto-load at AutoCAD startup")] bool? loadOnStartup = null,
            [Description("Any configuration the solution declares")] string? buildConfiguration = null,
            [Description("Replacement module .vcxproj list, in LOAD order (.dbx before .arx). Staged if the group is currently loaded.")] string[]? projectFilePaths = null,
            [Description("Replacement 'Name=Value' MSBuild property list")] string[]? msbuildProperties = null,
            [Description("Replacement list of native DLLs pinned by full path before the modules load")] string[]? preloadNativeModules = null,
            [Description("Replacement list of managed assemblies loaded before the modules")] string[]? preloadManagedAssemblies = null,
            [Description("Replacement list of managed assemblies loaded after the modules (an interop here PINS its dbx — the group stops being reloadable once it has run)")] string[]? postloadManagedAssemblies = null) =>
            OarxConfigLoader.UpdatePlugin(name, new OarxPluginPatch(
                commandPrefix, loadOnStartup, buildConfiguration, projectFilePaths,
                msbuildProperties, preloadNativeModules,
                preloadManagedAssemblies, postloadManagedAssemblies));

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Remove an OARX group from the live registry AND from plugins.json. Does NOT unload it first — call unload_plugin before this if the modules are loaded, otherwise they stay mapped with no registration to manage them.")]
        public static OarxActionResult Unregister(
            [Description("Registered OARX group name")] string name) =>
            OarxConfigLoader.Unregister(name);
    }
}
