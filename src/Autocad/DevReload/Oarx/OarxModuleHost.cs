using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

#if BRICSCAD
using Teigha.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif

using Exception = System.Exception;

using DevReload.Diagnostics;
using DevReload.Oarx.Payload;

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
    /// which DevReload does not use), so no idle-driven state machine is needed.
    /// Synchronous is not the same as successful: a module may refuse its own
    /// unload (BricsCAD keeps one whose kUnloadAppMsg answers with an error), so
    /// every unload is checked against the linker and the process afterwards
    /// (<see cref="ModuleUnloader"/>).</item>
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
        /// case-insensitively — not a path (F6). Either answer counts: the
        /// linker's own query, or the module in its list of loaded apps. A
        /// module that refused its unload must never read as gone because one
        /// of the two disagrees.</summary>
        public static bool IsLoaded(string moduleFileName)
        {
            if (string.IsNullOrWhiteSpace(moduleFileName)) return false;
            bool registered;
            try
            {
                registered = Linker.IsModuleLoaded(moduleFileName);
            }
            catch (Exception ex)
            {
                // Category B - report, do not rethrow. The list below still
                // answers; a linker that cannot answer is worth knowing about.
                DevReloadDiagnostics.Report($"OarxModuleHost.IsLoaded({moduleFileName})", ex);
                registered = false;
            }
            return registered || LoadedModules().Any(m => string.Equals(
                Path.GetFileName(m), moduleFileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Is this module still in the process: listed by the linker
        /// (<see cref="IsLoaded"/>) or still mapped? BricsCAD leaves a module that
        /// refused its unload mapped but no longer listed; it is still in, and an
        /// unload must still ask it.</summary>
        public static bool IsIn(string moduleFileName) =>
            !string.IsNullOrWhiteSpace(moduleFileName)
            && (IsLoaded(moduleFileName) || MappedPath(moduleFileName) is not null);

        /// <summary>Every module the linker currently reports (lowercased file
        /// names on AutoCAD). Part of <see cref="IsLoaded"/>.</summary>
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
                    $"{HostName} refused to load '{Path.GetFileName(fullPath)}'. " +
                    "The usual causes are a missing dependency next to the module, " +
                    $"a module built against a different SDK or {HostName} version, " +
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
        /// Unload modules by FILE NAME (F6), in the order given (.arx before the
        /// .dbx it uses). Every module is first asked whether it may unload now
        /// (its optional DevReloadMayUnload_v1 export); one no and nothing is
        /// unloaded. Each one is then proven out — not listed by the linker, not
        /// mapped — before the next is asked; the run stops at the first that
        /// stays. A module already out is skipped. Never throws: the result says
        /// what left and what did not, <see cref="DescribeRefusal"/> says why.
        /// </summary>
        public static ModuleUnloadRun Unload(IReadOnlyList<string> order) =>
            ModuleUnloader.Run(order, HostLinker.Instance);

        /// <summary>The refusal of an incomplete <see cref="Unload"/> run, for the
        /// caller to show. Null when the run was complete.</summary>
        public static string? DescribeRefusal(ModuleUnloadRun run)
        {
            if (run.StoppedAt is not string stopped) return null;
            // On BricsCAD a refused module shows as still mapped and no longer
            // listed, so that state gets the close-and-retry hint too.
            string hint = run.State == ModuleUnloadState.StillMapped
                && MappedPath(stopped) is string at
                    ? " " + DescribeStillLocked(at) + StillLoadedHint
                    : StillLoadedHint;
            return ModuleUnloader.Describe(run, HostName, hint);
        }

        /// <summary>The linker and the process image, as <see cref="ModuleUnloader"/>
        /// asks them.</summary>
        private sealed class HostLinker : IModuleLinker
        {
            public static readonly HostLinker Instance = new();

            public bool IsRegistered(string moduleFileName) => IsLoaded(moduleFileName);

            public string? MappedPath(string moduleFileName) => OarxModuleHost.MappedPath(moduleFileName);

            public string? TryUnload(string moduleFileName)
            {
                try
                {
                    // F1: the second argument MUST be false. True throws for every
                    // module in every context. Do not "tidy" this to true.
                    Linker.UnloadModule(moduleFileName, false);
                    return null;
                }
                catch (Exception ex)
                {
                    // How the linker reads a native false return. Not a fault to
                    // report: the module's state afterwards decides, and this text
                    // goes into the refusal if the module stayed.
                    return $"{HostName} answered: {ex.Message}";
                }
            }

            public MayUnloadExport? FindMayUnload(string moduleFileName)
            {
                // The file name with its extension: without one, Windows looks
                // for "<name>.dll".
                IntPtr module = GetModuleHandleW(moduleFileName);
                if (module == IntPtr.Zero) return null;
                IntPtr export = GetProcAddress(module, ModuleUnloader.MayUnloadExportName);
                if (export == IntPtr.Zero) return null;

                var call = Marshal.GetDelegateForFunctionPointer<NativeMayUnload>(export);
                return reason =>
                {
                    var pin = GCHandle.Alloc(reason, GCHandleType.Pinned);
                    try
                    {
                        return call(pin.AddrOfPinnedObject(), reason.Length);
                    }
                    finally
                    {
                        pin.Free();
                    }
                };
            }
        }

        // DevReloadMayUnload_v1 as the module exports it (ModuleUnloader).
        // Called on the host's main thread: every OARX tool and command that
        // unloads runs there.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeMayUnload(IntPtr reason, int reasonChars);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        // The export name is ANSI: GetProcAddress has no wide form.
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

#if BRICSCAD
        // BricsCAD keeps a module whose kUnloadAppMsg handler answers with an
        // error, which is what a module guarding its live objects does while an
        // open drawing holds them. The OARX group cycle closes the named drawings
        // first (OarxDrawingCycle), so what is left is an unnamed one, a drawing
        // opened in the meantime, or a payload reload, which closes none.
        private const string StillLoadedHint =
            " BricsCAD does not unload a module while an open drawing holds its objects. " +
            "Close every drawing that holds them, then unload again.";
#else
        private const string StillLoadedHint = "";
#endif

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

            if (MappedPath(name) is not null)
                reasons.Add(
                    $"it is still mapped into THIS {HostName} even though the linker " +
                    "released it — another loaded module imports a symbol from it, " +
                    "so Windows will not unmap it (structure the projects so nothing " +
                    "imports from a reloadable module)");

            // F8: the probe is process-global, so a second AutoCAD holding the
            // module blocks the build just as effectively.
            var others = OtherHostProcesses();
            if (others.Count > 0)
                reasons.Add(
                    $"another {HostName} is running (pid {string.Join(", ", others)}) " +
                    "and may have the same module loaded");

            if (reasons.Count == 0)
                reasons.Add(
                    "no cause could be identified — a debugger, antivirus scan or " +
                    "file indexer may be holding it");

            return $"'{name}' cannot be overwritten: " + string.Join("; ", reasons) + ".";
        }

        /// <summary>The full path this process has an image of that file name
        /// mapped from, or null when it has none.</summary>
        public static string? MappedPath(string moduleFileName)
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                return current.Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(m => string.Equals(
                        m.ModuleName, moduleFileName, StringComparison.OrdinalIgnoreCase))
                    ?.FileName;
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report(
                    $"OarxModuleHost: module-table probe for {moduleFileName}", ex);
                return null;
            }
        }

        /// <summary>The CAD this assembly was built for, as the user knows it.</summary>
#if BRICSCAD
        public const string HostName = "BricsCAD";
#else
        public const string HostName = "AutoCAD/Civil 3D";
#endif

        /// <summary>Other running instances of THIS host. The other host does not
        /// matter: it cannot load a module built for this one.</summary>
        public static List<int> OtherHostProcesses()
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                int self = current.Id;
                return Process.GetProcessesByName(current.ProcessName)
                    .Select(p => p.Id)
                    .Where(id => id != self)
                    .ToList();
            }
            catch (Exception ex)
            {
                DevReloadDiagnostics.Report("OarxModuleHost.OtherHostProcesses", ex);
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
