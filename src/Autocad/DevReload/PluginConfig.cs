using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using DevReload.Core;
using DevReload.Rpc;

namespace DevReload
{
    public class PluginConfig
    {
        public string? NsloadCsvPath { get; set; }
        public List<PluginEntry> Plugins { get; set; } = new();
    }

    public class PluginEntry
    {
        public string Name { get; set; } = "";
        public string? DllPath { get; set; }
        public string? CommandPrefix { get; set; }
        public bool LoadOnStartup { get; set; }
        public string? ProductionTarget { get; set; }
        public string BuildConfiguration { get; set; } = "Debug";
        public string? ProjectFilePath { get; set; }
        public string? ActiveWorktreePath { get; set; }

        // Legacy fields kept ONLY so MigrateIfNeeded can read old plugins.json
        // files. After migration the values are null → not re-serialised (see
        // DefaultIgnoreCondition.WhenWritingNull below). The JSON property
        // names stay lowercase-camel so old config files deserialise into
        // these properties. Do not reference outside MigrateIfNeeded — the
        // source of truth is <buildDir>/SharedAssemblies.Config.json.
        [JsonPropertyName("sharedAssemblies")]
        [Obsolete("Migrated to <buildDir>/SharedAssemblies.Config.json. Read only by MigrateIfNeeded.")]
        public List<string>? LegacySharedAssemblies { get; set; }

        [JsonPropertyName("mixedModeAssemblies")]
        [Obsolete("Migrated to <buildDir>/SharedAssemblies.Config.json. Read only by MigrateIfNeeded.")]
        public List<string>? LegacyMixedModeAssemblies { get; set; }
    }

    public static class PluginConfigLoader
    {
        private const string AppFolder = "DevReload";
        private const string ConfigFileName = "plugins.json";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppFolder, ConfigFileName);
        }

        public static PluginConfig? Load()
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PluginConfig>(json, _jsonOptions);
        }

        public static void Save(PluginConfig config)
        {
            string path = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Add a plugin to plugins.json and register it with PluginManager
        /// + create its loader commands. Sole entry point for "add a new
        /// plugin" — palette UI and MCP register_new_plugin tool both call
        /// this. Plugin name is the csproj file name (renaming is not
        /// supported); dllPath is resolved via MSBuild's TargetPath for
        /// the requested configuration.
        /// Idempotent: already-registered names return Success=false
        /// rather than throw, so the caller can safely re-issue.
        /// </summary>
        public static RegisterPluginResult RegisterNewPlugin(
            string projectFilePath,
            string buildConfiguration = "Debug",
            string? commandPrefix = null,
            bool loadOnStartup = false)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
                return new RegisterPluginResult(false, "", "projectFilePath is required");

            string name = Path.GetFileNameWithoutExtension(projectFilePath);
            if (string.IsNullOrWhiteSpace(name))
                return new RegisterPluginResult(false, "",
                    $"could not derive plugin name from '{projectFilePath}'");

            string? dllPath = BuildService.QueryMsBuildProperty(
                projectFilePath, "TargetPath", buildConfiguration, AcadBuild.Platform);
            if (string.IsNullOrEmpty(dllPath))
                return new RegisterPluginResult(false, name,
                    $"could not resolve TargetPath for '{name}' ({buildConfiguration}). " +
                    "Restore/build the project at least once and try again.");

            if (PluginManager.IsRegistered(name))
                return new RegisterPluginResult(false, name, "already registered");

            PluginConfig config = Load() ?? new PluginConfig();
            if (config.Plugins.Any(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return new RegisterPluginResult(false, name, "already in plugins.json");
            }

            var entry = new PluginEntry
            {
                Name = name,
                ProjectFilePath = projectFilePath,
                DllPath = dllPath,
                BuildConfiguration = buildConfiguration,
                CommandPrefix = string.IsNullOrWhiteSpace(commandPrefix)
                    ? null
                    : commandPrefix.Trim().ToUpperInvariant(),
                LoadOnStartup = loadOnStartup,
            };

            config.Plugins.Add(entry);
            Save(config);

            DevReloaderCommands.RegisterFromConfig(entry);
            return new RegisterPluginResult(true, name, "registered");
        }

        /// <summary>Update a single plugin entry in plugins.json and
        /// return true if the entry existed. Used by mutating MCP tools
        /// (update_build_configuration, update_active_worktree) so the
        /// agent's changes survive an AutoCAD restart — the palette UI
        /// already persists by the same pattern.</summary>
        public static bool UpdatePluginEntry(string name, Action<PluginEntry> mutate)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));

            PluginConfig? config = Load();
            if (config == null) return false;
            var entry = config.Plugins.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            mutate(entry);
            Save(config);
            return true;
        }

        /// <summary>Remove a plugin entry from plugins.json. Returns
        /// true if the entry existed. Paired with the unregister tool so
        /// removal is durable across restarts.</summary>
        public static bool RemovePluginEntry(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            PluginConfig? config = Load();
            if (config == null) return false;
            int removed = config.Plugins.RemoveAll(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(config);
            return true;
        }

        public static void MigrateIfNeeded(PluginConfig config)
        {
            bool changed = false;

            config.Plugins.RemoveAll(entry =>
            {
                if (entry.ProjectFilePath != null) return false;

                string? csproj = FindCsprojFromDllPath(entry.DllPath);
                if (csproj != null)
                {
                    entry.ProjectFilePath = csproj;
                    changed = true;
                    return false;
                }

                changed = true;
                return true;
            });

            // One-shot drain of legacy SharedAssemblies / MixedModeAssemblies
            // into per-build SharedAssemblies.Config.json files. Best effort:
            // if the build dir doesn't exist yet we drop the data (acceptable
            // because the user can always re-tick in the dialog). After this
            // pass the properties are null and won't re-serialise.
#pragma warning disable CS0618
            foreach (var entry in config.Plugins)
            {
                var legacyShared = entry.LegacySharedAssemblies;
                var legacyMixed = entry.LegacyMixedModeAssemblies;
                bool hasLegacy = (legacyShared?.Count ?? 0) > 0
                              || (legacyMixed?.Count ?? 0) > 0;
                if (!hasLegacy)
                {
                    // Even an empty-list legacy field gets nulled so it stops
                    // appearing in saved JSON.
                    if (legacyShared != null || legacyMixed != null)
                    {
                        entry.LegacySharedAssemblies = null;
                        entry.LegacyMixedModeAssemblies = null;
                        changed = true;
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.DllPath))
                {
                    string buildDir = Path.GetDirectoryName(entry.DllPath)!;
                    if (Directory.Exists(buildDir))
                    {
                        // Don't overwrite an existing per-build file — that
                        // would clobber a more recent user choice with
                        // whatever was last saved to plugins.json.
                        if (!File.Exists(SharedAssembliesFile.PathFor(buildDir)))
                        {
                            SharedAssembliesFile.Write(
                                buildDir,
                                legacyShared ?? new List<string>(),
                                legacyMixed ?? new List<string>(),
                                new List<string>());
                        }
                    }
                }

                entry.LegacySharedAssemblies = null;
                entry.LegacyMixedModeAssemblies = null;
                changed = true;
            }
#pragma warning restore CS0618

            if (changed)
                Save(config);
        }

        private static string? FindCsprojFromDllPath(string? dllPath)
        {
            if (string.IsNullOrEmpty(dllPath)) return null;

            string? dir = Path.GetDirectoryName(dllPath);
            string dllName = Path.GetFileNameWithoutExtension(dllPath);

            while (dir != null)
            {
                try
                {
                    var csprojFiles = Directory.GetFiles(dir, "*.csproj");
                    if (csprojFiles.Length == 1)
                        return csprojFiles[0];

                    // Prefer the one matching the DLL name
                    foreach (var f in csprojFiles)
                    {
                        if (Path.GetFileNameWithoutExtension(f)
                            .Equals(dllName, StringComparison.OrdinalIgnoreCase))
                            return f;
                    }
                }
                catch { }

                // Stop if we hit a .csproj at some level (ambiguous)
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
