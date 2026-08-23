<overview>
Question: can DevReload manage ObjectARX (.arx / .dbx / .crx) projects the same way it
manages .NET plugins — register a project, build it, load it, hot-reload it, unload it?

Verdict: **yes, and DevReload is an unusually good host for it** — but it is a *second*
lifecycle, not a variation of the existing one. The build half (MSBuild querying, worktree
remapping, plugins.json, palette, MCP tools) reuses verbatim and is verified working against a
real .vcxproj. The load half shares nothing: native modules are mapped from their file, so the
file is locked while loaded, so DevReload's core invariant — *build first, then swap; a failed
build leaves the old plugin running* — is physically impossible for OARX and inverts to
*unload → wait for unmap → build → load*.

Everything below marked **[verified]** was executed against this machine (AutoCAD 2025,
ObjectARX 2025 SDK at `C:\ObjectARX\2025`, MSBuild 18/Current). Everything marked
**[to verify]** needs a live-session spike before it is designed on.

Reference implementation studied:
`H:\GitHub\DamgaardRI\NorsynDrawingTools\utils\LoaderUnloader\acrxEntryPoint.cpp` (~880 lines,
the NDH `LDBG` / `RLD` / `RLD2` / `ULDP` dev loop).
</overview>

<what-the-reference-actually-does>
Stripped of the NDH-specific parts, the reference encodes six mechanics. Each is a requirement
on any DevReload OARX implementation.

1. **Ordered module set, not a single module.** `NSNorsynDistrictHeating.dbx` loads first, then
   `NDHNorsynDistrictHeating.arx`. Unload runs in reverse. This ordering is not a nicety — the
   dbx owns the custom classes the arx uses.

2. **Two load APIs, two unload APIs.**
   `acrxLoadModule(dbxPath, true)` + `acedArxLoad(arxPath)` to load;
   `acedArxUnload(arx)` then `acrxUnloadModule(dbx)` to unload.

3. **The .arx unmaps asynchronously.** `acrxUnloadModule` on the dbx unmaps *immediately*;
   AutoCAD defers the .arx's `FreeLibrary` to its next idle. The reference therefore cannot
   build inside the unload command — it registers `acedRegisterOnIdleWinMsg` and resumes the
   build once `GetModuleHandle(arx) == NULL`, with a 30 s timeout.

4. **A pre-flight that can abort with the session intact.** Before unloading anything, it
   checks whether any build output this session *pins but does not unload*
   (`NSNorsynObjects.dbx`) would need a relink — by running msbuild on just those targets. If
   they are stale, it aborts *before* tearing anything down. Without this you discover the
   LNK1168 after the session is already empty.

5. **The lock test is a file test, not a module test.** `CreateFile(GENERIC_WRITE, share=0)` →
   `ERROR_SHARING_VIOLATION`. Deliberately not `GetModuleHandle`, because the same base name is
   usually also mapped from Appload (a different file that blocks nothing), and the repo is
   reachable through a junction so comparing module paths as strings is unreliable.

6. **Native probing directories.** `SetDllDirectory(dir)` scoped around the load, plus a
   process-lifetime `AddDllDirectory` registry (registered once per directory — the cookies
   accumulate otherwise and are never released).

The reference also carries NDH-specific policy that is *not* DevReload's business: pinning one
canonical `NorsynLogging.dll`, loading a managed trace UI via `acdbmgd!LoadManagedDll`, and the
worktree picker (DevReload already has one).
</what-the-reference-actually-does>

<managed-api-surface-verified>
The reference runs inside an .arx. DevReload is managed .NET. The question was whether the
same control exists from managed code. It does.

**[verified]** by reflecting over `C:\Program Files\Autodesk\AutoCAD 2025\acdbmgd.dll` —
`Autodesk.AutoCAD.Runtime.SystemObjects.DynamicLinker` (type
`Autodesk.AutoCAD.Runtime.DynamicLinker`, base `RXObject`) exposes:

| Managed member | Native equivalent |
|---|---|
| `void LoadModule(string fileName, bool printit, bool asCmdrArg)` | `acrxLoadModule` |
| `void UnloadModule(string fileName, bool printit)` | `acrxUnloadModule` |
| `bool IsModuleLoaded(string fileName)` | `acrxAppIsLoaded` |
| `StringCollection GetLoadedModules()` | `acrxDynamicLinker` enumeration |
| `void LoadApp(string, bool, bool)` / `void UnloadApp(string, bool)` | `acedArxLoad` / `acedArxUnload` (closest) |
| `bool IsApplicationLocked(string name)` | `acrxApplicationIsLocked` |
| `bool IsAppMdiAware(string name)` | `acrxDynamicLinker->isAppMDIAware` |
| events `ModuleLoading/Loaded/LoadAborted/Unloading/Unloaded/UnloadAborted` | `AcRxDynamicLinkerReactor` |

**[verified]** there is no other managed ARX-load entry point — `Application`, `Runtime.Utils`
and `Internal.Utils` expose nothing of the kind. `SystemObjects.DynamicLinker` is the surface.

Two consequences worth naming:

- `IsApplicationLocked` gives DevReload a real **pre-flight**: it can tell the user "this module
  is locked, it will never unload" *before* starting a destructive reload, instead of
  discovering it halfway through. Same class of guard as the reference's retained-output check.
- The `ModuleUnloaded` event is a better completion signal than polling `GetModuleHandle`,
  though the file-writability probe (mechanic 5) is still the test that matters for the linker.

**[to verify]** — cheap, one live session via the existing MCP surface:

- The load/unload methods return `void`, so failure presumably raises
  `Autodesk.AutoCAD.Runtime.Exception`. Confirm, and confirm which `ErrorStatus` values appear
  for "locked", "still referenced", "file not found".
- Whether `LoadModule` on an `.arx` fully substitutes for `acedArxLoad` — specifically whether
  the module's `ACED_ARXCOMMAND_ENTRY_AUTO` commands register (they should: they are registered
  by `AcRxArxApp::On_kInitAppMsg`, which any `kInitAppMsg` triggers) and whether it appears in
  the ARX command's list. If not, `LoadApp`/`UnloadApp` are the fallbacks.
- Whether the deferred-unmap behaviour (mechanic 3) is a property of `acedArxUnload` or of
  `.arx` modules generally. This decides whether the idle-wait is always needed or only for .arx.
</managed-api-surface-verified>

<build-surface-verified>
This is the good news, and it is the reason the integration is cheap.

**[verified]** against `utils/LoaderUnloader/LoaderUnloader.vcxproj`:

```
MSBuild.exe LoaderUnloader.vcxproj -getProperty:TargetPath -p:Configuration=Debug -p:Platform=x64
  -> H:\...\utils\LoaderUnloader\x64\Debug\NDHLoaderUnloader.arx     (exit 0)
```

`BuildService.QueryMsBuildProperty` works on a `.vcxproj` **with no changes at all**:
`IsSdkStyle` sees the 2003 xmlns, returns false, and routes to the full MSBuild.exe located via
vswhere — exactly the path already built for the user's old-style Revit csprojs. The reference's
hardcoded `C:\Program Files\...\2022\Community\MSBuild\Current\Bin\MSBuild.exe` becomes
unnecessary.

**[verified]** the project kind comes for free:

```
-getProperty:ArxAppType  -> "arx"    (the ObjectARX wizard's Globals property; "dbx" on NorsynObjects)
-getProperty:TargetExt   -> ".arx"
```

In practice `Path.GetExtension(TargetPath)` is enough — `.arx` / `.dbx` / `.crx` is the whole
taxonomy, and DevReload needs it only to pick load order and unload API.

**[verified]** one real gap: `BuildService.GetConfigurations` asks for
`-getProperty:Configurations`, which .vcxproj does not define (it uses the `ProjectConfiguration`
item group). It returns empty, and `PluginManager.GetConfigurations` turns that into a hard
throw. Fix is one branch:

```
MSBuild.exe LoaderUnloader.vcxproj -getItem:ProjectConfiguration ...
  -> {"Items":{"ProjectConfiguration":[{"Identity":"Debug|x64","Configuration":"Debug","Platform":"x64"}, ...]}}
```

**[verified]** `GitWorktreeService.ResolveActiveCsproj` is pure path arithmetic with no file-type
assumption — worktree support works for .vcxproj unchanged. The reference's `RLD2` worktree popup
is already a DevReload feature.

Three smaller caveats:

- `-restore` on a **packages.config** vcxproj (`src/NorsynDistrictHeating` has one) is not the
  same as a PackageReference restore. Needs checking before assuming BuildService's existing
  `-restore` flag is right for C++.
- `ParseBuildSummary` matches the English strings `"Warning(s)"` / `"Error(s)"`. Fine today; a
  latent break on a localised MSBuild, and C++ builds are where you most want the counts.
- **C++ builds are slow.** DevReload builds synchronously behind a wait cursor — acceptable for a
  2-second C# build, not for a 90-second C++ link. The reference solved this with a pumped
  message loop (`runPumped`) precisely because AutoCAD was painting "Not Responding". DevReload
  needs the same, or an idle-driven async build.
</build-surface-verified>

<the-fundamental-difference>
DevReload's .NET lifecycle rests on one trick: `PluginHost<T>.Load` reads the DLL into a
`MemoryStream` and loads *bytes*, so the file is never locked. That is what makes
"build first, then swap" possible, and it is why a failed build is harmless — the old plugin
keeps running.

**A native module cannot do this.** Windows maps the .arx/.dbx from the file; the file is locked
for as long as it is mapped. The order is forced:

```
.NET :  build -> (succeed?) -> teardown old -> load new         failure => old plugin still running
OARX :  pre-flight -> unload -> await unmap -> build -> load    failure => NOTHING is loaded
```

Three consequences that are not implementation details but design facts:

1. **OARX reload is destructive.** A build failure empties the session. The reference states this
   plainly (`Modules remain UNLOADED`) and does not pretend otherwise; DevReload must do the same
   rather than paper over it.
2. ~~**Reload cannot be a function.**~~ **Superseded by F2** — measured, not assumed.
   `UnloadModule` unmaps synchronously, so the cycle is a plain sequential function:
   `unload (reverse order) -> verify every file is writable -> build -> load (order)`.
   The idle-driven state machine LoaderUnloader needs is an artefact of `acedArxUnload`'s
   deferred FreeLibrary, and DevReload does not use that path. The verify step is not optional:
   it is what turns "unload reported success" into "the linker can actually rewrite this".
3. **Custom entities degrade to proxies on every unload.** Inherent to ObjectARX
   (`AcDbProxyEntity` / `AcDbProxyObject`), documented, unavoidable. Any open drawing holding the
   plugin's custom objects pays this cost per reload cycle.

So `PluginHost<T>` / `IsolatedPluginContext` contribute nothing here. The right shape is a
sibling host with the same *interface* (Load / Reload / Unload / IsLoaded / Snapshot) and a
completely different implementation — a deep module, in the codebase's own vocabulary.
</the-fundamental-difference>

<plugin-side-requirements>
"With correct setup they can be unloaded natively" — here is exactly what that setup is, and how
much of it DevReload can check.

**[verified]** from `C:\ObjectARX\2025\inc\dbxEntryPoint.h:78-86` — `AcRxDbxApp::On_kInitAppMsg`
calls `acrxDynamicLinker->unlockApplication(pkt)` whenever `m_bUnlocked` is true, and the
constructor defaults it to true. So **any ArxAppWizard project using `IMPLEMENT_ARX_ENTRYPOINT`
with an `AcRxArxApp` / `AcRxDbxApp` base is unloadable out of the box** — which is why the NDH
repo unlocks nothing explicitly (`grep unlockApplication` over `src/` and `interop/` finds no
call) and yet `RLD` works. Applications are locked *by default* only in the raw-`acrxEntryPoint`
sense; the wizard base class opts you in.

The base classes also handle two of the five documented unload obligations automatically:
`AcRxArxApp::On_kUnloadAppMsg` removes every `ACED_ARXCOMMAND_ENTRY_AUTO` command, and
`AcRxDbxApp::On_kUnloadAppMsg` runs `deleteAcRxClass` over every
`ACDB_REGISTER_OBJECT_ENTRY_AUTO` class, leaves-first. What is left to the plugin author (per the
ObjectARX Developer's Guide, *Preparing for Unloading*): detach every reactor (`AcDbObject`,
`AcDbDatabase`, `AcRxDynamicLinker`, `AcEditor`), remove any registered service name from
`acrxServiceDictionary`, and drop any transient drawable — the reference does exactly this with
its HUD (`hud::hide()` in `On_kUnloadAppMsg`; "leaving one registered across an unload hands the
graphics system a dangling drawable").

**The one requirement DevReload cannot check and cannot fix:** *nothing else may import symbols
from the module you want to unload.* If the .arx implicitly imports from the .dbx, the .dbx stays
mapped after `acrxUnloadModule` "succeeds" and the relink dies. The reference solved this with a
build-time import guard ("the arx imports ZERO symbols from the dbx ... baseline 0") and calls it
"P4 runtime decoupling". This is a property of the *user's* project layout, not of the loader.

Same for satellite DLLs: anything a module pins for the session (`NorsynLogging.dll` in the
reference, or a mixed-mode interop assembly in the default ALC) can never participate in the loop
— "rebuilding NorsynLogging needs a Civil restart".

**Conclusion for DevReload's design:** it cannot enforce unloadability, so it must be
*diagnostic-heavy*. Concretely — pre-flight `IsApplicationLocked` per module; after each unload,
prove the file is writable and, if not, name what is still mapped; abort before tearing anything
down when a retained output is stale. Silence here produces exactly the failure mode the
reference's comments describe: "it died AFTER the unload sequence emptied the session, leaving no
modules loaded and a linker error naming neither the cause nor the cure."
</plugin-side-requirements>

<what-reuses-what-is-new>
Reused unchanged:

- `BuildService.QueryMsBuildProperty` (vcxproj **[verified]**), `LocateFrameworkMsBuild` (vswhere)
- `GitWorktreeService` — worktree remapping, branch enumeration, the picker
- `plugins.json` + `PluginConfigLoader` + the registration/mutation funnel
- `PluginManager`'s public shape: Load / DevReload / Unload / BuildOnly / snapshots / events
- The palette (`DevReloadViewModel`, per-plugin cards) and the MCP tool surface
- `AcadIdlePumpDispatcher` — the idle driver the state machine needs

New, and genuinely new:

- `OarxModuleHost` — `SystemObjects.DynamicLinker` calls, `SetDllDirectory` / `AddDllDirectory`
  P/Invoke, the file-writability probe, per-module load order
- `OarxReloadSequencer` — the idle-driven state machine, the pre-flight, the unmap timeout
- An `OarxRegistration` alongside `PluginRegistration`, and a `kind` discriminator in plugins.json

Modified:

- `BuildService.GetConfigurations` — `-getItem:ProjectConfiguration` branch for vcxproj
  **[verified gap]**
- Build execution — pumped / non-blocking, because C++ links are slow
- Palette card — an OARX card has no shared-assembly dialog and no command count; it has a module
  list and an unloadability indicator
</what-reuses-what-is-new>

<decisions>
Settled in review on 2026-08-23. These are constraints on the implementation, not options.

**D1 — Module grouping: one registration = an ordered list of projects.** Reload unloads the
whole group in reverse order, builds, reloads in order. The common case is a list of one; the
NDH case is `NSNorsynDistrictHeating.dbx` then `NDHNorsynDistrictHeating.arx`. No dependency
graph, no `dependsOn` edges.

**D2 — Mirror `RLD`'s call pairs from LoaderUnloader.** Not a spike question — the reference
already fixes the answer:

```
load    dbx:  acrxLoadModule(path, true)      arx:  acedArxLoad(path)
unload  arx:  acedArxUnload(name)             dbx:  acrxUnloadModule(name)
```

The only open item is the *managed* spelling of `acedArxLoad` / `acedArxUnload` — `LoadApp` /
`UnloadApp` are the closest match on `DynamicLinker` and the spike confirms which managed call
reproduces the LU behaviour. The semantics are not up for redesign.

**D3 — A build failure produces feedback, not silence.** Port LU's transient HUD
(`utils/LoaderUnloader/BuildHud.h`): step chips (`Preflight → Unload → Unmap → Build → Load`),
an indeterminate bar, a scrolling tail of the build log, and a closing verdict frame — green on
success, red on failure — held ~3 s before the transient is erased. Managed equivalent of the
native `AcGi` transient is `GraphicsInterface.Drawable` + `TransientManager`.

**D4 — OARX shares no .NET loader mechanism.** No ALC, no shared-assembly list, no
`SharedAssemblyPreloader`. Native probing is `SetDllDirectory` scoped around each load, exactly
as LU does it. The palette grows **two tabs — .NET and OARX** — rather than one card type
pretending to cover both.

**D5 — AutoCAD only.** Civil 3D ships no C++ SDK, so there is no Civil-specific ObjectARX target
to detect or tolerate. Struck.

**D6 — Parallel implementations, separate folders.** The OARX lifecycle sits beside the .NET one,
not inside it. Code goes in its own folder so it does not mix with the Revit DevReload sources.
Repo hygiene: sweep on-disk files under the new area that are not in git.
</decisions>

<risks>
1. **Destructive reload.** A build failure empties the session. Inherent, not fixable — which is
   why D3 makes the failure loud rather than silent.
2. **Proxy degradation.** Every unload turns the plugin's custom entities into proxies in every
   open drawing.
3. **A single pinning import silently breaks the loop.** DevReload detects and reports; it cannot
   fix. The complement is a project-side rule: **OARX projects must be structured without
   pinning** (the reference's "P4 runtime decoupling", enforced by a post-link import guard).
4. **UI freeze during C++ builds.** Solved the same way LU solved it: port `runPumped` — a frame
   loop that pumps PAINT messages only, dropping mouse/keyboard while the modules are unloaded
   and mid-relink.
5. **The unmap timeout is a guess.** The reference uses 30 s. If a module never unmaps, the state
   machine must give up loudly rather than wedge.
6. **Two lifecycles in one palette.** Kept parallel by D4/D6 — separate tabs, separate folders,
   a shared lifecycle interface rather than a widened `PluginManager`.
</risks>

<live-findings>
Run against Civil 3D 2025 (pid 98664 / 103496) on 2026-08-23 using `labs/oarx` — a purpose-built
minimal `.dbx` + `.arx` pair. **A full hot-reload cycle was completed with no AutoCAD restart:**

```
load stamp=1 -> LABPING stamp=1 -> unload both -> msbuild stamp=2 -> load -> LABPING stamp=2
```

Verbatim from the modules' own log:

```
02:25:39.663  LabDbx  kInitAppMsg    stamp=1
02:25:39.686  LabArx  kInitAppMsg    stamp=1
02:25:39.744  LabArx  LABPING        stamp=1
02:26:01.751  LabArx  kUnloadAppMsg  stamp=1
02:26:01.767  LabDbx  kUnloadAppMsg  stamp=1
02:26:25.087  LabDbx  kInitAppMsg    stamp=2
02:26:25.120  LabArx  kInitAppMsg    stamp=2
02:26:25.194  LabArx  LABPING        stamp=2
```

**F1 — `UnloadModule`'s second argument must be `false`.** This was the entire blocker.
`UnloadModule(name, true)` throws `InvalidOperationException` ("Operation is not valid due to the
current state of the object" — the wrapper's rendering of a native `false` return) for both .arx
and .dbx, from a command context and from idle alike. `UnloadModule(name, false)` succeeds every
time. Hours of "the module is locked / the context is wrong" theories were all this one boolean.

**F2 — the unload is SYNCHRONOUS. There is no deferred unmap, so there is no state machine.**
Measured inside a single call with zero idle ticks between the unload and the probe:

```
UnloadModule("LabArx.arx", false) => ok
  IsModuleLoaded = false | mapped in process = false | file = writable
```

This overturns the earlier conclusion drawn from LoaderUnloader. LU waits on
`acedRegisterOnIdleWinMsg` because **`acedArxUnload` defers its FreeLibrary** — that is a property
of the *ADS* unload path, not of .arx modules. `acrxUnloadModule` (managed `UnloadModule`) unmaps
immediately. DevReload's reload is therefore a plain sequential function, not an idle-driven
state machine, and D3's HUD does not need an `Unmap` step.

**F3 — `LoadModule` fully substitutes for `acedArxLoad`.** `LoadModule(fullPath, false, false)`
on the .arx registered its `ACED_ARXCOMMAND_ENTRY_AUTO` commands: `LABPING` and `LABWHERE` both
executed. `LoadApp` is NOT the answer — it rejects a full path (`InvalidOperationException`).

**F4 — the load and unload APIs are paired; do not mix them.** A module loaded with `LoadModule`
is invisible to the ADS app table: LISP `(arxunload "labarx.arx")` returns failure for it.
Conversely a module loaded with LISP `(arxload path)` unloads cleanly with
`(arxunload "labarx.arx")` and fires `kUnloadAppMsg`. Pick one pair. **DevReload uses
`LoadModule` / `UnloadModule`** — it is the managed-native path, needs no LISP round trip, and
unmaps synchronously (F2).

**F5 — `IsApplicationLocked` is NOT a usable pre-flight.** It returns `true` for a module that is
not loaded at all, and `false` for our loaded, unlocked module. It answers "unknown or locked",
which cannot be distinguished from "locked". Use it only on a module already known to be loaded.

**F6 — argument shapes.** `LoadModule` takes a **full path**; `UnloadModule` and `IsModuleLoaded`
take the **module file name with extension** (`"LabArx.arx"`), matched case-insensitively.
`GetLoadedModules()` reports lowercased file names.

**F7 — `TargetPath` for a .vcxproj is wrong unless `SolutionDir` is supplied.** MSBuild
synthesises `SolutionDir` as the *project* directory when a .vcxproj is evaluated standalone, so
the default `OutDir` of `$(SolutionDir)$(Platform)\$(Configuration)\` resolves somewhere the
solution build never writes:

```
LoaderUnloader.vcxproj -getProperty:TargetPath
  (standalone)              -> ...\utils\LoaderUnloader\x64\Debug\NDHLoaderUnloader.arx   [wrong]
  -p:SolutionDir=<reporoot>\ -> ...\x64\Debug\NDHLoaderUnloader.arx                        [correct]
```

An OARX registration must therefore record the **solution path**, and every query and build must
pass `-p:SolutionDir=`. Silently resolving the wrong output directory is precisely the
wrong-but-plausible failure the no-fallback rule exists to prevent.

**F8 — the file-writability probe is process-global.** After unloading LabArx in one AutoCAD, the
file still reported `LOCKED` because a *second* AutoCAD instance had it loaded. The probe is
still the right test (it is what the linker sees), but a "still locked after unload" diagnostic
must name the possibility of another AutoCAD holding the module, not just a pinning import.

**F9 — wizard defaults really are unloadable.** The lab modules do nothing special beyond
`IMPLEMENT_ARX_ENTRYPOINT` over `AcRxArxApp` / `AcRxDbxApp`, and both unloaded cleanly with their
`kUnloadAppMsg` running — including the .dbx that registers a custom `AcDbObject` class.

**F10 — ObjectARX sources need a prologue.** The SDK headers assume `<windows.h>`, `<tchar.h>` and
`#pragma pack(push, 8)` already happened (the wizard's StdAfx.h). Included cold they fail with
`syntax error: identifier 'RECT' / 'HMENU'`. See `labs/oarx/LabPrologue.h`.
**F11 — a managed `Drawable` subclass is never dispatched; a `DrawableOverrule` is.** LU derives
from `AcGiDrawable` and overrides `subViewportDraw`, which works because C++ reaches it through the
object's vtable. From .NET that route does not exist: `Drawable` and `Entity` have no managed
constructor that does not already require an unmanaged pointer, and subclassing a CONCRETE type
(`DBPoint`, `Circle`, ...) subclasses only the managed WRAPPER — the native object's vtable is
untouched, so AutoCAD keeps calling its own implementation. Measured: a `DBPoint` subclass added as
a transient reported **0** draw passes. A `DrawableOverrule` over `DBPoint`, with a bare `DBPoint`
as the transient carrier, reported **21/21/21** (SetAttributes/WorldDraw/ViewportDraw).

The filter must compare `RXObject.UnmanagedObject`, not wrapper identity: AutoCAD hands
`IsApplicable` a DIFFERENT managed wrapper around the same native object, so `ReferenceEquals`
is always false. With the pointer compare, a live run scored 33 applicable / 33 rejected and
`ViewportDraw` ran only for the carrier.

**F12 — `DeviceContextViewportCorners` is degenerate in the managed API.** LU sizes the HUD from
`AcGiViewport::getViewportDcCorners`, whose corners come back in DRAWING UNITS (outside perspective
the DCS is the eye coordinate system) that the pixel density converts. The managed wrapper does not
carry them: `ImpViewport.DeviceContextViewportCorners` returned `((0,0),(0,0))` on **every** draw
pass. This is the subtlest failure mode in the port, because nothing errors — the overrule is
dispatched, computes a 0x0 viewport, and is thrown away by its own "no room for a legible HUD"
guard. The symptom is identical to "never drawn", which sent the first diagnosis down the wrong path.

Two substitutes, both proven consistent live (`VIEWSIZE * density.Y == SCREENSIZE.Y == 1410`, and
`* aspect == SCREENSIZE.X == 3733`):

- **rectangle** — `SCREENSIZE`, already in pixels, so no density conversion at all.
- **centre** — `WorldToEyeTransform * CameraTarget`. The camera target IS the centre of the view,
  and taken through that viewport's own transform it survives a rotated UCS, which `VIEWCTR` (UCS
  coordinates) would not.

`SCREENSIZE` describes the CURRENT viewport while `ViewportDraw` runs once per visible viewport, so
in a tiled layout every HUD takes its size from the current viewport (it still centres on each
viewport's own target). LU carries the same assumption — an OARX cycle is driven from an ordinary
editing view. Not worth a viewport gate until someone hits it: `AcadWindowId == CVPORT` held in the
one single-viewport sample taken, which is not enough to gate on, and a wrong gate hides the HUD
entirely.

Reflection over `ImpViewport` (the full member list is worth knowing, since the docs describe the
C++ surface): properties `LinetypeGenerationCriteria, LinetypeScaleMultiplier, FrontAndBackClipping,
DeviceContextViewportCorners, AcadWindowId, ViewportId, CameraUpVector, CameraTarget, CameraLocation,
IsPerspective, EyeToWorldTransform, WorldToEyeTransform, EyeToModelTransform, ModelToEyeTransform,
ViewDirection`; methods `DoPerspective, DoInversePerspective, GetNumPixelsInUnitSquare, LayerVisible`.
There is no member that reports the viewport rectangle in pixels.

**F13 — `acedUpdateDisplay` really is enough; the message pump is not what was missing.** LU's
`tick()` calls `updateTransient` + `acedUpdateDisplay()` and does NOT pump — it pumps only in
`finish()` and inside `runPumped`. `Application.UpdateScreen()` behaves the same: with the main
thread held by the command, a 25-iteration loop still elaborated 26 draw passes. The pump is what
keeps the WINDOW alive (and input suppressed) during the compile, not what triggers elaboration.

**F14 — a failed OARX build leaves the modules UNLOADED, and that is not fixable.** The .NET path
builds first and swaps only on success, so a broken build leaves the old plugin running. OARX cannot
do that: a loaded `.arx`/`.dbx` is locked by the process, so the modules MUST come out before the
linker can write over them. The order is therefore unload -> build -> load, and a build failure
strands the group unloaded. Verified live: an injected syntax error in `LabArx/entry.cpp` produced
`[OARX] FAILED - build failed` with the compiler diagnostics echoed, and both `LabArx.arx` and
`LabDbx.dbx` reported `IsModuleLoaded == false` afterwards. Fixing the source and re-running the DEV
command rebuilt and reloaded both. This asymmetry is inherent to the platform, not a defect — but it
is why the failure verdict has to be loud (D3): the user is left with nothing loaded and must be
told why.

</live-findings>

<code-smells-noticed>
Out of scope for this research; reported per the standing rule, the user decides.

1. `BuildService.QueryMsBuildProperty` (`src/Shared/DevReload.BuildCore/BuildService.cs`)
   swallows every exception and returns `null`. Callers cannot distinguish "MSBuild not
   installed" from "property is empty" from "project failed to evaluate" — and
   `GetConfigurations` compounds it by turning that `null` into an empty list, which
   `PluginManager.GetConfigurations` turns into a generic "could not resolve configurations".
   The vcxproj gap above was invisible for exactly this reason. Suggest returning a
   discriminated result, or at minimum letting the failure text through.
2. `BuildService.ParseBuildSummary` matches the English MSBuild strings `"Warning(s)"` /
   `"Error(s)"`. Silently reports 0/0 on a localised toolchain.
3. `PluginConfigLoader.FindCsprojFromDllPath` exists only to migrate configs that predate
   `ProjectFilePath`. If the migration has run everywhere, it and the two `[Obsolete]` legacy
   properties on `PluginEntry` are dead and can go.
4. `PluginEntry.DllPath` is persisted in plugins.json but `LoadCore` recomputes the real path
   from the build every time — two sources of truth for the same fact.
</code-smells-noticed>
