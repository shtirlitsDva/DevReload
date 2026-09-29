<bricscad-port>

<summary>
DevReload on BricsCAD V26 (.NET 8). The .NET plugin loop (register, LOAD / DEV / UNLOAD,
DEVRELOAD palette), the in-process MCP tools and the out-of-process `acad_*` process tools work,
checked live in BricsCAD V26.2.08. ACD-MCP runs in BricsCAD too (its own BricsCAD head), and
DevReload loads it on dev machines like any other plugin.
</summary>

<structure>
- `src/Bricscad/BcadDevReload/BcadDevReload.csproj` is the BricsCAD host head. It compiles
  the SAME sources as `src/Autocad/DevReload` (linked `Compile`/`Page` items) with
  `DefineConstants=BRICSCAD`, referencing `BrxMgd.dll` + `TD_Mgd.dll` (`Private=False`).
- Host differences live in `#if BRICSCAD` blocks inside those sources. Never fork a file.
- `BricsCADPath` in `Directory.Build.props` (default `C:\Program Files\Bricsys\BricsCAD V26 en_US`);
  you can override it the same ways as `AutoCADPath`.
- Output: `BcadDevReload.dll`. A Release build also writes `Deploy\BcadDevReload.bundle`.
</structure>

<api-mapping>
| AutoCAD | BricsCAD |
|---|---|
| `Autodesk.AutoCAD.ApplicationServices` / `EditorInput` / `Windows` / `Internal` | `Bricscad.*` (same sub-names) |
| `Autodesk.AutoCAD.Runtime` / `DatabaseServices` / `Geometry` / `GraphicsInterface` / `Colors` | `Teigha.*` |
| `Core.Application.MainWindow`, `Core.Application.DocumentManager` | `Bricscad.ApplicationServices.Application` (no Core equivalents) |
| `Core.Application.IsQuiescent` | `doc.Editor.IsQuiescent` |
| `Utils.IsCommandDefined` | `Utils.IsCommandNameInUse(name) != CommandTypeFlags.NoneCmd` |
| `DrawableAttributes` enum | raw ARX/ODA flag values (256, 2048, 16384) |
| `Utils.AddCommand` / `RemoveCommand` / `CommandCallback` | identical signatures in `Bricscad.Internal` |
| `PaletteSet` + `AddVisual` | `Bricscad.Windows.Panel` (see `<ui>`) |
</api-mapping>

<ui>
BricsCAD's own UI is Qt. Its `PaletteSet` is a compatibility shim: docked, it loses auto-hide,
mashes `AddVisual` tabs and forgets its position. The native .NET container is
`Bricscad.Windows.Panel(name, DockingTemplate, Visual)`. It docks into a panel stack, e.g.
`RDOCK` (Properties, Layers), with its own title and icon, and it hosts our WPF views unchanged.
Porting another plugin's UI: follow `ui-migration.md`.

References:
- API: https://developer.bricsys.com/bricscad/help/en_US/CurVer/DevRef/ (BricsCAD .NET API >
  Bricscad.Windows > Panel; the BRX side is `BcUiPanel`). Signatures only, no remarks.
- Samples shipped with BricsCAD: `API\dotnet\CsBrxMgd\CsBrxMgd\Commands.cs` (.NET Panel created
  in `Initialize`) and `API\brx\brxSample\cmd\cmdGui.cpp` (`BcUiPanelMFC`).
- User side: https://help.bricsys.com/en-us/document/bricscad/panels (stacks, `STACKPANELTYPE`,
  `-TOOLPANEL`).
The behaviour below was measured in V26.2.

- DevReload creates one Panel, `DevReloadManager`, in `RDOCK` when it loads; `DEVRELOAD`
  brings it forward. ACD-MCP does the same with one Panel, `ACD-MCP` (`ScriptPanel.cs`), with
  our own SCRIPT/BATCH side tabs inside (`SideTabHost.cs`), since a Panel has no tabs of its own.
- `Panel.Icon` must be a bitmap. A `DrawingImage` shows BricsCAD's "P" placeholder, so we
  render a Segoe MDL2 glyph into a `RenderTargetBitmap`.
- A Panel cannot be removed, and a second Panel with the same name is silently ignored (the
  first keeps its content). A hot-reloaded plugin therefore creates each Panel once per
  session around a `ContentControl`, parks both in `AppDomain` data (only BricsCAD/WPF types,
  so the ALC is not pinned), and swaps its views in on load and out on unload.
- A Panel is a native tool panel, a peer of Properties and Layers: it shows in the stack, in
  right-click > Panels and in `-TOOLPANEL` (Show/Hide/Toggle), and the workspace (`default.cui`,
  `WSESW` entries) stores where it sits. So create it when the plugin loads, as Bricsys'
  `API\dotnet\CsBrxMgd` sample does; created only inside a command, BricsCAD doesn't know it
  exists until that command runs.
- Layout belongs to the user and BricsCAD. `STACKPANELTYPE` is one setting for every stack; a
  plugin doesn't choose it. 0 tabs; 1 collapsible (icon strip, the panel flies out over the
  drawing); 2 resizable flyout (icon strip, the panel narrows the drawing). In tabs mode the tab
  label is the panel's `Name`, not its `Title`.
- "Bring forward" is `Visible = false` then `Visible = true`: the panel stays in its stack
  and expands (or gets its tab selected), the same as `-TOOLPANEL Show`. Verified live, in one
  call and across calls. Setting `Visible = true` on a panel that is already visible does
  nothing. A ✕ close gives `Visible=false` and a Hide event.
- One command per panel (BricsCAD's own convention, e.g. `LAYERSPANELOPEN`), not one command
  that opens several. Plugin teardown empties the panels instead of closing them.
- No `RegisterRestartableTool` yet (untested). A BricsCAD started by an agent process
  (`acad_start`, or `Start-Process` from the tool host) never fires `Application.Idle`, so both
  RPC pipes starve. This happens even with panel-free builds. A BricsCAD the user starts works.
- Colours: each project compiles one `Palette.<name>.xaml` as `Themes/Palette.xaml`, which
  Theme.xaml merges. BricsCAD heads use `Palette.BricsCAD.xaml` (greys and blue sampled from
  BricsCAD's UI). Switching palettes is one csproj property (`ThemePalette`).
</ui>

<auto-scan>
BricsCAD DOES scan assemblies as they load, like AutoCAD. The scanner is the private static
`Bricscad.ApplicationServices.AssemblyLoader.OnLoad`, subscribed directly to
`AppDomain.AssemblyLoad`. Without suppression (measured live): Initialize ran twice, and after
UNLOAD the command from the first load still answered.

`BricsCadScanSuppressor` unsubscribes that handler and adds a wrapper that skips assemblies in
an `IsolatedPluginContext`. Call sites use a `ScanSuppressor` alias, so AutoCAD and BricsCAD
share one path. Verified live: Initialize runs once, DEV swaps the code, and UNLOAD removes
the command.
</auto-scan>

<config>
`%APPDATA%\DevReload\plugins.bricscad.json`. Each host has its own file, because a plugin is
built against one host's API.
</config>

<autoload>
BricsCAD reads Autodesk-format bundles from `%APPDATA%\Bricsys\ApplicationPlugins`. Install
`Deploy\BcadDevReload.bundle` there as `DevReload.bundle`.

`RuntimeRequirements` takes the bare major: `SeriesMin="26" SeriesMax="26"`. `"V26"` and
`"R25.0"` never match, so the bundle is silently skipped. No registry key is needed.
</autoload>

<process-tools>
`Acad.Process` discovers BricsCAD from `HKLM\SOFTWARE\Bricsys\BricsCAD\V<n>x64\<locale>`
(`InstallDir`, `FullVersion`) as flavor `BricsCAD`, and enumerates `bricscad.exe` alongside
`acad.exe`. The MCP tools are shared; they route to a host by pid.
</process-tools>

<plugins>
A plugin must reference `BrxMgd`/`TD_Mgd` and use the `Bricscad.*`/`Teigha.*` namespaces. A
plugin that targets both hosts can use the same `#if` pattern with two csproj heads (ACD-MCP's
`Bcad.Mcp` is the worked example).
</plugins>

<deferred>
- Reload HUD: the DrawableOverrule transient is never drawn in BricsCAD mid-command
  (`SetAttributes` is called; `WorldDraw` only on an idle regen). Each reload logs "HUD
  registered but never drawn". It does no harm.
- OARX: ObjectARX-only, so the OARX tab and `oarx_*` tools are left out on BricsCAD. The
  BricsCAD equivalent is a BRX module, which waits for the BRX SDK.
</deferred>

</bricscad-port>
