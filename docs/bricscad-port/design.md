<bricscad-port>

<summary>
DevReload on BricsCAD V26 (.NET 8). The .NET plugin loop (register, LOAD / DEV / UNLOAD,
DEVRELOAD palette), the in-process MCP tools and the out-of-process `acad_*` process tools work,
checked live in BricsCAD V26.2.08. ACD-MCP runs in BricsCAD too (its own BricsCAD head), and
DevReload loads it on dev machines like any other plugin.

This file holds only what is specific to DevReload. General BricsCAD porting knowledge (API
mapping, panels, transients, `Application.Idle`, auto-scan, bundles, testing) lives in the
shared porting docs:
- `X:\AutoCAD DRI - 01 Civil 3D\Dev\00 Bricscad porting\bricscad-porting.md` (main, start here)
- `X:\AutoCAD DRI - 01 Civil 3D\Dev\00 Bricscad porting\ui-panels.md`
- `X:\AutoCAD DRI - 01 Civil 3D\Dev\00 Bricscad porting\transients.md`

Add general findings there, not here.
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

<ui>
General panel rules: `ui-panels.md` on X:.

- DevReload creates one Panel, `DevReloadManager`, in `RDOCK` when it loads
  (`EnsureManagerPanel()` in `DevReloaderCommands.cs`); `DEVRELOAD` brings it forward.
  DevReload is never hot-reloaded, so the Panel lives in a static field and is never removed.
- The icon is a Segoe MDL2 glyph rendered by `GlyphIcon` in `DevReloaderCommands.cs`.
- Colours: each project compiles one `Palette.<name>.xaml` as `Themes/Palette.xaml`, which
  Theme.xaml merges. The csproj property `ThemePalette` (`Default` / `BricsCAD`) picks it;
  BricsCAD heads use `Palette.BricsCAD.xaml`.
- `BricsCadTitleBars` (WinEvent hook) themes every BricsCAD window's title bar. It starts in
  `Initialize`, which BricsCAD defers until the first drawing opens, so windows opened from the
  Start page stay light.
- No `RegisterRestartableTool` yet (untested).
</ui>

<main-thread>
Background: `bricscad-porting.md` `<main-thread>` on X: (`Application.Idle` never fires in an
agent-started BricsCAD).

- `AcadMainThreadDispatcher` posts a drain to the main thread's `SynchronizationContext` and
  runs tool work only when `DocumentManager.IsApplicationContext` and no modal loop is up;
  otherwise it retries every 100 ms.
- ACD-MCP posts its auto-start and every tool call (`Pipe/MainThread.cs`).
- `acad_start` opens a new drawing from `Default-m.dwt` (meters) with `/T`, since the Start page
  has no document.
</main-thread>

<auto-scan>
Background: `bricscad-porting.md` `<auto-scan>` on X:.

`BricsCadScanSuppressor` unsubscribes BricsCAD's `AssemblyLoader.OnLoad` and adds a wrapper that
skips assemblies in an `IsolatedPluginContext`. Call sites use a `ScanSuppressor` alias, so
AutoCAD and BricsCAD share one path. Verified live: Initialize runs once, DEV swaps the code,
and UNLOAD removes the command.
</auto-scan>

<config>
`%APPDATA%\DevReload\plugins.bricscad.json`. Each host has its own file, because a plugin is
built against one host's API.
</config>

<autoload>
Install `Deploy\BcadDevReload.bundle` in `%APPDATA%\Bricsys\ApplicationPlugins` as
`DevReload.bundle` (`SeriesMin="26" SeriesMax="26"`; why: `bricscad-porting.md` `<autoload>`
on X:).
</autoload>

<process-tools>
`Acad.Process` discovers BricsCAD from the registry (see `bricscad-porting.md` `<discovery>` on
X:) as flavor `BricsCAD`, and enumerates `bricscad.exe` alongside `acad.exe`. The MCP tools are
shared; they route to a host by pid.
</process-tools>

<plugins>
A plugin must reference `BrxMgd`/`TD_Mgd` and use the `Bricscad.*`/`Teigha.*` namespaces. A
plugin that targets both hosts can use the same `#if` pattern with two csproj heads (ACD-MCP's
`Bcad.Mcp` is the worked example).
</plugins>

<hud>
Background: `transients.md` on X: (the `DrawableIsAnEntity` bit).

- `ReloadHudOverrule` on a `DBPoint` transient carrier returns `1 | 256 | 2048 | 16384` under
  `#if BRICSCAD`. Without bit 1 the HUD drew nothing in BricsCAD.
- Layout is SCREENSIZE-based on both hosts (why: `transients.md` `<viewport-data>`).
- `ReloadHud.PumpPaint` pumps while the reload holds the main thread (`transients.md`
  `<driving-updates>`).
- The HUD needs a drawing area at least 220 px tall; a small window clips or hides it.
- It logs "HUD registered but never drawn" when a cycle ends with zero frames; that is the
  symptom a missing entity bit causes.
</hud>

<deferred>
- OARX: ObjectARX-only, so the OARX tab and `oarx_*` tools are left out on BricsCAD. The
  BricsCAD equivalent is a BRX module, which waits for the BRX SDK.
</deferred>

</bricscad-port>
