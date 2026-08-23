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
    }

    /// <summary>Outcome of registering a new OARX plugin.</summary>
    public record RegisterOarxResult(bool Success, string Name, string Message);

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
            var reg = new OarxRegistration
            {
                Name = entry.Name,
                SolutionFilePath = entry.SolutionFilePath,
                Modules = entry.ProjectFilePaths
                    .Select(p => new OarxModule { ProjectFilePath = p })
                    .ToList(),
                BuildConfiguration = entry.BuildConfiguration,
                ActiveWorktreePath = entry.ActiveWorktreePath,
            };

            OarxManager.Add(reg);
            OarxManager.RegisterLoaderCommands(
                entry.Name, entry.CommandPrefix ?? entry.Name);
        }

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
            bool loadOnStartup = false)
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
            };

            config.OarxPlugins.Add(entry);
            PluginConfigLoader.Save(config);
            RegisterFromConfig(entry);
            return new RegisterOarxResult(true, resolved, "registered");
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
