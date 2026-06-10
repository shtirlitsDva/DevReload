// Legacy loader — Revit 2022-2024 (.NET Framework 4.8). No collectible ALC
// exists there, so true unload is impossible. What we keep from the modern
// approach: byte-loading (Assembly.Load(byte[])), so plugin files are never
// locked and `dotnet build` can overwrite them while the old image stays
// resident. Reload = load a fresh copy; the registry drops all references to
// the old one (memory is the unavoidable trade, same as RevitAddInManager).
#if !NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitDevReload.Core
{
    public sealed class LegacyPluginLoader : IPluginLoader
    {
        // Build dirs of currently/previously loaded plugins; the
        // AssemblyResolve hook byte-loads dependencies from them.
        private static readonly List<string> _probeDirs = new();
        private static readonly object _lock = new();
        private static bool _hooked;

        public bool SupportsTrueUnload => false;

        public LoadedPluginHandle Load(
            string dllPath, IReadOnlyList<string> sharedAssemblyNames)
        {
            // sharedAssemblyNames is an ALC concept; on net48 everything lives
            // in one AppDomain so the parameter is intentionally unused.
            string buildDir = Path.GetDirectoryName(dllPath)!;
            lock (_lock)
            {
                if (!_probeDirs.Contains(buildDir, StringComparer.OrdinalIgnoreCase))
                    _probeDirs.Insert(0, buildDir); // newest dir wins probing order
                if (!_hooked)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveFromProbeDirs;
                    _hooked = true;
                }
            }

            byte[] asmBytes = File.ReadAllBytes(dllPath);
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            Assembly assembly = File.Exists(pdbPath)
                ? Assembly.Load(asmBytes, File.ReadAllBytes(pdbPath))
                : Assembly.Load(asmBytes);

            return new LoadedPluginHandle(assembly, context: null);
        }

        public void Unload(LoadedPluginHandle handle)
        {
            // Nothing to do: net48 cannot unload assemblies. The caller drops
            // its references; the image stays resident for the session.
        }

        private static Assembly? ResolveFromProbeDirs(object? sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name ?? "";
            if (name.Length == 0 || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            lock (_lock)
            {
                foreach (string dir in _probeDirs)
                {
                    string candidate = Path.Combine(dir, name + ".dll");
                    if (!File.Exists(candidate)) continue;
                    return Assembly.Load(File.ReadAllBytes(candidate));
                }
            }
            return null;
        }
    }
}
#endif
