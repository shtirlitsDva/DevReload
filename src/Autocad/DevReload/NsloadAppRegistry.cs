using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DevReload
{
    public static class NsloadAppRegistry
    {
        public static List<(string Name, string DllDir)> Load(string? csvPath)
        {
            var result = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Read predefined apps from NSLOAD's CSV
            if (!string.IsNullOrEmpty(csvPath) && File.Exists(csvPath))
            {
                foreach (string line in File.ReadAllLines(csvPath).Skip(1))
                {
                    string[] parts = line.Split(';', 2);
                    if (parts.Length < 2) continue;

                    string name = parts[0].Trim();
                    string dllPath = parts[1].Trim();
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dllPath))
                        continue;

                    string dir = Path.GetDirectoryName(dllPath)!;
                    result.Add((name, dir));
                    seen.Add(name);
                }
            }

            // 2. Read user-defined plugins from NSLOAD's config.json
            string nsloadConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NSLOAD", "config.json");

            if (File.Exists(nsloadConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(nsloadConfigPath);
                    var opts = new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    var config = JsonSerializer.Deserialize<NsloadConfigDto>(json, opts);

                    if (config?.Plugins != null)
                    {
                        foreach (var plugin in config.Plugins)
                        {
                            if (string.IsNullOrEmpty(plugin.Name) ||
                                string.IsNullOrEmpty(plugin.DllPath))
                                continue;
                            if (seen.Contains(plugin.Name)) continue;

                            string dir = Path.GetDirectoryName(plugin.DllPath)!;
                            result.Add((plugin.Name, dir));
                            seen.Add(plugin.Name);
                        }
                    }
                }
                catch { /* best-effort — ignore corrupt config */ }
            }

            return result;
        }

        private class NsloadConfigDto
        {
            public List<NsloadPluginDto>? Plugins { get; set; }
        }

        private class NsloadPluginDto
        {
            public string Name { get; set; } = "";
            public string DllPath { get; set; } = "";
        }
    }
}
