using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Autodesk.Revit.UI;

using RevitDevReload.Core;

namespace RevitDevReload
{
    // Renders PanelLayouts onto Revit ribbon panels through the OFFICIAL
    // API only. Host-agnostic on purpose: the DevReload host binds buttons
    // to switchboard proxies, the standalone NorsynApps reflector binds
    // them directly to the installed addin DLL — both flow through the
    // bindingFactory delegate, so the layout/rendering logic exists once.
    public static class RibbonBuilder
    {
        public static void Render(
            RibbonPanel panel,
            PanelLayout layout,
            Func<ButtonDefinition, PushButtonData> bindingFactory,
            Func<string, ImageSource?> iconResolver)
        {
            bool slideOutOpen = false;
            foreach (var entry in layout.Entries)
            {
                if (entry.SlideOut && !slideOutOpen)
                {
                    panel.AddSlideOut();
                    slideOutOpen = true;
                }

                if (entry.SeparatorBefore && panel.GetItems().Count > 0)
                    panel.AddSeparator();

                if (entry.Single != null)
                {
                    panel.AddItem(CreatePushButtonData(
                        entry.Single, bindingFactory, iconResolver));
                }
                else if (entry.Group != null)
                {
                    AddGroup(panel, entry.Group, bindingFactory, iconResolver);
                }
                else if (entry.Stack != null)
                {
                    AddStacks(panel, entry.Stack, bindingFactory, iconResolver);
                }
            }
        }

        private static void AddGroup(
            RibbonPanel panel,
            ButtonGroup group,
            Func<ButtonDefinition, PushButtonData> bindingFactory,
            Func<string, ImageSource?> iconResolver)
        {
            // Container internal names must be unique per panel; the group
            // name doubles as both id and visible text.
            bool split = string.Equals(group.Kind, "Split", StringComparison.OrdinalIgnoreCase);

            if (split)
            {
                var data = new SplitButtonData(group.Name, group.Name);
                var button = (SplitButton)panel.AddItem(data);
                foreach (var member in group.Members)
                    button.AddPushButton(CreatePushButtonData(
                        member, bindingFactory, iconResolver));
            }
            else
            {
                var data = new PulldownButtonData(group.Name, group.Name);
                // A pulldown shows its own icon: borrow the first member's.
                ApplyIcons(data, group.Members.FirstOrDefault(), iconResolver);
                var button = (PulldownButton)panel.AddItem(data);
                foreach (var member in group.Members)
                    button.AddPushButton(CreatePushButtonData(
                        member, bindingFactory, iconResolver));
            }
        }

        private static void AddStacks(
            RibbonPanel panel,
            IReadOnlyList<ButtonDefinition> members,
            Func<ButtonDefinition, PushButtonData> bindingFactory,
            Func<string, ImageSource?> iconResolver)
        {
            // Revit stacks hold exactly 2 or 3 items; chunk accordingly,
            // letting a trailing single fall back to a normal button.
            for (int i = 0; i < members.Count;)
            {
                int remaining = members.Count - i;
                if (remaining >= 3 && remaining != 4)
                {
                    panel.AddStackedItems(
                        CreatePushButtonData(members[i], bindingFactory, iconResolver),
                        CreatePushButtonData(members[i + 1], bindingFactory, iconResolver),
                        CreatePushButtonData(members[i + 2], bindingFactory, iconResolver));
                    i += 3;
                }
                else if (remaining >= 2)
                {
                    panel.AddStackedItems(
                        CreatePushButtonData(members[i], bindingFactory, iconResolver),
                        CreatePushButtonData(members[i + 1], bindingFactory, iconResolver));
                    i += 2;
                }
                else
                {
                    panel.AddItem(CreatePushButtonData(
                        members[i], bindingFactory, iconResolver));
                    i += 1;
                }
            }
        }

        private static PushButtonData CreatePushButtonData(
            ButtonDefinition def,
            Func<ButtonDefinition, PushButtonData> bindingFactory,
            Func<string, ImageSource?> iconResolver)
        {
            PushButtonData data = bindingFactory(def);
            data.Text = def.Text;
            if (def.Tooltip != null) data.ToolTip = def.Tooltip;
            if (def.LongDescription != null) data.LongDescription = def.LongDescription;
            ApplyIcons(data, def, iconResolver);
            return data;
        }

        private static void ApplyIcons(
            ButtonData data, ButtonDefinition? def,
            Func<string, ImageSource?> iconResolver)
        {
            if (def == null) return;
            if (def.Icon16 != null) data.Image = iconResolver(def.Icon16);
            if (def.Icon32 != null) data.LargeImage = iconResolver(def.Icon32);
        }

        // Loads an embedded image resource matched by name SUFFIX (resource
        // names carry the per-host root namespace prefix). The resource is
        // copied into a plain MemoryStream first: a frozen BitmapImage keeps
        // its StreamSource forever, and the runtime's ManifestResourceStream
        // pins its RuntimeAssembly (and thus a collectible ALC) for the
        // stream's lifetime — the ribbon must never hold that chain.
        public static ImageSource? LoadEmbeddedIcon(Assembly assembly, string nameSuffix)
        {
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return null;

            var detached = new MemoryStream();
            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                stream.CopyTo(detached);
            }
            detached.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = detached;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
