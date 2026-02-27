<section>
<overview>

# DevReload

Hot-reload framework for AutoCAD .NET 8 plugins. Build, unload, and reload your plugin code **without restarting AutoCAD**.

</overview>
</section>

<section>
<overview>

## Features

- **No file locks** — Plugin DLL is loaded from a memory stream, so Visual Studio can rebuild freely
- **Command hot-reload** — `[CommandMethod]` attributes are registered/unregistered via `Utils.AddCommand`/`RemoveCommand`
- **WPF palette support** — PaletteSet with WPF UserControls reloads cleanly across ALC boundaries
- **One-command dev cycle** — Define AutoCAD commands to be able to build from VS and reload in one step
- **VS integration** — Finds running Visual Studio instances via COM ROT, builds projects via EnvDTE
- **Shared project pattern** — Zero extra DLLs to deploy; DevReload code compiles directly into your Loader

</overview>
</section>

<section>
<overview>

## Architecture
Illustrated with example project.
```
┌───────────────────────────────────────────────────────────────────┐
│  AutoCAD Process (.NET 8)                                         │
│                                                                   │
│  ┌────────────────────────┐     ┌───────────────────────────────┐ │
│  │  Default ALC           │     │  Isolated Collectible ALC     │ │
│  │                        │     │                               │ │
│  │  EXAMPLELOADER.dll     │────>│  Example.Plugin.dll           │ │
│  │  (Loader)              │     │  (Core - hot-reloadable)      │ │
│  │                        │     │                               │ │
│  │  Contains:             │     │  Contains:                    │ │
│  │  · EXLOAD/EXDEV/       │     │  · [CommandMethod] commands   │ │
│  │    EXUNLOAD commands   │     │  · IPlugin implementation     │ │
│  │  · PluginHost<IPlugin> │     │  · WPF UserControls           │ │
│  │  · CommandRegistrar    │     │  · PaletteSet                 │ │
│  │  · DevReloadService    │     │                               │ │
│  │                        │     │  Loaded from stream (no lock) │ │
│  │  DevReload.Interface   │◄────│  DevReload.Interface          │ │
│  │  (shared type identity)│     │  (Private=false, falls back)  │ │
│  └────────────────────────┘     └───────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────┘
```

The **Loader** is permanently loaded into AutoCAD via `NETLOAD`. It imports the DevReload shared project code and manages the lifecycle of the **Plugin (Core)**, which lives in an isolated collectible `AssemblyLoadContext` that can be unloaded and reloaded at will.

</overview>
</section>

<section>
<overview>

## Prerequisites

- **AutoCAD 2025** (or any .NET 8-based AutoCAD/Civil 3D version)
- **Visual Studio 2022/2026** (must have the project opened for the dev-reload command)
- **.NET 8 SDK**

</overview>
</section>

<section>
<overview>

## Setup

1. Clone this repository
2. Configure your AutoCAD installation path (choose one method):

   **Option A** — Local props file (recommended):
   ```
   Copy Directory.Build.props.user.example to Directory.Build.props.user
   Edit the AutoCADPath value to match your installation
   ```

   **Option B** — Environment variable:
   ```
   set AutoCADPath=C:\Program Files\Autodesk\AutoCAD 2025
   ```

   **Option C** — Command line:
   ```
   dotnet build -p:AutoCADPath="C:\Program Files\Autodesk\AutoCAD 2025"
   ```

3. Open `DevReload.sln` in Visual Studio
4. Build the solution (`Debug|x64`)

</overview>
</section>

<section>
<overview>

## Quick Start

1. **Publish or locate** `Example.Loader\bin\Debug\EXAMPLELOADER.dll`
2. In AutoCAD, run `NETLOAD` and select `EXAMPLELOADER.dll`
3. Type **`EXLOAD`** — loads the example plugin, opens a palette, registers commands
4. Type **`EXCMD`** — draws a line (proves commands work from isolated ALC)
5. **Edit** `ExampleCommands.cs` in Visual Studio (change the message or coordinates)
6. Type **`EXDEV`** — builds from VS, unloads old plugin, loads new one. Your changes are live!
7. Type **`EXUNLOAD`** — cleans up everything

</overview>
</section>

<section>
<overview>

## Creating Your Own Plugin

### Command-Only Plugin (no UI)

If your plugin only has `[CommandMethod]` commands and no PaletteSet, you don't need `IPlugin` or `DevReload.Interface` at all.

**Core project requirements:**
```csharp
// Suppress AutoCAD's auto-registration (REQUIRED for hot-reload)
[assembly: CommandClass(typeof(MyPlugin.NoAutoCommands))]

namespace MyPlugin
{
    internal class NoAutoCommands { }

    public class MyCommands
    {
        [CommandMethod("MYCMD")]
        public void MyCommand() { /* your code */ }
    }
}
```

**Loader just needs:**
```csharp
var context = new IsolatedPluginContext(dllPath);
var assembly = context.LoadFromStream(/* stream */);
_registrar.RegisterFromAssembly(assembly);
```

### Plugin with UI (PaletteSet + Commands)

Follow the Example.Plugin/Example.Loader pattern:

1. **Create a Core project** (the hot-reloadable part):
   - Set `EnableDynamicLoading=true` and `CopyLocalLockFileAssemblies=true`
   - Output to Loader's `Isolated/` subfolder
   - Reference `DevReload.Interface` with `Private=false`
   - Add `[assembly: CommandClass(typeof(NoAutoCommands))]`
   - Implement `IPlugin` (Initialize, CreatePaletteSet, Terminate)

2. **Create a Loader project** (permanent in AutoCAD):
   - Import `DevReload.projitems` and `DevReload.Forms.projitems`
   - Reference `DevReload.Interface` normally
   - Add a build-order dependency on the Core: `ReferenceOutputAssembly=false`
   - Implement LOAD/DEV/UNLOAD commands following `ExampleLoaderCommands.cs`

</overview>
</section>

<section>
<overview>

## How It Works

### Stream-Based Loading (No File Locks)

`PluginHost.Load()` reads the DLL bytes via `File.ReadAllBytes()` and loads via `AssemblyLoadContext.LoadFromStream()`. The file handle is released immediately after reading — Visual Studio can rebuild the DLL at any time. NuGet dependencies are still loaded from disk (via `LoadFromAssemblyPath`), which is fine because they don't change during development.

### The Two Command Registries Problem

AutoCAD .NET 8 has **two separate command registration systems**:

1. **`CommandClass.AddCommand`** — AutoCAD's internal system, triggered automatically by `ExtensionLoader.ProcessAssembly` when `AppDomain.AssemblyLoad` fires (which happens for ALL `AssemblyLoadContext`s in .NET 8, not just the default). There is no public API to remove commands from this registry.

2. **`Utils.AddCommand` / `Utils.RemoveCommand`** — The public API in `Autodesk.AutoCAD.Internal`. Commands added here can be cleanly removed.

Without intervention, loading a plugin assembly triggers system #1, and reloading throws `eDuplicateKey` because the old commands can't be removed. The solution:

- **`[assembly: CommandClass(typeof(EmptyClass))]`** in the Core assembly tells AutoCAD to only scan that empty class — finding no commands, registering nothing via system #1.
- **`CommandRegistrar`** uses system #2 (`Utils.AddCommand`) to register commands, and `Utils.RemoveCommand` to clean them up before reload.

### Visual Studio Integration

`DevReloadService.FindAndBuild()` uses COM interop to enumerate the Running Object Table (ROT) for `VisualStudio.DTE` monikers. It finds your project by name across all running VS instances, builds it via `EnvDTE.SolutionBuild.BuildProject()`, and returns the output DLL path.

### Collectible AssemblyLoadContext

`IsolatedPluginContext` extends `AssemblyLoadContext` with `isCollectible: true`. When `Unload()` is called and all references are released (commands unregistered, PaletteSet closed), the GC can collect the entire context and all assemblies within it.

</overview>
</section>

<section>
<overview>

## Project Structure

```
DevReload/
├── DevReload.sln
├── Directory.Build.props           # AutoCAD path + common MSBuild settings
├── Directory.Build.props.user.example
│
├── src/
│   ├── DevReload/                  # Shared project (.shproj)
│   │   ├── CommandRegistrar.cs     # Utils.AddCommand/RemoveCommand wrapper
│   │   ├── DevReloadService.cs     # VS COM interop: find project, build, return DLL
│   │   ├── IsolatedPluginContext.cs # Collectible ALC with dependency resolution
│   │   ├── PluginHost.cs           # Load from stream, find IPlugin, manage lifecycle
│   │   └── VsInstanceFinder.cs     # COM ROT enumeration for running VS instances
│   │
│   ├── DevReload.Interface/        # Shared contract (IPlugin interface)
│   │   └── IPlugin.cs
│   │
│   └── DevReload.Forms/            # Shared project: UI selection dialogs
│       └── Forms/                  # Dark-themed grid forms for VS instance selection
│
└── example/
    ├── Example.Plugin/             # Hot-reloadable Core (commands + WPF palette)
    └── Example.Loader/             # Permanent Loader (EXLOAD/EXDEV/EXUNLOAD)
```

</overview>
</section>

<section>
<overview>

## License

MIT License. See [LICENSE](LICENSE) for details.

</overview>
</section>
