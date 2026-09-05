using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Autodesk.AutoCAD.Runtime;

using Exception = System.Exception;

using DevReload.Diagnostics;

namespace DevReload.Oarx
{
    /// <summary>
    /// Raised when a native module refuses to load or unload. Carries a message
    /// written for the person staring at the palette, not a status code.
    /// </summary>
    public class OarxModuleException : Exception
    {
        public OarxModuleException(string message) : base(message) { }
        public OarxModuleException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The only place in DevReload that touches AutoCAD's dynamic linker.
    /// Everything the OARX lifecycle knows about loading, unloading and proving
    /// a native module actually left the process lives behind this surface.
    /// </summary>
    /// <remarks>
    /// The behaviour encoded here was measured against Civil 3D 2025 with the
    /// <c>labs/oarx</c> module pair; see <c>docs/oarx-port/research.md</c>
    /// (findings F1-F8). Three of those findings are load-bearing and easy to
    /// undo by accident:
    ///
    /// <list type="number">
    /// <item><b>F1</b> — <c>UnloadModule</c>'s second argument MUST be false.
    /// Passing true throws InvalidOperationException for every module, in every
    /// calling context. This is the single reason the whole approach looked
    /// impossible at first.</item>
    /// <item><b>F2</b> — the unload is synchronous. The module is unregistered,
    /// unmapped from the process, and its file writable before the call returns.
    /// There is no deferred FreeLibrary here (that belongs to <c>acedArxUnload</c>,
    /// which DevReload does not use), so no idle-driven state machine is needed.</item>
    /// <item><b>F4</b> — load and unload APIs are paired. A module loaded through
    /// <c>LoadModule</c> is invisible to the ADS application table, so LISP
    /// <c>arxunload</c> cannot unload it, and vice versa. Do not mix.</item>
    /// </list>
    /// </remarks>
    internal static class OarxModuleHost
    {
        // Scopes the native DLL search path around a load so a module's
        // dependencies resolve out of its own build directory. Deliberately
        // NOT AddDllDirectory: that returns a cookie only RemoveDllDirectory
        // releases, and a reload loop calling it per cycle accumulates
        // process-wide search entries that are never reclaimed.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectoryW(string? lpPathName);

        private static DynamicLinker Linker => SystemObjects.DynamicLinker;

        /// <summary>Is this module registered with the dynamic linker right now?
        /// Takes the module FILE NAME with extension ("Foo.arx"), matched
        /// case-insensitively — not a path (F6).</summary>
        public static bool IsLoaded(string moduleFileName)
        {
            if (string.IsNullOrWhiteSpace(moduleFileName)) return false;
            try
            {
                return Linker.IsModuleLoaded(moduleFileName);
            }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. "Not loaded" is the safe
                // answer, but a linker that cannot answer is worth knowing about:
                // it makes a group look unloaded when it may not be.
                DevReloadDiagnostics.Report($"OarxModuleHost.IsLoaded({moduleFileName})", ex);
                return false;
            }
        }

        /// <summary>Every module the linker currently reports, lowercased file
        /// names. Used for diagnostics, not for control flow.</summary>
        public static IReadOnlyList<string> LoadedModules()
        {
            try
            {
                return Linker.GetLoadedModules().Cast<string>().ToList();
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report("OarxModuleHost.LoadedModules", ex);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Load one module by FULL PATH (F6), resolving its dependencies from its
        /// own directory. Throws <see cref="OarxModuleException"/> with a usable
        /// message rather than letting the linker's bare InvalidOperationException
        /// escape.
        /// </summary>
        public static void Load(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new OarxModuleException("Cannot load an OARX module with no path.");
            if (!File.Exists(fullPath))
                throw new OarxModuleException(
                    $"OARX module not found on disk: {fullPath}. Build the project first.");

            string dir = Path.GetDirectoryName(fullPath)!;
            bool scoped = SetDllDirectoryW(dir);
            try
            {
                // printit:false keeps the linker quiet — DevReload reports load
                // results itself. asCmdrArg:false: this is not an ARX-command
                // argument.
                Linker.LoadModule(fullPath, false, false);
            }
            catch (Exception ex)
            {
                throw new OarxModuleException(
                    $"AutoCAD refused to load '{Path.GetFileName(fullPath)}'. " +
                    "The usual causes are a missing dependency next to the module, " +
                    "a module built against a different ObjectARX/AutoCAD version, " +
                    "or a mismatched platform. " + DescribeDependencyHint(fullPath), ex);
            }
            finally
            {
                if (scoped) SetDllDirectoryW(null);
            }

            string name = Path.GetFileName(fullPath);
            if (!IsLoaded(name))
                throw new OarxModuleException(
                    $"'{name}' reported no error but is not registered with the dynamic linker.");
        }

        /// <summary>
        /// Unload one module by FILE NAME (F6). Returns without throwing when the
        /// module is not loaded — unloading nothing is a success, not an error.
        /// </summary>
        public static void Unload(string moduleFileName)
        {
            if (string.IsNullOrWhiteSpace(moduleFileName)) return;
            if (!IsLoaded(moduleFileName)) return;

            try
            {
                // F1: the second argument MUST be false. True throws for every
                // module in every context. Do not "tidy" this to true.
                Linker.UnloadModule(moduleFileName, false);
            }
            catch (Exception ex)
            {
                throw new OarxModuleException(
                    $"AutoCAD refused to unload '{moduleFileName}'. " +
                    "The module is locked (its entry point never called " +
                    "unlockApplication) or something still depends on it.", ex);
            }

            if (IsLoaded(moduleFileName))
                throw new OarxModuleException(
                    $"'{moduleFileName}' reported no error but is still registered " +
                    "with the dynamic linker.");
        }

        /// <summary>
        /// Can the linker rewrite this file right now? Opening for write with no
        /// sharing is exactly the test link.exe applies, which is why this — and
        /// not "is a module of that name mapped" — is the question that matters.
        /// A missing file is writable: the linker will simply create it.
        /// </summary>
        public static bool IsFileWritable(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                return true;
            }
            // Both mean the same thing and are the QUESTION being asked, not a
            // fault: the file is still locked. Nothing to report.
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// Why is <paramref name="path"/> still locked after we unloaded it?
        /// Names the concrete suspects instead of leaving the user with LNK1168.
        /// </summary>
        public static string DescribeStillLocked(string path)
        {
            string name = Path.GetFileName(path);
            var reasons = new List<string>();

            if (IsMappedInThisProcess(name))
                reasons.Add(
                    "it is still mapped into THIS AutoCAD even though the linker " +
                    "released it — another loaded module imports a symbol from it, " +
                    "so Windows will not unmap it (structure the projects so nothing " +
                    "imports from a reloadable module)");

            // F8: the probe is process-global, so a second AutoCAD holding the
            // module blocks the build just as effectively.
            var others = OtherAutocadProcesses();
            if (others.Count > 0)
                reasons.Add(
                    $"another AutoCAD/Civil 3D is running (pid {string.Join(", ", others)}) " +
                    "and may have the same module loaded");

            if (reasons.Count == 0)
                reasons.Add(
                    "no cause could be identified — a debugger, antivirus scan or " +
                    "file indexer may be holding it");

            return $"'{name}' cannot be overwritten: " + string.Join("; ", reasons) + ".";
        }

        private static bool IsMappedInThisProcess(string moduleFileName)
        {
            try
            {
                return Process.GetCurrentProcess().Modules
                    .Cast<ProcessModule>()
                    .Any(m => string.Equals(
                        m.ModuleName, moduleFileName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report(
                    $"OarxModuleHost: module-table probe for {moduleFileName}", ex);
                return false;
            }
        }

        private static List<int> OtherAutocadProcesses()
        {
            try
            {
                int self = Process.GetCurrentProcess().Id;
                return Process.GetProcessesByName("acad")
                    .Select(p => p.Id)
                    .Where(id => id != self)
                    .ToList();
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report("OarxModuleHost.OtherAutocadProcesses", ex);
                return new List<int>();
            }
        }

        // A load failure is nearly always a missing sibling DLL. Saying which
        // directory was searched turns "it refused" into something actionable.
        private static string DescribeDependencyHint(string fullPath)
        {
            string dir = Path.GetDirectoryName(fullPath)!;
            return $"Dependencies were searched in: {dir}";
        }
    }
}
