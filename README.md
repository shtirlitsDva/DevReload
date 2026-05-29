# DevReload — Hot-Reload Plugin System for AutoCAD 2025

DevReload lets you edit, build, and reload AutoCAD .NET plugins without restarting AutoCAD. It uses .NET 8 collectible `AssemblyLoadContext` to isolate plugins and stream-loads DLLs so your build can rebuild freely while the old plugin runs. The `{PREFIX}DEV` command builds your project, tears down the old plugin, and loads the new one — all in one step.

You register a plugin by picking its `.csproj` file; the plugin name is the project-file name. Builds run directly via `dotnet build` — no Visual Studio instance required. A per-plugin **Debug/Release toggle** lets you switch build configurations from the management palette.

**Git worktree support**: DevReload detects worktrees for registered projects and lets you select which worktree to build from via a dropdown in the management palette.

## Install as a Claude Code / Codex plugin

DevReload also ships an MCP bridge (`Acad.Rpc.Bridge`) and the `/acd-agentic-dev` skill, so an agent (Claude Code or Codex) can drive the full edit → reload → live-test loop directly. The MCP bridge exposes two tool groups:

- `acad_*` — AutoCAD/Civil 3D process control: launch, attach, send commands, wait for readiness, open/close drawings, quit.
- `devreload_*` — plugin lifecycle: register, load, reload (build + hot-swap), unload, query state, switch build config and worktree.

> Note: this installs the **agent-side** MCP bridge + skill. The **AutoCAD-side** DevReload plugin (the palette + commands) is installed separately into AutoCAD — see [Installing the AutoCAD plugin](#installing-the-autocad-plugin).

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

If you're iterating on the bridge or the skill, run `.\scripts\Pack-Plugin.ps1` after each change and re-install the plugin pointed at your local checkout. Pack-Plugin does a framework-dependent publish (~5 MB, requires the .NET 8 runtime on consumers); `-SelfContained` switches it to a ~60 MB self-contained build that bundles the runtime.

## Installing the AutoCAD plugin

The AutoCAD-side plugin is the `DevReload.dll` assembly that provides the `DEVRELOAD` palette and the generated plugin commands. Install it one of two ways:

- **Bundle (autoload):** build Release to produce `Deploy/DevReload.bundle`, then drop that bundle into AutoCAD's `ApplicationPlugins` folder so it autoloads on startup.

  ```powershell
  dotnet build src/DevReload/DevReload.csproj -c Release -p:Platform=x64
  ```

- **Manual:** `NETLOAD` `DevReload.dll` into a running AutoCAD session.

Then type `DEVRELOAD` to open the management palette.

## Quickstart

**Prepare your plugin** (prevents stale commands on reload):
Add an empty `NoCommands` marker class that prevents AutoCAD from registering `[CommandMethod]`s when the DLL is (re)loaded.
`DevReload` manages registering and unregistering of commands on load/reload via `Utils.AddCommand`, which is the only mechanism AutoCAD exposes that supports unregistration.

```csharp
[assembly: CommandClass(typeof(YourNamespace.NoCommands))]
// ...
public class NoCommands { }
```

Your plugin must implement `IExtensionApplication`. DevReload calls `Terminate()` when unloading a plugin before reloading. All event subscriptions and other AutoCAD references must be unregistered in `Terminate()` using *static* fields, because AutoCAD and DevReload create separate instances of your class (see Dual-Instance Problem below).

**Use DevReload**

1. Start AutoCAD with the DevReload plugin installed (see [above](#installing-the-autocad-plugin)), type `DEVRELOAD` to open the management palette.
2. Click **+ Add Plugin** → pick your plugin's `.csproj` in the file dialog.
3. Optionally set a Command Prefix and Load-on-Startup, then click **Add** → your plugin is registered with `{PREFIX}LOAD` / `{PREFIX}DEV` / `{PREFIX}UNLOAD`.
4. Type `{PREFIX}LOAD` — loads your DLL (builds first if it doesn't exist).
5. Edit code in your editor → type `{PREFIX}DEV` (or click **Reload**) → see changes instantly, no restart.

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

## Adding Plugins

1. Open the `DEVRELOAD` management palette in AutoCAD.
2. Click **"+ Add Plugin"**.
3. Pick the plugin's `.csproj` in the file dialog.
4. The plugin **name** is the `.csproj` file name (renaming is not supported); the `.csproj` path and the output DLL path (resolved via MSBuild's `TargetPath`) are stored automatically.
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
| **Reload** | Split button: click = build **and** hot-reload; the **▾** flyout offers **Build only** (build without loading, e.g. to produce a fresh worktree's DLLs before configuring Shared) |
| **Unload** | Tear down plugin, unregister commands, unload ALC |
| **Shared** | Configure shared assemblies (loaded into default ALC for WPF XAML compatibility). The button is **green-tinted** when the current branch + build configuration already has a shared-assembly config |
| **Push** | Push the shared-assembly config to a production NSLOAD app |
| **Auto-load** | Checkbox to auto-load plugin when DevReload starts |
| **X** | Remove plugin registration |

Bottom toolbar: **Settings** (NSLOAD CSV path), **+ Add Plugin**, **Reload Config** (re-read plugins.json).

## Shared Assemblies

Some dependencies (notably WPF XAML-referenced NuGets like OxyPlot) must resolve to a single shared type identity across the ALC boundary, so they have to be loaded into the **default** ALC rather than the plugin's isolated one. The **Shared** dialog configures this per build.

The configuration is stored **per build directory** in `SharedAssemblies.Config.json` (next to the built DLL) — not in `plugins.json`. Switching branch / worktree / configuration switches build directories and therefore switches configs; if the file is absent, that build has no shared assemblies (no implicit inheritance). The file holds three lists:

- **Shared** — loaded into the default ALC via `Assembly.LoadFrom`.
- **Mixed-mode (C++/CLI)** — shared assemblies that also get an auto-generated `runtimeconfig.json`.
- **Streamed (no lock)** — shared assemblies loaded via `Assembly.Load(byte[])` so the file lock is released and the project can be rebuilt without restarting AutoCAD (the running image stays loaded for the session).

Because the dialog lists the DLLs physically present in the build directory, a freshly-selected worktree must be **built first**: if its build directory is missing or empty, Shared tells you to build it via **Reload ▾ → Build only**, then reopen Shared. To carry a configuration over from another branch, use **Copy from `<branch>`** in the dialog — it copies the config (the selection only, not the DLLs) and applies just the entries whose DLL actually exists in the current worktree, reporting any it skipped.

## Git Worktree Support

When developing features in git worktrees, DevReload lets you build and load from any worktree without re-registering the plugin:

1. The original `.csproj` path (stored at registration in `projectFilePath`) always points to the **main repo** and is never overwritten.
2. When you open the worktree dropdown in the management palette, DevReload runs `git worktree list` to enumerate available worktrees.
3. Selecting a worktree remaps the `.csproj` path at build time: `{worktreePath}/{relativeProjectPath}`.
4. Clicking **Reload** builds from the selected worktree via `dotnet build`.
5. The selection persists in `plugins.json` as `activeWorktreePath` and survives AutoCAD restarts.

Shared assemblies and mixed-mode DLLs are resolved relative to the built DLL's output directory, which changes to the worktree's output when a worktree is selected — so each worktree carries its own `SharedAssemblies.Config.json`.

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
| `buildConfiguration` | `"Debug"` | Build configuration — toggle via DBG/REL button in palette |
| `projectFilePath` | *(auto)* | Path to `.csproj` in the main repo (immutable after registration) |
| `activeWorktreePath` | `null` | Selected git worktree root path (`null` = build from main repo) |
| `productionTarget` | `null` | Target NSLOAD app name for "Push to Production" |
| `nsloadCsvPath` | `null` | Path to NSLOAD register CSV (top-level config field) |

Shared/mixed/streamed assembly selections are **not** stored here — they live in each build directory's `SharedAssemblies.Config.json` (see [Shared Assemblies](#shared-assemblies)).

On startup, old config entries missing `projectFilePath` are migrated by searching for the `.csproj` from the `dllPath`; entries that cannot be migrated are removed. Legacy `vsProject`, `sharedAssemblies`, and `mixedModeAssemblies` fields from older configs are read once, drained into per-build `SharedAssemblies.Config.json` files where possible, and then dropped.

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

1. Resolves the effective `.csproj` path (remapped to the active worktree if one is selected).
2. Queries the output DLL path via `dotnet msbuild -getProperty:TargetPath`.
3. Builds via `dotnet build "{csproj}" -c {configuration} -p:Platform=x64`.
4. Verifies the output DLL exists.
5. Stream-loads the DLL + PDB into an isolated `AssemblyLoadContext`.

Visual Studio is never contacted — registration and builds are entirely VS-independent; you can use any editor.

## Dev Workflow

1. Open your project in your editor (Visual Studio, VS Code, etc.).
2. Start AutoCAD with the DevReload plugin loaded.
3. `DEVRELOAD` → **+ Add Plugin** → select your `.csproj`.
4. Edit your plugin code.
5. In AutoCAD, type `{PREFIX}DEV` (e.g., `TESTDEV`) or click **Reload** in the palette.
6. DevReload builds, tears down the old plugin, loads the new DLL.
7. See your changes immediately — no AutoCAD restart needed.

The `{PREFIX}DEV` command is safe: it builds **before** tearing down. If the build fails, the old plugin stays loaded and functional.

The `{PREFIX}LOAD` command will auto-build if the DLL doesn't exist yet.

### Working with Worktrees

1. Create a worktree: `git worktree add ../my-feature -b my-feature`.
2. In the management palette, click the worktree dropdown on your plugin.
3. Select the worktree branch.
4. If it hasn't been built yet, use **Reload ▾ → Build only** first (and configure **Shared** if your plugin needs it).
5. Click **Reload** — DevReload builds from the worktree and loads the result.
6. Switch back to `main` in the dropdown when done.
