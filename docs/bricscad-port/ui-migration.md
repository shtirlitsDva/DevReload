<bricscad-ui-migration>

<summary>
How to port an AutoCAD .NET plugin's UI to BricsCAD V26. Learned while porting DevReload and
ACD-MCP; measured in BricsCAD V26.2. The one big change is that a `PaletteSet` becomes a
`Bricscad.Windows.Panel`. The WPF views themselves carry over unchanged: same XAML, same
view-models. What changes is the container around them, when it is created, how it is shown,
and the colours. `design.md` holds the rest of the port (namespaces, auto-scan, bundles).
</summary>

<why-not-paletteset>
BricsCAD does have a `Bricscad.Windows.PaletteSet`, and AutoCAD code compiles against it. Don't
use it. It is a compatibility shim over BricsCAD's Qt UI:
- docked, it loses auto-hide;
- it squashes `AddVisual` tabs together;
- it forgets where it was docked.

BricsCAD's own panels (Properties, Layers) are native tool panels, and `Panel` gives a plugin
the same thing.
</why-not-paletteset>

<mental-model>
AutoCAD: a plugin owns a PaletteSet window. It sets the size, dock side, auto-hide style and
tabs (`AddVisual`), and shows or hides it.

BricsCAD: a plugin hands BricsCAD a view, a name, a title and an icon. BricsCAD owns the rest.
- **Stacks.** Panels live in dock stacks: `RDOCK` (right, with Properties and Layers),
  `LDOCK`, `TDOCK`, `BDOCK`, `CDOCK`. The user can drag a panel to another stack or float it.
- **Stack look.** How every stack looks is ONE user setting, `STACKPANELTYPE`, and a plugin
  never picks it:
  - `0` tabs. The tab label is the panel's `Name`, not its `Title`.
  - `1` collapsible. An icon strip; the panel flies out OVER the drawing.
  - `2` resizable flyout. An icon strip; an open panel NARROWS the drawing.
- **Peer of the built-in panels.** A Panel shows in the stack, in right-click > Panels, and in
  `-TOOLPANEL` (Show / Hide / Toggle).
- **Layout is saved by BricsCAD.** Where a panel sits is stored in the workspace:
  `default.cui`, one `WSESW` entry per panel name.
- **No tabs inside a panel.** A panel has no tabs of its own. A multi-tab palette becomes one
  panel with our own tab strip inside (see `<tabs>`).
</mental-model>

<mapping>
| AutoCAD `PaletteSet` | BricsCAD `Panel` |
|---|---|
| `new PaletteSet(title, cmd, guid)` | `new Panel(name, new DockingTemplate(DockSides.Right, "RDOCK", z), visual)`; the `name` is the identity, there is no Guid |
| `AddVisual("TAB", view)` ×n | one Panel; tabs are our own control (`SideTabHost`) |
| `Visible = true` | bring forward: `Visible = false; Visible = true` (see `<showing>`) |
| `Activate(tabIndex)` | select our own tab, then bring forward |
| `Style` (auto-hide, close button, snappable), `Dock`, `Size`, `MinimumSize` | nothing; owned by the user, the stack and `STACKPANELTYPE` |
| `Icon` (`System.Drawing.Icon`) | `Icon` must be a WPF `BitmapSource`; a `DrawingImage` shows a "P" placeholder |
| `StateChanged` | `StateChanged` (`PanelStateEventArgs.NewState`) |
| created lazily in the palette command | created in `Initialize` (see `<lifecycle>`) |
| `Close()` / `Dispose()` | cannot be removed; empty it instead |
| position saved per Guid | position saved in the workspace per `name` |
</mapping>

<lifecycle>
- **Create panels when the plugin loads.** Do it in `IExtensionApplication.Initialize`, as
  Bricsys' own `API\dotnet\CsBrxMgd` sample does. The icon is then on the stack from startup,
  and BricsCAD restores the panel where the workspace last had it. Created only inside a
  command, BricsCAD doesn't know the panel exists until someone runs that command. This is a
  rule for all our BricsCAD plugins.
- **The command only brings the panel forward.** It never creates the panel.
- **One Panel per name, per session.** A Panel can't be removed, and a second Panel with the
  same name is silently ignored (the first keeps its content). With hot reload (DevReload), a
  reload therefore must not create the panel again.
  - Create each Panel once per BricsCAD session around a `ContentControl`.
  - Park both in `AppDomain` data.
  - Each load puts new views into the ContentControl; `Terminate` / `Dispose` empties it and
    unhooks every event.
  - Put only BricsCAD and WPF types into `AppDomain` data. A type from the plugin's own
    assembly there would pin the collectible ALC, and the plugin could never unload.
- **Choose names with care.**
  - The name is permanent: renaming leaves a stale `WSESW` entry behind in `default.cui`.
  - The name is also the tab label in tabs mode, so use something readable (`ACD-MCP`, not
    `AcdMcpScriptPanel`).
- **Plain plugins.** A plugin that is never hot-reloaded (DevReload itself) can simply keep
  the Panel in a static field. `EnsureManagerPanel()` in `DevReloaderCommands.cs` does that.

```csharp
// Hot-reloadable panel (ACD-MCP ScriptPanel.cs, trimmed).
const string SlotKey = "MyPlugin.Panel";
if (AppDomain.CurrentDomain.GetData(SlotKey) is not object[] slot)
{
    var host = new ContentControl();
    slot = [new Panel("MyPlugin", new DockingTemplate(DockSides.Right, "RDOCK", 40), host)
               { Title = "My Plugin", Icon = GlyphIcon("") }, host];
    AppDomain.CurrentDomain.SetData(SlotKey, slot);
}
var panel = (Panel)slot[0];
var hostControl = (ContentControl)slot[1];
hostControl.Content = new MyView();   // on load
// ...
hostControl.Content = null;           // in Terminate
```
</lifecycle>

<showing>
- **Bring forward = hide, then show.**

  ```csharp
  if (panel.Visible) panel.Visible = false;
  panel.Visible = true;
  ```

  It opens a closed panel. For an open one, it expands it (icon and flyout stacks) or selects
  its tab (tabs mode), and the panel stays in its stack. It does exactly what
  `-TOOLPANEL Show` does; verified live, both within one call and across calls.
- **`Visible = true` alone does nothing on an open panel.** That includes a panel collapsed to
  its icon.
- **✕ close.** The user's ✕ gives `Visible = false` plus a Hide event.
- **One command per panel.** That is BricsCAD's own convention (`LAYERSPANELOPEN`), not one
  command that opens several.
- **Agent- or code-driven changes.** When code changes what a panel shows (for example, the
  agent edits the BATCH tab), select the right tab and bring the panel forward, so the user
  sees the change.
- **Collapsed state is not reported.** No API tells you whether a panel is collapsed. `Visible`
  only tells whether it is on the stack at all.
</showing>

<tabs>
AutoCAD's PaletteSet gives tabs down its edge for free; a BricsCAD Panel gives nothing. So we
build them ourselves, as vertical tabs INSIDE the panel's left edge: ACD-MCP's
`Ui/SideTabHost.cs`, with the `SideTab` / `SideTab.Strip` styles in `Theme.xaml`.
- **The look.** Each tab is a `RadioButton` with its label rotated -90°. The selected tab takes
  the page colour and gets a 3 px accent bar, so it reads as joined to the page.
- **Switching.** It hides the other page, as a PaletteSet does, and never swaps it out. Editors
  keep their caret, scroll and undo history.
- **AutoCAD.** It keeps `PaletteSet.AddVisual`. The tab host is compiled only under `BRICSCAD`.
- **Tabs sticking out past the panel's left edge** (like AutoCAD's): rejected after a
  prototype.
  - WPF can't draw outside its host window, so such a strip must be a separate `Popup` window.
  - The popup must follow the panel on a timer.
  - It is topmost, so it can float over other applications.
  - Clicking it moves focus out of the flyout.
  - It is fragile for a cosmetic gain. Put tabs along the top if the side strip ever costs too
    much width.
</tabs>

<theming>
BricsCAD's dark UI is not AutoCAD's, so each host gets its own colours. The styles stay shared.
- **One Theme.xaml, one palette per host.**
  - Theme.xaml holds every style and full `ControlTemplate`. It merges `Palette.xaml`, which
    defines only `Color` keys.
  - Each head compiles ONE `Palette.<host>.xaml` as `Palette.xaml`. DevReload uses the csproj
    property `ThemePalette` (`Default` / `BricsCAD`). ACD-MCP's BricsCAD head links
    `Palette.BricsCAD.xaml` in its `Page` items.
  - Both palettes must define exactly the same keys.
- **Styles live only in the theme.** Views never use inline colours or styles, and new
  controls (like the side tabs) get their styles in Theme.xaml, never in the view.
- **Colour rules for BricsCAD:**
  - Take neutrals from Bricsys' own design tokens (bricscad.octave.com): neutral 900
    `#1A1A1F`, 800 `#29292E`, 700 `#3E4047`, 600 `#565A64`, 500 `#6F7480`, 400 `#9096A2`,
    300 `#B2B7C4`, 200 `#CBD0D8`, 100 `#E7EBF2`.
  - BricsCAD's own chrome measures `#18191C` (strips, drawing, status bar) and `#2D3135`
    (ribbon, panels).
  - No pure greys (R = G = B). They read brownish next to BricsCAD's cool UI. Every neutral
    gets a slight blue bias.
- **Colours in use:**

  | Role | Colour |
  |---|---|
  | Panel body | `#25292E` |
  | Cards, bars | slate navy `#1F2633` |
  | Controls | `#2C3647` (hover `#36425A`) |
  | Inset, editor, tab strip | `#1A1F28` |
  | Border | `#3E4047` |
  | Text | `#E7EBF2` (muted `#9096A2`) |
  | Accent | `#3D78C5` |
  | Selection | `#37629A` |
- **Full templates.** Every control with OS-drawn parts (ComboBox, ScrollBar, CheckBox,
  Expander, ToolTip) needs a full template; a brush override is not enough. Otherwise light
  popups and scrollbars leak through. Same rule as on AutoCAD.
- **Icons.** Render a Segoe MDL2 glyph into a 32 px `RenderTargetBitmap` (the `GlyphIcon`
  helper in `ScriptPanel.cs` and `DevReloaderCommands.cs`). In C# source, write the glyph as
  an escape (`""`): the literal private-use character gets lost in copy-paste and tools.
</theming>

<build>
- **BricsCAD head.** Add a second csproj next to the AutoCAD one:
  - it compiles the SAME `.cs` and `.xaml` files as links, with `DefineConstants=BRICSCAD`;
  - host differences go in `#if BRICSCAD` blocks, never in forked files;
  - a type alias hides the container difference from callers, e.g.
    `using ScriptPalette = Acd.Mcp.Ui.ScriptPanel;` vs `ScriptPaletteSet`.
- **References.** `BrxMgd` + `TD_Mgd` (`Private=False`), or the `BricsCAD.26.NET` package with
  `ExcludeAssets="runtime"`.
- **UI namespaces.** `Bricscad.Windows` (Panel, DockingTemplate, DockSides) and
  `Teigha.Runtime`. Alias `Panel = Bricscad.Windows.Panel`, because WPF also has a `Panel`.
- **CS0012 about `System.Windows.Forms`.** `Panel` has WinForms `Control` constructor
  overloads, so the compiler needs WinForms. Set `<UseWindowsForms>true</UseWindowsForms>`.
  It is a compile-time reference only; nothing WinForms runs. The side effect is ambiguous
  names such as `UserControl` (CS0104) under `ImplicitUsings`. Turn ImplicitUsings off or alias
  the names.
- **Keep the assembly name the same** as the AutoCAD head, so pack URIs
  (`/Acd.Mcp;component/...`) resolve in both.
</build>

<testing>
- **Throwaway sandbox.** `PanelStackDemo` (a throwaway plugin, loaded through DevReload) has
  three dummy panels, stack-type switch commands (`PSD_TABS` / `PSD_ICONS` / `PSD_FLYOUT` /
  `PSD_RESTORE`), a colour board and the tab prototype. Use it to try panel behaviour before
  touching a real plugin.
- **Checks for every port:**
  - the icon is on the stack right after startup;
  - the command brings the panel forward in all three `STACKPANELTYPE` modes;
  - after a DevReload reload, the panel still shows the new views;
  - the panel appears in `-TOOLPANEL` and in right-click > Panels.
- **Start BricsCAD yourself.** A BricsCAD started by an agent process never fires
  `Application.Idle`, so anything waiting for Idle (auto-start, the RPC pipes) starves.
- **Screenshots.**
  - `ui_screenshot_window` (PrintWindow) often misses flyout content: a flyout can come back as
    an empty frame. Look at the screen before calling a panel broken.
  - Region capture grabs whatever window is in front.
- **Reading `-TOOLPANEL ?` output.** Its paging swallows input. Set `LOGFILEMODE 1` and read
  the log file instead.
</testing>

<wrong-turns>
Assumptions that turned out false, so nobody repeats them:
- "The .NET Panel is undocumented." Wrong: the DevRef has it, and BricsCAD ships samples (see
  `<references>`).
- "Code can't expand a docked panel." Wrong: hide + show does it.
- "Hiding a panel in code pulls it out of its stack." Wrong: it stays in the stack.
- "Flyout and collapsible are the same." Wrong: collapsible (1) overlays the drawing, while the
  resizable flyout (2) narrows it.
- "Create the panel lazily in its command." It works, but the icon is missing until someone
  runs the command.
</wrong-turns>

<references>
- API: https://developer.bricsys.com/bricscad/help/en_US/CurVer/DevRef/ (BricsCAD .NET API >
  Bricscad.Windows > Panel; the BRX side is `BcUiPanel`). The site is a frameset: grep
  `files/treearr.js` for the page GUID, then fetch `source/html/<guid>.htm`.
- Samples in the BricsCAD install:
  - `API\dotnet\CsBrxMgd\CsBrxMgd\Commands.cs` (.NET Panel created in Initialize);
  - `API\brx\brxSample\cmd\cmdGui.cpp` (`BcUiPanelMFC`).
- User docs: https://help.bricsys.com/en-us/document/bricscad/panels (stacks,
  `STACKPANELTYPE`, `-TOOLPANEL`).
- Worked examples:
  - DevReload `DevReloaderCommands.cs`: a single view, static Panel.
  - ACD-MCP `Ui/ScriptPanel.cs` + `Ui/SideTabHost.cs`: two tabs, hot-reloadable.
</references>

</bricscad-ui-migration>
