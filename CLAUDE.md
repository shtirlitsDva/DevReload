# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

DevReload is a hot-reload plugin system for AutoCAD 2025. It uses .NET 8 collectible `AssemblyLoadContext` to load/unload AutoCAD .NET plugins without restarting AutoCAD. Plugins are registered via Visual Studio COM automation, then built/reloaded via `dotnet build` independently of VS.

## Build Commands

```bash
# Build the main DevReload project (requires AutoCAD 2025 assemblies)
dotnet build src/Autocad/DevReload/DevReload.csproj -c Debug -p:Platform=x64

# Build entire solution
dotnet build DevReload.sln -c Debug -p:Platform=x64

# Release build (also creates Deploy/DevReload.bundle)
dotnet build src/Autocad/DevReload/DevReload.csproj -c Release -p:Platform=x64
```

## Repo Layout

- `src/Autocad/` — AutoCAD host: `DevReload` (plugin + palette), `EventManager` (AcadEventManager shproj), `Acad.Process`, `Acad.Rpc.Bridge`
- `src/Shared/` — host-agnostic: `Acad.Rpc.Core` (MCP/RPC engine; legacy name, used by both hosts), `WpfSHARED` (shproj)
- `src/Revit/` — Revit host: per-version add-in projects (`RevitDevReload.R22`…) over a shared `.shproj`

The AutoCAD path defaults to `C:\Program Files\Autodesk\AutoCAD 2025`. Override it by copying `Directory.Build.props.user.example` to `Directory.Build.props.user` and setting your path, or via `-p:AutoCADPath="..."`.

## Architecture

The system has a clear layered architecture centered around plugin lifecycle management:

**Entry point**: `DevReloaderCommands` — implements `IExtensionApplication`, loaded by AutoCAD via NETLOAD/autoload. Reads `%APPDATA%\DevReload\plugins.json` on startup, registers plugins, and provides the `DEVRELOAD` management palette command.

**Plugin lifecycle** (`PluginManager` → `PluginHost<T>` → `IsolatedPluginContext`):
- `PluginManager` — static registry of all plugins. Orchestrates Load/DevReload/Unload. Uses builder pattern (`PluginRegistrationBuilder`) for registration. Generates three dynamic commands per plugin: `{PREFIX}LOAD`, `{PREFIX}DEV`, `{PREFIX}UNLOAD`.
- `PluginHost<T>` — owns a single plugin's ALC instance. Stream-loads DLL+PDB bytes into the collectible ALC. Finds and instantiates the `IExtensionApplication` implementation. Handles `StalePluginException` (version mismatch detection).
- `IsolatedPluginContext` — the collectible `AssemblyLoadContext`. Resolves plugin dependencies in isolation; shared assemblies (for WPF XAML compatibility) fall through to the default ALC.

**Build system** (`DevReloadService`):
- Queries output path via `dotnet msbuild -getProperty:TargetPath`
- Builds via `dotnet build "{csproj}" -c {config} -p:Platform=x64`

**Command registration** (`CommandRegistrar`):
- Scans loaded assemblies for `[CommandMethod]` attributes
- Registers via `Utils.AddCommand` (removable), NOT AutoCAD's permanent `CommandClass.AddCommand`
- Unregisters all before ALC unload to prevent dangling delegate references

**Supporting services**:
- `GitWorktreeService` — detects worktrees, remaps `.csproj` paths for worktree builds
- `PluginConfigLoader` — JSON serialization of `plugins.json`, includes migration logic for old config format
- `NsloadAppRegistry` — reads NSLOAD's CSV + config.json for "Push to Production" feature

**UI layer** (WPF, MVVM via CommunityToolkit.Mvvm):
- `DevReloadPanel.xaml` — management palette hosted in AutoCAD `PaletteSet`
- `DevReloadViewModel` — drives all palette interactions (add/remove/load/unload/settings)
- `PluginItemViewModel` — per-plugin state (loaded status, build config toggle, worktree selection)
- `SharedAssembliesWindow` — dialog for configuring shared/mixed-mode assemblies

**Shared projects** (compiled into consuming projects, no separate DLLs):
- `EventManager` — `AcadEventManager` for per-document event subscription tracking with auto-cleanup
- `WpfSHARED` — reusable WPF forms (`StringGridFormCaller`, `TGridFormCaller`) used as selection dialogs

## Key Design Decisions

- DLLs are stream-loaded (read bytes into MemoryStream) so the file is never locked — VS can rebuild while old plugin runs
- Commands registered via `Utils.AddCommand`/`RemoveCommand` instead of AutoCAD's permanent `CommandClass.AddCommand` — this is what enables reload without eDuplicateKey errors
- Shared assemblies are loaded into the default ALC via `Assembly.LoadFrom` before plugin load — required for WPF XAML type resolution across ALC boundaries
- Mixed-mode (C++/CLI) assemblies need auto-generated `runtimeconfig.json` files
- `TearDown` always runs before `LoadCore` — build-first-then-swap ensures old plugin stays running if build fails
- All AutoCAD API references are `Private=False` (not copied to output) since they're provided by the host process

## Platform Constraints

- x64 only (AutoCAD requirement)
- net8.0-windows8.0 target framework
- WPF and WinForms both enabled (AutoCAD palette hosting + dialogs)
- CA1416 warning suppressed (Windows-only API usage is intentional)
