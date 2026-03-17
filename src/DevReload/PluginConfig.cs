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
        public List<string> SharedAssemblies { get; set; } = new();
        public List<string> MixedModeAssemblies { get; set; } = new();
        public string? ProductionTarget { get; set; }
        public string BuildConfiguration { get; set; } = "Debug";
        public string? ProjectFilePath { get; set; }
        public string? ActiveWorktreePath { get; set; }
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
