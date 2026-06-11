---
name: revit-new-command
description: Scaffold a new Revit IExternalCommand in a DevReload-managed plugin project, interview the user about its ribbon UI (button kind, grouping, icons, placement), generate icon images on request, and hot-reload so the button appears immediately. Activates when the user asks to "add a command", "create a ribbon button", "new Revit command", or wants to design/extend a plugin's ribbon UI.
when_to_use: User wants a new command (or a ribbon redesign) in a Revit plugin that is developed with DevReload. The skill owns the full cycle - interview, scaffold, icons, attribute, reload, verify the button on the DevReload tab.
---

<how-ribbon-rendering-works>
Commands get ribbon buttons ONLY through a `[DevReloadButton]` attribute on
the command class. The attribute is matched BY NAME via reflection — each
plugin declares its OWN copy of `DevReloadButtonAttribute` (no shared
contract dll). Two renderers read it:
- the DevReload host: hot-reload ribbon on the "DevReload" tab (panel per
  plugin, exists only while the plugin is loaded, rebuilt on every reload);
- the NorsynApps standalone reflector: release ribbon on the "Norsyn" tab
  (buttons bound directly to the installed dll).
Commands WITHOUT the attribute get no button; they remain invocable from
the DevReload manager window (dev) and the NorsynApps "All Commands"
browser (release).

Attribute fields (all optional except none):
Text, Tooltip, LongDescription, Icon16, Icon32 (embedded-resource name
suffixes; omit for a text-only button), Panel (defaults to a panel named
after the plugin), Group + GroupKind ("Pulldown" default | "Split"),
Stack (2-3 commands per stack), SeparatorBefore (bool), SlideOut (bool —
renders in the panel's expandable slide-out region, always last), Order
(int sort key). The renderer source of truth is
`src/Revit/RevitDevReload.Shared/Core/RibbonDefinition.cs` and
`Host/RibbonBuilder.cs` in the DevReload repo.
</how-ribbon-rendering-works>

<the-interview>
Ask only what the request leaves open — never re-ask what the user already
said. Decision tree:

1. WHICH PROJECT - the target plugin csproj (must be registered in
   DevReload; if not, register it first via the manager or pipe).
2. UI PRESENCE - does this command need a ribbon button at all?
   No → scaffold without attribute, done after reload (manager-window /
   command-browser invocation only).
3. KIND - map the user's intent onto what Revit offers:
   - Standalone push button (the default).
   - Member of a PULLDOWN (menu of related commands, one shared top icon)
     → same `Group` value on every member; GroupKind omitted.
   - Member of a SPLIT button (last-used command becomes the big button,
     arrow opens the rest) → same `Group` + GroupKind = "Split".
   - STACKED (2-3 small buttons in a column, saves panel width)
     → same `Stack` value on the members.
   - Visual separation from the previous item → SeparatorBefore = true.
   - Secondary/rarely-used command → SlideOut = true (panel's expandable
     bottom region).
   - Panel placement: default plugin panel, or a named `Panel`.
   - Order: position within the panel (lower = further left).
   NOT command-bound (explain if asked, don't offer): ComboBox, TextBox,
   RadioButtonGroup, ToggleButton — these are data-entry ribbon items with
   event callbacks, not IExternalCommand targets; DevReload does not render
   them from attributes. Official reference for everything ribbon: Revit
   Developer's Guide > Add-In Integration > "Ribbon Panels and Controls"
   (help.autodesk.com), API details on revitapidocs.com (PushButtonData,
   PulldownButtonData, SplitButtonData, RibbonPanel.AddStackedItems).
4. TEXT/TOOLTIP - short button text (ribbon buttons truncate; two words
   max, `\n` for a deliberate line break), one-sentence tooltip.
5. ICONS - three options:
   a. None (text-only button) — perfectly legal.
   b. Existing embedded resources — ask for the two resource name suffixes.
   c. GENERATE them (default offer when the command has UI presence):
      create 16x16 and 32x32 PNGs — flat, single-glyph, dark-theme-legible
      (Revit 2024+ has light/dark themes; avoid pure black or pure white
      strokes, mid-tone colors read in both). Generation routes: a short
      python script with Pillow in the repo's .venv, or System.Drawing via
      PowerShell for simple letterform glyphs. Embed via
      `<EmbeddedResource Include="Resources\<Name>16.png" />` (match the
      project's existing resource conventions first).
</the-interview>

<scaffold-steps>
1. Ensure the plugin declares `DevReloadButtonAttribute`. If missing, copy
   the canonical template (see `revit-pcf-exporter-shared/App/App.cs` in
   the Revit-PCF-Exporter repo) into the plugin's namespace - sealed
   attribute class, AttributeUsage(Class), the exact property set listed
   above. Do NOT reference any DevReload assembly for it.
2. Create the command class: `[Transaction(TransactionMode.Manual)]`,
   `[DevReloadButton(...)]` per the interview, `IExternalCommand.Execute`
   with the real work (or a TaskDialog placeholder if the user only wants
   the wiring now). Match the project's existing command file layout.
3. Icons chosen/generated → add as embedded resources; verify resource
   NAME SUFFIXES match the attribute's Icon16/Icon32 values (resource names
   are prefixed with the project root namespace - suffix matching is the
   contract).
4. Hot-reload: `revit-cli send --cmd reload --plugin <name>` (pipe command
   `reload`; `revit-cli send --cmd get_state` to confirm), or the Reload
   button in the DevReload manager window if no CLI session is up.
5. VERIFY: the button must appear on the DevReload tab in the plugin's
   panel. If the plugin wasn't loaded, `load` first - panels exist only
   while the plugin is loaded. Check `get_log` for "ribbon rebuilt" and
   for switchboard slot errors (pool exhaustion says so explicitly).
</scaffold-steps>

<release-side-note>
Nothing extra to do for release: the NorsynApps reflector picks the new
attribute up from the recompiled dll automatically (its per-year project
references the plugin project — if this plugin is new to NorsynApps, add a
ProjectReference in the matching NorsynApps-<year>.csproj).
</release-side-note>
