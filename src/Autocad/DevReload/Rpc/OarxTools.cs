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
    /// <para>What a group builds, and from which folder, is a PROFILE. An agent
    /// working in its own worktree publishes a profile for that folder (usually
    /// copied from an existing one, plus its new modules) so the user can pick it
    /// in the palette without setting anything up by hand.</para>
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
         Description("List every registered OARX group with its live state, its modules in LOAD order, and its profiles. 'loaded' is the state of the group as a WHOLE; 'partiallyLoaded' means a previous cycle died part-way. 'modules' and 'liveProfile' are what is registered live; 'profiles' and 'activeProfile' are the saved configuration — they differ while 'configPending' is true (a change staged against a loaded group, applied at its next load/reload). A profile's projectFilePaths are relative to its worktreePath; folderExists=false means the worktree was removed. A module's targetPath/moduleFileName are null until MSBuild has been asked where it lands.")]
        public static IReadOnlyList<OarxPluginInfo> ListPlugins() =>
            OarxManager.ListSnapshots();

        // ── Lifecycle ────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("The OARX dev loop: unload the whole group, prove every module output is writable, rebuild from the ACTIVE profile's folder, load again in order. Equivalent to the generated {PREFIX}DEV command. BLOCKS for the length of the compile. If the build fails the group is left UNLOADED (a loaded module locks its file, so it must come out before the linker can write it) and the response carries the build log.")]
        public static OarxActionResult Reload(
            [Description("Registered OARX group name as in plugins.json (e.g. \"NdhPipeline\")")] string name) =>
            OarxManager.Reload(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Load the group from its ACTIVE profile as it currently sits on disk, in registration order, building only the modules whose output is missing. Equivalent to the generated {PREFIX}LOAD command. No-op if the group is already fully loaded.")]
        public static OarxActionResult LoadPlugin(
            [Description("Registered OARX group name")] string name) =>
            OarxManager.Load(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Unload every module in the group, walking the registration order BACKWARDS (the .arx comes out before the .dbx whose classes it uses). Equivalent to the generated {PREFIX}UNLOAD command. No-op if nothing is loaded.")]
        public static OarxActionResult UnloadPlugin(
            [Description("Registered OARX group name")] string name) =>
            OarxManager.Unload(name);

        // ── Group ────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Register a new OARX group and persist it to plugins.json, with ONE profile for the worktree (or clone) folder the solution sits in — git's top level for the solution's directory; the profile is named after that folder and is active. projectFilePaths is ORDERED and the order is load order: a .dbx owning custom classes must come before the .arx that uses them; every project must be inside that folder. solutionFilePath is required and is not inferred — MSBuild resolves a C++ project's output through $(SolutionDir). After this call the group is available via load_plugin/reload and the generated {PREFIX}LOAD/DEV/UNLOAD commands. To add another worktree later, use publish_profile — do NOT register a second group for it.")]
        public static RegisterOarxResult RegisterNewPlugin(
            [Description("Absolute path to the .sln the modules build under")] string solutionFilePath,
            [Description("Absolute paths to the module .vcxproj files, in LOAD order (.dbx before .arx)")] string[] projectFilePaths,
            [Description("Any configuration the solution declares (default 'Debug'). Applies to every profile of the group.")] string buildConfiguration = "Debug",
            [Description("Group name. Defaults to the LAST module project's file name, which for a dbx+arx pair is the .arx — what the user calls the plugin.")] string? name = null,
            [Description("Optional command prefix for the generated {prefix}LOAD/DEV/UNLOAD commands. Defaults to the group name.")] string? commandPrefix = null,
            [Description("Auto-load at AutoCAD startup")] bool loadOnStartup = false,
            [Description("Extra 'Name=Value' MSBuild properties applied to the first profile's builds AND its TargetPath queries (e.g. a repo's fast-dev-loop switch).")] string[]? msbuildProperties = null,
            [Description("Native DLLs mapped by FULL PATH before the modules load, so later base-name references bind to these canonical copies (a shared logging hub). Absolute, or relative to the profile's folder. Never unloaded.")] string[]? preloadNativeModules = null,
            [Description("Managed assemblies loaded (NETLOAD-equivalent, default ALC) BEFORE the modules — e.g. a trace UI that must be listening while a dbx logs during load. Absolute, or relative to the profile's folder. Never unloaded; idempotent per assembly.")] string[]? preloadManagedAssemblies = null,
            [Description("Managed assemblies loaded AFTER the modules — e.g. a mixed-mode interop that statically imports the group's dbx. Absolute, or relative to the profile's folder. Never unloaded, so such an interop PINS the dbx: the group loads fine but stops being reloadable once the postload has run.")] string[]? postloadManagedAssemblies = null) =>
            OarxConfigLoader.RegisterNewPlugin(
                solutionFilePath, projectFilePaths, buildConfiguration,
                name, commandPrefix, loadOnStartup,
                msbuildProperties, preloadNativeModules,
                preloadManagedAssemblies, postloadManagedAssemblies);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Patch an OARX group's OWN fields in plugins.json AND live. Every parameter except name is optional (omitted = keep). The group's name and solution are its identity and cannot be patched. Modules, MSBuild properties and companions belong to PROFILES — change those with publish_profile. No unload is needed: the configuration applies at the next build, a new prefix replaces the commands immediately.")]
        public static OarxActionResult UpdatePlugin(
            [Description("Registered OARX group name as in plugins.json")] string name,
            [Description("New command prefix for the generated {prefix}LOAD/DEV/UNLOAD commands; the old commands are replaced immediately")] string? commandPrefix = null,
            [Description("Auto-load at AutoCAD startup")] bool? loadOnStartup = null,
            [Description("Any configuration the solution declares. Applies to every profile.")] string? buildConfiguration = null) =>
            OarxConfigLoader.UpdatePlugin(name, new OarxGroupPatch(
                commandPrefix, loadOnStartup, buildConfiguration));

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Remove an OARX group (all its profiles) from the live registry AND from plugins.json, unloading its modules first.")]
        public static OarxActionResult Unregister(
            [Description("Registered OARX group name")] string name) =>
            OarxConfigLoader.Unregister(name);

        // ── Profiles ─────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Create or update a PROFILE of an OARX group: which folder the group builds from and what it builds and loads there. The usual agent call: after adding or changing module projects in your own git worktree, publish a profile for that worktree, copied from an existing profile, so the user can pick it in the palette's dropdown. A profile names a FOLDER, never a branch. Upsert by profile name (default: the folder's name): creating one takes copyFrom (or starts empty and then needs projectFilePaths); on an existing one copyFrom is refused, and each list you pass REPLACES that list (omitted = keep, [] = clear). Validated before saving: the group's solution and every module must exist in the folder. Does NOT activate unless activate=true — activation changes what the USER's AutoCAD builds next, so only activate when the user asked you to test in AutoCAD.")]
        public static OarxActionResult PublishProfile(
            [Description("Registered OARX group name")] string name,
            [Description("Absolute path of the worktree/clone folder this profile builds from — normally your own worktree")] string worktreePath,
            [Description("Profile name. Defaults to the folder's name.")] string? profile = null,
            [Description("Name of an existing profile to copy modules, properties and companions from. Only when creating.")] string? copyFrom = null,
            [Description("Module .vcxproj files in LOAD order (.dbx before .arx): relative to the folder, or absolute paths inside it (stored relative). Replaces the list.")] string[]? projectFilePaths = null,
            [Description("'Name=Value' MSBuild properties. Replaces the list.")] string[]? msbuildProperties = null,
            [Description("Native DLLs pinned by full path before the modules load: absolute, or relative to the folder. Replaces the list.")] string[]? preloadNativeModules = null,
            [Description("Managed assemblies loaded before the modules: absolute, or relative to the folder. Replaces the list.")] string[]? preloadManagedAssemblies = null,
            [Description("Managed assemblies loaded after the modules: absolute, or relative to the folder (an interop here PINS its dbx). Replaces the list.")] string[]? postloadManagedAssemblies = null,
            [Description("Also make this the group's active profile. Default false. If the group is loaded, the switch applies at its next load/reload.")] bool activate = false) =>
            OarxConfigLoader.PublishProfile(new OarxProfilePublish(
                name, worktreePath, profile, copyFrom, projectFilePaths,
                msbuildProperties, preloadNativeModules,
                preloadManagedAssemblies, postloadManagedAssemblies, activate));

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Make a profile the group's active one — what Load/Reload and the {PREFIX} commands build and load from. Refused if the profile's folder is gone. If the group is loaded and the profile's modules differ, the switch is STAGED and applied at the next load/reload (list_plugins shows configPending until then). Only do this when the user asked for it.")]
        public static OarxActionResult ActivateProfile(
            [Description("Registered OARX group name")] string name,
            [Description("Profile name")] string profile) =>
            OarxConfigLoader.ActivateProfile(name, profile);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Delete a profile from an OARX group. The active profile cannot be deleted — activate another one first. Use this to clean up after removing your worktree.")]
        public static OarxActionResult DeleteProfile(
            [Description("Registered OARX group name")] string name,
            [Description("Profile name")] string profile) =>
            OarxConfigLoader.DeleteProfile(name, profile);
    }
}
