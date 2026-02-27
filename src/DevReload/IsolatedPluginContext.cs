using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

namespace DevReload
{
    /// <summary>
    /// A collectible <see cref="AssemblyLoadContext"/> that isolates a plugin
    /// assembly and its NuGet dependencies from the host (AutoCAD) process.
    /// <para>
    /// Collectible (<c>isCollectible: true</c>) means the entire context — and
    /// all assemblies loaded into it — can be unloaded and garbage-collected,
    /// enabling hot-reload without restarting AutoCAD.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Shared assemblies:</b> Assemblies listed in <paramref name="sharedAssemblyNames"/>
    /// (e.g., <c>"DevReload.Interface"</c>) are <i>not</i> loaded into this context.
    /// Returning <c>null</c> from <see cref="Load"/> causes the runtime to fall back
    /// to the default ALC, ensuring both the Loader and Core see the same type
    /// identity for shared interfaces.
    /// <para>
    /// <b>AutoCAD assemblies</b> (accoremgd, acdbmgd, acmgd, etc.) are also resolved
    /// from the default ALC via the fallback mechanism, since they are already loaded
    /// by the AutoCAD host process.
    /// </para>
    /// <para>
    /// <b>NuGet dependencies</b> are resolved via <see cref="AssemblyDependencyResolver"/>
    /// which reads the <c>.deps.json</c> file on disk. These are loaded from disk via
    /// <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> (file-locking is acceptable
    /// for NuGet packages since they are not rebuilt during development).
    /// </para>
    /// </remarks>
    public class IsolatedPluginContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly HashSet<string> _sharedAssemblies;

        /// <summary>
        /// Creates a new isolated, collectible assembly load context.
        /// </summary>
        /// <param name="pluginPath">
        /// Full path to the plugin DLL. Used by <see cref="AssemblyDependencyResolver"/>
        /// to locate the corresponding <c>.deps.json</c> for NuGet dependency resolution.
        /// </param>
        /// <param name="sharedAssemblyNames">
        /// Assembly names (without <c>.dll</c>) that should NOT be loaded into this
        /// context. These fall back to the default ALC for shared type identity.
        /// Typically includes <c>"DevReload.Interface"</c>.
        /// </param>
        public IsolatedPluginContext(string pluginPath, params string[] sharedAssemblyNames)
            : base("PluginIsolated", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
            _sharedAssemblies = new HashSet<string>(sharedAssemblyNames);
        }

        /// <inheritdoc/>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Keep shared interface assemblies in default context for type identity
            if (_sharedAssemblies.Contains(assemblyName.Name!))
                return null;

            // Use .deps.json to resolve isolated dependencies (NuGet packages)
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
                return LoadFromAssemblyPath(assemblyPath);

            // Return null → falls back to default context.
            // AutoCAD assemblies, .NET framework, and WPF types resolve this way.
            return null;
        }

        /// <inheritdoc/>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
                return LoadUnmanagedDllFromPath(libraryPath);
            return IntPtr.Zero;
        }
    }
}
