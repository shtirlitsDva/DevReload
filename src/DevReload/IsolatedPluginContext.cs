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
            : base("PluginIsolated", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _sharedAssemblies = new HashSet<string>(sharedAssemblyNames);
            _pluginDir = Path.GetDirectoryName(pluginPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Keep shared interface assemblies in default context for type identity
            if (_sharedAssemblies.Contains(assemblyName.Name!))
                return null;

            // 1. NuGet/package deps — deps.json drives this.
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
                return LoadFromAssemblyPath(assemblyPath);

            // 2. Project references — by design AssemblyDependencyResolver ignores
            //    type:"project" entries in deps.json (per SDK runtime-configuration
            //    spec). Probe the plugin directory directly. Same pattern as
            //    natemcmaster/DotNetCorePlugins.
            string sideBySide = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (File.Exists(sideBySide))
                return LoadFromAssemblyPath(sideBySide);

            // 3. AutoCAD / BCL / WPF — fall through to default ALC.
            return null;
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
