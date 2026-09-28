<bricscad-port>

<summary>
DevReload on BricsCAD V26 (.NET 8). Phase 1: the .NET plugin loop (register, LOAD / DEV /
UNLOAD, DEVRELOAD palette) works, and it has been checked live in BricsCAD V26.2.08.
</summary>

<structure>
- `src/Bricscad/BcadDevReload/BcadDevReload.csproj` is the BricsCAD host head. It compiles
  the SAME sources as `src/Autocad/DevReload` (linked `Compile`/`Page` items) with
  `DefineConstants=BRICSCAD`, referencing `BrxMgd.dll` + `TD_Mgd.dll` (`Private=False`).
- Host differences live in `#if BRICSCAD` blocks inside those sources. Never fork a file.
- `BricsCADPath` in `Directory.Build.props` (default `C:\Program Files\Bricsys\BricsCAD V26 en_US`);
  you can override it the same ways as `AutoCADPath`.
- Output: `BcadDevReload.dll`.
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
Dev loop: `HKCU\Software\Bricsys\BricsCAD\V26x64\en_US\Applications\BcadDevReload` with
`LOADER`=<path to BcadDevReload.dll>, `LOADCTRLS`=2, `MANAGED`=1.
</autoload>

<plugins>
A plugin must reference `BrxMgd`/`TD_Mgd` and use the `Bricscad.*`/`Teigha.*` namespaces. A
plugin that targets both hosts can use the same `#if` pattern with two csproj heads.
</plugins>

<deferred>
- Reload HUD: the DrawableOverrule transient is never drawn in BricsCAD. Each reload logs
  "HUD registered but never drawn". It does no harm.
- OARX: ObjectARX-only. The companion load is a no-op with a warning. The OARX tab still shows.
- Out-of-process bridge / MCP tools: `Acad.Process` only discovers `acad.exe`. The in-process
  pipe (`acad-rpc-<pid>`) does open in BricsCAD.
- Release bundle / installer for BricsCAD (`PackageContents.xml` RuntimeRequirements for
  BricsCAD not researched).
- The palette opens with its .NET/OARX tabs. Nobody has looked at the WPF content by eye yet,
  because PrintWindow could not capture it.
</deferred>

</bricscad-port>
