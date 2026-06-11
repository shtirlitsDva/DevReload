using System;
using System.Collections.Generic;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using RevitDevReload.Core;

namespace RevitDevReload
{
    // The switchboard behind every plugin ribbon button. Buttons are bound
    // to numbered proxy classes (SwitchboardProxies.cs) compiled into this
    // host assembly — they never point at plugin DLLs (Revit would load a
    // frozen copy into the default ALC and lock the file). A proxy's
    // Execute forwards here; Route resolves the command class inside the
    // plugin's CURRENT collectible ALC, so a reload transparently retargets
    // every button. Slots are session-scoped: panels and bindings are
    // rebuilt on every load.
    public static class ButtonSwitchboard
    {
        public const int SlotCount = 64;

        private sealed class Binding
        {
            public Binding(string pluginName, string fullClassName)
            {
                PluginName = pluginName;
                FullClassName = fullClassName;
            }

            public string PluginName { get; }
            public string FullClassName { get; }
        }

        private static readonly Binding?[] _slots = new Binding?[SlotCount];
        // Session-sticky reservations: (plugin, class) → slot. A command
        // keeps its slot for the whole Revit session even across unloads of
        // OTHER plugins, so a Quick-Access-Toolbar clone of a button (which
        // captures the proxy CLASS, not the live ribbon item) can never be
        // re-pointed at a different command by slot reshuffling.
        private static readonly Dictionary<string, int> _reservations =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        public static int Assign(string pluginName, string fullClassName)
        {
            string key = $"{pluginName}.{fullClassName}";
            lock (_lock)
            {
                if (_reservations.TryGetValue(key, out int reserved) &&
                    _slots[reserved] == null)
                {
                    _slots[reserved] = new Binding(pluginName, fullClassName);
                    return reserved;
                }

                for (int i = 0; i < SlotCount; i++)
                {
                    if (_slots[i] == null && !_reservations.ContainsValue(i))
                    {
                        _slots[i] = new Binding(pluginName, fullClassName);
                        _reservations[key] = i;
                        return i;
                    }
                }
            }
            throw new InvalidOperationException(
                $"All {SlotCount} ribbon button slots are reserved this " +
                "session. Raise ButtonSwitchboard.SlotCount and add proxy " +
                "classes in SwitchboardProxies.cs, or restart Revit.");
        }

        public static void FreeAll(string pluginName)
        {
            lock (_lock)
            {
                for (int i = 0; i < SlotCount; i++)
                {
                    if (_slots[i]?.PluginName.Equals(
                            pluginName, StringComparison.OrdinalIgnoreCase) == true)
                        _slots[i] = null;
                }
            }
        }

        // Called by the numbered proxies with the LIVE ExternalCommandData
        // Revit just created for the click — also refreshed into
        // RevitContext so manager-window invocations get the newest handle.
        public static Result Route(
            int slot, ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            RevitContext.CapturedCommandData = commandData;
            RevitContext.UiApp = commandData.Application;

            Binding? binding;
            lock (_lock) binding = _slots[slot];

            if (binding == null)
            {
                message = $"DevReload: button slot {slot} has no plugin bound " +
                          "(was the plugin unloaded?).";
                return Result.Failed;
            }

            var reg = RevitPluginManager.Get(binding.PluginName);
            if (reg == null || !reg.IsLoaded || reg.Handle == null)
            {
                message = $"DevReload: plugin '{binding.PluginName}' is not " +
                          "loaded. Load it from the DevReload manager window.";
                return Result.Failed;
            }

            try
            {
                Result result = RevitPluginManager.ExecuteInPluginContext(
                    reg, binding.FullClassName, commandData, ref message, elements);
                DevReloadLogBuffer.Add(
                    $"{binding.PluginName}.{binding.FullClassName} (ribbon) → {result}");
                return result;
            }
            catch (Exception ex)
            {
                DevReloadLogBuffer.Add(
                    $"{binding.PluginName}.{binding.FullClassName} (ribbon) " +
                    $"error: {ex.GetType().Name}: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
