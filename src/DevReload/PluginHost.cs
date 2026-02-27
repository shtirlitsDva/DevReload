using System;
using System.IO;
using System.Reflection;

namespace DevReload
{
    /// <summary>
    /// Manages the lifecycle of a plugin loaded into an isolated, collectible
    /// <see cref="IsolatedPluginContext"/>. Supports loading from a byte stream
    /// (no file lock) and full unload with GC.
    /// </summary>
    /// <typeparam name="TPlugin">
    /// The plugin interface type (e.g., <see cref="IPlugin"/>). Must be defined
    /// in a shared assembly so both the Loader and Core see the same type.
    /// </typeparam>
    /// <remarks>
    /// <b>Stream-based loading:</b> The main plugin DLL is read into memory via
    /// <see cref="File.ReadAllBytes"/> and loaded via
    /// <see cref="AssemblyLoadContext.LoadFromStream"/>. This means Visual Studio
    /// can rebuild the DLL while the old version runs in memory — no file lock.
    /// <para>
    /// NuGet dependencies (resolved by <see cref="IsolatedPluginContext"/>) are
    /// still loaded from disk via <c>LoadFromAssemblyPath</c>, which is fine
    /// because they are not rebuilt during development.
    /// </para>
    /// </remarks>
    public class PluginHost<TPlugin> where TPlugin : class
    {
        private IsolatedPluginContext? _context;

        /// <summary>Gets whether a plugin is currently loaded.</summary>
        public bool IsLoaded => _context != null;

        /// <summary>Gets the currently loaded plugin instance, or <c>null</c>.</summary>
        public TPlugin? Plugin { get; private set; }

        /// <summary>
        /// Gets the <see cref="Assembly"/> loaded into the isolated ALC.
        /// Used by <see cref="CommandRegistrar"/> to scan for
        /// <c>[CommandMethod]</c> attributes.
        /// </summary>
        public Assembly? LoadedAssembly { get; private set; }

        /// <summary>
        /// Loads a plugin assembly from disk into a new isolated, collectible ALC.
        /// If a plugin is already loaded, it is unloaded first.
        /// </summary>
        /// <param name="assemblyPath">
        /// Full path to the plugin DLL (the "Core" assembly).
        /// </param>
        /// <param name="sharedAssemblyNames">
        /// Assembly names that should remain in the default ALC for shared type
        /// identity (e.g., <c>"DevReload.Interface"</c>).
        /// </param>
        /// <returns>The instantiated plugin implementing <typeparamref name="TPlugin"/>.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no type implementing <typeparamref name="TPlugin"/> is found
        /// in the loaded assembly.
        /// </exception>
        public TPlugin Load(string assemblyPath, params string[] sharedAssemblyNames)
        {
            if (_context != null)
                Unload();

            _context = new IsolatedPluginContext(assemblyPath, sharedAssemblyNames);

            // Load main DLL from stream — file is NOT locked after reading.
            // AssemblyDependencyResolver still resolves NuGet deps via .deps.json on disk.
            byte[] asmBytes = File.ReadAllBytes(assemblyPath);
            Assembly pluginAssembly;

            using (var asmStream = new MemoryStream(asmBytes))
            {
                // Also load PDB from stream if available (enables debugging)
                string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                    using var pdbStream = new MemoryStream(pdbBytes);
                    pluginAssembly = _context.LoadFromStream(asmStream, pdbStream);
                }
                else
                {
                    pluginAssembly = _context.LoadFromStream(asmStream);
                }
            }

            LoadedAssembly = pluginAssembly;

            // Find the TPlugin implementation in the loaded assembly
            Type? pluginType = null;
            foreach (Type type in pluginAssembly.GetExportedTypes())
            {
                if (typeof(TPlugin).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    pluginType = type;
                    break;
                }
            }

            if (pluginType == null)
                throw new InvalidOperationException(
                    $"Could not find {typeof(TPlugin).Name} implementation in {Path.GetFileName(assemblyPath)}");

            Plugin = (TPlugin)Activator.CreateInstance(pluginType)!;
            return Plugin;
        }

        /// <summary>
        /// Unloads the current plugin and its isolated ALC.
        /// Triggers garbage collection to allow the collectible context to be freed.
        /// <para>
        /// <b>Important:</b> Call <see cref="CommandRegistrar.UnregisterAll"/>
        /// before this method to release AutoCAD's delegate references.
        /// </para>
        /// </summary>
        public void Unload()
        {
            Plugin = null;
            LoadedAssembly = null;

            _context?.Unload();
            _context = null;

            // Force GC to collect the unloaded ALC
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
