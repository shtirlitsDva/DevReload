using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;

using Autodesk.Revit.UI;

using ricaun.Revit.UI;

using RevitDevReload.Core;

namespace RevitDevReload
{
    // Plugin panels on the "DevReload" ribbon tab (the tab itself plus the
    // Manager panel are created at startup by RevitDevReloadApp): one panel
    // (minimum) per LOADED plugin, rebuilt from scratch on every load/
    // reload, removed on unload. Buttons bind to switchboard proxy slots,
    // never to plugin DLLs.
    // Panel creation is official API via ricaun.Revit.UI's CreatePanel;
    // removal uses its Remove() — the one place that touches AdWindows and
    // cleans Revit's private RibbonItemDictionary so stable names can be
    // recreated in-session. Must only be called inside API context
    // (ApiContextRunner).
    public static class DevReloadRibbonService
    {
        public const string TabName = "DevReload";

        private static readonly Dictionary<string, List<RibbonPanel>> _panels =
            new(StringComparer.OrdinalIgnoreCase);

        // Tear down whatever the plugin had, then render the current
        // command set. One procedure for load AND reload — by design there
        // is no diffing.
        public static void Rebuild(RevitPluginRegistration reg)
        {
            Teardown(reg.Entry.Name);

            if (reg.Handle == null) return;
            var uiCtrlApp = RevitContext.UiCtrlApp
                ?? throw new InvalidOperationException(
                    "UIControlledApplication not captured");

            IReadOnlyList<ButtonDefinition> defs =
                ButtonDefinitionScanner.Scan(reg.Handle.Assembly);
            if (defs.Count == 0) return; // no attributed commands → no panel

            Assembly pluginAssembly = reg.Handle.Assembly;
            string hostAssemblyPath = Assembly.GetExecutingAssembly().Location;
            var created = new List<RibbonPanel>();

            try
            {
                foreach (PanelLayout layout in
                    RibbonLayoutBuilder.Build(defs, reg.Entry.Name))
                {
                    RibbonPanel panel = uiCtrlApp.CreatePanel(TabName, layout.Title);
                    created.Add(panel);

                    RibbonBuilder.Render(
                        panel,
                        layout,
                        def => CreateProxyBinding(
                            reg.Entry.Name, def, hostAssemblyPath),
                        suffix =>
                        {
                            var icon = RibbonBuilder.LoadEmbeddedIcon(
                                pluginAssembly, suffix);
                            if (icon == null)
                                DevReloadLogBuffer.Add(
                                    $"{reg.Entry.Name}: declared icon " +
                                    $"'{suffix}' not found among embedded " +
                                    "resources — button rendered without it");
                            return icon;
                        });
                }
            }
            catch
            {
                // Half-built ribbon = inconsistent state; remove what was
                // created and let the caller's rollback handle the rest.
                RemovePanels(created);
                ButtonSwitchboard.FreeAll(reg.Entry.Name);
                throw;
            }

            _panels[reg.Entry.Name] = created;
            DevReloadLogBuffer.Add(
                $"{reg.Entry.Name}: ribbon rebuilt — {defs.Count} button(s), " +
                $"{created.Count} panel(s) on '{TabName}' tab");
        }

        public static void Teardown(string pluginName)
        {
            ButtonSwitchboard.FreeAll(pluginName);

            if (!_panels.TryGetValue(pluginName, out var panels)) return;
            _panels.Remove(pluginName);
            RemovePanels(panels);
            DevReloadLogBuffer.Add($"{pluginName}: ribbon panels removed");
        }

        private static void RemovePanels(List<RibbonPanel> panels)
        {
            foreach (RibbonPanel panel in panels)
            {
                try
                {
                    panel.Remove();
                }
                catch (Exception ex)
                {
                    DevReloadLogBuffer.Add(
                        $"ribbon panel removal failed: {ex.Message}");
                }
            }
        }

        // The switchboard binding: a real official-API button whose command
        // class is a numbered proxy in THIS assembly. Internal button name
        // stays stable across reloads (plugin + class), so QAT pins and
        // keyboard shortcuts survive — the registry entry is cleaned by
        // panel.Remove() in between.
        private static PushButtonData CreateProxyBinding(
            string pluginName, ButtonDefinition def, string hostAssemblyPath)
        {
            int slot = ButtonSwitchboard.Assign(pluginName, def.FullClassName);
            return new PushButtonData(
                $"{pluginName}.{def.FullClassName}",
                def.Text,
                hostAssemblyPath,
                $"RevitDevReload.DevReloadButton{slot:00}");
        }
    }
}
