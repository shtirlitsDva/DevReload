using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Internal;
using Autodesk.AutoCAD.Runtime;

using DevReload.Core;
using DevReload.Diagnostics;
using DevReload.Hud;

using Exception = System.Exception;

namespace DevReload.Oarx
{
    /// <summary>Outcome of one OARX lifecycle operation.</summary>
    public record OarxActionResult(
        string Name,
        bool Success,
        bool Loaded,
        string Message,
        IReadOnlyList<string>? Modules = null,
        string? BuildLog = null);

    /// <summary>One module inside an OARX group, projected for callers outside
    /// the Oarx module.</summary>
    /// <remarks><see cref="TargetPath"/> and <see cref="ModuleFileName"/> are null
    /// until MSBuild has been asked where the module lands. There is no guessed
    /// default — a wrong output directory is the exact wrong-but-plausible failure
    /// this design refuses to have (research F7).</remarks>
    public sealed record OarxModuleInfo(
        string ProjectFilePath,
        string ProjectName,
        string? TargetPath,
        string? ModuleFileName,
        bool Loaded);

    /// <summary>One profile of an OARX group, as configured. Paths are as stored:
    /// module projects relative to <see cref="WorktreePath"/>, companions absolute
    /// or relative to it.</summary>
    public sealed record OarxProfileInfo(
        string Name,
        string WorktreePath,
        bool FolderExists,
        bool Active,
        IReadOnlyList<string> ProjectFilePaths,
        IReadOnlyList<string> MsBuildProperties,
        IReadOnlyList<string> PreloadNativeModules,
        IReadOnlyList<string> PreloadManagedAssemblies,
        IReadOnlyList<string> PostloadManagedAssemblies);

    /// <summary>One OARX group's registration and live state.</summary>
    /// <remarks><see cref="Loaded"/> is the state of the group AS A WHOLE. A cycle
    /// that died part-way sets <see cref="PartiallyLoaded"/> instead, which is a
    /// different thing and must not read as loaded.
    /// <para><see cref="Modules"/> and <see cref="LiveProfile"/> describe what is
    /// registered live; <see cref="Profiles"/> and <see cref="ActiveProfile"/> the
    /// configuration as last saved. They differ exactly while
    /// <see cref="ConfigPending"/> — a change staged against a loaded group.</para></remarks>
    public sealed record OarxPluginInfo(
        string Name,
        bool Loaded,
        bool PartiallyLoaded,
        string BuildConfiguration,
        string Solution,
        string ActiveProfile,
        string LiveProfile,
        string LiveWorktreePath,
        bool ConfigPending,
        string? Problem,
        IReadOnlyList<OarxModuleInfo> Modules,
        IReadOnlyList<OarxProfileInfo> Profiles);

    /// <summary>
    /// The OARX plugin lifecycle: registry, build, load, unload, reload.
    /// </summary>
    /// <remarks>
    /// Deliberately a sibling of <see cref="PluginManager"/> rather than an
    /// extension of it. The two lifecycles share a shape (Load / Reload / Unload
    /// / BuildOnly over a named registration) and nothing else:
    ///
    /// <para>The .NET path stream-loads assembly BYTES into a collectible ALC, so
    /// the file is never locked and a build can happen while the old plugin is
    /// still running — build first, then swap, and a failed build costs nothing.</para>
    ///
    /// <para>A native module is MAPPED from its file, so the file is locked for as
    /// long as it is loaded. The order is forced the other way — unload, verify,
    /// build, load — and a failed build therefore leaves the session with nothing
    /// loaded. That is not a defect to hide; it is reported loudly instead.</para>
    /// </remarks>
    internal static class OarxManager
    {
        private const string Platform = "x64";

        private static readonly Dictionary<string, OarxRegistration> _plugins =
            new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? Registered;
        public static event Action<string>? Unregistered;
        public static event Action<string>? StateChanged;

        // ── Registry ──────────────────────────────────────────────────

        public static void Add(OarxRegistration reg)
        {
            _plugins[reg.Name] = reg;
            Registered?.Invoke(reg.Name);
        }

        public static bool IsRegistered(string name) => _plugins.ContainsKey(name);

        public static bool IsLoaded(string name) =>
            _plugins.TryGetValue(name, out var reg) && reg.IsLoaded;

        public static IReadOnlyList<string> GetRegisteredNames() => _plugins.Keys.ToList();

        /// <summary>Every registered group with its live state. The single source
        /// of truth for registration + load status, mirroring
        /// <c>PluginManager.ListPluginSnapshots</c>.</summary>
        public static IReadOnlyList<OarxPluginInfo> ListSnapshots() =>
            _plugins.Values.Select(SnapshotOf).ToList();

        private static OarxPluginInfo SnapshotOf(OarxRegistration reg)
        {
            var entry = reg.PendingEntry ?? reg.Source;
            return new(
                Name: reg.Name,
                Loaded: reg.IsLoaded,
                PartiallyLoaded: reg.IsPartiallyLoaded,
                BuildConfiguration: reg.BuildConfiguration,
                Solution: entry.Solution,
                ActiveProfile: entry.ActiveProfile,
                LiveProfile: reg.ProfileName,
                LiveWorktreePath: reg.WorktreePath,
                ConfigPending: reg.PendingEntry != null,
                Problem: reg.Problem,
                Modules: reg.Modules.Select(m => new OarxModuleInfo(
                    ProjectFilePath: m.ProjectFilePath,
                    ProjectName: m.ProjectName,
                    TargetPath: m.TargetPath,
                    ModuleFileName: m.ModuleFileName,
                    Loaded: m.IsLoaded)).ToList(),
                Profiles: entry.Profiles.Select(p => new OarxProfileInfo(
                    Name: p.Name,
                    WorktreePath: p.WorktreePath,
                    FolderExists: p.FolderExists,
                    Active: p.Name.Equals(entry.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                    ProjectFilePaths: p.ProjectFilePaths.ToList(),
                    MsBuildProperties: p.MsBuildProperties.ToList(),
                    PreloadNativeModules: p.PreloadNativeModules.ToList(),
                    PreloadManagedAssemblies: p.PreloadManagedAssemblies.ToList(),
                    PostloadManagedAssemblies: p.PostloadManagedAssemblies.ToList())).ToList());
        }

        /// <summary>The group's configuration as last saved — the staged entry
        /// while one is pending, otherwise the one the registration was built
        /// from. What the palette card shows.</summary>
        public static OarxPluginEntry? GetEntry(string name) =>
            _plugins.TryGetValue(name, out var reg) ? reg.PendingEntry ?? reg.Source : null;

        /// <summary>A short note for a group with a staged change, or null.</summary>
        public static string? DescribePending(string name)
        {
            if (!_plugins.TryGetValue(name, out var reg) || reg.PendingEntry == null) return null;
            return reg.PendingEntry.ActiveProfile.Equals(reg.ProfileName, StringComparison.OrdinalIgnoreCase)
                ? "config change staged"
                : $"switch to '{reg.PendingEntry.ActiveProfile}' staged";
        }


        internal static bool TryGet(string name, out OarxRegistration reg) =>
            _plugins.TryGetValue(name, out reg!);

        public static bool UnregisterInMemory(string name)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return false;
            // Category A. Reported and collected rather than discarded: a module
            // that fails to unmap keeps its .arx file locked, which is exactly the
            // failure worth knowing about. Rethrown after the registration is
            // cleaned up, so the in-memory state stays consistent either way.
            var failures = new List<Exception>();
            DevReloadDiagnostics.Step(failures, $"{name}: UnloadModules",
                () => UnloadModules(reg, NullReloadProgress.Instance));
            foreach (var (group, cmd, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmd);
            reg.LoaderCommands.Clear();
            _plugins.Remove(name);
            Unregistered?.Invoke(name);
            DevReloadDiagnostics.ThrowIfAny($"OarxManager.UnregisterInMemory({name})", failures);
            return true;
        }

        // ── Config edits ──────────────────────────────────────────────

        /// <summary>True when the live registration was built from an entry
        /// equal to <paramref name="entry"/> — the diff a config resync needs.
        /// Serialize-compare: cheap, and it cannot drift from the entry shape.</summary>
        public static bool MatchesSource(string name, OarxPluginEntry entry) =>
            _plugins.TryGetValue(name, out var reg) &&
            System.Text.Json.JsonSerializer.Serialize(reg.Source) ==
            System.Text.Json.JsonSerializer.Serialize(entry);

        public static bool HasPendingConfig(string name) =>
            _plugins.TryGetValue(name, out var reg) && reg.PendingEntry != null;

        /// <summary>Stage a changed on-disk entry against a group whose modules
        /// are currently mapped. Applied by the next Load/Reload; the running
        /// registration is never yanked out from under loaded modules.</summary>
        internal static void StagePendingEntry(string name, OarxPluginEntry entry)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return;
            reg.PendingEntry = entry;
            StateChanged?.Invoke(name);
        }

        /// <summary>
        /// Take a freshly saved config entry live.
        /// </summary>
        /// <remarks>
        /// The group's own fields (configuration, prefix) always apply now. What
        /// the active profile resolves to applies now too, UNLESS its module list
        /// changed on a group with mapped modules — a mapped module can't be
        /// swapped under itself — in which case the whole entry is staged and the
        /// next load/reload applies it. Switching profile is exactly such a
        /// change whenever the other profile builds from another folder.
        /// </remarks>
        internal static OarxActionResult ApplyEntry(OarxPluginEntry entry, bool prefixChanged)
        {
            if (!_plugins.TryGetValue(entry.Name, out var reg))
                return new OarxActionResult(entry.Name, true, false,
                    "updated plugins.json (group is not registered live)");

            var fresh = OarxConfigLoader.BuildRegistration(entry);
            bool modulesChanged = !reg.Modules.Select(m => m.ProjectFilePath)
                .SequenceEqual(fresh.Modules.Select(m => m.ProjectFilePath),
                    StringComparer.OrdinalIgnoreCase);

            if (modulesChanged && (reg.IsLoaded || reg.IsPartiallyLoaded))
            {
                PatchGroupFields(reg, entry, prefixChanged);
                reg.PendingEntry = entry;
                StateChanged?.Invoke(entry.Name);
                return new OarxActionResult(entry.Name, true, reg.IsLoaded,
                    "saved — the group is loaded, so the new module set applies at the next " +
                    "load/reload (the current modules stay mapped until then)");
            }

            if (modulesChanged)
            {
                SwapRegistration(reg, entry);
                return new OarxActionResult(entry.Name, true, false, "saved");
            }

            PatchGroupFields(reg, entry, prefixChanged);
            PatchProfileFields(reg, fresh);
            reg.Source = entry;
            reg.PendingEntry = null;
            StateChanged?.Invoke(entry.Name);
            return new OarxActionResult(entry.Name, true, reg.IsLoaded,
                reg.IsLoaded
                    ? "saved — properties apply at the next build, companions at the next load"
                    : "saved");
        }

        private static void PatchGroupFields(
            OarxRegistration reg, OarxPluginEntry entry, bool prefixChanged)
        {
            reg.BuildConfiguration = entry.BuildConfiguration;
            if (!prefixChanged) return;

            foreach (var (group, cmd, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmd);
            reg.LoaderCommands.Clear();
            RegisterLoaderCommands(entry.Name, entry.CommandPrefix ?? entry.Name);
        }

        /// <remarks>
        /// Only reached when the module projects are unchanged, and deliberately
        /// does NOT invalidate the modules' resolved TargetPaths. The dynamic
        /// linker keys a loaded module on its FILE NAME (research F6), so the
        /// group's loaded state stays readable, and the next Load/Reload
        /// re-resolves the paths anyway.
        /// </remarks>
        private static void PatchProfileFields(OarxRegistration reg, OarxRegistration fresh)
        {
            reg.ProfileName = fresh.ProfileName;
            reg.WorktreePath = fresh.WorktreePath;
            reg.SolutionFilePath = fresh.SolutionFilePath;
            reg.Problem = fresh.Problem;
            reg.MsBuildProperties.Clear();
            reg.MsBuildProperties.AddRange(fresh.MsBuildProperties);
            reg.PreloadNativeModules.Clear();
            reg.PreloadNativeModules.AddRange(fresh.PreloadNativeModules);
            reg.PreloadManagedAssemblies.Clear();
            reg.PreloadManagedAssemblies.AddRange(fresh.PreloadManagedAssemblies);
            reg.PostloadManagedAssemblies.Clear();
            reg.PostloadManagedAssemblies.AddRange(fresh.PostloadManagedAssemblies);
        }

        /// <summary>Replace a registration wholesale from its entry. Only legal
        /// with nothing mapped — the callers guarantee that.</summary>
        private static OarxRegistration SwapRegistration(
            OarxRegistration reg, OarxPluginEntry entry)
        {
            foreach (var (group, cmd, _) in reg.LoaderCommands)
                Utils.RemoveCommand(group, cmd);
            reg.LoaderCommands.Clear();

            var fresh = OarxConfigLoader.BuildRegistration(entry);
            _plugins[entry.Name] = fresh;
            RegisterLoaderCommands(entry.Name, entry.CommandPrefix ?? entry.Name);
            StateChanged?.Invoke(entry.Name);
            return fresh;
        }

        /// <summary>Apply a staged config entry once the group's modules are
        /// out. Returns the registration the cycle must continue with.</summary>
        private static OarxRegistration ConsumePending(
            OarxRegistration reg, IReloadProgress ui)
        {
            if (reg.PendingEntry == null) return reg;
            ui.Line("applying the config change that was staged while the group was loaded");
            return SwapRegistration(reg, reg.PendingEntry);
        }

        // ── Lifecycle ─────────────────────────────────────────────────

        /// <summary>Load the group as it currently sits on disk, building only
        /// the modules whose output is missing.</summary>
        public static OarxActionResult Load(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: load", Cycle);

            try
            {
                if (reg.IsLoaded)
                {
                    // The modules may be resident without this group having run —
                    // the dynamic linker keys on FILE NAME, so a demand-loaded
                    // copy from another directory reads as "loaded" here. The
                    // companions are idempotent, so deliver them regardless: a
                    // load command's contract is "the whole stack is up", not
                    // "the native half happened to be".
                    RunPreloads(reg, ui);
                    RunPostloads(reg, ui);
                    ui.Finish("already loaded", true);
                    return Result(reg, true, reg.PendingEntry == null
                        ? "already loaded (companions ensured)"
                        : "already loaded (companions ensured); a staged config " +
                          "change applies at the next reload");
                }

                // A staged config edit applies now — the group is not (fully)
                // loaded, so the module list is free to change. A partial load
                // is emptied first so no old module stays mapped unmanaged.
                if (reg.PendingEntry != null)
                {
                    if (reg.IsPartiallyLoaded)
                    {
                        ui.Step(ReloadStep.Unload);
                        ui.Line("clearing a partially-loaded group");
                        UnloadModules(reg, ui);
                    }
                    reg = ConsumePending(reg, ui);
                }

                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }

                // A partially-loaded group (a previous cycle died mid-way) must be
                // emptied before loading, or the load order is not what it claims.
                if (reg.IsPartiallyLoaded)
                {
                    ui.Step(ReloadStep.Unload);
                    ui.Line("clearing a partially-loaded group");
                    UnloadModules(reg, ui);
                }

                var missing = reg.Modules.Where(m => !File.Exists(m.TargetPath!)).ToList();
                if (missing.Count > 0)
                {
                    ui.Step(ReloadStep.Build);
                    ui.Line($"{missing.Count} module(s) not built yet");
                    var build = BuildModules(reg, ui);
                    if (build != null) { ui.Finish(build.Value.Message, false); return Result(reg, false, build.Value.Message, build.Value.Log); }
                }

                ui.Step(ReloadStep.Load);
                RunPreloads(reg, ui);
                LoadModules(reg, ui);
                RunPostloads(reg, ui);
                ui.Finish("loaded", true);
                return Result(reg, true, "loaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        /// <summary>
        /// The dev loop: unload the whole group, prove every output is writable,
        /// rebuild, load again.
        /// </summary>
        public static OarxActionResult Reload(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: reload", Cycle);

            try
            {
                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }
                WarnAboutOtherHosts(ui);

                ui.Step(ReloadStep.Unload);
                UnloadModules(reg, ui);

                // With the OLD module set out, a staged config edit can land.
                // The new module list needs its own target resolution before
                // the writability check below can speak about it.
                if (reg.PendingEntry != null)
                {
                    reg = ConsumePending(reg, ui);
                    var reResolve = ResolveTargets(reg, ui);
                    if (reResolve != null) { ui.Finish(reResolve, false); return Result(reg, false, reResolve); }
                }

                // The unload said it succeeded. This is where that is checked
                // against the only authority that matters — whether the linker
                // could actually rewrite the file.
                ui.Step(ReloadStep.Verify);
                var locked = reg.Modules
                    .Where(m => !OarxModuleHost.IsFileWritable(m.TargetPath!))
                    .ToList();
                if (locked.Count > 0)
                {
                    string why = string.Join(" ",
                        locked.Select(m => OarxModuleHost.DescribeStillLocked(m.TargetPath!)));
                    string msg =
                        "ABORTED after unload — the build would fail with LNK1168. " + why +
                        " Nothing was rebuilt; the modules are UNLOADED.";
                    ui.Line(msg);
                    ui.Finish("still locked after unload", false);
                    return Result(reg, false, msg);
                }
                ui.Line("all module files are writable");

                ui.Step(ReloadStep.Build);
                var build = BuildModules(reg, ui);
                if (build != null)
                {
                    string msg = build.Value.Message +
                        " The modules remain UNLOADED — a native module is mapped from its " +
                        "file, so it had to be unloaded before the linker could rewrite it.";
                    ui.Finish("build failed", false);
                    return Result(reg, false, msg, build.Value.Log);
                }

                ui.Step(ReloadStep.Load);
                RunPreloads(reg, ui);
                LoadModules(reg, ui);
                RunPostloads(reg, ui);
                ui.Finish("reloaded", true);
                return Result(reg, true, "reloaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        public static OarxActionResult Unload(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            try
            {
                if (!reg.IsLoaded && !reg.IsPartiallyLoaded)
                    return Result(reg, true, "not loaded");

                ui.Begin($"{name}: unload", Cycle);
                ui.Step(ReloadStep.Unload);
                UnloadModules(reg, ui);
                ui.Finish("unloaded", true);
                return Result(reg, true, "unloaded");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        /// <summary>Build without loading. Only possible while the group is
        /// unloaded — the outputs are locked otherwise.</summary>
        public static OarxActionResult BuildOnly(string name, IReloadProgress? progress = null)
        {
            if (!_plugins.TryGetValue(name, out var reg))
                return new OarxActionResult(name, false, false, "not registered");

            var ui = progress ?? DefaultProgress();
            ui.Begin($"{name}: build", Cycle);
            try
            {
                ui.Step(ReloadStep.Preflight);
                var resolve = ResolveTargets(reg, ui);
                if (resolve != null) { ui.Finish(resolve, false); return Result(reg, false, resolve); }

                if (reg.Modules.Any(m => m.IsLoaded))
                {
                    const string msg =
                        "cannot build while the group is loaded — a native module's file is " +
                        "locked while it is mapped. Unload it first, or use Reload.";
                    ui.Finish("loaded; build refused", false);
                    return Result(reg, false, msg);
                }

                ui.Step(ReloadStep.Build);
                var build = BuildModules(reg, ui);
                if (build != null) { ui.Finish("build failed", false); return Result(reg, false, build.Value.Message, build.Value.Log); }

                ui.Finish("built", true);
                return Result(reg, true, "built (not loaded)");
            }
            catch (Exception ex)
            {
                ui.Finish(ex.Message, false);
                return Result(reg, false, ex.Message);
            }
        }

        public static OarxActionResult UnloadAll()
        {
            int n = 0;
            // Category A. One group failing to unmap must not stop the others
            // from being unmapped, but it must not vanish either — a still-mapped
            // .arx keeps its file locked for whatever runs next.
            var failures = new List<Exception>();
            foreach (var reg in _plugins.Values)
            {
                if (!reg.Modules.Any(m => m.IsLoaded)) continue;
                int before = failures.Count;
                DevReloadDiagnostics.Step(failures, $"{reg.Name}: UnloadModules",
                    () => UnloadModules(reg, NullReloadProgress.Instance));
                // Only count groups that actually came out.
                if (failures.Count == before) n++;
            }
            DevReloadDiagnostics.ThrowIfAny("OarxManager.UnloadAll", failures);
            return new OarxActionResult("*", true, false, $"unloaded {n} OARX group(s)");
        }

        // ── Steps ─────────────────────────────────────────────────────

        /// <summary>Ask MSBuild where each module lands. Returns null on success,
        /// or the reason it could not be resolved — never a guessed path.</summary>
        private static string? ResolveTargets(OarxRegistration reg, IReloadProgress ui)
        {
            if (reg.Problem != null)
                return reg.Problem;
            // Checked on every cycle, not just on save: agents remove worktrees,
            // and a profile pointing at a vanished folder must say so rather
            // than build something else.
            if (!Directory.Exists(reg.WorktreePath))
                return $"profile '{reg.ProfileName}': worktree folder not found: {reg.WorktreePath}. " +
                       "Activate another profile, or remove this one in the profiles window.";
            if (reg.Modules.Count == 0)
                return $"'{reg.Name}' has no modules registered.";

            string solutionDir = reg.SolutionDirectory;
            if (!Directory.Exists(solutionDir))
                return $"solution directory does not exist: {solutionDir}";

            foreach (var m in reg.Modules)
            {
                string proj = m.ProjectFilePath;
                if (!File.Exists(proj))
                    return $"project file not found: {proj}";

                string? target = BuildService.QueryMsBuildProperty(
                    proj, "TargetPath", reg.BuildConfiguration, Platform, solutionDir,
                    reg.MsBuildProperties);

                if (string.IsNullOrEmpty(target))
                    return $"MSBuild could not resolve TargetPath for '{m.ProjectName}' " +
                           $"({reg.BuildConfiguration}|{Platform}). " +
                           "Check the configuration exists and the project evaluates.";

                m.TargetPath = target;
                _ = m.Kind; // throws OarxModuleException if the extension is not ObjectARX
                ui.Line($"{m.ProjectName} -> {Path.GetFileName(target)}");
            }
            return null;
        }

        /// <summary>Companions that come BEFORE the modules: full-path native pins
        /// (so base-name references bind to the canonical copies) and managed
        /// assemblies that must be running while a module initialises (a trace UI
        /// listening for a dbx's load-time logs).</summary>
        private static void RunPreloads(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var p in reg.PreloadNativeModules)
                OarxCompanionHost.PinNative(p, ui);
            foreach (var p in reg.PreloadManagedAssemblies)
                OarxCompanionHost.LoadManaged(p, ui);
        }

        /// <summary>Companions that come AFTER the modules: managed assemblies
        /// that import from a module and must not be the thing that maps it
        /// (a mixed-mode interop over the group's dbx).</summary>
        private static void RunPostloads(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var p in reg.PostloadManagedAssemblies)
                OarxCompanionHost.LoadManaged(p, ui);
        }

        private static void LoadModules(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var m in reg.Modules)
            {
                OarxModuleHost.Load(m.TargetPath!);
                ui.Line($"loaded {m.ModuleFileName}");
            }
            StateChanged?.Invoke(reg.Name);
        }

        /// <summary>Unload in REVERSE declaration order: the .arx that uses the
        /// .dbx's classes must go first.</summary>
        private static void UnloadModules(OarxRegistration reg, IReloadProgress ui)
        {
            foreach (var m in Enumerable.Reverse(reg.Modules))
            {
                if (m.ModuleFileName == null) continue;
                OarxModuleHost.Unload(m.ModuleFileName);
                ui.Line($"unloaded {m.ModuleFileName}");
            }
            StateChanged?.Invoke(reg.Name);
        }

        private static (string Message, string? Log)? BuildModules(
            OarxRegistration reg, IReloadProgress ui)
        {
            // ONE msbuild run over every module, with -m: modules that do not
            // reference each other (a dbx and the arx that binds it by name)
            // compile side by side, and a shared static lib builds once. A
            // per-module loop would make each module wait for the previous link.
            ui.Line($"building {string.Join(", ", reg.Modules.Select(m => m.ProjectName))} " +
                    $"({reg.BuildConfiguration}|{Platform})");

            // The HUD's own sink already streams every build line, so the
            // BuildService progress callback is left null here — routing it
            // through ui.Line as well would report each line twice.
            var result = BuildService.BuildProjects(
                reg.Modules.Select(m => m.ProjectFilePath).ToList(),
                reg.BuildConfiguration, Platform, null, reg.SolutionDirectory,
                new PumpedBuildRunner(ui), reg.MsBuildProperties);

            if (!result.Success)
            {
                string where = result.FailedProjects.Count > 0
                    ? string.Join(", ", result.FailedProjects.Select(p => $"'{p}'"))
                    : $"group '{reg.Name}'";
                return ($"build FAILED in {where} ({result.Errors} error(s)).", result.Log);
            }

            for (int i = 0; i < reg.Modules.Count; i++)
                reg.Modules[i].TargetPath = result.OutputPaths[i];
            return null;
        }

        // F8: the writability probe is process-global, so a second AutoCAD holding
        // the same modules blocks the build. Cheap to check, and it turns a
        // baffling post-unload abort into an obvious one.
        private static void WarnAboutOtherHosts(IReloadProgress ui)
        {
            try
            {
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                var others = System.Diagnostics.Process.GetProcessesByName("acad")
                    .Select(p => p.Id).Where(id => id != self).ToList();
                if (others.Count > 0)
                    ui.Line($"NOTE: another AutoCAD is running (pid {string.Join(", ", others)}). " +
                            "If it has these modules loaded, the rebuild will be blocked.");
            }
            catch (Exception ex)
            {
                // Category B — report, do not rethrow. This only prints an advisory
                // note before a build; failing to enumerate processes must not stop
                // the build the user actually asked for.
                DevReloadDiagnostics.Report("OarxManager: sibling-AutoCAD probe", ex);
            }
        }

        // ── Settings ──────────────────────────────────────────────────

        // No UpdateBuildConfiguration / UpdateActiveWorktree here, on purpose.
        // Both used to exist for the palette while the MCP surface wrote the same
        // fields through OarxConfigLoader.UpdatePlugin — two write paths to one
        // field, which is how the worktree ended up reachable from the palette and
        // not from the tool. Every caller goes through UpdatePlugin now.

        /// <summary>Module file names in load order, for display. Falls back to
        /// the project name for modules MSBuild has not been asked about yet —
        /// this is a label, not a path, so there is nothing to get wrong.</summary>
        public static IReadOnlyList<string> DescribeModules(string name) =>
            _plugins.TryGetValue(name, out var reg)
                ? reg.Modules.Select(m => m.ModuleFileName ?? m.ProjectName).ToList()
                : Array.Empty<string>();

        // ── Loader commands ───────────────────────────────────────────

        public static void RegisterLoaderCommands(string name, string prefix)
        {
            if (!_plugins.TryGetValue(name, out var reg)) return;
            prefix = prefix.ToUpperInvariant();
            const string group = "DEVRELOAD";

            void Add(string suffix, Action action)
            {
                string cmd = prefix + suffix;
                CommandCallback cb = () => action();
                Utils.AddCommand(group, cmd, cmd, CommandFlags.Modal, cb);
                reg.LoaderCommands.Add((group, cmd, cb));
            }

            Add("LOAD", () => Load(name));
            Add("DEV", () => Reload(name));
            Add("UNLOAD", () => Unload(name));
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static OarxActionResult Result(
            OarxRegistration reg, bool success, string message, string? log = null)
        {
            StateChanged?.Invoke(reg.Name);
            return new OarxActionResult(
                reg.Name, success, reg.IsLoaded, message,
                reg.Modules.Select(m => m.ModuleFileName ?? m.ProjectName).ToList(),
                log);
        }

        /// <summary>
        /// The sink a cycle reports to when the caller does not supply one: the
        /// transient HUD for the person watching it happen, and the command line
        /// for the record they can scroll back to.
        /// </summary>
        /// <remarks>
        /// No editor means no drawing, and both sinks need one — that is the MCP /
        /// startup path, which reads the returned <see cref="OarxActionResult"/>
        /// instead of watching.
        /// </remarks>
        /// <summary>
        /// The OARX cycle, in the order it runs. Unload comes BEFORE build because
        /// a loaded native module locks its own file: the linker cannot write over
        /// an .arx that is still mapped. That ordering is why a failed build leaves
        /// the group unloaded (research F14).
        /// </summary>
        private static readonly ReloadCycle Cycle = new(
            (ReloadStep.Preflight, "resolving module outputs"),
            (ReloadStep.Unload,    "unloading modules"),
            (ReloadStep.Verify,    "checking the files are writable"),
            (ReloadStep.Build,     "building"),
            (ReloadStep.Load,      "loading modules"));

        private static IReloadProgress DefaultProgress()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return NullReloadProgress.Instance;

            void Write(string msg) => ed.WriteMessage("\n" + msg);
            return new CompositeReloadProgress(
                new ReloadHud(warn => Write("[OARX] " + warn)),
                new EditorReloadProgress(Write, "OARX"));
        }
    }
}
