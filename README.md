# DevReload — Hot-Reload Plugin System for AutoCAD 2025

DevReload lets you edit, build, and reload AutoCAD .NET plugins without restarting AutoCAD. It uses .NET 8 collectible `AssemblyLoadContext` to isolate plugins and stream-loads DLLs so Visual Studio can rebuild freely while the old plugin runs. The `{PREFIX}DEV` command builds your project via VS COM automation, tears down the old plugin, and loads the new one — all in one step.

DevReload works exclusively with **Debug builds**. The "Add Plugin" flow contacts Visual Studio via COM, lists loaded projects, and auto-derives the Debug output path and DLL name.

## Quickstart

**Prepare your plugin** (prevents stale commands on reload):
We add a `#if DEBUG`guarded empty class that prevents autocad from registering [CommandMethods] when the .dll is (re)loaded.
`DevReload` manages the registering and unregistering of commands on load/reload.
This also means that you cannot have `Release` build loaded before running `Debug` build.

```csharp
#if DEBUG
[assembly: CommandClass(typeof(YourNamespace.NoAutoCommands))]
#endif
// ...
#if DEBUG
public class NoAutoCommands { }
#endif
```

Your plugin must implement `IExtensionApplication`. DevReload calls `Terminate()` when unloading a plugin before reloading. All event subscriptions and other AutoCAD references must be unregistered in `Terminate()` using *static* fields, because AutoCAD and DevReload create separate instances of your class (see Dual-Instance Problem below).

**Launch DevReload**

1. Place `DevReload.dll` in a folder.
2. Open your plugin solution in Visual Studio (**Debug** configuration)
3. Start AutoCAD, NETLOAD or autoload `DevReload.dll`, type `DEVRELOAD` to open the management palette
4. Click **+ Add Plugin** → pick your project from the VS project list
5. Click **Add** → your plugin is registered with `{PREFIX}LOAD` / `{PREFIX}DEV` / `{PREFIX}UNLOAD`
6. Type `{PREFIX}LOAD` — loads your Debug DLL (builds first if it doesn't exist)
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
See `example/DevReloadTest/DevReloadTest.csproj` for a complete working example.

## Dual-Mode: Release vs Debug

Plugins work in **two modes** from the same DLL:
- **Release** (for users): loaded via `NETLOAD` — AutoCAD calls `IExtensionApplication.Initialize()` and registers commands normally
- **Debug** (for you): loaded via DevReload — AutoCAD *still* calls `Initialize()` automatically, but command registration is suppressed

### Dual-Instance Problem & Static State

AutoCAD and DevReload create **separate instances** of your plugin class:
- **Instance A**: AutoCAD creates via `[assembly: ExtensionApplication]` → calls `Initialize()`
- **Instance B**: DevReload creates via `Activator.CreateInstance` → calls `Terminate()` on unload

These are different objects. Instance fields set in `Initialize()` on Instance A are NOT visible to `Terminate()` on Instance B. **Use static fields for anything that needs cleanup.** The `AcadEventManager` (see below) solves this for event subscriptions.

### CommandClass Suppression (Required for Debug)

AutoCAD's `ExtensionLoader` scans loaded assemblies for `[CommandMethod]` attributes and registers them via `CommandClass.AddCommand`. These registrations are **permanent** — no public API to remove them. On reload, this causes `eDuplicateKey` errors and stale commands.

To prevent this, plugin assemblies must suppress AutoCAD's command scanning in Debug builds:

```csharp
#if DEBUG
[assembly: CommandClass(typeof(MyNamespace.NoAutoCommands))]
#endif

namespace MyNamespace
{
#if DEBUG
    public class NoAutoCommands { }
#endif
}
```

- **Debug**: AutoCAD sees `CommandClass(typeof(NoAutoCommands))`, scans only that empty class, finds no commands. DevReload's `CommandRegistrar` handles registration via `Utils.AddCommand` (which CAN be unregistered on reload).
- **Release**: No `CommandClass` attribute → AutoCAD scans all types and registers commands normally via `NETLOAD`.

If your plugin already has a custom `[assembly: CommandClass]` for Release, guard it:

```csharp
#if DEBUG
[assembly: CommandClass(typeof(NoAutoCommands))]
#else
[assembly: CommandClass(typeof(MyProductionCommands))]
#endif
```

### Lifecycle Summary

| Method | NETLOAD (Release) | DevReload (Debug) |
|--------|-------------------|-------------------|
| `Initialize()` | AutoCAD calls it | AutoCAD calls it (DevReload skips) |
| `Terminate()` | AutoCAD calls on shutdown | DevReload calls on unload/reload |
| Commands | AutoCAD registers via `CommandClass.AddCommand` | DevReload registers via `Utils.AddCommand` |

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

Works in both Release (NETLOAD) and Debug (DevReload) modes. Multiple documents can have independent subscriptions. Closed documents are cleaned up automatically.

## Implement IExtensionApplication

Your plugin class implements `IExtensionApplication`. Palettes must be stored in a static field and cleaned up in `Terminate()`. Use `AcadEventManager` for event subscriptions:

```csharp
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using EventManager;

[assembly: ExtensionApplication(typeof(MyNamespace.MyPlugin))]

#if DEBUG
[assembly: CommandClass(typeof(MyNamespace.NoAutoCommands))]
#endif

namespace MyNamespace
{
#if DEBUG
    public class NoAutoCommands { }
#endif

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
5. Project name, Debug DLL path, and VS project name are auto-derived
6. Optionally set: Command Prefix, Load on Startup
7. Click **Add**

If multiple VS instances are open, projects are shown as `SolutionName:ProjectName`.

## plugins.json Configuration

Plugins are stored in `%APPDATA%\DevReload\plugins.json`:

```json
{
  "plugins": [
    {
      "name": "DevReloadTest",
      "dllPath": "C:\\Path\\To\\bin\\Debug\\DevReloadTest.dll",
      "vsProject": "DevReloadTest",
      "commandPrefix": "TEST",
      "loadOnStartup": false
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `name` | *(required)* | Unique plugin name (auto-derived from VS project) |
| `dllPath` | *(auto)* | Path to Debug output DLL (auto-derived from VS) |
| `vsProject` | *(auto)* | VS project name (auto-derived) |
| `commandPrefix` | `{name}` | Prefix for generated LOAD/DEV/UNLOAD commands |
| `loadOnStartup` | `false` | Auto-load when DevReload starts |

## Generated Commands

For each plugin, DevReload registers three commands using the `commandPrefix`:

| Command | Action |
|---------|--------|
| `{PREFIX}LOAD` | Load from Debug DLL path. If DLL not found, builds the project first if VS is found. |
| `{PREFIX}DEV` | Build from VS, then reload. If build fails, old plugin stays running. |
| `{PREFIX}UNLOAD` | Unregister commands, terminate, unload ALC. |

The management palette is opened with the `DEVRELOAD` command.

## Dev Workflow

1. Open your solution in Visual Studio (Debug configuration)
2. Start AutoCAD (DevReload loads via autoload)
3. `DEVRELOAD` → Add Plugin → select your project
4. Edit your plugin code in VS
5. In AutoCAD, type `{PREFIX}DEV` (e.g., `TESTDEV`)
6. DevReload builds, tears down old plugin, loads new DLL
7. See your changes immediately — no AutoCAD restart needed

The `{PREFIX}DEV` command is safe: it builds **before** tearing down. If the build fails, the old plugin stays loaded and functional.

The `{PREFIX}LOAD` command will auto-build if the Debug DLL doesn't exist yet.
