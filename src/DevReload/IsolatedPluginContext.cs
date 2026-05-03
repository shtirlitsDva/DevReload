using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload
{
    public class IsolatedPluginContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly HashSet<string> _sharedAssemblies;
        private readonly string _pluginDir;

        public IsolatedPluginContext(string pluginPath, params string[] sharedAssemblyNames)
            : base($"PluginIsolated::{Path.GetFileName(pluginPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _sharedAssemblies = new HashSet<string>(sharedAssemblyNames);
            _pluginDir = Path.GetDirectoryName(pluginPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? "";

            // Shared assemblies stay in default ALC for type identity (WPF XAML etc.)
            if (_sharedAssemblies.Contains(name))
                return null;

            // Resolve path: deps.json first (NuGet/package), then plugin dir for
            // project refs (type:"project" entries are by design ignored by
            // AssemblyDependencyResolver per the SDK runtime-config spec).
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
            {
                string sideBySide = Path.Combine(_pluginDir, name + ".dll");
                if (File.Exists(sideBySide))
                    assemblyPath = sideBySide;
            }

            if (assemblyPath == null)
                return null;

            // Stream-load (same approach as PluginHost) — leaves no file lock,
            // so collectible-ALC unload truly releases the DLL on disk and the
            // next build can overwrite it.
            byte[] asmBytes = File.ReadAllBytes(assemblyPath);
            string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            using var asmStream = new MemoryStream(asmBytes);
            if (File.Exists(pdbPath))
            {
                byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);
                return LoadFromStream(asmStream, pdbStream);
            }
            return LoadFromStream(asmStream);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
                return LoadUnmanagedDllFromPath(libraryPath);
            return IntPtr.Zero;
        }
    }
}
