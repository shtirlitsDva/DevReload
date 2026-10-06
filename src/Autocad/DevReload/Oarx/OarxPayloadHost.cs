using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using DevReload.Hud;
using DevReload.Oarx.Payload;

using Exception = System.Exception;

namespace DevReload.Oarx
{
    /// <summary>One file a payload has mapped, as the caller can check it.</summary>
    public sealed record PayloadLoadedFile(string Path, string Version, string Sha256);

    /// <summary>Outcome of one <c>oarx_reload_payload</c> call. A refusal or a
    /// failure is a normal result, never an exception: <see cref="RestartRequired"/>
    /// says a new AutoCAD would accept the payload, <see cref="File"/> names the
    /// file the refusal is about.</summary>
    public sealed record PayloadReloadResult(
        string Name,
        bool Success,
        bool RestartRequired,
        string Message,
        string? File,
        IReadOnlyList<PayloadLoadedFile> Loaded);

    /// <summary>
    /// Reloads a PREBUILT payload in place: the previous payload of the same name
    /// comes out, the new one goes in, AutoCAD keeps running. The tester's
    /// sibling of <see cref="OarxManager.Reload"/> — no build, no solution, no
    /// profile.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <see cref="OarxRegistration"/>: nothing here is
    /// written to plugins.json and nothing is shown in the palette. What a name
    /// has mapped lives in memory only and dies with the process, which is the
    /// right lifetime — a new AutoCAD has mapped nothing.
    ///
    /// <para>The decision (what to unload, which companions to keep, when only a
    /// restart helps) is <see cref="PayloadPlanner"/>'s, pure and unit-tested.
    /// This class only takes the process snapshot, carries the plan out, and
    /// checks every step against the process afterwards: the companion hosts
    /// warn rather than throw, so a step is proven by what is mapped, not by the
    /// call returning.</para>
    /// </remarks>
    internal static class OarxPayloadHost
    {
        /// <summary>Per payload name: the files it has mapped right now. A
        /// companion kept from an earlier payload keeps its earlier path.</summary>
        private static readonly Dictionary<string, PayloadManifest> _mapped =
            new(StringComparer.OrdinalIgnoreCase);

        public static PayloadReloadResult Reload(string name, string payloadDir)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Refused(name, false, "A payload needs a name.", null);

            PayloadManifest next;
            try
            {
                next = PayloadManifestReader.Read(payloadDir);
            }
            catch (PayloadException ex)
            {
                return Refused(name, false, ex.Message, null);
            }

            _mapped.TryGetValue(name, out var previous);
            var decision = PayloadPlanner.Decide(previous, next, Snapshot());
            if (decision is PayloadDecision.Refuse refuse)
                return Refused(name, refuse.RestartRequired, refuse.Reason, refuse.File);

            var plan = (PayloadDecision.Proceed)decision;
            var log = new LineLog();

            // ── Unload the previous modules, .arx first ──────────────────
            // Each is proven out (not listed by the linker, not mapped: a
            // same-named image still mapped would take the new module's place at
            // load) before the next is asked. A module may refuse its own unload;
            // then the record keeps every module still in, at the path it is
            // mapped from, so the next call plans against them instead of
            // calling them another payload's. Not a restart case: the retry asks
            // the module again, so once its cause is gone (its drawings closed)
            // the same call continues from here.
            var run = OarxModuleHost.Unload(plan.UnloadModules);
            if (OarxModuleHost.DescribeRefusal(run) is string refusal)
            {
                _mapped[name] = ModuleUnloader.Remaining(previous!, run.Unloaded);
                string? at = previous!.Modules
                    .FirstOrDefault(m => m.FileName.Equals(run.StoppedAt, StringComparison.OrdinalIgnoreCase))?.Path;
                return Refused(name, false, refusal + " Nothing new was loaded.", at ?? run.StoppedAt);
            }

            // From here the previous modules are out. Whatever fails below, the
            // record must say what is mapped NOW, or the next call plans against
            // modules that are no longer there.
            var companions = KeptCompanions(previous, next, plan);
            var loadedModules = new List<PayloadFile>();
            _mapped[name] = Mapped(next, companions, loadedModules);

            // ── Companions before the modules ────────────────────────────
            foreach (var f in plan.PinNative)
                OarxCompanionHost.PinNative(f.Path, log);
            foreach (var f in plan.PreloadManaged)
                OarxCompanionHost.LoadManaged(f.Path, log);

            var images = Snapshot();
            var missing = NotMappedAtPath(plan.PinNative, images.NativePath)
                          ?? NotMappedAtPath(plan.PreloadManaged, images.ManagedPath);
            if (missing is not null)
                return Failed(name, companions, loadedModules, next, missing, log);

            // ── Modules, .dbx first ──────────────────────────────────────
            foreach (var m in plan.Modules)
            {
                try
                {
                    OarxModuleHost.Load(m.Path);
                    loadedModules.Add(m);
                }
                catch (OarxModuleException ex)
                {
                    // No half-loaded payload: what did load comes out again,
                    // and whatever refuses to stays on the record.
                    UnloadInReverse(loadedModules, log);
                    _mapped[name] = Mapped(next, companions, loadedModules);
                    return new PayloadReloadResult(name, false, false,
                        ex.Message + Tail(log), m.Path, Describe(_mapped[name]));
                }
            }

            // ── Postloads: an interop here pins the dbx it imports ───────
            foreach (var f in plan.PostloadManaged)
                OarxCompanionHost.LoadManaged(f.Path, log);
            missing = NotMappedAtPath(plan.PostloadManaged, Snapshot().ManagedPath);
            if (missing is not null)
                return Failed(name, companions, loadedModules, next, missing, log);

            _mapped[name] = Mapped(next, companions, loadedModules);
            string verdict = plan.UnloadModules.Count == 0 ? "loaded" : "reloaded";
            return new PayloadReloadResult(name, true, false, verdict + Tail(log), null,
                                           Describe(_mapped[name]));
        }

        // ── Steps ────────────────────────────────────────────────────

        /// <summary>The companions of <paramref name="next"/> as they will be
        /// mapped: a kept one at the path it is already mapped from, a new one at
        /// the payload's path.</summary>
        private static Dictionary<PayloadFile, PayloadFile> KeptCompanions(
            PayloadManifest? previous, PayloadManifest next, PayloadDecision.Proceed plan)
        {
            var toMap = new HashSet<PayloadFile>(
                plan.PinNative.Concat(plan.PreloadManaged).Concat(plan.PostloadManaged));
            var earlier = previous is null
                ? new List<PayloadFile>()
                : previous.PreloadNative.Concat(previous.PreloadManaged).Concat(previous.PostloadManaged).ToList();

            var result = new Dictionary<PayloadFile, PayloadFile>();
            foreach (var c in next.PreloadNative.Concat(next.PreloadManaged).Concat(next.PostloadManaged))
            {
                var kept = toMap.Contains(c)
                    ? c
                    : earlier.FirstOrDefault(e => e.FileName.Equals(c.FileName, StringComparison.OrdinalIgnoreCase)
                                                  && e.Sha256 == c.Sha256) ?? c;
                result[c] = kept;
            }
            return result;
        }

        private static PayloadManifest Mapped(
            PayloadManifest next, Dictionary<PayloadFile, PayloadFile> companions, List<PayloadFile> modules) =>
            new(next.Directory,
                next.PreloadNative.Select(c => companions[c]).ToList(),
                next.PreloadManaged.Select(c => companions[c]).ToList(),
                modules.ToList(),
                next.PostloadManaged.Select(c => companions[c]).ToList(),
                next.Files);

        /// <summary>The first file not mapped at its own path. The companion hosts
        /// warn instead of throwing, so this is where a failed pin or load shows.</summary>
        private static PayloadFile? NotMappedAtPath(IEnumerable<PayloadFile> files, Func<string, string?> mappedFrom) =>
            files.FirstOrDefault(f => mappedFrom(f.FileName) is not string at ||
                                      !Path.GetFullPath(at).Equals(f.Path, StringComparison.OrdinalIgnoreCase));

        private static PayloadReloadResult Failed(
            string name, Dictionary<PayloadFile, PayloadFile> companions, List<PayloadFile> loadedModules,
            PayloadManifest next, PayloadFile missing, LineLog log)
        {
            UnloadInReverse(loadedModules, log);
            _mapped[name] = Mapped(next, companions, loadedModules);
            return new PayloadReloadResult(name, false, false,
                $"'{missing.Path}' did not load at its payload path." + Tail(log),
                missing.Path, Describe(_mapped[name]));
        }

        /// <summary>Takes <paramref name="modules"/> out again, last first, and
        /// leaves in the list only the ones still in: a module that refuses its
        /// unload stays on the record.</summary>
        private static void UnloadInReverse(List<PayloadFile> modules, LineLog log)
        {
            var run = OarxModuleHost.Unload(modules.Select(m => m.FileName).Reverse().ToList());
            if (OarxModuleHost.DescribeRefusal(run) is string refusal)
                log.Line(refusal);
            var gone = new HashSet<string>(run.Unloaded, StringComparer.OrdinalIgnoreCase);
            modules.RemoveAll(m => gone.Contains(m.FileName));
        }

        // ── Process snapshot ─────────────────────────────────────────

        private static ProcessImages Snapshot()
        {
            var native = Process.GetCurrentProcess().Modules
                .Cast<ProcessModule>()
                .Select(m => m.FileName)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            var managed = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .ToList();
            return ProcessImages.FromPaths(native, managed);
        }

        // ── Results ──────────────────────────────────────────────────

        private static PayloadReloadResult Refused(string name, bool restart, string reason, string? file) =>
            new(name, false, restart, reason, file,
                _mapped.TryGetValue(name ?? "", out var m) ? Describe(m) : Array.Empty<PayloadLoadedFile>());

        private static IReadOnlyList<PayloadLoadedFile> Describe(PayloadManifest m) =>
            m.LoadOrder.Select(f => new PayloadLoadedFile(
                f.Path, FileVersionInfo.GetVersionInfo(f.Path).FileVersion ?? "", f.Sha256)).ToList();

        private static string Tail(LineLog log) =>
            log.Lines.Count == 0 ? "" : " " + string.Join(" ", log.Lines);

        /// <summary>Collects the companion hosts' warnings into the result: this
        /// call runs headless, with no HUD and no palette.</summary>
        private sealed class LineLog : IReloadProgress
        {
            public List<string> Lines { get; } = new();
            public void Begin(string title, ReloadCycle cycle) { }
            public void Step(ReloadStep step) { }
            public void Line(string text) => Lines.Add(text);
            public void Finish(string verdict, bool ok) { }
        }
    }
}
