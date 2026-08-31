using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevReload.Oarx
{
    /// <summary>
    /// One OARX plugin as it is persisted in plugins.json.
    /// </summary>
    public class OarxPluginEntry
    {
        public string Name { get; set; } = "";

        /// <summary>The .sln the modules build under. Required — MSBuild resolves
        /// a C++ project's output through $(SolutionDir), and getting it wrong
        /// silently points at a directory the build never writes to.</summary>
        public string SolutionFilePath { get; set; } = "";

        /// <summary>Module projects in LOAD order (.dbx before the .arx that uses
        /// it). Unload walks this backwards.</summary>
        public List<string> ProjectFilePaths { get; set; } = new();

        public string BuildConfiguration { get; set; } = "Debug";
        public string? ActiveWorktreePath { get; set; }
        public string? CommandPrefix { get; set; }
        public bool LoadOnStartup { get; set; }

        /// <summary>Extra "Name=Value" MSBuild properties for this group's builds
        /// and property queries.</summary>
        public List<string> MsBuildProperties { get; set; } = new();

        /// <summary>Native DLLs pinned by full path before the modules load.</summary>
        public List<string> PreloadNativeModules { get; set; } = new();

        /// <summary>Managed assemblies (NETLOAD-equivalent) loaded before the modules.</summary>
        public List<string> PreloadManagedAssemblies { get; set; } = new();

        /// <summary>Managed assemblies (NETLOAD-equivalent) loaded after the modules.</summary>
        public List<string> PostloadManagedAssemblies { get; set; } = new();
    }

    /// <summary>Outcome of registering a new OARX plugin.</summary>
    public record RegisterOarxResult(bool Success, string Name, string Message);

    /// <summary>
    /// A partial update of one OARX group. Null = keep the current value; an
    /// EMPTY list = clear. The group's name is its identity and is not
    /// patchable (rename = remove + re-add); the solution is likewise fixed —
    /// it defines what the group IS, not how it behaves.
    /// </summary>
    public sealed record OarxPluginPatch(
        string? CommandPrefix = null,
        bool? LoadOnStartup = null,
        string? BuildConfiguration = null,
        IReadOnlyList<string>? ProjectFilePaths = null,
        IReadOnlyList<string>? MsBuildProperties = null,
        IReadOnlyList<string>? PreloadNativeModules = null,
        IReadOnlyList<string>? PreloadManagedAssemblies = null,
        IReadOnlyList<string>? PostloadManagedAssemblies = null);

    /// <summary>
    /// plugins.json ↔ <see cref="OarxManager"/> bridge. Mirrors
    /// <c>PluginConfigLoader</c>'s role for .NET plugins and shares its file.
    /// </summary>
    public static class OarxConfigLoader
    {
        /// <summary>Build the live registration for one config entry and create
        /// its {PREFIX}LOAD / DEV / UNLOAD commands.</summary>
        internal static void RegisterFromConfig(OarxPluginEntry entry)
        {
            OarxManager.Add(BuildRegistration(entry));
            OarxManager.RegisterLoaderCommands(
                entry.Name, entry.CommandPrefix ?? entry.Name);
        }

        /// <summary>Project one config entry into a live registration. Shared by
        /// registration and by the pending-config swap in the manager.</summary>
        internal static OarxRegistration BuildRegistration(OarxPluginEntry entry) =>
            new()
            {
                Name = entry.Name,
                SolutionFilePath = entry.SolutionFilePath,
                Modules = entry.ProjectFilePaths
                    .Select(p => new OarxModule { ProjectFilePath = p })
                    .ToList(),
                BuildConfiguration = entry.BuildConfiguration,
                ActiveWorktreePath = entry.ActiveWorktreePath,
                MsBuildProperties = entry.MsBuildProperties.ToList(),
                PreloadNativeModules = entry.PreloadNativeModules.ToList(),
                PreloadManagedAssemblies = entry.PreloadManagedAssemblies.ToList(),
                PostloadManagedAssemblies = entry.PostloadManagedAssemblies.ToList(),
                Source = entry,
            };

        /// <summary>
        /// Add an OARX plugin to plugins.json and register it live. Sole entry
        /// point for "add an OARX plugin" — palette and MCP both come here.
        /// </summary>
        public static RegisterOarxResult RegisterNewPlugin(
            string solutionFilePath,
            IReadOnlyList<string> projectFilePaths,
            string buildConfiguration = "Debug",
            string? name = null,
            string? commandPrefix = null,
            bool loadOnStartup = false,
            IReadOnlyList<string>? msbuildProperties = null,
            IReadOnlyList<string>? preloadNativeModules = null,
            IReadOnlyList<string>? preloadManagedAssemblies = null,
            IReadOnlyList<string>? postloadManagedAssemblies = null)
        {
            if (string.IsNullOrWhiteSpace(solutionFilePath))
                return new RegisterOarxResult(false, "", "solutionFilePath is required");
            if (!File.Exists(solutionFilePath))
                return new RegisterOarxResult(false, "", $"solution not found: {solutionFilePath}");
            if (projectFilePaths == null || projectFilePaths.Count == 0)
                return new RegisterOarxResult(false, "",
                    "at least one module project is required, in load order");

            foreach (var p in projectFilePaths)
                if (!File.Exists(p))
                    return new RegisterOarxResult(false, "", $"project not found: {p}");

            // Default the group name to the last module's project — for a dbx+arx
            // pair that is the .arx, which is what the user calls the plugin.
            string resolved = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(projectFilePaths[projectFilePaths.Count - 1])
                : name!.Trim();

            if (OarxManager.IsRegistered(resolved))
                return new RegisterOarxResult(false, resolved, "already registered");

            var config = PluginConfigLoader.Load() ?? new PluginConfig();
            if (config.OarxPlugins.Any(p =>
                    p.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
                return new RegisterOarxResult(false, resolved, "already in plugins.json");

            var entry = new OarxPluginEntry
            {
                Name = resolved,
                SolutionFilePath = solutionFilePath,
                ProjectFilePaths = projectFilePaths.ToList(),
                BuildConfiguration = buildConfiguration,
                CommandPrefix = string.IsNullOrWhiteSpace(commandPrefix)
                    ? null : commandPrefix!.Trim().ToUpperInvariant(),
                LoadOnStartup = loadOnStartup,
                MsBuildProperties = msbuildProperties?.ToList() ?? new List<string>(),
                PreloadNativeModules = preloadNativeModules?.ToList() ?? new List<string>(),
                PreloadManagedAssemblies = preloadManagedAssemblies?.ToList() ?? new List<string>(),
                PostloadManagedAssemblies = postloadManagedAssemblies?.ToList() ?? new List<string>(),
            };

            config.OarxPlugins.Add(entry);
            PluginConfigLoader.Save(config);
            RegisterFromConfig(entry);
            return new RegisterOarxResult(true, resolved, "registered");
        }

        /// <summary>
        /// Patch an existing OARX group in plugins.json AND live. Sole entry
        /// point for "edit an OARX group" — the palette's edit form and the MCP
        /// update tool both come here, mirroring <see cref="RegisterNewPlugin"/>.
        /// </summary>
        /// <remarks>
        /// No unload is required: properties apply at the next BUILD, companions
        /// at the next LOAD, so a loaded group takes the patch immediately and
        /// nothing running is disturbed. The one exception is the module list —
        /// a loaded group's modules cannot be swapped under it, so that part is
        /// staged and applied at the next Load/Reload.
        /// </remarks>
        public static OarxActionResult UpdatePlugin(string name, OarxPluginPatch patch)
        {
            // Validate before touching anything — a bad patch must not
            // half-apply.
            if (patch.MsBuildProperties != null)
                foreach (var p in patch.MsBuildProperties)
                {
                    int eq = p.IndexOf('=');
                    if (eq <= 0)
                        return new OarxActionResult(name, false, OarxManager.IsLoaded(name),
                            $"MSBuild property '{p}' is not Name=Value");
                }
            if (patch.ProjectFilePaths != null)
            {
                if (patch.ProjectFilePaths.Count == 0)
                    return new OarxActionResult(name, false, OarxManager.IsLoaded(name),
                        "a group cannot have zero modules — unregister it instead");
                foreach (var p in patch.ProjectFilePaths)
                    if (!File.Exists(p))
                        return new OarxActionResult(name, false, OarxManager.IsLoaded(name),
                            $"project not found: {p}");
            }

            var config = PluginConfigLoader.Load();
            var entry = config?.OarxPlugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (config == null || entry == null)
                return new OarxActionResult(name, false, false, "not in plugins.json");

            bool prefixChanged = patch.CommandPrefix != null &&
                !string.Equals(entry.CommandPrefix ?? entry.Name,
                    patch.CommandPrefix, StringComparison.OrdinalIgnoreCase);

            if (patch.CommandPrefix != null)
                entry.CommandPrefix = patch.CommandPrefix.Trim().ToUpperInvariant();
            if (patch.LoadOnStartup != null) entry.LoadOnStartup = patch.LoadOnStartup.Value;
            if (patch.BuildConfiguration != null) entry.BuildConfiguration = patch.BuildConfiguration;
            if (patch.ProjectFilePaths != null) entry.ProjectFilePaths = patch.ProjectFilePaths.ToList();
            if (patch.MsBuildProperties != null) entry.MsBuildProperties = patch.MsBuildProperties.ToList();
            if (patch.PreloadNativeModules != null) entry.PreloadNativeModules = patch.PreloadNativeModules.ToList();
            if (patch.PreloadManagedAssemblies != null) entry.PreloadManagedAssemblies = patch.PreloadManagedAssemblies.ToList();
            if (patch.PostloadManagedAssemblies != null) entry.PostloadManagedAssemblies = patch.PostloadManagedAssemblies.ToList();

            PluginConfigLoader.Save(config);
            return OarxManager.ApplyEntry(entry, prefixChanged);
        }

        public static bool UpdateEntry(string name, Action<OarxPluginEntry> mutate)
        {
            var config = PluginConfigLoader.Load();
            if (config == null) return false;
            var entry = config.OarxPlugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            mutate(entry);
            PluginConfigLoader.Save(config);
            return true;
        }

        public static bool RemoveEntry(string name)
        {
            var config = PluginConfigLoader.Load();
            if (config == null) return false;
            int removed = config.OarxPlugins.RemoveAll(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            PluginConfigLoader.Save(config);
            return true;
        }

        /// <summary>Drop from both the live registry and plugins.json.</summary>
        public static OarxActionResult Unregister(string name)
        {
            bool live = OarxManager.UnregisterInMemory(name);
            bool onDisk = RemoveEntry(name);
            string msg = live
                ? (onDisk ? "unregistered and removed from plugins.json" : "unregistered")
                : (onDisk ? "removed from plugins.json only" : "was not registered");
            return new OarxActionResult(name, live || onDisk, false, msg);
        }
    }
}
