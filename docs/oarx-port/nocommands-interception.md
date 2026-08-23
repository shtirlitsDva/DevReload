# Removing the NoCommands requirement

<question>
Can DevReload intercept a plugin that does not define a `NoCommands` marker
class, stop AutoCAD from auto-registering its commands, and register them
itself?
</question>

<answer>
Yes. Two mechanisms work; both are proven live in `labs/nocommands/`.

One of them also fixes a defect this research turned up: **DevReload leaks the
collectible ALC on every reload today**, marker or no marker. See
`<the-leak>`.
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

It also leaves a brief window in which the command is registered twice.
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

Two earlier revisions of this lab were wrong and were discarded. The first held
a live local reference to the ALC across the GC, so "still alive" measured the
harness, not AutoCAD. The second reported holder counts and lisp state as
absolutes, so leftovers from one scenario leaked into the next. Both metrics are
deltas now, and the scenarios are ordered so the process-global lisp name is
never pre-set by an earlier scenario.
</the-lab>

<what-changes-in-devreload>
Only if you take option 1. Scope, for your call:

| module | change |
| --- | --- |
| new, `DevReload/Loader/AutoCadScanSuppressor.cs` | install/restore the filter; hard-fail if the field is gone |
| `DevReloaderCommands.Initialize` / `Terminate` | install / restore |
| `PluginManager.LoadCore` | call `plugin.Initialize()` after load — AutoCAD no longer does |
| `CommandRegistrar` | unchanged; it already ignores `[assembly: CommandClass]` and scans all exported types |
| `README.md`, `skills/acd-agentic-dev/SKILL.md` | drop the marker requirement; correct the "permanent, no public API to remove" claim |

`NoCommands` markers in existing plugins stay harmless — `CommandRegistrar`
already ignores the attribute.
</what-changes-in-devreload>

<separate-findings>
Reported, not acted on.

* `README.md:122` and `skills/acd-agentic-dev/SKILL.md:263` both assert that
  AutoCAD's auto-registrations cannot be removed. Disproved above regardless of
  which option you pick.
* `PluginManager.cs:485` comments that DevReload does not call `Initialize()`
  because AutoCAD does. True today, and the reason for the dual-instance wart.
* `labs/` is untracked and now contains build output (`bin/`, `obj/`, `stage/`,
  `lab.log`, `lab.scr`). Needs `.gitignore` treatment before any commit.
</separate-findings>
