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
            [Description("Auto-load at AutoCAD startup")] bool loadOnStartup = false) =>
            OarxConfigLoader.RegisterNewPlugin(
                solutionFilePath, projectFilePaths, buildConfiguration,
                name, commandPrefix, loadOnStartup);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Remove an OARX group from the live registry AND from plugins.json. Does NOT unload it first — call unload_plugin before this if the modules are loaded, otherwise they stay mapped with no registration to manage them.")]
        public static OarxActionResult Unregister(
            [Description("Registered OARX group name")] string name) =>
            OarxConfigLoader.Unregister(name);
    }
}
