# DevReload — Hot-Reload Plugin System for AutoCAD 2025

DevReload lets you edit, build, and reload AutoCAD .NET plugins without restarting AutoCAD. It uses .NET 8 collectible `AssemblyLoadContext` to isolate plugins and stream-loads DLLs so Visual Studio can rebuild freely while the old plugin runs. The `{PREFIX}DEV` command builds your project, tears down the old plugin, and loads the new one — all in one step.

At registration time, DevReload discovers projects via Visual Studio COM automation. After registration, builds run directly via `dotnet build` — no running VS instance required for reloading. A per-plugin **Debug/Release toggle** lets you switch build configurations from the management palette.

**Git worktree support**: DevReload detects worktrees for registered projects and lets you select which worktree to build from via a dropdown in the management palette.

## Install as a Claude Code / Codex plugin

DevReload also ships an MCP bridge (`Acad.Rpc.Bridge`) and the `/acd-agentic-dev` skill, so an agent (Claude Code or Codex) can drive the full edit → reload → live-test loop directly. The MCP bridge exposes two tool groups:

- `acad_*` — AutoCAD/Civil 3D process control: launch, attach, send commands, wait for readiness, open/close drawings, quit.
- `devreload_*` — plugin lifecycle: register, load, reload (build + hot-swap), unload, query state, switch build config and worktree.

### Claude Code

From any Claude Code session:

```
/plugin marketplace add shtirlitsDva/DevReload
/plugin install devreload
```

The marketplace entry resolves the plugin from the auto-built `release` branch (kept in sync by GitHub Actions on every master push), which includes the pre-packed MCP bridge — no clone, no .NET SDK, no Pack-Plugin step required. The `acad_*` / `devreload_*` MCP tools appear automatically, and the skill becomes invokable as `/devreload:acd-agentic-dev`.

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

If you're iterating on the bridge or the skill, run `.\scripts\Pack-Plugin.ps1` after each change and re-install the plugin pointed at your local checkout. `-SelfContained` switches Pack-Plugin to a ~60 MB self-contained build if you want to remove the .NET 8 runtime dependency on consumers.

## Quickstart

**Prepare your plugin** (prevents stale commands on reload):
We add an empty `NoCommands` marker class that prevents AutoCAD from registering `[CommandMethod]`s when the DLL is (re)loaded.
`DevReload` manages registering and unregistering of commands on load/reload via `Utils.AddCommand`, which is the only mechanism AutoCAD exposes that supports unregistration.

```csharp
[assembly: CommandClass(typeof(YourNamespace.NoCommands))]
// ...
public class NoCommands { }
```

Your plugin must implement `IExtensionApplication`. DevReload calls `Terminate()` when unloading a plugin before reloading. All event subscriptions and other AutoCAD references must be unregistered in `Terminate()` using *static* fields, because AutoCAD and DevReload create separate instances of your class (see Dual-Instance Problem below).

**Launch DevReload**

1. Place `DevReload.dll` in a folder.
2. Open your plugin solution in Visual Studio
3. Start AutoCAD, NETLOAD or autoload `DevReload.dll`, type `DEVRELOAD` to open the management palette
4. Click **+ Add Plugin** → pick your project from the VS project list
5. Click **Add** → your plugin is registered with `{PREFIX}LOAD` / `{PREFIX}DEV` / `{PREFIX}UNLOAD`
6. Type `{PREFIX}LOAD` — loads your DLL (builds first if it doesn't exist)
7. Edit code in VS → type `{PREFIX}DEV` → see changes instantly, no restart

## Project Setup (.csproj)

Your plugin project needs these settings:
Note: This was written by AI, I don't actually know which of these are actually needed.

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

## Plugin Lifecycle

Plugins built for DevReload are **always loaded through DevReload** — there is no separate `NETLOAD` release path to keep working. That simplifies the lifecycle to two facts you have to internalize.

### Dual-Instance Problem & Static State

AutoCAD and DevReload create **separate instances** of your plugin class:
- **Instance A**: AutoCAD's `ExtensionLoader` scans every loaded assembly for `IExtensionApplication` implementations, instantiates each one, and calls `Initialize()`. (`[assembly: ExtensionApplication(typeof(MyPlugin))]` is the explicit form; the scan happens regardless.)
- **Instance B**: DevReload creates its own instance via `Activator.CreateInstance` so it can hold a typed reference, and calls `Terminate()` on that instance when unloading.

These are different objects. Instance fields set in `Initialize()` on Instance A are NOT visible to `Terminate()` on Instance B. **Use static fields for anything cleanup touches.** The `AcadEventManager` (see below) solves this for event subscriptions.

### CommandClass Suppression

AutoCAD's `ExtensionLoader` scans loaded assemblies for `[CommandMethod]` attributes and registers them via `CommandClass.AddCommand`. These registrations are **permanent** — no public API to remove them. On reload, this causes `eDuplicateKey` errors and stale commands.

Suppress the scan by pointing it at an empty marker class (canonical name `NoCommands`):

```csharp
[assembly: CommandClass(typeof(MyNamespace.NoCommands))]

namespace MyNamespace
{
    public class NoCommands { }
}
```

With this attribute present, AutoCAD scans ONLY `NoCommands` and finds zero commands. DevReload's `CommandRegistrar` then enumerates the assembly's exported types itself and registers each `[CommandMethod]` via the removable `Utils.AddCommand` path. Apply this unconditionally.

## AcadEventManager

The `EventManager` shared project (`src/EventManager/`) provides `AcadEventManager` — a centralized tracker for per-document event subscriptions. Import it as a shared project so it compiles directly into your plugin DLL (no extra dependency).

**Problem:** Subscribing to a `Document`-level event (like `CommandEnded`) on one document, then unsubscribing from `MdiActiveDocument` in `Terminate()` breaks if the user switched documents. Storing a `Document` reference breaks if that document is closed before `Terminate()`.

**Solution:** `AcadEventManager` tracks unsubscribe actions per document, auto-cleans when a document is closed (`DocumentToBeDestroyed`), and bulk-cleans on `Dispose()`.

```csharp
// Subscribe to an event on the current document
var doc = Application.DocumentManager.MdiActiveDocument;
doc.CommandEnded += OnCommandEnded;
_events.Track(doc, () => doc.CommandEnded -= OnCommandEnded);

// In Terminate() — cleans up ALL tracked subscriptions across ALL documents
_events.Dispose();
```

Multiple documents can have independent subscriptions. Closed documents are cleaned up automatically.

## Implement IExtensionApplication

Your plugin class implements `IExtensionApplication`. Palettes must be stored in a static field and cleaned up in `Terminate()`. Use `AcadEventManager` for event subscriptions:

```csharp
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using EventManager;

[assembly: ExtensionApplication(typeof(MyNamespace.MyPlugin))]
[assembly: CommandClass(typeof(MyNamespace.NoCommands))]

namespace MyNamespace
{
    public class NoCommands { }

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

## Adding Plugins (VS-Driven)

1. Open `DEVRELOAD` management palette in AutoCAD
2. Click **"+ Add Plugin"**
3. DevReload contacts Visual Studio via COM and lists all loaded projects
4. Select a project from the picker
5. Project name, DLL path, and `.csproj` path are auto-derived and stored
6. Optionally set: Command Prefix, Load on Startup
7. Click **Add**

If multiple VS instances are open, projects are shown as `SolutionName:ProjectName`.

After registration, VS is no longer needed for builds — DevReload builds directly via `dotnet build` using the stored `.csproj` path.

## Management Palette

The `DEVRELOAD` command opens a WPF management palette with the following per-plugin controls:

| Control | Description |
|---------|-------------|
| **Status indicator** | Green circle when loaded, gray when unloaded |
| **Worktree dropdown** | Select which git worktree to build from (auto-detected, appears when worktrees exist) |
| **DBG/REL toggle** | Switch between Debug and Release build configurations |
| **Reload** | Build and hot-reload the plugin |
| **Unload** | Tear down plugin, unregister commands, unload ALC |
| **Shared** | Configure shared assemblies (loaded into default ALC for WPF XAML compatibility) |
| **Push** | Push SharedAssemblies config to a production NSLOAD app |
| **Auto-load** | Checkbox to auto-load plugin when DevReload starts |
| **X** | Remove plugin registration |

Bottom toolbar: **Settings** (NSLOAD CSV path), **+ Add Plugin**, **Reload Config** (re-read plugins.json).

## Git Worktree Support

When developing features in git worktrees, DevReload lets you build and load from any worktree without re-registering the plugin:

1. The original `.csproj` path (stored at registration in `projectFilePath`) always points to the **main repo** and is never overwritten
2. When you open the worktree dropdown in the management palette, DevReload runs `git worktree list` to enumerate available worktrees
3. Selecting a worktree remaps the `.csproj` path at build time: `{worktreePath}/{relativeProjectPath}`
4. Clicking **Reload** builds from the selected worktree via `dotnet build`
5. The selection persists in `plugins.json` as `activeWorktreePath` and survives AutoCAD restarts

Shared assemblies and mixed-mode DLLs work automatically — they are resolved relative to the built DLL's output directory, which changes to the worktree's output when a worktree is selected.

## plugins.json Configuration

Plugins are stored in `%APPDATA%\DevReload\plugins.json`:

```json
{
  "plugins": [
    {
      "name": "DevReloadTest",
      "dllPath": "C:\\Path\\To\\bin\\Debug\\DevReloadTest.dll",
      "vsProject": "DevReloadTest",
      "projectFilePath": "C:\\Path\\To\\DevReloadTest.csproj",
      "commandPrefix": "TEST",
      "loadOnStartup": false,
      "buildConfiguration": "Debug",
      "sharedAssemblies": [],
      "mixedModeAssemblies": [],
      "activeWorktreePath": null,
      "productionTarget": null
    }
  ],
  "nsloadCsvPath": null
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `name` | *(required)* | Unique plugin name (auto-derived from VS project) |
| `dllPath` | *(auto)* | Path to last-built output DLL (updated after each build) |
| `vsProject` | *(auto)* | VS project name (used at registration time) |
| `projectFilePath` | *(auto)* | Path to `.csproj` in the main repo (immutable after registration) |
| `commandPrefix` | `{name}` | Prefix for generated LOAD/DEV/UNLOAD commands |
| `loadOnStartup` | `false` | Auto-load when DevReload starts |
| `buildConfiguration` | `"Debug"` | Build configuration — toggle via DBG/REL button in palette |
| `sharedAssemblies` | `[]` | Assembly names loaded into default ALC (for WPF XAML dependencies) |
| `mixedModeAssemblies` | `[]` | C++/CLI assembly names requiring `runtimeconfig.json` auto-creation |
| `activeWorktreePath` | `null` | Selected git worktree root path (`null` = build from main repo) |
| `productionTarget` | `null` | Target NSLOAD app name for "Push to Production" |
| `nsloadCsvPath` | `null` | Path to NSLOAD register CSV (top-level config field) |

On startup, old config entries missing `projectFilePath` are automatically migrated by searching for the `.csproj` file from the `dllPath`. Entries that cannot be migrated are removed.

## Generated Commands

For each plugin, DevReload registers three commands using the `commandPrefix`:

| Command | Action |
|---------|--------|
| `{PREFIX}LOAD` | Load from DLL path. If DLL not found, builds the project first via `dotnet build`. |
| `{PREFIX}DEV` | Build via `dotnet build`, then reload. If build fails, old plugin stays running. |
| `{PREFIX}UNLOAD` | Unregister commands, terminate, unload ALC. |

The management palette is opened with the `DEVRELOAD` command.

## Build Process

DevReload builds plugins using the .NET CLI directly — no running Visual Studio instance required:

1. Resolves the effective `.csproj` path (remapped to worktree if one is selected)
2. Queries the output DLL path via `dotnet msbuild -getProperty:TargetPath`
3. Builds via `dotnet build "{csproj}" -c {configuration} -p:Platform=x64`
4. Verifies the output DLL exists
5. Stream-loads the DLL + PDB into an isolated `AssemblyLoadContext`

VS COM automation is only used during the **"+ Add Plugin"** registration flow to discover projects and their paths. After registration, all builds are VS-independent.

## Dev Workflow

1. Open your solution in Visual Studio
2. Start AutoCAD (DevReload loads via autoload)
3. `DEVRELOAD` → Add Plugin → select your project
4. Edit your plugin code in VS
5. In AutoCAD, type `{PREFIX}DEV` (e.g., `TESTDEV`) or click **Reload** in the palette
6. DevReload builds, tears down old plugin, loads new DLL
7. See your changes immediately — no AutoCAD restart needed

The `{PREFIX}DEV` command is safe: it builds **before** tearing down. If the build fails, the old plugin stays loaded and functional.

The `{PREFIX}LOAD` command will auto-build if the DLL doesn't exist yet.

### Working with Worktrees

1. Create a worktree: `git worktree add ../my-feature -b my-feature`
2. In the management palette, click the worktree dropdown on your plugin
3. Select the worktree branch
4. Click **Reload** — DevReload builds from the worktree and loads the result
5. Switch back to `main` in the dropdown when done
