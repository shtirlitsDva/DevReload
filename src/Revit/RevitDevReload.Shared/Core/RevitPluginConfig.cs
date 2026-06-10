using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevitDevReload.Core
{
    public class RevitPluginConfig
    {
        public List<RevitPluginEntry> Plugins { get; set; } = new();
    }

    public class RevitPluginEntry
    {
        public string Name { get; set; } = "";
        public string? ProjectFilePath { get; set; }
        public string? DllPath { get; set; }
        public string BuildConfiguration { get; set; } = "Debug";
        public string? ActiveWorktreePath { get; set; }
        public bool LoadOnStartup { get; set; }
    }

    // One config file per Revit major version (plugins.R2024.json …) because
    // plugin builds are per-version: the same logical plugin points at a
    // different csproj/DLL in Revit 2022 than in 2025. No cross-version state.
    public static class RevitPluginConfigLoader
    {
        private const string AppFolder = "RevitDevReload";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        // baseDirOverride exists for tests; production callers pass null and
        // get %APPDATA%\RevitDevReload.
        public static string GetConfigPath(int revitVersion, string? baseDirOverride = null)
        {
            string baseDir = baseDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolder);
            return Path.Combine(baseDir, $"plugins.R{revitVersion}.json");
        }

        public static RevitPluginConfig? Load(int revitVersion, string? baseDirOverride = null)
        {
            string path = GetConfigPath(revitVersion, baseDirOverride);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RevitPluginConfig>(json, _jsonOptions);
        }

        public static void Save(int revitVersion, RevitPluginConfig config, string? baseDirOverride = null)
        {
            string path = GetConfigPath(revitVersion, baseDirOverride);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(path, json);
        }
    }
}
