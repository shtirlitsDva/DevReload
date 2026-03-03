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
        public string? ProductionTarget { get; set; }
        public string BuildConfiguration { get; set; } = "Debug";
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
    }
}
