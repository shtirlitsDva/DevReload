using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevReload.Core
{
    // Single canonical reader/writer for SharedAssemblies.Config.json — the file
    // that records which DLLs in a build directory should be pre-loaded into the
    // default ALC (shared) and which need a generated runtimeconfig.json (mixed
    // mode).
    //
    // Lifecycle: one file per build directory.
    //
    //   - Dev:  <csprojDir>/bin/<config>/SharedAssemblies.Config.json
    //   - Prod: <prodAppDir>/SharedAssemblies.Config.json
    //
    // Each build's choice is intrinsically scoped to that build's set of DLLs.
    // Switching worktree / branch / configuration switches build directories and
    // therefore switches files; no shared mutable state in plugins.json, no
    // cross-build contamination. If the file is absent, the build has no shared
    // configuration — not "inherits from somewhere." See PluginManager.LoadCore.
    public static class SharedAssembliesFile
    {
        public const string FileName = "SharedAssemblies.Config.json";

        // JSON shape matches what DevReloadViewModel.PushToProduction has been
        // writing since day one; the production loader reads exactly this.
        private static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public sealed class Config
        {
            public List<string> SharedAssemblies { get; set; } = new();
            public List<string> MixedModeAssemblies { get; set; } = new();
            // Subset of SharedAssemblies loaded via Assembly.Load(byte[]) instead
            // of Assembly.LoadFrom — releases the file lock so the project can
            // be rebuilt without restarting AutoCAD. The old assembly image
            // remains in the default ALC for the rest of the session; only
            // applicable when the public surface is stable. Mutually exclusive
            // with MixedModeAssemblies (native deps probe the DLL's folder).
            public List<string> StreamedAssemblies { get; set; } = new();
        }

        public static string PathFor(string buildDir) =>
            Path.Combine(buildDir, FileName);

        public static Config Read(string buildDir)
        {
            string path = PathFor(buildDir);
            if (!File.Exists(path)) return new Config();

            try
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<Config>(json, _options);
                return cfg ?? new Config();
            }
            catch
            {
                // Malformed file: treat as empty rather than crash the load /
                // dialog path. The user re-saves to overwrite.
                return new Config();
            }
        }

        public static void Write(
            string buildDir,
            IEnumerable<string> shared,
            IEnumerable<string> mixedMode,
            IEnumerable<string> streamed)
        {
            Directory.CreateDirectory(buildDir);
            var cfg = new Config
            {
                SharedAssemblies = new List<string>(shared),
                MixedModeAssemblies = new List<string>(mixedMode),
                StreamedAssemblies = new List<string>(streamed),
            };
            string json = JsonSerializer.Serialize(cfg, _options);
            File.WriteAllText(PathFor(buildDir), json);
        }
    }
}
