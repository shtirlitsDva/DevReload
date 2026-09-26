using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DevReload.Oarx.Payload
{
    /// <summary>
    /// What the process has mapped right now: native images by FILE NAME and
    /// managed assemblies by SIMPLE NAME, each with the full path it came from.
    /// File names, because that is how both AutoCAD's dynamic linker and the
    /// Windows loader key an image: a second copy of a name from another folder
    /// resolves to the first one.
    /// </summary>
    public sealed class ProcessImages
    {
        private readonly Dictionary<string, string> _native;
        private readonly Dictionary<string, string> _managed;

        private ProcessImages(Dictionary<string, string> native, Dictionary<string, string> managed)
        {
            _native = native;
            _managed = managed;
        }

        public static ProcessImages FromPaths(IEnumerable<string> nativeImagePaths,
                                              IEnumerable<string> managedAssemblyPaths)
        {
            var native = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in nativeImagePaths)
                native.TryAdd(Path.GetFileName(p), p);
            var managed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in managedAssemblyPaths)
                managed.TryAdd(Path.GetFileNameWithoutExtension(p), p);
            return new ProcessImages(native, managed);
        }

        public string? NativePath(string fileName) =>
            _native.TryGetValue(fileName, out var p) ? p : null;

        public string? ManagedPath(string assemblyFileName) =>
            _managed.TryGetValue(Path.GetFileNameWithoutExtension(assemblyFileName), out var p) ? p : null;
    }

    /// <summary>The planner's answer: go ahead with these steps, or refuse.</summary>
    public abstract record PayloadDecision
    {
        /// <summary>Unload <see cref="UnloadModules"/> (file names, already in
        /// reverse load order), then map the rest in the order given. Companions
        /// already mapped with the same bytes are left out.</summary>
        public sealed record Proceed(
            IReadOnlyList<string> UnloadModules,
            IReadOnlyList<PayloadFile> PinNative,
            IReadOnlyList<PayloadFile> PreloadManaged,
            IReadOnlyList<PayloadFile> Modules,
            IReadOnlyList<PayloadFile> PostloadManaged) : PayloadDecision;

        /// <summary>Nothing may be mapped. <see cref="RestartRequired"/> says a
        /// fresh AutoCAD would accept the payload; false means it would not.</summary>
        public sealed record Refuse(bool RestartRequired, string Reason, string? File) : PayloadDecision;
    }

    /// <summary>
    /// Decides how a payload replaces the one loaded before it, from nothing but
    /// the two manifests and what the process has mapped. Pure: it touches no
    /// file and no AutoCAD API, so every rule below is unit-tested.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The previous payload's modules come out in REVERSE load order (the
    /// .arx before the .dbx whose classes it uses).</item>
    /// <item>A companion is never unloaded. One already mapped with the bytes the
    /// new payload asks for is kept; one mapped with other bytes, or from a
    /// source whose bytes are unknown, can only be replaced by a new AutoCAD:
    /// restart required.</item>
    /// <item>A module whose file name is mapped from anywhere other than the
    /// previous payload would silently resolve to that other copy, so the new
    /// build would never run. That is a clash, and a restart would not fix it
    /// (whatever loaded the other copy loads it again): refused.</item>
    /// </list>
    /// </remarks>
    public static class PayloadPlanner
    {
        public static PayloadDecision Decide(PayloadManifest? previous, PayloadManifest next, ProcessImages now)
        {
            var previousModules = previous?.Modules ?? Array.Empty<PayloadFile>();

            foreach (var m in next.Modules)
            {
                string? mapped = now.NativePath(m.FileName);
                if (mapped is null) continue;
                bool fromPrevious = previousModules.Any(p => SamePath(p.Path, mapped));
                if (!fromPrevious)
                    return new PayloadDecision.Refuse(false,
                        $"'{m.FileName}' is already loaded from {mapped}, not by this payload. " +
                        $"Loading {m.Path} would resolve to that copy. Unload whatever loaded it first.",
                        m.Path);
            }

            var previousCompanions = previous is null
                ? Array.Empty<PayloadFile>()
                : previous.PreloadNative.Concat(previous.PreloadManaged).Concat(previous.PostloadManaged).ToArray();

            var pin = new List<PayloadFile>();
            foreach (var c in next.PreloadNative)
            {
                if (Companion(c, now.NativePath(c.FileName), previousCompanions, out var refuse)) pin.Add(c);
                else if (refuse is not null) return refuse;
            }
            var pre = new List<PayloadFile>();
            foreach (var c in next.PreloadManaged)
            {
                if (Companion(c, now.ManagedPath(c.FileName), previousCompanions, out var refuse)) pre.Add(c);
                else if (refuse is not null) return refuse;
            }
            var post = new List<PayloadFile>();
            foreach (var c in next.PostloadManaged)
            {
                if (Companion(c, now.ManagedPath(c.FileName), previousCompanions, out var refuse)) post.Add(c);
                else if (refuse is not null) return refuse;
            }

            var unload = previousModules
                .Reverse()
                .Where(p => now.NativePath(p.FileName) is string mapped && SamePath(mapped, p.Path))
                .Select(p => p.FileName)
                .ToList();

            return new PayloadDecision.Proceed(unload, pin, pre, next.Modules, post);
        }

        /// <summary>True: map it. False with no refusal: already mapped with these
        /// bytes, keep it. False with a refusal: it can only change in a new AutoCAD.</summary>
        private static bool Companion(PayloadFile c, string? mappedFrom, PayloadFile[] previousCompanions,
                                      out PayloadDecision.Refuse? refuse)
        {
            refuse = null;
            if (mappedFrom is null) return true;
            if (SamePath(mappedFrom, c.Path)) return false;

            var known = previousCompanions.FirstOrDefault(p => SamePath(p.Path, mappedFrom));
            if (known is not null && known.Sha256 == c.Sha256) return false;

            refuse = new PayloadDecision.Refuse(true,
                known is null
                    ? $"'{c.FileName}' is already loaded from {mappedFrom}, whose bytes this payload cannot check. " +
                      "A companion is never unloaded, so only a new AutoCAD can load the payload's copy."
                    : $"'{c.FileName}' changed (loaded: {mappedFrom}, payload: {c.Path}). " +
                      "A companion is never unloaded, so only a new AutoCAD can load the new copy.",
                c.Path);
            return false;
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
