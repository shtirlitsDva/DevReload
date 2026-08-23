# Removing the NoCommands requirement

<question>
Can DevReload intercept a plugin that does not define a `NoCommands` marker
class, stop AutoCAD from auto-registering its commands, and register them
itself?
</question>

<answer>
Yes. Two mechanisms work; both are proven live in `labs/nocommands/`.

**Option 1 is implemented** in `src/Autocad/DevReload/AutoCadScanSuppressor.cs`.
Plugins no longer need the marker. See `<live-verification>` for the run against
the real code rather than the lab harness.

It also fixes a defect this research turned up: DevReload leaked the collectible
ALC on every reload, marker or no marker. See `<the-leak>`.
</answer>

<how-autocad-actually-does-it>
Decompiled from `accoremgd.dll` / `acdbmgd.dll` (AutoCAD 2025), then confirmed
live.

`Autodesk.AutoCAD.Runtime.ExtensionLoader` (acdbmgd, **public sealed**) hooks
`AppDomain.CurrentDomain.AssemblyLoad` at startup. Every loaded assembly is
passed through `CheckReferences`, which sets two flags off the AssemblyRef
table alone:

| flag | set when the assembly references |
| --- | --- |
| `MayHaveExtensionApplication` | `acdbmgd` |
| `MayHaveCommands` | `accoremgd` |

The assembly is then raised on the **public static event**
`ExtensionLoader.DeferredAssemblyLoad`. The lab dumped its subscriber list —
there are exactly two, and they split the work:

```
Autodesk.AutoCAD.Runtime.ExtensionLoader.OnDeferredAssemblyLoad          <- acdbmgd
Autodesk.AutoCAD.ApplicationServices.ExtensionLoader.OnExtensionLoad     <- accoremgd
```

* **acdbmgd's handler** honours `MayHaveExtensionApplication`. It reads
  `[assembly: ExtensionApplication]` (or scans for the first `IExtensionApplication`),
  `Activator.CreateInstance`s it, and wraps it in an `ExtensionApplicationHolder`
  whose constructor calls `Initialize()`. The holder goes into a **static
  `Hashtable m_extensions` keyed by Assembly** and is never removed until
  AutoCAD shuts down.

* **accoremgd's handler** honours `MayHaveCommands`. `AutoCADApplicationHolder.Initialize`
  contains the branch the marker exploits:

  ```csharp
  var attrs = assembly.GetCustomAttributes(typeof(CommandClassAttribute), false);
  Type[] types = attrs.Length > 0
      ? attrs.Select(a => ((CommandClassAttribute)a).Type).ToArray()
      : assembly.GetExportedTypes();          // <- no attribute: scan everything
  ```

  That is the entire `NoCommands` trick: one attribute swaps a full scan for a
  one-element list that has no commands in it.

The whole sequence runs **synchronously inside `LoadFromStream`**, before
`PluginHost.Load` returns.
</how-autocad-actually-does-it>

<option-1-filter-the-event>
Replace the event's backing delegate with a wrapper that drops assemblies
DevReload owns and forwards everything else:

```csharp
var field = typeof(Autodesk.AutoCAD.Runtime.ExtensionLoader)
    .GetField("m_deferredAssemblyLoadEventHandler",
              BindingFlags.NonPublic | BindingFlags.Static);

var original = (DeferredAssemblyLoadEventHandler)field.GetValue(null);

DeferredAssemblyLoadEventHandler filtered = (sender, e) =>
{
    if (AssemblyLoadContext.GetLoadContext(e.LoadedAssembly) is IsolatedPluginContext)
        return;                    // ours: DevReload registers these itself
    original?.Invoke(sender, e);
};

field.SetValue(null, filtered);
```

The predicate is **structural, not temporal**: it asks "is this assembly in one
of our collectible ALCs", so there is no timing window, no thread race, and no
possibility of suppressing an unrelated assembly. Install it once in
`Initialize`, restore in `Terminate`.

It covers plugin dependencies loaded into the same ALC for free — those get
scanned today too.

**Consequence:** AutoCAD no longer creates the plugin instance or calls
`Initialize()`. `PluginManager.LoadCore` must call `Initialize()` on its own
instance. That is a fix, not a cost: it retires the dual-instance wart the
README documents, since one instance would then get both `Initialize()` and
`Terminate()`.

**Cost:** one private static field on a public type. If a future AutoCAD renames
it, `GetField` returns null — detectable at startup, and it must fail loudly
there rather than silently reverting to the marker.
</option-1-filter-the-event>

<option-2-unregister-afterwards>
Let AutoCAD register, then take the registrations back. This uses **public API
only**.

The README states these registrations are permanent:

> These registrations are **permanent** — no public API to remove them.

That is wrong, and the lab disproves it. `Utils.AddCommand`,
`Utils.RemoveCommand` and AutoCAD's own `CommandClass` all operate on the same
`ACAD_REGISTERED_COMMANDS` command stack — they differ only in which vtable slot
they call (`addCommand`, `removeCmd`, `removeGroup`). So:

```csharp
Utils.RemoveCommand(assembly.FullName, globalName);
```

removes a command AutoCAD auto-registered. The group name for a
`[CommandMethod]` that declares no `GroupName` is
`mi.ReflectedType.Assembly.FullName` — the **full** name, e.g.
`LabPlugin, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`.

**Three things it does not fix:**

1. `[LispFunction]` defuns are registered through a different path
   (`AcEdDefunExWrapper`) and `Utils.RemoveCommand` does not undo them. Measured:
   still defined afterwards. `Utils` exposes no undefun.
2. AutoCAD still builds instance A and calls `Initialize()` on it — so the
   dual-instance wart stays.
3. The ALC still leaks (`<the-leak>`).

It also leaves a window in which the command is registered twice, once by
AutoCAD under the assembly full name and once by `CommandRegistrar` under the
assembly simple name. That is survivable but only by accident: `Utils.AddCommand`
discards the `Acad::ErrorStatus` the command stack returns, so a duplicate is
silently accepted. `CommandClass.AddCommand`, AutoCAD's own path, wraps the same
call in `Interop.Check` and **does** throw `eDuplicateKey`. That asymmetry is
where the classic symptom comes from: the throw is raised by AutoCAD's scan on
the second reload, not by DevReload's registration on the first.

The same asymmetry is why option 1 fails loudly at startup instead of quietly at
registration time. If the suppressor could not install and DevReload registered
anyway, `Utils.AddCommand` would not complain.
</option-2-unregister-afterwards>

<rejected>
* **Rewrite the assembly in memory** to inject `[assembly: CommandClass(typeof(NoCommands))]`
  before handing bytes to the ALC. Needs a metadata rewriter, changes the MVID
  so the PDB stops matching and breakpoints/line numbers break, and invalidates
  strong names. It also would not stop the `IExtensionApplication` path, which
  ignores `CommandClass` entirely.
* **Strip the `accoremgd` AssemblyRef** so `MayHaveCommands` comes back false.
  The plugin genuinely needs those types at runtime — `CommandMethodAttribute`
  lives in `accoremgd`.
* **Bracket the load** by unsubscribing both handlers for the duration of
  `LoadFromStream`. Works, but the suppression window is process-wide and
  time-based; option 1's predicate is per-assembly and strictly better.
</rejected>

<the-leak>
Independent of the question asked, and worth deciding on separately.

`Runtime.ExtensionLoader.m_extensions` is a static `Hashtable` holding
`Assembly -> ExtensionApplicationHolder -> IExtensionApplication instance`.
Nothing removes an entry before AutoCAD exits. That instance lives in the
plugin's collectible ALC, so the table is a permanent GC root into it.

The `NoCommands` marker does not help here: it only gates the accoremgd
command path. The acdbmgd `IExtensionApplication` path is gated by
`[assembly: ExtensionApplication]`, which every DevReload plugin carries.

Measured on a correctly-marked plugin — DevReload exactly as it ships:

```
-- 2. NoCommands marker, no interception (DevReload as it ships)
       command LABPING registered by AutoCAD    : False
       new ExtensionApplicationHolder (acdbmgd) : 1
       ALC still alive after Unload + 10x GC    : True
```

So every `devreload_reload` leaves the previous ALC, its assemblies and its JIT
code in the process forever. Nothing breaks — the DLL is stream-loaded so the
file is never locked and the build always succeeds — it just accumulates.

Option 1 is the only one of the two that fixes it:

```
-- 1. unprepared plugin + DeferredAssemblyLoad filter (proposed)
       command LABPING registered by AutoCAD    : False
       new ExtensionApplicationHolder (acdbmgd) : 0
       ALC still alive after Unload + 10x GC    : False
```
</the-leak>

<the-lab>
`labs/nocommands/` — `pwsh labs/nocommands/run.ps1`. Builds three assemblies,
starts AutoCAD on a script that NETLOADs the probe, runs one command, quits,
and prints the log. About 40 s.

* `LabPlugin` — an *unprepared* plugin: `[CommandMethod]`, `[LispFunction]`,
  `[assembly: ExtensionApplication]`, and deliberately no marker.
* `LabPluginMarked` — same source with `MARKED` defined, so it carries the
  `NoCommands` marker. Models a correctly-prepared plugin.
* `LabProbe` — NETLOAD'd harness. Loads the plugins into collectible ALCs and
  reports command state, lisp state, holder deltas and whether the ALC collects.

Full output of the final run:

```
-- 0. subscribers to ExtensionLoader.DeferredAssemblyLoad
           Autodesk.AutoCAD.Runtime.ExtensionLoader.OnDeferredAssemblyLoad
           Autodesk.AutoCAD.ApplicationServices.ExtensionLoader.OnExtensionLoad
-- 1. unprepared plugin + DeferredAssemblyLoad filter (proposed)
           filter suppressed LabPlugin (mayHaveCommands=True, mayHaveExtApp=True)
           command LABPING registered by AutoCAD : False
           lisp    labfn   defined by AutoCAD    : False
           new ExtensionApplicationHolder (acdbmgd)  : 0
           new AutoCADApplicationHolder   (accoremgd): 0
           ALC still alive after Unload + 10x GC   : False
-- 2. NoCommands marker, no interception (DevReload as it ships)
    plugin| Ext.Initialize   hash=59375904
           command LABPING registered by AutoCAD : False
           lisp    labfn   defined by AutoCAD    : False
           new ExtensionApplicationHolder (acdbmgd)  : 1
           new AutoCADApplicationHolder   (accoremgd): 0
           ALC still alive after Unload + 10x GC   : True
-- 3. unprepared plugin, no interception (today's failure mode)
    plugin| Ext.Initialize   hash=44718932
           command LABPING registered by AutoCAD : True
           lisp    labfn   defined by AutoCAD    : True
           new ExtensionApplicationHolder (acdbmgd)  : 1
           new AutoCADApplicationHolder   (accoremgd): 1
           after Utils.RemoveCommand(fullName, ..)  : False
           ..but the lisp defun labfn survives it   : True
           ALC still alive after Unload + 10x GC   : True
-- 4. filter installed, but assembly loaded into the DEFAULT ALC
    plugin| Ext.Initialize   hash=65463703
           command LABPING registered by AutoCAD : True
           new ExtensionApplicationHolder (acdbmgd)  : 1
```

Line 4 is the safety check: with the filter installed, an assembly loaded into
the **default** ALC is still processed by AutoCAD exactly as before. The filter
touches only what DevReload owns.

The same run repeated under two more conditions, byte-identical results both
times:

* **Civil 3D profile** (`acad.exe /product C3D`). Same two subscribers, same
  outcomes. The approach is not sensitive to the vertical.
* **From start-up**, with the probe autoloaded as an ApplicationPlugins bundle so
  it ran from `IExtensionApplication.Initialize` with
  `ExtensionLoader.m_startingUp = True`. That is the branch DevReload's
  `loadOnStartup` plugins go through, and `ProcessDeferred` dispatches
  differently there. A permanent filter is immune to it, because the decision is
  per-assembly rather than per-moment.

Two earlier revisions of this lab were wrong and were discarded. The first held
a live local reference to the ALC across the GC, so "still alive" measured the
harness, not AutoCAD. The second reported holder counts and lisp state as
absolutes, so leftovers from one scenario leaked into the next. Both metrics are
deltas now, and the scenarios are ordered so the process-global lisp name is
never pre-set by an earlier scenario.
</the-lab>

<what-changed-in-devreload>
| module | change |
| --- | --- |
| new, `DevReload/AutoCadScanSuppressor.cs` | install/restore the filter; throws rather than degrade quietly if the field is gone |
| `DevReloaderCommands.Initialize` / `Terminate` | install / restore; a failed install is reported on the command line |
| `PluginManager.LoadCore` | calls `plugin.Initialize()` after load, guarded on `IsActive` so a failed install does not double-initialize |
| `CommandRegistrar` | unchanged; it already ignores `[assembly: CommandClass]` and scans all exported types |
| `README.md`, `skills/acd-agentic-dev/SKILL.md` | marker requirement dropped; the "permanent, no public API to remove" claim corrected |

`NoCommands` markers in existing plugins stay harmless, because
`CommandRegistrar` ignores the attribute either way.
</what-changed-in-devreload>

<live-verification>
Against the published bundle in a real Civil 3D session, using `LabPlugin` — the
unprepared plugin, no marker — registered as an ordinary DevReload plugin.

Loaded once, then reloaded three times. All four succeeded. Under the previous
code the second reload would have thrown `eDuplicateKey`.

The plugin's own log, one line per lifecycle call:

```
Ext.Initialize   hash=11726308
Ext.Terminate    hash=11726308
Ext.Initialize   hash=25653181
Ext.Terminate    hash=25653181
Ext.Initialize   hash=44904986
Ext.Terminate    hash=44904986
Ext.Initialize   hash=36098836
LABPING invoked
```

Every `Initialize` is paired with a `Terminate` **on the same instance hash**,
exactly one per load. That is the dual-instance problem gone: one object gets
both halves. `LABPING invoked` is the command `CommandRegistrar` registered.

Reflecting into the host afterwards, with the plugin still loaded:

```
handlers          DevReload.AutoCadScanSuppressor+<>c__DisplayClass5_0.<Install>b__0
                  Autodesk.AutoCAD.MacroRecorderUi.ThisApplication.domain_AssemblyLoad
acdbmgd holders   no LabPlugin, no Acd.Mcp
accoremgd holders no LabPlugin, no Acd.Mcp
live plugin ALCs  PluginIsolated::Acd.Mcp.dll
                  PluginIsolated::LabPlugin.dll
LABPING           ARXCmd
```

Four `LabPlugin` ALCs were created across the load and three reloads. **One is
alive.** The other three were collected, which is the leak fix on real reloads
rather than in the harness. After unloading, `LabPlugin`'s ALC is gone entirely
and `LABPING` reads `NoneCmd`.

The handler list also shows something the lab never produced: Civil 3D's
`MacroRecorderUi` subscribed to the event **after** DevReload installed, so it
combined onto the wrapper instead of sitting inside it. Two consequences, both
already handled. That late subscriber is not filtered, which is fine because it
does not register commands. And the field is no longer reference-equal to the
wrapper, so `Restore()` deliberately leaves it alone rather than overwriting the
field and dropping the macro recorder.
</live-verification>

<separate-findings>
Reported, not acted on.

All three are now fixed; kept here as the record.

* `README.md` and `skills/acd-agentic-dev/SKILL.md` both asserted that AutoCAD's
  auto-registrations cannot be removed. Disproved by `<option-2-unregister-afterwards>`.
* `PluginManager.LoadCore` documented that DevReload does not call
  `Initialize()` because AutoCAD does. That was the dual-instance wart.
* `labs/` build output needed `.gitignore` treatment.
</separate-findings>
