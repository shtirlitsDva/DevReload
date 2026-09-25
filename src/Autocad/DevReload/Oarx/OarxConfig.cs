using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

using DevReload.Core;
using DevReload.Diagnostics;

namespace DevReload.Oarx
{
    /// <summary>
    /// One OARX group as it is persisted in plugins.json: the group's identity
    /// plus its profiles.
    /// </summary>
    /// <remarks>
    /// The group is what the AutoCAD commands are called and which solution the
    /// modules build under. WHAT gets built and loaded, and FROM WHICH FOLDER,
    /// is a <see cref="OarxProfile"/> — a worktree can need a different module
    /// list than the clone it came from, and one list per group could not say
    /// that. Exactly one profile is active at a time.
    /// </remarks>
    public class OarxPluginEntry
    {
        public string Name { get; set; } = "";
        public string? CommandPrefix { get; set; }
        public bool LoadOnStartup { get; set; }
        public string BuildConfiguration { get; set; } = "Debug";

        /// <summary>The .sln the modules build under, RELATIVE to each profile's
        /// folder. Required — MSBuild resolves a C++ project's output through
        /// $(SolutionDir), and getting it wrong silently points at a directory
        /// the build never writes to.</summary>
        public string Solution { get; set; } = "";

        /// <summary>Name of the profile Load/Reload build and load from.</summary>
        public string ActiveProfile { get; set; } = "";

        public List<OarxProfile> Profiles { get; set; } = new();

        public OarxProfile? FindProfile(string? name) =>
            name == null ? null : Profiles.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Legacy fields, kept ONLY so MigrateIfNeeded can read the pre-profile
        // shape of plugins.json. Null after migration, so never re-serialised.
        // Do not reference outside OarxConfigLoader.MigrateIfNeeded.

        [JsonPropertyName("solutionFilePath")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public string? LegacySolutionFilePath { get; set; }

        [JsonPropertyName("projectFilePaths")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public List<string>? LegacyProjectFilePaths { get; set; }

        [JsonPropertyName("activeWorktreePath")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public string? LegacyActiveWorktreePath { get; set; }

        [JsonPropertyName("msBuildProperties")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public List<string>? LegacyMsBuildProperties { get; set; }

        [JsonPropertyName("preloadNativeModules")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public List<string>? LegacyPreloadNativeModules { get; set; }

        [JsonPropertyName("preloadManagedAssemblies")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public List<string>? LegacyPreloadManagedAssemblies { get; set; }

        [JsonPropertyName("postloadManagedAssemblies")]
        [Obsolete("Pre-profile shape. Read only by OarxConfigLoader.MigrateIfNeeded.")]
        public List<string>? LegacyPostloadManagedAssemblies { get; set; }
    }

    /// <summary>
    /// What one OARX group builds and loads, and from which folder.
    /// </summary>
    /// <remarks>
    /// A profile names a FOLDER, never a branch: a worktree is always its own
    /// folder, so which branch it has checked out is irrelevant here. Module
    /// paths are relative to that folder, which is what lets a profile be copied
    /// from one worktree to another, and what lets a project that exists only in
    /// one worktree be added at all.
    /// </remarks>
    public class OarxProfile
    {
        public string Name { get; set; } = "";

        /// <summary>Absolute path of the worktree (or clone) this profile builds from.</summary>
        public string WorktreePath { get; set; } = "";

        /// <summary>Module projects in LOAD order (.dbx before the .arx that uses
        /// it), relative to <see cref="WorktreePath"/>. Unload walks this backwards.</summary>
        public List<string> ProjectFilePaths { get; set; } = new();

        /// <summary>Extra "Name=Value" MSBuild properties for builds and property queries.</summary>
        public List<string> MsBuildProperties { get; set; } = new();

        /// <summary>Native DLLs pinned by full path before the modules load.
        /// Absolute, or relative to <see cref="WorktreePath"/>.</summary>
        public List<string> PreloadNativeModules { get; set; } = new();

        /// <summary>Managed assemblies (NETLOAD-equivalent) loaded before the
        /// modules. Absolute, or relative to <see cref="WorktreePath"/>.</summary>
        public List<string> PreloadManagedAssemblies { get; set; } = new();

        /// <summary>Managed assemblies (NETLOAD-equivalent) loaded after the
        /// modules. Absolute, or relative to <see cref="WorktreePath"/>.</summary>
        public List<string> PostloadManagedAssemblies { get; set; } = new();

        /// <summary>A path as stored in this profile, made absolute: relative
        /// paths are resolved inside the profile's folder.</summary>
        public string Resolve(string path) =>
            Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(WorktreePath, path));

        [JsonIgnore]
        public bool FolderExists => Directory.Exists(WorktreePath);

        public OarxProfile Clone(string name, string worktreePath) => new()
        {
            Name = name,
            WorktreePath = worktreePath,
            ProjectFilePaths = ProjectFilePaths.ToList(),
            MsBuildProperties = MsBuildProperties.ToList(),
            PreloadNativeModules = PreloadNativeModules.ToList(),
            PreloadManagedAssemblies = PreloadManagedAssemblies.ToList(),
            PostloadManagedAssemblies = PostloadManagedAssemblies.ToList(),
        };
    }

    /// <summary>Outcome of registering a new OARX group.</summary>
    public record RegisterOarxResult(bool Success, string Name, string Message);

    /// <summary>
    /// A partial update of an OARX group's OWN fields. Null = keep. The name
    /// and the solution are the group's identity and are not patchable; what a
    /// group builds lives in its profiles (<see cref="OarxProfilePublish"/>).
    /// </summary>
    public sealed record OarxGroupPatch(
        string? CommandPrefix = null,
        bool? LoadOnStartup = null,
        string? BuildConfiguration = null);

    /// <summary>
    /// Create or update one profile. Null lists = keep the current value (or
    /// the copied value, on create); an EMPTY list = clear.
    /// </summary>
    /// <param name="Group">The OARX group the profile belongs to.</param>
    /// <param name="WorktreePath">Absolute folder the profile builds from.</param>
    /// <param name="Profile">Profile name. Defaults to the folder's name.</param>
    /// <param name="CopyFrom">Profile to copy the lists from. Only when creating.</param>
    /// <param name="ProjectFilePaths">Module projects in load order: relative to
    /// the folder, or absolute paths inside it (stored relative).</param>
    /// <param name="Activate">Also make this the group's active profile.</param>
    public sealed record OarxProfilePublish(
        string Group,
        string WorktreePath,
        string? Profile = null,
        string? CopyFrom = null,
        IReadOnlyList<string>? ProjectFilePaths = null,
        IReadOnlyList<string>? MsBuildProperties = null,
        IReadOnlyList<string>? PreloadNativeModules = null,
        IReadOnlyList<string>? PreloadManagedAssemblies = null,
        IReadOnlyList<string>? PostloadManagedAssemblies = null,
        bool Activate = false);

    /// <summary>
    /// plugins.json ↔ <see cref="OarxManager"/> bridge. Mirrors
    /// <c>PluginConfigLoader</c>'s role for .NET plugins and shares its file.
    /// </summary>
    /// <remarks>
    /// Every write to an OARX group goes through a method here — the palette,
    /// the profiles window and the MCP tools all call the same ones. Each one
    /// validates BEFORE anything is saved, so a bad request never half-applies.
    /// </remarks>
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

        /// <summary>Project one config entry into a live registration: the
        /// ACTIVE profile's paths, resolved inside its folder.</summary>
        /// <remarks>An entry whose active profile does not exist still registers
        /// (its commands must exist so the user sees the error when running
        /// them); the registration carries the reason and every lifecycle
        /// operation refuses with it.</remarks>
        internal static OarxRegistration BuildRegistration(OarxPluginEntry entry)
        {
            var profile = entry.FindProfile(entry.ActiveProfile);
            if (profile == null)
                return new OarxRegistration
                {
                    Name = entry.Name,
                    ProfileName = entry.ActiveProfile,
                    WorktreePath = "",
                    SolutionFilePath = "",
                    Modules = new List<OarxModule>(),
                    BuildConfiguration = entry.BuildConfiguration,
                    Problem = string.IsNullOrEmpty(entry.ActiveProfile)
                        ? $"'{entry.Name}' has no active profile."
                        : $"'{entry.Name}': the active profile '{entry.ActiveProfile}' does not exist.",
                    Source = entry,
                };

            return new OarxRegistration
            {
                Name = entry.Name,
                ProfileName = profile.Name,
                WorktreePath = profile.WorktreePath,
                SolutionFilePath = profile.Resolve(entry.Solution),
                Modules = profile.ProjectFilePaths
                    .Select(p => new OarxModule { ProjectFilePath = profile.Resolve(p) })
                    .ToList(),
                BuildConfiguration = entry.BuildConfiguration,
                MsBuildProperties = profile.MsBuildProperties.ToList(),
                PreloadNativeModules = profile.PreloadNativeModules.Select(profile.Resolve).ToList(),
                PreloadManagedAssemblies = profile.PreloadManagedAssemblies.Select(profile.Resolve).ToList(),
                PostloadManagedAssemblies = profile.PostloadManagedAssemblies.Select(profile.Resolve).ToList(),
                Source = entry,
            };
        }

        // ── Group ────────────────────────────────────────────────────

        /// <summary>
        /// Add an OARX group to plugins.json with its first profile, and register
        /// it live. Sole entry point for "add an OARX group".
        /// </summary>
        /// <remarks>
        /// The first profile's folder is the worktree (or clone) the solution
        /// sits in — git's top level for the solution's directory. A solution
        /// outside git has no worktree to speak of, so its own directory is the
        /// folder.
        /// </remarks>
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
            if (!Path.IsPathRooted(solutionFilePath) || !File.Exists(solutionFilePath))
                return new RegisterOarxResult(false, "", $"solution not found: {solutionFilePath}");
            if (projectFilePaths == null || projectFilePaths.Count == 0)
                return new RegisterOarxResult(false, "",
                    "at least one module project is required, in load order");

            string folder = FolderForSolution(solutionFilePath);

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

            var profile = new OarxProfile
            {
                Name = FolderName(folder),
                WorktreePath = folder,
            };
            string? error =
                SetModules(profile, projectFilePaths) ??
                SetProperties(profile, msbuildProperties);
            if (error != null)
                return new RegisterOarxResult(false, resolved, error);
            profile.PreloadNativeModules = Paths(preloadNativeModules);
            profile.PreloadManagedAssemblies = Paths(preloadManagedAssemblies);
            profile.PostloadManagedAssemblies = Paths(postloadManagedAssemblies);

            var entry = new OarxPluginEntry
            {
                Name = resolved,
                Solution = Path.GetRelativePath(folder, Path.GetFullPath(solutionFilePath)),
                BuildConfiguration = buildConfiguration,
                CommandPrefix = string.IsNullOrWhiteSpace(commandPrefix)
                    ? null : commandPrefix!.Trim().ToUpperInvariant(),
                LoadOnStartup = loadOnStartup,
                ActiveProfile = profile.Name,
                Profiles = { profile },
            };

            config.OarxPlugins.Add(entry);
            PluginConfigLoader.Save(config);
            RegisterFromConfig(entry);
            return new RegisterOarxResult(true, resolved,
                $"registered with profile '{profile.Name}' ({folder})");
        }

        /// <summary>
        /// Patch an OARX group's own fields in plugins.json AND live. Nothing
        /// here needs an unload: the configuration applies at the next build,
        /// the prefix replaces the commands immediately.
        /// </summary>
        public static OarxActionResult UpdatePlugin(string name, OarxGroupPatch patch)
        {
            bool prefixChanged = false;
            return Mutate(name, entry =>
            {
                prefixChanged = patch.CommandPrefix != null &&
                    !string.Equals(entry.CommandPrefix ?? entry.Name,
                        patch.CommandPrefix.Trim(), StringComparison.OrdinalIgnoreCase);

                if (patch.CommandPrefix != null)
                    entry.CommandPrefix = patch.CommandPrefix.Trim().ToUpperInvariant();
                if (patch.LoadOnStartup != null) entry.LoadOnStartup = patch.LoadOnStartup.Value;
                if (patch.BuildConfiguration != null) entry.BuildConfiguration = patch.BuildConfiguration;
                return null;
            }, () => prefixChanged);
        }

        // ── Profiles ─────────────────────────────────────────────────

        /// <summary>
        /// Create or update one profile of a group. Sole entry point for
        /// "change what a group builds" — the profiles window saves through
        /// here, and so does the MCP publish tool.
        /// </summary>
        public static OarxActionResult PublishProfile(OarxProfilePublish p)
        {
            if (string.IsNullOrWhiteSpace(p.WorktreePath) || !Path.IsPathRooted(p.WorktreePath))
                return Fail(p.Group, "worktreePath must be an absolute folder path");
            string folder = NormalizeFolder(p.WorktreePath);
            if (!Directory.Exists(folder))
                return Fail(p.Group, $"worktree folder not found: {folder}");

            string profileName = string.IsNullOrWhiteSpace(p.Profile)
                ? FolderName(folder) : p.Profile!.Trim();
            string verb = "";

            return Mutate(p.Group, entry =>
            {
                var profile = entry.FindProfile(profileName);
                if (profile == null)
                {
                    if (p.CopyFrom != null)
                    {
                        var source = entry.FindProfile(p.CopyFrom);
                        if (source == null)
                            return $"copyFrom: no profile named '{p.CopyFrom}' in '{entry.Name}'";
                        profile = source.Clone(profileName, folder);
                    }
                    else
                    {
                        profile = new OarxProfile { Name = profileName, WorktreePath = folder };
                    }
                    entry.Profiles.Add(profile);
                    verb = "created";
                }
                else
                {
                    if (p.CopyFrom != null)
                        return $"profile '{profile.Name}' already exists — copyFrom only applies " +
                               "when creating one (pass the lists to change it)";
                    profile.WorktreePath = folder;
                    verb = "updated";
                }

                string? error =
                    (p.ProjectFilePaths != null ? SetModules(profile, p.ProjectFilePaths) : null) ??
                    SetProperties(profile, p.MsBuildProperties);
                if (error != null) return error;
                if (p.PreloadNativeModules != null) profile.PreloadNativeModules = Paths(p.PreloadNativeModules);
                if (p.PreloadManagedAssemblies != null) profile.PreloadManagedAssemblies = Paths(p.PreloadManagedAssemblies);
                if (p.PostloadManagedAssemblies != null) profile.PostloadManagedAssemblies = Paths(p.PostloadManagedAssemblies);

                error = ValidateProfile(entry, profile);
                if (error != null) return error;

                if (p.Activate) entry.ActiveProfile = profile.Name;
                return null;
            }, message: () => $"profile '{profileName}' {verb}" + (p.Activate ? " and activated" : ""));
        }

        /// <summary>Make a profile the one Load/Reload use. A loaded group takes
        /// the switch at its next load/reload — a mapped module can't be swapped
        /// under itself.</summary>
        public static OarxActionResult ActivateProfile(string group, string profile) =>
            Mutate(group, entry =>
            {
                var target = entry.FindProfile(profile);
                if (target == null) return $"no profile named '{profile}' in '{entry.Name}'";
                if (!target.FolderExists)
                    return $"profile '{target.Name}': worktree folder not found: {target.WorktreePath}";
                entry.ActiveProfile = target.Name;
                return null;
            }, message: () => $"active profile is now '{profile}'");

        /// <summary>Remove one profile. The active profile can't be removed —
        /// that would leave the group with nothing to build.</summary>
        public static OarxActionResult DeleteProfile(string group, string profile) =>
            Mutate(group, entry =>
            {
                var target = entry.FindProfile(profile);
                if (target == null) return $"no profile named '{profile}' in '{entry.Name}'";
                if (target.Name.Equals(entry.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                    return $"'{target.Name}' is the active profile — activate another one first";
                entry.Profiles.Remove(target);
                return null;
            }, message: () => $"profile '{profile}' deleted");

        /// <summary>Remove every profile whose folder is gone, except the active
        /// one (which is reported, not removed).</summary>
        public static OarxActionResult RemoveMissingProfiles(string group)
        {
            var removed = new List<string>();
            bool activeMissing = false;
            return Mutate(group, entry =>
            {
                foreach (var p in entry.Profiles.Where(p => !p.FolderExists).ToList())
                {
                    if (p.Name.Equals(entry.ActiveProfile, StringComparison.OrdinalIgnoreCase))
                    {
                        activeMissing = true;
                        continue;
                    }
                    entry.Profiles.Remove(p);
                    removed.Add(p.Name);
                }
                return null;
            }, message: () =>
                (removed.Count == 0 ? "no missing profiles removed"
                                    : $"removed {string.Join(", ", removed)}") +
                (activeMissing ? " — the ACTIVE profile's folder is missing too; activate another one" : ""));
        }

        // ── Removal ──────────────────────────────────────────────────

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

        // ── Migration ────────────────────────────────────────────────

        /// <summary>
        /// Convert pre-profile entries (one absolute module list per group) to
        /// the profile shape: one profile for the folder the solution sits in.
        /// Returns true when anything changed. An entry whose modules are not
        /// inside that folder cannot be expressed relatively and is dropped,
        /// loudly.
        /// </summary>
        /// <remarks>The old activeWorktreePath is not carried over: the group
        /// comes back on its clone folder, and a worktree gets its own profile.</remarks>
#pragma warning disable CS0618
        internal static bool MigrateIfNeeded(PluginConfig config)
        {
            bool changed = false;
            config.OarxPlugins.RemoveAll(entry =>
            {
                if (entry.LegacySolutionFilePath == null) return false;
                changed = true;

                string sln = Path.GetFullPath(entry.LegacySolutionFilePath);
                string folder = FolderForSolution(sln);

                var profile = new OarxProfile
                {
                    Name = FolderName(folder),
                    WorktreePath = folder,
                    MsBuildProperties = entry.LegacyMsBuildProperties ?? new(),
                    PreloadNativeModules = entry.LegacyPreloadNativeModules ?? new(),
                    PreloadManagedAssemblies = entry.LegacyPreloadManagedAssemblies ?? new(),
                    PostloadManagedAssemblies = entry.LegacyPostloadManagedAssemblies ?? new(),
                };
                string? error = SetModules(profile, entry.LegacyProjectFilePaths ?? new());
                if (error != null)
                {
                    DevReloadDiagnostics.Report(
                        $"OARX migration: '{entry.Name}' dropped",
                        new InvalidOperationException(error));
                    return true;
                }

                entry.Solution = Path.GetRelativePath(folder, sln);
                entry.Profiles = new List<OarxProfile> { profile };
                entry.ActiveProfile = profile.Name;

                entry.LegacySolutionFilePath = null;
                entry.LegacyProjectFilePaths = null;
                entry.LegacyActiveWorktreePath = null;
                entry.LegacyMsBuildProperties = null;
                entry.LegacyPreloadNativeModules = null;
                entry.LegacyPreloadManagedAssemblies = null;
                entry.LegacyPostloadManagedAssemblies = null;
                return false;
            });
            return changed;
        }
#pragma warning restore CS0618

        // ── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Load plugins.json, run <paramref name="mutate"/> on the group's entry,
        /// and only if it returned no error save and take the entry live.
        /// </summary>
        private static OarxActionResult Mutate(
            string group,
            Func<OarxPluginEntry, string?> mutate,
            Func<bool>? prefixChanged = null,
            Func<string>? message = null)
        {
            var config = PluginConfigLoader.Load();
            var entry = config?.OarxPlugins.FirstOrDefault(
                p => p.Name.Equals(group, StringComparison.OrdinalIgnoreCase));
            if (config == null || entry == null)
                return Fail(group, "not in plugins.json");

            string? error = mutate(entry);
            if (error != null) return Fail(entry.Name, error);

            PluginConfigLoader.Save(config);
            var live = OarxManager.ApplyEntry(entry, prefixChanged?.Invoke() ?? false);
            return message == null
                ? live
                : live with { Message = $"{message()} — {live.Message}" };
        }

        private static OarxActionResult Fail(string group, string message) =>
            new(group, false, OarxManager.IsLoaded(group), message);

        /// <summary>Store module projects relative to the profile's folder. An
        /// absolute path must be inside that folder — a module from somewhere
        /// else would not follow the profile to another worktree.</summary>
        private static string? SetModules(OarxProfile profile, IReadOnlyList<string> projects)
        {
            var result = new List<string>();
            foreach (var raw in projects)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                string? rel = Path.IsPathRooted(p) ? ToProfileRelative(profile.WorktreePath, p) : p;
                if (rel == null)
                    return $"project '{p}' is not inside the profile's folder {profile.WorktreePath}";
                result.Add(rel);
            }
            if (result.Count == 0)
                return "a profile needs at least one module project, in load order";
            profile.ProjectFilePaths = result;
            return null;
        }

        private static string? SetProperties(OarxProfile profile, IReadOnlyList<string>? props)
        {
            if (props == null) return null;
            foreach (var p in props)
                if (p.IndexOf('=') <= 0)
                    return $"MSBuild property '{p}' is not Name=Value";
            profile.MsBuildProperties = props.Select(p => p.Trim()).ToList();
            return null;
        }

        private static List<string> Paths(IReadOnlyList<string>? paths) =>
            paths?.Select(p => p.Trim()).Where(p => p.Length > 0).ToList() ?? new List<string>();

        /// <summary>A profile must be buildable where it points: the solution and
        /// every module must exist in ITS folder. Checked on save rather than at
        /// the next build, which would be one step too late to say what was wrong.</summary>
        private static string? ValidateProfile(OarxPluginEntry entry, OarxProfile profile)
        {
            if (profile.ProjectFilePaths.Count == 0)
                return "a profile needs at least one module project — pass projectFilePaths or copyFrom";
            string sln = profile.Resolve(entry.Solution);
            if (!File.Exists(sln))
                return $"the group's solution '{entry.Solution}' is not in {profile.WorktreePath}";
            foreach (var m in profile.ProjectFilePaths)
                if (!File.Exists(profile.Resolve(m)))
                    return $"project not found in this folder: {profile.Resolve(m)}";
            return null;
        }

        public static string NormalizeFolder(string path) =>
            Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');

        public static string FolderName(string folder) =>
            Path.GetFileName(folder.TrimEnd('\\', '/'));

        /// <summary>The folder a group's first profile builds from, for a given
        /// solution: the worktree (or clone) it sits in — git's top level — or,
        /// outside git, the solution's own directory.</summary>
        public static string FolderForSolution(string solutionFilePath)
        {
            string slnDir = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath))!;
            return NormalizeFolder(GitWorktreeService.GetRepoRoot(slnDir) ?? slnDir);
        }

        /// <summary>An absolute path as stored in a profile: relative to the
        /// profile's folder, or null when it is not inside that folder.</summary>
        public static string? ToProfileRelative(string folder, string absolutePath)
        {
            string rel = Path.GetRelativePath(folder, Path.GetFullPath(absolutePath));
            return Path.IsPathRooted(rel) || rel.StartsWith("..") ? null : rel;
        }
    }
}
