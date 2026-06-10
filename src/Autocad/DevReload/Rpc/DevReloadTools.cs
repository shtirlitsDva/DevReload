using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Acad.Rpc.Core;

namespace DevReload.Rpc
{
    /// <summary>
    /// The MCP tool surface DevReload contributes to the in-AutoCAD RPC
    /// host. Each method here is a thin wrapper that converts the
    /// internal lifecycle API into structured records the agent can
    /// reason about. No try/catch-to-string: if a wrapper throws, the
    /// host turns it into an MCP error response with the type and
    /// message preserved.
    /// </summary>
    [AcadRpcSurface(Group = "devreload")]
    public static class DevReloadTools
    {
        // ── Lifecycle ────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Build the plugin from source and load (or reload) it into a fresh isolated ALC. Equivalent to clicking 'Reload' in the palette. If the build fails, the response carries the full build log under 'build'.")]
        public static PluginActionResult Reload(
            [Description("Registered plugin name as in plugins.json (e.g. \"MyPlugin\")")] string name) =>
            PluginManager.DevReload(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Load a registered plugin if not already loaded. If a build is triggered (missing DLL) the full build log is returned under 'build'.")]
        public static PluginActionResult LoadPlugin(
            [Description("Registered plugin name")] string name) =>
            PluginManager.Load(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Unload a loaded plugin (tear down its isolated ALC). No-op if not loaded.")]
        public static PluginActionResult UnloadPlugin(
            [Description("Registered plugin name")] string name) =>
            PluginManager.Unload(name);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Unload every loaded plugin. Used during shutdown or to recover from a wedged state.")]
        public static UnloadAllResult UnloadAll() => PluginManager.UnloadAll();

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Remove a plugin from the registry AND from plugins.json. Unloads it first if loaded. Durable across AutoCAD restarts.")]
        public static PluginActionResult Unregister(
            [Description("Registered plugin name")] string name) =>
            PluginManager.Unregister(name);

        // ── Query ────────────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("List every registered plugin with its current state. The single source of truth for registration + load status; filter the returned list rather than asking with three separate query tools.")]
        public static IReadOnlyList<PluginInfo> ListPlugins() =>
            PluginManager.ListPluginSnapshots();

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Inspect the loaded assembly behind a plugin: name, version, file location, last-write timestamp.")]
        public static PluginAssemblyInfo GetAssemblyInfo(
            [Description("Registered plugin name")] string name) =>
            PluginManager.GetAssemblyInfo(name);

        [AcadRpcTool,
         Description("List every MCP tool currently registered with the Acad.Rpc host, grouped by source assembly.")]
        public static IReadOnlyList<ToolListEntry> ListTools() =>
            AcadRpcHost.Current.ListRegisteredTools()
                .OrderBy(t => t.SourceAssembly)
                .ThenBy(t => t.ToolName)
                .Select(t => new ToolListEntry(t.SourceAssembly, t.ToolName, t.Description))
                .ToList();

        // ── Configuration ───────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Switch the build configuration (Debug / Release) used for the next build of this plugin. Persists to plugins.json via the same path the palette UI uses.")]
        public static PluginActionResult UpdateBuildConfiguration(
            [Description("Registered plugin name")] string name,
            [Description("Configuration name, typically 'Debug' or 'Release'")] string buildConfiguration) =>
            PluginManager.UpdateBuildConfiguration(name, buildConfiguration);

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Point this plugin at a git worktree path (or null to clear). Persists to plugins.json via the same path the palette UI uses.")]
        public static PluginActionResult UpdateActiveWorktree(
            [Description("Registered plugin name")] string name,
            [Description("Absolute worktree path, or null to use the main checkout")] string? worktreePath) =>
            PluginManager.UpdateActiveWorktree(name, worktreePath);

        // ── Build ────────────────────────────────────────────────────

        // Build runs `dotnet build` and blocks for seconds — keep it off
        // the AutoCAD main thread so the UI doesn't freeze. The build
        // process has no AutoCAD dependencies anyway.
        [AcadRpcTool,
         Description("Invoke `dotnet build` for a csproj and return the structured result with full log.")]
        public static BuildResult BuildProject(
            [Description("Absolute path to the .csproj")] string csprojPath,
            [Description("Configuration: 'Debug' or 'Release'")] string buildConfiguration) =>
            DevReloadService.BuildProject(csprojPath, buildConfiguration, ed: null);

        // ── Worktree ─────────────────────────────────────────────────

        [AcadRpcTool,
         Description("List git worktrees rooted at the given repo path, with branch names. Each entry is { path, branch, isMain }.")]
        public static IReadOnlyList<WorktreeInfo> ListWorktrees(
            [Description("Absolute path to the repo root (any path inside the repo also works)")] string repoRoot) =>
            GitWorktreeService.ListWorktrees(repoRoot);

        // ── Shared assemblies ────────────────────────────────────────

        [AcadRpcTool,
         Description("Read SharedAssemblies.Config.json from a build directory. Returns shared/mixed-mode/streamed lists.")]
        public static SharedAssembliesConfig ReadSharedAssemblies(
            [Description("Build directory containing SharedAssemblies.Config.json")] string buildDir)
        {
            var cfg = SharedAssembliesFile.Read(buildDir);
            return new SharedAssembliesConfig(
                cfg.SharedAssemblies,
                cfg.MixedModeAssemblies,
                cfg.StreamedAssemblies);
        }

        [AcadRpcTool,
         Description("Write SharedAssemblies.Config.json into a build directory. Overwrites any existing file.")]
        public static SharedAssembliesConfig WriteSharedAssemblies(
            [Description("Build directory in which to write SharedAssemblies.Config.json")] string buildDir,
            [Description("Assembly simple names to load into the default ALC (LoadFrom)")] string[] sharedAssemblies,
            [Description("Subset of sharedAssemblies that are mixed-mode (C++/CLI). Auto-generates runtimeconfig.json.")] string[] mixedModeAssemblies,
            [Description("Subset of sharedAssemblies to stream-load (Default.LoadFromStream) so the file isn't locked.")] string[] streamedAssemblies)
        {
            SharedAssembliesFile.Write(
                buildDir, sharedAssemblies, mixedModeAssemblies, streamedAssemblies);
            return new SharedAssembliesConfig(
                sharedAssemblies, mixedModeAssemblies, streamedAssemblies);
        }

        // ── Registration ─────────────────────────────────────────────

        [AcadRpcTool, RunOnAcadMainThread,
         Description("Register a new plugin with DevReload and persist it to plugins.json. The plugin name is the csproj filename (renaming is not supported); dllPath is resolved via MSBuild's TargetPath for the requested configuration, so the project must have been restored/built at least once. After this call the plugin is available for load via this surface's load_plugin tool or the generated AutoCAD {prefix}LOAD command.")]
        public static RegisterPluginResult RegisterNewPlugin(
            [Description("Absolute path to the .csproj. The plugin name is the file name without extension; renaming is not supported.")] string projectFilePath,
            [Description("'Debug' or 'Release' (default Debug). Determines which TargetPath MSBuild resolves for the auto-derived dllPath.")] string buildConfiguration = "Debug",
            [Description("Optional command prefix for the generated {prefix}LOAD/DEV/UNLOAD commands")] string? commandPrefix = null,
            [Description("Auto-load at AutoCAD startup")] bool loadOnStartup = false) =>
            PluginConfigLoader.RegisterNewPlugin(
                projectFilePath, buildConfiguration, commandPrefix, loadOnStartup);

    }
}
