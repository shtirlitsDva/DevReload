using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevReload.Oarx.Payload
{
    /// <summary>A payload.json that cannot be loaded as written. The message names
    /// the file and the reason; nothing in a refused payload is mapped.</summary>
    public sealed class PayloadException : Exception
    {
        public PayloadException(string message) : base(message) { }
        public PayloadException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>One file of a payload: its absolute path and the SHA-256 the
    /// payload promises for it (lower-case hex).</summary>
    public sealed record PayloadFile(string Path, string Sha256)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// A prebuilt payload: what to map, and in which order, from a folder that
    /// was never built on this machine. The lists mean what the same lists mean
    /// in an OARX profile: native pins and managed companions are loaded once and
    /// never unloaded; the modules (.dbx before .arx) are the reloadable part.
    /// </summary>
    public sealed record PayloadManifest(
        string Directory,
        IReadOnlyList<PayloadFile> PreloadNative,
        IReadOnlyList<PayloadFile> PreloadManaged,
        IReadOnlyList<PayloadFile> Modules,
        IReadOnlyList<PayloadFile> PostloadManaged)
    {
        /// <summary>Every file in load order.</summary>
        public IEnumerable<PayloadFile> LoadOrder =>
            PreloadNative.Concat(PreloadManaged).Concat(Modules).Concat(PostloadManaged);
    }

    /// <summary>
    /// Reads and verifies <c>payload.json</c>. The schema is the one NSLOAD's
    /// native groups already use (<c>preloadNative</c>, <c>preloadManaged</c>,
    /// <c>modules</c>; comments and trailing commas allowed), plus
    /// <c>postloadManaged</c> and a <c>sha256</c> map keyed by each entry exactly
    /// as written. An entry is relative to the payload folder and may not leave
    /// it, or absolute (a pin shared with the stable stack).
    /// </summary>
    /// <remarks>Everything is checked before anything is mapped: a payload that
    /// fails any check is refused whole, never half-loaded.</remarks>
    public static class PayloadManifestReader
    {
        public const string FileName = "payload.json";

        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static PayloadManifest Read(string payloadDir)
        {
            string dir = Path.GetFullPath(payloadDir);
            string manifestPath = Path.Combine(dir, FileName);
            if (!File.Exists(manifestPath))
                throw new PayloadException($"No {FileName} in the payload folder {dir}.");

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(manifestPath), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new PayloadException($"{manifestPath} is not valid JSON: {ex.Message}", ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                var hashes = ReadHashes(root, manifestPath);

                var manifest = new PayloadManifest(
                    dir,
                    PreloadNative: ReadList(root, "preloadNative", dir, hashes),
                    PreloadManaged: ReadList(root, "preloadManaged", dir, hashes),
                    Modules: ReadList(root, "modules", dir, hashes),
                    PostloadManaged: ReadList(root, "postloadManaged", dir, hashes));

                if (manifest.Modules.Count == 0)
                    throw new PayloadException($"{manifestPath} lists no modules.");
                foreach (var m in manifest.Modules)
                {
                    string ext = Path.GetExtension(m.Path);
                    if (!ext.Equals(".dbx", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".arx", StringComparison.OrdinalIgnoreCase))
                        throw new PayloadException(
                            $"Module '{m.FileName}' is not a .dbx or .arx. Managed assemblies " +
                            "belong in preloadManaged or postloadManaged.");
                }

                foreach (var f in manifest.LoadOrder)
                    Verify(f);

                return manifest;
            }
        }

        private static Dictionary<string, string> ReadHashes(JsonElement root, string manifestPath)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("sha256", out var map) || map.ValueKind != JsonValueKind.Object)
                throw new PayloadException($"{manifestPath} has no \"sha256\" map.");
            foreach (var p in map.EnumerateObject())
                hashes[p.Name] = (p.Value.GetString() ?? "").Trim().ToLowerInvariant();
            return hashes;
        }

        private static IReadOnlyList<PayloadFile> ReadList(
            JsonElement root, string key, string dir, Dictionary<string, string> hashes)
        {
            if (!root.TryGetProperty(key, out var list))
                return Array.Empty<PayloadFile>();
            if (list.ValueKind != JsonValueKind.Array)
                throw new PayloadException($"\"{key}\" in {FileName} must be a list.");

            var files = new List<PayloadFile>();
            foreach (var item in list.EnumerateArray())
            {
                string entry = item.GetString() ?? "";
                if (!hashes.TryGetValue(entry, out string? sha))
                    throw new PayloadException($"'{entry}' in \"{key}\" has no entry in the sha256 map.");
                files.Add(new PayloadFile(Resolve(entry, dir), sha));
            }
            return files;
        }

        private static string Resolve(string entry, string dir)
        {
            if (Path.IsPathRooted(entry))
                return Path.GetFullPath(entry);

            string full = Path.GetFullPath(Path.Combine(dir, entry));
            string root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PayloadException($"'{entry}' points outside the payload folder {dir}.");
            return full;
        }

        private static void Verify(PayloadFile f)
        {
            if (!File.Exists(f.Path))
                throw new PayloadException($"Payload file not found: {f.Path}");
            string actual = Sha256Of(f.Path);
            if (!actual.Equals(f.Sha256, StringComparison.Ordinal))
                throw new PayloadException(
                    $"'{f.Path}' does not match its hash in {FileName}: expected {f.Sha256}, the file has {actual}.");
        }

        public static string Sha256Of(string path)
        {
            // Read-share + write-share: another process holding the file open for
            // writing must not make the check fail with a sharing violation.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
    }
}
