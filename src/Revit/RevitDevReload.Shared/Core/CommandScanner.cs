using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitDevReload.Core
{
    public sealed class DiscoveredCommand
    {
        public DiscoveredCommand(string fullClassName, string displayName)
        {
            FullClassName = fullClassName;
            DisplayName = displayName;
        }

        public string FullClassName { get; }
        public string DisplayName { get; }
    }

    // Discovers plugin entry points by interface NAME, not type identity:
    // the host and the plugin may reference different RevitAPI builds, and
    // name matching also keeps this testable without Revit. At invocation
    // time the instance is cast to the host's IExternalCommand — type
    // identity holds because RevitAPIUI always resolves to the host's copy.
    public static class CommandScanner
    {
        public static IReadOnlyList<DiscoveredCommand> FindExternalCommands(Assembly assembly)
        {
            return GetLoadableTypes(assembly)
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
                .Where(t => t.GetInterfaces().Any(i => i.Name == "IExternalCommand"))
                .Select(t => new DiscoveredCommand(t.FullName!, t.Name))
                .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<Type> FindExternalApplications(Assembly assembly)
        {
            return GetLoadableTypes(assembly)
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
                .Where(t => t.GetInterfaces().Any(i => i.Name == "IExternalApplication"))
                .ToList();
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).Cast<Type>();
            }
        }
    }
}
