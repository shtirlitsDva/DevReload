# DevReload: Hot-Reload Plugin System for AutoCAD 2025

DevReload lets you edit, build, and reload AutoCAD .NET plugins without restarting AutoCAD. It uses .NET 8 collectible `AssemblyLoadContext` to isolate plugins and stream-loads DLLs so your build can rebuild freely while the old plugin runs. The `{PREFIX}DEV` command builds your project, tears down the old plugin, and loads the new one in one step. There's also an Autocad palette where you can manage plugins you want to reload which is opened by `DEVRELOAD` command.

Your plugin needs no DevReload-specific source code: no marker class, no attribute, no reference to DevReload. An existing AutoCAD plugin works as-is, and the same DLL still loads under `NETLOAD`.

You register a plugin by picking its `.csproj` file; the plugin name is the project-file name. Builds run directly via `dotnet build`. No Visual Studio instance is required. A per-plugin **Debug/Release toggle** lets you switch build configurations from the management palette.

**Git worktree support**: DevReload detects worktrees for registered projects and lets you select which worktree to build from via a dropdown in the management palette.

## Install as a Claude Code / Codex plugin

DevReload also ships an MCP bridge (`Acad.Rpc.Bridge`) and the `/acd-agentic-dev` skill, so an agent (Claude Code or Codex) can drive the full edit → reload → live-test loop directly. The MCP bridge exposes three tool groups:

- `acad_*` covers AutoCAD/Civil 3D process and drawing control: locate installs, launch, attach/detach, list instances, send/post commands, wait for readiness, open/close drawings, quit.
- `devreload_*` covers plugin lifecycle: register, load, reload (build + hot-swap), unload, query state, switch build config and worktree.
- `ui_*` covers UI automation, so an agent can test a plugin's *UI*, not just drawing state: introspect and drive WPF palettes at the ViewModel level (invoke / set-value / toggle / select via UI Automation peers); enumerate and headlessly click native modal dialogs that have no .NET API (e.g. the COGO-point projection dialog); synthesize mouse input for jigs, grips and OSNAP; and capture inline screenshots scoped to a window, region, WPF element, or WCS box, including frame-by-frame drag bursts to watch a jig animate. This feature is quite weak, just something Claude whipped up in a session. Needs more work, but the goal is for autocad ui to be able to be controlled by AI.

All control flows over a per-instance named pipe (`acad-rpc-<pid>`) rather than COM, so you can run multiple AutoCAD/Civil 3D instances at once and drive any of them independently. Every `acad_*`, `devreload_*` and `ui_*` tool takes an optional `pid`: omit it to use the bound (default) instance, or pass it to target a specific one. `acad_wait_pipe` is the per-instance readiness gate; `acad_list_instances` enumerates running instances and their pipe state. (One caveat: the `ui_*` *synthetic-input* tools (mouse move/click/drag, key press) drive the one shared OS cursor/foreground, so they're addressable per `pid` but serialize across instances; the WPF-introspection, dialog and screenshot tools are fully per-instance and parallel-safe.)

> Note: this installs the **agent-side** MCP bridge + skill. The **AutoCAD-side** DevReload plugin (the palette + commands) is installed separately into AutoCAD. See [Installing the AutoCAD plugin](#installing-the-autocad-plugin).

### Claude Code

From any Claude Code session:

```
/plugin marketplace add shtirlitsDva/DevReload
/plugin install devreload
```

The marketplace entry resolves the plugin from the auto-built `release` branch (kept in sync by GitHub Actions on every master push), which includes the pre-packed MCP bridge. It needs no clone, no .NET SDK, and no Pack-Plugin step. The `acad_*` / `devreload_*` MCP tools appear automatically, and the skill becomes invokable as `/devreload:acd-agentic-dev`.

### Codex

Codex needs the bridge built locally because there is no equivalent of Claude Code's marketplace fetch for arbitrary skills + MCP servers.

```powershell
git clone https://github.com/shtirlitsDva/DevReload
cd DevReload
.\scripts\Install-Codex.ps1
```

`Install-Codex.ps1` runs `Pack-Plugin.ps1` (publishing the bridge into `./server/`), copies the skill into `%USERPROFILE%\.agents\skills\acd-agentic-dev\` (Codex's user-scope skills directory), and registers the MCP server in `%USERPROFILE%\.codex\config.toml`. If the `codex` CLI is on PATH it uses `codex mcp add`; otherwise it appends an idempotent `[mcp_servers.devreload]` block.

Restart Codex (or reload its config) and the skill is discoverable via `/skills`, the MCP tools via the usual tool selector.

### Local development on the plugin itself

If you're iterating on the bridge or the skill, run `.\scripts\Pack-Plugin.ps1` after each change and re-install the plugin pointed at your local checkout. Pack-Plugin does a framework-dependent publish (~5 MB, requires the .NET 8 runtime on consumers); `-SelfContained` switches it to a ~60 MB self-contained build that bundles the runtime.

## Installing the AutoCAD plugin

The AutoCAD-side plugin is the `DevReload.dll` assembly that provides the `DEVRELOAD` palette and the generated plugin commands. Install it one of two ways:

- **Bundle (autoload):** build Release to produce `Deploy/DevReload.bundle`, then drop that bundle into AutoCAD's `ApplicationPlugins` folder so it autoloads on startup.

  ```powershell
  dotnet build src/Autocad/DevReload/DevReload.csproj -c Release -p:Platform=x64
  ```

- **Manual:** `NETLOAD` `DevReload.dll` into a running AutoCAD session.

Then type `DEVRELOAD` to open the management palette.

## Quickstart

**Prepare your plugin**: implement `IExtensionApplication`.

DevReload calls `Initialize()` after load and `Terminate()` before unloading, both on the same instance. Every event subscription, palettes and other AutoCAD reference the plugin takes must be released in `Terminate()`, or the old build stays alive alongside the new one (see [Plugin Instance Lifetime](#plugin-instance-lifetime)).

**Use DevReload**

1. Start AutoCAD with the DevReload plugin installed (see [above](#installing-the-autocad-plugin)), type `DEVRELOAD` to open the management palette.
2. Click **+ Add Plugin** → pick your plugin's `.csproj` in the file dialog.
3. Optionally set a Command Prefix and Load-on-Startup, then click **Add** → your plugin is registered with `{PREFIX}LOAD` / `{PREFIX}DEV` / `{PREFIX}UNLOAD`.
4. Type `{PREFIX}LOAD` to load your DLL. It builds first if the DLL does not exist.
5. Edit code in your editor → type `{PREFIX}DEV` (or click **Reload**) → see changes instantly, no restart.

## Project Setup (.csproj)

Your plugin project needs these settings:
Note: this was written by AI. I don't know which of these are needed.

```xml
<PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Platforms>x64</Platforms>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <ImportWindowsDesktopTargets>true</ImportWindowsDesktopTargets>
    <OutputType>Library</OutputType>

    <!-- REQUIRED for collectible ALC -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

## Plugin Instance Lifetime

DevReload constructs one instance of your `IExtensionApplication` per load. It calls `Initialize()` on that instance once the assembly is loaded, and `Terminate()` on the same instance before the ALC is unloaded. Fields written in `Initialize()` are readable in `Terminate()`. Instance and static fields both work.

## Command Registration

`CommandRegistrar` scans the loaded assembly's exported types and registers every `[CommandMethod]` it finds with `Utils.AddCommand`. Commands registered that way can be removed with `Utils.RemoveCommand`, which DevReload does before it unloads the ALC. `[assembly: CommandClass]` has no effect on this scan; all exported types are read either way.

### What AutoCAD does when an assembly loads

`Autodesk.AutoCAD.Runtime.ExtensionLoader` subscribes to `AppDomain.CurrentDomain.AssemblyLoad` at startup. For each assembly it reads the AssemblyRef table and sets two flags: `MayHaveExtensionApplication` if the assembly references `acdbmgd`, `MayHaveCommands` if it references `accoremgd`. A plugin references both. The assembly is then raised on the public static event `ExtensionLoader.DeferredAssemblyLoad`, which has two subscribers:

- `Runtime.ExtensionLoader.OnDeferredAssemblyLoad` reads `[assembly: ExtensionApplication]`, or takes the first exported `IExtensionApplication` if the attribute is absent, constructs it and calls `Initialize()` on it. That instance goes into a static table keyed by assembly, which is emptied when AutoCAD exits.
- `ApplicationServices.ExtensionLoader.OnExtensionLoad` collects the types named by `[assembly: CommandClass]`, or every exported type if there are none, and registers each `[CommandMethod]` and `[LispFunction]` it finds on them.

Both run synchronously inside `AssemblyLoadContext.LoadFromStream`, before `PluginHost.Load` returns.

### What the suppressor does

The event carries no cancellation flag, so `AutoCadScanSuppressor` works on its delegate directly. `Install()` reads the private static backing field `m_deferredAssemblyLoadEventHandler`, keeps the delegate it finds there, and writes a wrapper in its place:

```csharp
DeferredAssemblyLoadEventHandler filtered = (sender, e) =>
{
    if (AssemblyLoadContext.GetLoadContext(e.LoadedAssembly) is IsolatedPluginContext)
        return;

    original?.Invoke(sender, e);
};
```

The test is which load context the assembly is in. It does not depend on when the assembly loaded, on which thread, or on a flag raised and lowered around the load, so there is no interval in which an unrelated assembly can be caught. Plugin dependencies resolve through the plugin's own `IsolatedPluginContext` and are covered by the same test.

`Install()` runs from `DevReloaderCommands.Initialize()`, before any plugin loads. It throws if the field is absent or is not a `DeferredAssemblyLoadEventHandler`.

`Restore()` runs at shutdown and writes the original delegate back only if the field still holds the wrapper. Anything subscribing to the event after DevReload starts is combined onto the wrapper rather than into it, and Civil 3D's macro recorder does exactly that. Overwriting the field unconditionally would discard those subscribers.

### What follows from it

- Commands come from `CommandRegistrar` alone, through `Utils.AddCommand`, so unloading can take them off the command stack again.
- AutoCAD constructs no instance of the plugin, so `PluginManager.LoadCore` calls `Initialize()` itself. The call is guarded on `AutoCadScanSuppressor.IsActive`, so an install that failed cannot produce two calls.
- No part of the plugin reaches AutoCAD's static table, so nothing outside the ALC holds a reference into it and it is collected after unload.

Assemblies loaded outside DevReload are unaffected. A `NETLOAD`ed DLL lands in the default load context, the wrapper forwards it, and AutoCAD registers its commands from the exported types.

If the suppressor cannot install, DevReload writes a warning to the command line at startup and AutoCAD's scan stays in place. Plugins on that AutoCAD version need `[assembly: CommandClass(typeof(NoCommands))]` pointing at an empty class.

`docs/oarx-port/nocommands-interception.md` records the decompiled sources these facts come from, and `labs/nocommands/` is the lab that verifies them against a running AutoCAD.

## AcadEventManager

The `EventManager` shared project (`src/Autocad/EventManager/`) provides `AcadEventManager`, a centralized tracker for per-document event subscriptions. Import it as a shared project so it compiles directly into your plugin DLL (no extra dependency).

**Problem:** Subscribing to a `Document`-level event (like `CommandEnded`) on one document, then unsubscribing from `MdiActiveDocument` in `Terminate()` breaks if the user switched documents. Storing a `Document` reference breaks if that document is closed before `Terminate()`.

**Solution:** `AcadEventManager` tracks unsubscribe actions per document, auto-cleans when a document is closed (`DocumentToBeDestroyed`), and bulk-cleans on `Dispose()`.

```csharp
// Subscribe to an event on the current document
var doc = Application.DocumentManager.MdiActiveDocument;
doc.CommandEnded += OnCommandEnded;
_events.Track(doc, () => doc.CommandEnded -= OnCommandEnded);

// In Terminate(): cleans up ALL tracked subscriptions across ALL documents
_events.Dispose();
```

Multiple documents can have independent subscriptions. Closed documents are cleaned up automatically.

## Implement IExtensionApplication

`NOTE:` In current version it no longer needs to be a static field that holds references as the Initialize() is now run on our ALC resident instance and NOT on Autocad's internal static instance which we don't have access to.
Your plugin class implements `IExtensionApplication`. Palettes must be cleaned up in `Terminate()`. Use `AcadEventManager` for event subscriptions:

```csharp
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using EventManager;

[assembly: ExtensionApplication(typeof(MyNamespace.MyPlugin))]

namespace MyNamespace
{
    public class MyPlugin : IExtensionApplication
    {
        private static PaletteSet? _palette;
        private static AcadEventManager? _events;

        public void Initialize()
        {
            _events = new AcadEventManager();
        }

        public void Terminate()
        {
            _events?.Dispose();
            _events = null;

            if (_palette != null)
            {
                _palette.Close();
                _palette.Dispose();
                _palette = null;
            }
        }

        [CommandMethod("MYPALETTE")]
        public static void ShowPalette()
        {
            if (_palette == null)
                _palette = new MyPaletteSet();
            _palette.Visible = true;
        }
    }
}
```

## Adding Plugins

1. Open the `DEVRELOAD` management palette in AutoCAD.
2. Click **"+ Add Plugin"**.
3. Pick the plugin's `.csproj` in the file dialog.
4. The plugin name is the `.csproj` file name (renaming is not supported); the `.csproj` path and the output DLL path (resolved via MSBuild's `TargetPath`) are stored automatically.
5. Optionally set a Command Prefix and Load-on-Startup.
6. Click **Add**.

The project must have been restored/built at least once so MSBuild can resolve its `TargetPath`; otherwise registration reports an error asking you to build first.

The same single entry point backs both the palette and the MCP `register_new_plugin` tool, so an agent and the UI register plugins identically.

## Management Palette

The `DEVRELOAD` command opens a WPF management palette with the following per-plugin controls:

| Control | Description |
|---------|-------------|
| **Status indicator** | Green circle when loaded, gray when unloaded |
| **Worktree dropdown** | Select which git worktree to build from (auto-detected, appears when worktrees exist) |
| **DBG/REL toggle** | Switch between Debug and Release build configurations |
| **Reload** | Split button: click = build and hot-reload; the **▾** flyout offers **Build only** (build without loading, e.g. to produce a fresh worktree's DLLs before configuring Shared) |
| **Unload** | Tear down plugin, unregister commands, unload ALC |
| **Shared** | Configure shared assemblies (loaded into default ALC for WPF XAML compatibility). The button is green-tinted when the current branch + build configuration already has a shared-assembly config |
| **Push** | Push the shared-assembly config to a production NSLOAD app |
| **Auto-load** | Checkbox to auto-load plugin when DevReload starts |
| **X** | Remove plugin registration |

Bottom toolbar: **Settings** (NSLOAD CSV path), **+ Add Plugin**, **Reload Config** (re-read plugins.json).

## Shared Assemblies

Some dependencies (WPF XAML-referenced NuGets such as OxyPlot) must resolve to a single shared type identity across the ALC boundary, so they have to be loaded into the default ALC rather than the plugin's isolated one. The **Shared** dialog configures this per build.

The configuration is stored per build directory in `SharedAssemblies.Config.json`, next to the built DLL and not in `plugins.json`. Switching branch / worktree / configuration switches build directories and therefore switches configs; if the file is absent, that build has no shared assemblies (no implicit inheritance). The file holds three lists:

- **Shared**: loaded into the default ALC via `Assembly.LoadFrom`.
- **Mixed-mode (C++/CLI)**: shared assemblies that also get an auto-generated `runtimeconfig.json`.
- **Streamed (no lock)**: shared assemblies loaded via `Assembly.Load(byte[])` so the file lock is released and the project can be rebuilt without restarting AutoCAD (the running image stays loaded for the session).

Because the dialog lists the DLLs physically present in the build directory, a freshly-selected worktree must be built first: if its build directory is missing or empty, Shared tells you to build it via **Reload ▾ → Build only**, then reopen Shared. To carry a configuration over from another branch, use **Copy from `<branch>`** in the dialog. It copies the config (the selection only, not the DLLs) and applies just the entries whose DLL exists in the current worktree, reporting any it skipped.

## Git Worktree Support

When developing features in git worktrees, DevReload lets you build and load from any worktree without re-registering the plugin:

1. The original `.csproj` path (stored at registration in `projectFilePath`) always points to the main repo and is never overwritten.
2. When you open the worktree dropdown in the management palette, DevReload runs `git worktree list` to enumerate available worktrees.
3. Selecting a worktree remaps the `.csproj` path at build time: `{worktreePath}/{relativeProjectPath}`.
4. Clicking **Reload** builds from the selected worktree via `dotnet build`.
5. The selection persists in `plugins.json` as `activeWorktreePath` and survives AutoCAD restarts.

Shared assemblies and mixed-mode DLLs are resolved relative to the built DLL's output directory, which changes to the worktree's output when a worktree is selected, so each worktree carries its own `SharedAssemblies.Config.json`.

A fresh worktree typically isn't built yet, so the flow is: select the worktree → **Reload ▾ → Build only** → **Shared** (configure, or **Copy from** the main branch) → **Reload**.

## plugins.json Configuration

Plugins are stored in `%APPDATA%\DevReload\plugins.json`:

```json
{
  "plugins": [
    {
      "name": "DevReloadTest",
      "dllPath": "C:\\Path\\To\\bin\\Debug\\DevReloadTest.dll",
      "commandPrefix": "TEST",
      "loadOnStartup": false,
      "buildConfiguration": "Debug",
      "projectFilePath": "C:\\Path\\To\\DevReloadTest.csproj",
      "activeWorktreePath": null,
      "productionTarget": null
    }
  ],
  "nsloadCsvPath": null
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `name` | *(required)* | Unique plugin name (the `.csproj` file name) |
| `dllPath` | *(auto)* | Path to last-built output DLL (updated after each build) |
| `commandPrefix` | `{name}` | Prefix for generated LOAD/DEV/UNLOAD commands (stored upper-cased) |
| `loadOnStartup` | `false` | Auto-load when DevReload starts |
| `buildConfiguration` | `"Debug"` | Build configuration. Toggle via DBG/REL button in palette |
| `projectFilePath` | *(auto)* | Path to `.csproj` in the main repo (immutable after registration) |
| `activeWorktreePath` | `null` | Selected git worktree root path (`null` = build from main repo) |
| `productionTarget` | `null` | Target NSLOAD app name for "Push to Production" |
| `nsloadCsvPath` | `null` | Path to NSLOAD register CSV (top-level config field) |

Shared/mixed/streamed assembly selections live in each build directory's `SharedAssemblies.Config.json`, not here (see [Shared Assemblies](#shared-assemblies)).

On startup, old config entries missing `projectFilePath` are migrated by searching for the `.csproj` from the `dllPath`; entries that cannot be migrated are removed. Legacy `sharedAssemblies` and `mixedModeAssemblies` fields from older configs are read once, drained into per-build `SharedAssemblies.Config.json` files where possible, and then dropped.

## Generated Commands

For each plugin, DevReload registers three commands using the `commandPrefix`:

| Command | Action |
|---------|--------|
| `{PREFIX}LOAD` | Load from DLL path. If DLL not found, builds the project first via `dotnet build`. |
| `{PREFIX}DEV` | Build via `dotnet build`, then reload. If build fails, old plugin stays running. |
| `{PREFIX}UNLOAD` | Unregister commands, terminate, unload ALC. |

The management palette is opened with the `DEVRELOAD` command.

## Build Process

DevReload builds plugins using the .NET CLI directly:

1. Resolves the effective `.csproj` path (remapped to the active worktree if one is selected).
2. Queries the output DLL path via `dotnet msbuild -getProperty:TargetPath`.
3. Builds via `dotnet build "{csproj}" -c {configuration} -p:Platform=x64`.
4. Verifies the output DLL exists.
5. Stream-loads the DLL + PDB into an isolated `AssemblyLoadContext`.

Registration and builds are VS-independent. Use any editor.

## Dev Workflow

1. Open your project in your editor (Visual Studio, VS Code, etc.).
2. Start AutoCAD with the DevReload plugin loaded.
3. `DEVRELOAD` → **+ Add Plugin** → select your `.csproj`.
4. Edit your plugin code.
5. In AutoCAD, type `{PREFIX}DEV` (e.g., `TESTDEV`) or click **Reload** in the palette.
6. DevReload builds, tears down the old plugin, loads the new DLL.
7. See your changes immediately. No AutoCAD restart is needed.

The `{PREFIX}DEV` command is safe: it builds **before** tearing down. If the build fails, the old plugin stays loaded and functional.

The `{PREFIX}LOAD` command will auto-build if the DLL doesn't exist yet.

### Working with Worktrees

1. Create a worktree: `git worktree add ../my-feature -b my-feature`.
2. In the management palette, click the worktree dropdown on your plugin.
3. Select the worktree branch.
4. If it hasn't been built yet, use **Reload ▾ → Build only** first (and configure **Shared** if your plugin needs it).
5. Click **Reload**. DevReload builds from the worktree and loads the result.
6. Switch back to `main` in the dropdown when done.
