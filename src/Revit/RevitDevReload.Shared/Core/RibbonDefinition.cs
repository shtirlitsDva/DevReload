using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitDevReload.Core
{
    // One command's ribbon presence, read from a [DevReloadButton] attribute.
    // The attribute is matched BY NAME (like CommandScanner matches
    // IExternalCommand by interface name): the plugin declares its own copy
    // of DevReloadButtonAttribute, no shared contract assembly exists, and
    // the attribute instance may live in a foreign AssemblyLoadContext —
    // every property is therefore read via reflection.
    public sealed class ButtonDefinition
    {
        public ButtonDefinition(string fullClassName)
        {
            FullClassName = fullClassName;
        }

        public string FullClassName { get; }

        // Visible text; defaults to the command class name when omitted.
        public string Text { get; set; } = "";
        public string? Tooltip { get; set; }
        public string? LongDescription { get; set; }

        // Embedded-resource name suffixes (e.g. "ImgPcfExport16.png").
        // Optional — buttons without icons are legal (text-only).
        public string? Icon16 { get; set; }
        public string? Icon32 { get; set; }

        // Panel title within the plugin's tab; empty = the plugin's default
        // panel (named after the plugin).
        public string? Panel { get; set; }

        // Buttons sharing a Group render as one container button.
        // GroupKind: "Pulldown" (default) or "Split".
        public string? Group { get; set; }
        public string? GroupKind { get; set; }

        // Buttons sharing a Stack render as stacked items (2 or 3 per stack).
        public string? Stack { get; set; }

        public bool SeparatorBefore { get; set; }

        // Renders in the panel's slide-out region (the expandable area
        // below the panel title). Slide-out items always come last.
        public bool SlideOut { get; set; }

        // Sort key within the panel; ties broken by Text.
        public int Order { get; set; }
    }

    public static class ButtonDefinitionScanner
    {
        public const string AttributeName = "DevReloadButtonAttribute";

        // Scans an assembly for IExternalCommand classes carrying a
        // [DevReloadButton] attribute (matched by type name). Commands
        // without the attribute get no ribbon presence by design.
        public static IReadOnlyList<ButtonDefinition> Scan(Assembly assembly)
        {
            var result = new List<ButtonDefinition>();
            foreach (var command in CommandScanner.FindExternalCommands(assembly))
            {
                Type? type = assembly.GetType(command.FullClassName);
                if (type == null) continue;

                // GetCustomAttributes instantiates EVERY attribute on the
                // type — an unrelated attribute whose type can't resolve
                // (missing optional dependency) must cost that command its
                // button, not the whole plugin load.
                object? attribute;
                try
                {
                    attribute = type
                        .GetCustomAttributes(inherit: false)
                        .FirstOrDefault(a => a.GetType().Name == AttributeName);
                }
                catch (Exception)
                {
                    continue;
                }
                if (attribute == null) continue;

                var def = new ButtonDefinition(command.FullClassName)
                {
                    Text = ReadString(attribute, "Text") is { Length: > 0 } text
                        ? text
                        : command.DisplayName,
                    Tooltip = ReadString(attribute, "Tooltip"),
                    LongDescription = ReadString(attribute, "LongDescription"),
                    Icon16 = ReadString(attribute, "Icon16"),
                    Icon32 = ReadString(attribute, "Icon32"),
                    Panel = ReadString(attribute, "Panel"),
                    Group = ReadString(attribute, "Group"),
                    GroupKind = ReadString(attribute, "GroupKind"),
                    Stack = ReadString(attribute, "Stack"),
                    SeparatorBefore = ReadBool(attribute, "SeparatorBefore"),
                    SlideOut = ReadBool(attribute, "SlideOut"),
                    Order = ReadInt(attribute, "Order"),
                };
                result.Add(def);
            }
            return result;
        }

        private static string? ReadString(object attribute, string property)
        {
            object? value = attribute.GetType()
                .GetProperty(property)?.GetValue(attribute);
            string? s = value as string;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static bool ReadBool(object attribute, string property)
        {
            object? value = attribute.GetType()
                .GetProperty(property)?.GetValue(attribute);
            return value is bool b && b;
        }

        private static int ReadInt(object attribute, string property)
        {
            object? value = attribute.GetType()
                .GetProperty(property)?.GetValue(attribute);
            return value is int i ? i : 0;
        }
    }

    // Panel-level layout computed from button definitions: stable ordering,
    // container groups (pulldown/split) and stacks resolved. Pure data — the
    // Revit-API rendering happens in RibbonBuilder.
    public sealed class PanelLayout
    {
        public PanelLayout(string title, IReadOnlyList<PanelEntry> entries)
        {
            Title = title;
            Entries = entries;
        }

        public string Title { get; }
        public IReadOnlyList<PanelEntry> Entries { get; }
    }

    // Exactly one of Single / Group / Stack is non-null per entry.
    // (Constructor-shaped, no init accessors — this file compiles for net48
    // hosts too, which lack IsExternalInit.)
    public sealed class PanelEntry
    {
        private PanelEntry(
            ButtonDefinition? single, ButtonGroup? group,
            IReadOnlyList<ButtonDefinition>? stack, bool separatorBefore,
            bool slideOut)
        {
            Single = single;
            Group = group;
            Stack = stack;
            SeparatorBefore = separatorBefore;
            SlideOut = slideOut;
        }

        public static PanelEntry ForSingle(ButtonDefinition single, bool separatorBefore)
            => new(single, null, null, separatorBefore, single.SlideOut);

        public static PanelEntry ForGroup(ButtonGroup group, bool separatorBefore)
            => new(null, group, null, separatorBefore,
                   group.Members.All(m => m.SlideOut));

        public static PanelEntry ForStack(IReadOnlyList<ButtonDefinition> stack, bool separatorBefore)
            => new(null, null, stack, separatorBefore,
                   stack.All(m => m.SlideOut));

        public ButtonDefinition? Single { get; }
        public ButtonGroup? Group { get; }
        public IReadOnlyList<ButtonDefinition>? Stack { get; }
        public bool SeparatorBefore { get; }
        public bool SlideOut { get; }
    }

    public sealed class ButtonGroup
    {
        public ButtonGroup(string name, string kind, IReadOnlyList<ButtonDefinition> members)
        {
            Name = name;
            Kind = kind;
            Members = members;
        }

        public string Name { get; }
        public string Kind { get; } // "Pulldown" | "Split"
        public IReadOnlyList<ButtonDefinition> Members { get; }
    }

    public static class RibbonLayoutBuilder
    {
        // defaultPanelTitle: used for definitions without an explicit Panel —
        // by convention the plugin/addin name, so buttons stay grouped per
        // plugin and never mix (one panel per plugin minimum).
        public static IReadOnlyList<PanelLayout> Build(
            IEnumerable<ButtonDefinition> definitions, string defaultPanelTitle)
        {
            var panels = new List<PanelLayout>();
            var byPanel = definitions
                .GroupBy(d => string.IsNullOrWhiteSpace(d.Panel) ? defaultPanelTitle : d.Panel!)
                .OrderBy(g => g.Key == defaultPanelTitle ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var panelGroup in byPanel)
            {
                var ordered = panelGroup
                    .OrderBy(d => d.Order)
                    .ThenBy(d => d.Text, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var entries = new List<PanelEntry>();
                var consumed = new HashSet<ButtonDefinition>();

                foreach (var def in ordered)
                {
                    if (consumed.Contains(def)) continue;

                    if (!string.IsNullOrWhiteSpace(def.Group))
                    {
                        var members = ordered
                            .Where(d => string.Equals(
                                d.Group, def.Group, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        members.ForEach(m => consumed.Add(m));
                        string kind = members
                            .Select(m => m.GroupKind)
                            .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k)) ?? "Pulldown";
                        entries.Add(PanelEntry.ForGroup(
                            new ButtonGroup(def.Group!, kind, members),
                            members.Any(m => m.SeparatorBefore)));
                    }
                    else if (!string.IsNullOrWhiteSpace(def.Stack))
                    {
                        var members = ordered
                            .Where(d => string.Equals(
                                d.Stack, def.Stack, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        members.ForEach(m => consumed.Add(m));
                        entries.Add(PanelEntry.ForStack(
                            members, members.Any(m => m.SeparatorBefore)));
                    }
                    else
                    {
                        consumed.Add(def);
                        entries.Add(PanelEntry.ForSingle(def, def.SeparatorBefore));
                    }
                }

                // Slide-out entries must come last — Revit's AddSlideOut is
                // one-way: everything added after it lands in the slide-out.
                panels.Add(new PanelLayout(
                    panelGroup.Key,
                    entries.OrderBy(e => e.SlideOut ? 1 : 0).ToList()));
            }

            return panels;
        }
    }
}
