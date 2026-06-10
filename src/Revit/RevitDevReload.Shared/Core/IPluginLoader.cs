using System.Collections.Generic;
using System.Reflection;

namespace RevitDevReload.Core
{
    public sealed class LoadedPluginHandle
    {
        public LoadedPluginHandle(Assembly assembly, object? context)
        {
            Assembly = assembly;
            Context = context;
        }

        public Assembly Assembly { get; }

        // The collectible ALC on net8; null on net48 (nothing to unload).
        public object? Context { get; }
    }

    // One implementation compiles per target framework — AlcPluginLoader on
    // net8 (Revit 2025+), LegacyPluginLoader on net48 (Revit 2022-2024).
    // The interface keeps RevitPluginManager identical across both.
    public interface IPluginLoader
    {
        LoadedPluginHandle Load(string dllPath, IReadOnlyList<string> sharedAssemblyNames);
        void Unload(LoadedPluginHandle handle);

        // True when Unload really frees memory (ALC); false when reloads
        // accumulate assemblies for the session (net48) — surfaced in the UI
        // so the difference is visible, not silent.
        bool SupportsTrueUnload { get; }
    }
}
