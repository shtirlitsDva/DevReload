using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using DevReload.Hud;

using Exception = System.Exception;

namespace DevReload.Oarx
{
    /// <summary>
    /// The companions an OARX group carries besides its native modules: pinned
    /// native DLLs and managed assemblies loaded through AutoCAD's extension
    /// loader. Everything here is load-only — companions are never unloaded,
    /// which is precisely why they are separate from the module lifecycle in
    /// <see cref="OarxModuleHost"/>.
    /// </summary>
    /// <remarks>
    /// The behaviour is ported from the Norsyn LoaderUnloader arx, which this
    /// surface replaces. Two of its lessons are load-bearing:
    ///
    /// <list type="number">
    /// <item><b>The pin.</b> Windows maps two DLLs sharing a base name when they
    /// come from different directories — a native import resolves the copy
    /// adjacent to its module while a .NET [DllImport] resolves another — and a
    /// DLL holding process-wide state (a log hub) then exists twice. Mapping the
    /// canonical copy by FULL PATH before any module loads makes every later
    /// base-name reference bind to it. If a NON-canonical copy is already
    /// resident the split cannot be undone without a restart, so it is warned
    /// about loudly rather than silently tolerated.</item>
    /// <item><b>Order.</b> Preloads run BEFORE the group's modules (a trace UI
    /// must be listening before a dbx logs during load); postloads run AFTER
    /// (a mixed-mode interop statically imports the dbx it wraps, so loading it
    /// first would map the dbx by search-path accident).</item>
    /// </list>
    /// </remarks>
    internal static class OarxCompanionHost
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(
            IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

        /// <summary>
        /// Map one native DLL by full path so later base-name references bind to
        /// it. Idempotent; warns loudly (and returns) when a same-named module is
        /// already mapped from a DIFFERENT path — that split is unfixable without
        /// a restart and must not pass silently. Never throws: a missing pin is
        /// reported and the cycle continues, matching the loader it replaces.
        /// </summary>
        public static void PinNative(string fullPath, IReloadProgress ui)
        {
            string baseName = Path.GetFileName(fullPath);
            IntPtr existing = GetModuleHandleW(baseName);
            if (existing != IntPtr.Zero)
            {
                var mapped = new System.Text.StringBuilder(1024);
                if (GetModuleFileNameW(existing, mapped, 1024) != 0 &&
                    !string.Equals(mapped.ToString(), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    ui.Line($"WARNING: {baseName} is already mapped from a NON-canonical path: " +
                            $"{mapped} (canonical: {fullPath}). Process-wide state in it is " +
                            "split; restart AutoCAD to bind everything to one copy.");
                }
                return;
            }

            if (!File.Exists(fullPath))
            {
                ui.Line($"WARNING: pinned native module not found: {fullPath}");
                return;
            }
            if (LoadLibraryW(fullPath) == IntPtr.Zero)
                ui.Line($"WARNING: could not pin {fullPath} (GetLastError={Marshal.GetLastWin32Error()})");
        }

        /// <summary>
        /// Load one managed assembly through AutoCAD's extension loader — the
        /// NETLOAD-equivalent path, so IExtensionApplication.Initialize runs and
        /// [CommandMethod]s register. Default ALC, never unloaded, so this is
        /// idempotent by assembly simple name. Never throws: the failure is
        /// reported and the cycle continues.
        /// </summary>
        public static void LoadManaged(string fullPath, IReloadProgress ui)
        {
            string simpleName = Path.GetFileNameWithoutExtension(fullPath);
            bool alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded)
                return;

            if (!File.Exists(fullPath))
            {
                ui.Line($"WARNING: managed companion not found: {fullPath}");
                return;
            }

            try
            {
                Autodesk.AutoCAD.Runtime.ExtensionLoader.Load(fullPath);
                ui.Line($"loaded companion {Path.GetFileName(fullPath)}");
            }
            catch (Exception ex)
            {
                ui.Line($"WARNING: companion {Path.GetFileName(fullPath)} failed to load: {ex.Message}");
            }
        }
    }
}
