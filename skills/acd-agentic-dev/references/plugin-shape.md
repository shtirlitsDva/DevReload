<csproj>
For a new plugin or a throwaway repro, write the `.csproj` yourself:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Platforms>x64</Platforms>
    <Nullable>enable</Nullable>
    <!-- portable PDB: DevReload stream-loads it, stack traces get line numbers -->
    <DebugType>portable</DebugType>
    <DebugSymbols>true</DebugSymbols>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
    <AutoCADPath Condition="'$(AutoCADPath)' == ''">C:\Program Files\Autodesk\AutoCAD 2025</AutoCADPath>
  </PropertyGroup>
  <ItemGroup>
    <!-- Provided by the host process — Private=False keeps them out of the output. -->
    <Reference Include="accoremgd"><HintPath>$(AutoCADPath)\accoremgd.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="acdbmgd"><HintPath>$(AutoCADPath)\acdbmgd.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="acmgd"><HintPath>$(AutoCADPath)\acmgd.dll</HintPath><Private>False</Private></Reference>
    <!-- Civil 3D: add AecBaseMgd / AeccDbMgd / AeccPressurePipesMgd from $(AutoCADPath)\C3D as needed. -->
  </ItemGroup>
</Project>
```

Hex offsets in stack traces = no PDB.

Register with `devreload_register_new_plugin(projectFilePath="...\\Foo.csproj")`. Name = csproj file name. DLL path = MSBuild `TargetPath`. Do not hard-code either.
</csproj>

<invariants>
Two invariants: commands are registered through the removable path, and `Terminate()` releases every reference AutoCAD holds into the plugin. A break in either gives `eDuplicateKey` on reload, or an old assembly that stays loaded and keeps firing next to the new one.
</invariants>

<entry-class>
A plugin needs `[assembly: ExtensionApplication(typeof(MyPlugin))]` and one `IExtensionApplication`. DevReload:
- suppresses AutoCAD's assembly scan for assemblies in its `IsolatedPluginContext` (`AutoCadScanSuppressor`);
- registers every `[CommandMethod]` on public types through `Utils.AddCommand`/`RemoveCommand` (`CommandRegistrar`), and ignores `[assembly: CommandClass]`;
- creates one instance of the entry class and calls `Initialize()` and `Terminate()` on it. Teardown state can be in instance fields or static fields.

Do not call `CommandClass.AddCommand`: its registrations are permanent and give `eDuplicateKey` on reload.

Startup warning `DevReload: WARNING - could not suppress AutoCAD's assembly scan`: AutoCAD scans the plugin itself, registers its commands permanently, and calls `Initialize()` on its own instance. Then each plugin needs `[assembly: CommandClass(typeof(NoCommands))]` with an empty `public class NoCommands {}`, and teardown state must be static. Symptom: the second `devreload_reload` fails with `eDuplicateKey` on one of the plugin's commands. Report the warning to the user.
</entry-class>

<terminate-must-unpin-everything>
The collectible ALC unloads only when the default ALC holds no reference into it. `Terminate()` releases:

- **PaletteSets**: `Close()`, `Dispose()`, set the field to null.
- **Event subscriptions**: a `-=` for every `+=`. Use `AcadEventManager`.
- **Overrules**: `Overrule.RemoveOverrule(...)`, then `Dispose()`.
- **Transient graphics**: `TransientManager.CurrentTransientManager.EraseTransient(...)` for each entity.
- **Static caches** of `DBObject`/`Document`/`Editor`: clear.

Test: does AutoCAD hold a delegate or COM reference to anything in the DLL after `Initialize()` returns? Each one is released in `Terminate()`.

```csharp
public class MyPlugin : IExtensionApplication
{
    private PaletteSet? _palette;
    internal static AcadEventManager? Events { get; private set; }

    public void Initialize()
    {
        Events = new AcadEventManager();
        // wire services, lifecycle hooks, DI root
    }

    public void Terminate()
    {
        Events?.Dispose();
        Events = null;

        _palette?.Close();
        _palette?.Dispose();
        _palette = null;
    }
}
```

A command runs its latest registration, so new command behavior after a reload does not prove the old assembly unloaded. Diagnose with events that fire twice, old palettes or overrules that stay, and memory growth. `devreload_reload` returned `success:true` and old behavior continues → the defect is in `Terminate()`.
</terminate-must-unpin-everything>

<acad-event-manager>
`AcadEventManager` is in the `EventManager` shared project of [Autocad-Civil3d-Tools](https://github.com/shtirlitsDva/Autocad-Civil3d-Tools) (`Acad-C3D-Tools/EventManager/`). Import it by relative path; example for one disk layout: `<Import Project="..\..\..\..\..\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\EventManager\EventManager.projitems" Label="Shared" />`; it compiles into the plugin DLL. Without that repo, write the same tracking in one class.

It records one unsubscribe `Action` per `Document`, runs them on `DocumentToBeDestroyed`, and runs all on `Dispose()`. This handles the two failures of manual cleanup: `MdiActiveDocument` changed before `Terminate()` (unsubscribes the wrong document), and a closed document (`-=` on a dead reference does nothing).

```csharp
var doc = Application.DocumentManager.MdiActiveDocument;
doc.CommandEnded += OnCommandEnded;
MyPlugin.Events!.Track(doc, () => doc.CommandEnded -= OnCommandEnded);

// Terminate():
Events?.Dispose();   // unsubscribes every tracked handler in every document
```

Use it for every subscription on `Application.*`, `DocumentManager.*`, and per-`Document` events. With the repo present, a hand-written `List<Action> _unsubscribes` field is a duplicate of it.
</acad-event-manager>

<side-databases>
`Database.Dispose()` releases the OS file handle later (finalizer). Open with `FileShare.ReadWrite` when a `SaveAs` follows; `FileShare.Read` blocks the writer.
</side-databases>
