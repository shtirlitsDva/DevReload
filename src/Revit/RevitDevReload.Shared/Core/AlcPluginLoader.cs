// Collectible-ALC loader — Revit 2025+ (.NET 8). The DevReload (AutoCAD)
// approach verbatim: byte-load DLL+PDB into an IsolatedPluginContext so the
// files on disk stay writable and unload truly releases everything.
#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using DevReload.Core;

namespace RevitDevReload.Core
{
    public sealed class AlcPluginLoader : IPluginLoader
    {
        // RevitAPI*/AdWindows + WPF infra must resolve in the default ALC for
        // type identity, exactly like acmgd/acdbmgd on the AutoCAD side. They
        // are merged with the per-build SharedAssemblies.Config.json names by
        // the caller.
        private static readonly string[] _alwaysShared =
        {
            "RevitAPI", "RevitAPIUI", "AdWindows", "UIFramework",
        };

        public bool SupportsTrueUnload => true;

        public LoadedPluginHandle Load(
            string dllPath, IReadOnlyList<string> sharedAssemblyNames)
        {
            string[] shared = _alwaysShared
                .Concat(sharedAssemblyNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var context = new IsolatedPluginContext(dllPath, shared);

            byte[] asmBytes = File.ReadAllBytes(dllPath);
            Assembly assembly;
            using (var asmStream = new MemoryStream(asmBytes))
            {
                string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                if (File.Exists(pdbPath))
                {
                    byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                    using var pdbStream = new MemoryStream(pdbBytes);
                    assembly = context.LoadFromStream(asmStream, pdbStream);
                }
                else
                {
                    assembly = context.LoadFromStream(asmStream);
                }
            }

            return new LoadedPluginHandle(assembly, context);
        }

        public void Unload(LoadedPluginHandle handle)
        {
            if (handle.Context is IsolatedPluginContext context)
                context.Unload();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
#endif
