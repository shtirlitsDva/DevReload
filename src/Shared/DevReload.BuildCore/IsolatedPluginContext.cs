// Collectible-ALC plugin isolation — .NET 8 hosts only (AutoCAD 2025+,
// Revit 2025+). net48 consumers compile this file to nothing and use their
// legacy loader instead.
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload.Core
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

            // Shared assemblies stay in default ALC for type identity (WPF XAML etc.).
            //
            // PluginManager loads them via LoadFrom or via
            // AssemblyLoadContext.Default.LoadFromStream — both put the assembly
            // into the Default ALC. The explicit lookup below is belt-and-braces:
            // returning null and letting the default binder resolve also works, but
            // handing the runtime the resolved instance is unambiguous.
            //
            // Note: Assembly.Load(byte[]) — which we explicitly DO NOT use — would
            // put the assembly in a brand-new anonymous ALC, where it would be
            // invisible to default-binder name resolution.
            if (_sharedAssemblies.Contains(name))
            {
                foreach (var asm in AssemblyLoadContext.Default.Assemblies)
                {
                    if (string.Equals(
                            asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                        return asm;
                }
                return null;
            }

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
#endif
