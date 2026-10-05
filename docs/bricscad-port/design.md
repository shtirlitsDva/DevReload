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

- `AcadMainThreadDispatcher` posts a drain to the main thread's WPF `Dispatcher` and
  runs tool work only when `DocumentManager.IsApplicationContext` and the main frame is
  enabled (every modal dialog disables it); otherwise it retries every 100 ms.
  `GetGUIThreadInfo` has no modal-loop flag: the old gate's "GUI_INMODALLOOP" 0x1 was
  `GUI_CARETBLINKING`, which BricsCAD leaves set at idle after a command ended with Enter, and
  every later call waited (2026-10-05). Never the `SynchronizationContext` current at
  Initialize: WinForms swaps that one when its outermost modal loop ends, and a drain posted
  through a swapped-in plain context runs on the thread pool, where it can never run its work
  and retries forever (the Civil hang after a Drawing Recovery box, 2026-10-05).
- `acad_open_drawing`, `acad_new_drawing` and `acad_activate_document` reply once the drawing
  is the active one and the host is quiescent: Open/Add return while the activation is still
  pending, and a reply then let the caller's next input land in the previous drawing.
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

<oarx>
- OARX runs on both hosts: the `oarx_*` tools and the `{PREFIX}` commands are gated live on
  BricsCAD V26.2 (the OARX tab compiles there; not yet looked at). BricsCAD loads BRX modules through the same `SystemObjects.DynamicLinker`
  `LoadModule`/`UnloadModule`, and the groups live in `plugins.bricscad.json`.
- BricsCAD will not unload a module while an open drawing holds objects of its classes. It sends
  `kUnloadAppMsg` first, takes the module's OK, and only then keeps the module, so a module with no
  guard of its own is left torn down under live objects and the next regen crashes (measured
  2026-10-02, NorsynDrawingTools). AutoCAD unloads and keeps the objects as stand-ins.
- So on BricsCAD `OarxDrawingCycle` closes every named drawing before the unload and, on a reload,
  reopens them after the load and makes the active one active again. One drawing always stays open
  (the Start tab has no document to run in): an unnamed one with no unsaved changes, or a new blank one.
- Unsaved changes: `modifiedDrawings` on `oarx_reload` / `oarx_unload_plugin` is `refuse` (default),
  `save` or `discard`. Everything is decided before anything closes, so a refusal changes nothing.
  An unnamed drawing with unsaved changes cannot be reopened, so only `discard` closes it.
- `{PREFIX}LOAD/DEV/UNLOAD` are `Session` commands on BricsCAD: a document command cannot close its
  own document. The in-editor commands use `refuse`.
- A failed build leaves the group unloaded and the drawings reopened with stand-ins for its
  objects. If BricsCAD then counts them as modified, the next reload refuses until it is given
  `discard`; the refusal names the drawings.
</oarx>

</bricscad-port>
