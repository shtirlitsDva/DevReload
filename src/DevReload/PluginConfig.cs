using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        public string? VsProject { get; set; }
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
                                legacyMixed ?? new List<string>());
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
