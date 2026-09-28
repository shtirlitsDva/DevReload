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
| `PaletteSet` default style (auto-hide button shown) | default style has no auto-hide button; set `Style` explicitly |
</api-mapping>

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
