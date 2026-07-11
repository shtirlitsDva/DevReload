<feature>
Load shared/interop assemblies from their csproj-referenced (external) location
</feature>

<problem>
DevReload's shared/interop-ALC mechanism can only load assemblies that physically
sit in the plugin **build dir**. Consumers that reference a shared, mixed-mode
interop from a *central* location (e.g. `X:\...\Appload\2025\NorsynObjectsInterop.dll`,
referenced via `<HintPath>`) must therefore ALSO carry a private copy in their own
build dir (`<Private>True</Private>`), which is deployed into
`NetloadV2\2025\<Plugin>\`.

That means the same interop lives in TWO places and must be kept in sync by hand:
  1. `Appload\2025` (the machinery deploy — refreshed by `deploy-machinery.cmd`)
  2. `NetloadV2\2025\IntersectUtilities` (IU's private copy — refreshed only by a
     full IU rebuild + redeploy)

When the machinery is rebuilt (native `.dbx` ABI changes) but IU's private copy is
NOT refreshed, the loaded native base and the stale managed interop mismatch, and
the plugin fails to load: *"A procedure imported by NorsynObjectsInterop.dll could
not be loaded."* This is what happened on 2026-07-10.

Goal: let DevReload (and then NSLOAD) detect assemblies the csproj references from
OTHER directories (via `HintPath`) and load THOSE into the shared/interop ALC —
exactly as it loads local ones. The plugin then keeps a single source of truth
(Appload) with no private copy to drift.
</problem>

<current-mechanism>
End-to-end, as it exists today (verified in source):

1. Discovery — `SharedAssembliesViewModel` ctor
   Candidates = `Directory.GetFiles(pluginDir, "*.dll")`. ONLY DLLs physically in
   the build dir can be ticked shared / mixed-mode / streamed.

2. Persist — `SharedAssembliesFile.Config` (DevReload.BuildCore)
   Three lists of **names only** (sharedAssemblies / mixedModeAssemblies /
   streamedAssemblies). One `SharedAssemblies.Config.json` per build dir. No paths.

3. Load (dev) — `SharedAssemblyPreloader.Preload(buildDir, config)`
   For each shared name: `Path.Combine(buildDir, name + ".dll")`. Has the key guard
   `if (IsLoadedInDefaultAlc(name)) continue;` — bind to an already-present instance
   instead of double-loading. Mixed-mode → `EnsureRuntimeConfig` + `Assembly.LoadFrom`.

4. Load (prod) — `NSLOAD.PluginManager.LoadCore`
   A DRIFTED DUPLICATE of step 3. Same loop, resolves from `pluginDir`, BUT is
   MISSING the `IsLoadedInDefaultAlc` guard. (Smell: the "hard-won logic" was
   extracted into `SharedAssemblyPreloader` so hosts share one copy; NSLOAD never
   adopted it and has drifted.)

5. Deploy — `DevReloadViewModel.PushToProduction`
   Writes the dev `SharedAssemblies.Config.json` (names only) into the production
   app's `DllDir`. It does NOT copy the assemblies — the interop reaches NetloadV2
   only via IU's own `Private=True` build + redeploy.

Supporting facts:
  - `BuildService.QueryMsBuildProperty` shells `dotnet msbuild -getProperty:` (17.8+).
  - Mixed-mode MUST load from a real dir with `Ijwhost.dll` beside it
    (`AssemblyItem` comment); streamed/"location-unknown" is incompatible with
    mixed-mode. Appload has `Ijwhost.dll` beside the interop — so external-dir
    loading of the mixed-mode interop from Appload is valid.
</current-mechanism>

<validated-discovery>
Parsing the csproj `<Reference><HintPath>` XML directly (NOT MSBuild items) is
robust and sufficient. On IU's real csproj it cleanly yields:

  NorsynObjectsInterop                 Private=True   dir=X:\...\Appload\2025  exists
  NorsynProjectionProfileLabelInterop  Private=False  dir=X:\...\Appload\2025  exists

Why XML, not MSBuild `-getItem`: XML parsing needs no `restore`/evaluation and
survives a project that can't evaluate. (Proven relevant: IU's csproj `<Import>`
was pointing at a stale, moved path — now FIXED, see separate-issues — but XML
parsing would have worked regardless.)
</validated-discovery>

<proposed-design>
Six changes across three modules. "External" = a referenced assembly whose HintPath
dir is not the build dir.

1. Schema — `SharedAssembliesFile.Config` (DevReload.BuildCore) [shared]
   Add a backward-compatible map recording where an external assembly lives:
     "assemblyLocations": { "NorsynObjectsInterop": "X:\\...\\Appload\\2025" }
   Absent entry ⇒ resolve from buildDir (today's behaviour). Old files keep working.

2. Discovery — new helper `CsprojReferenceScanner` (BuildCore) [shared]
   Parse csproj `<Reference><HintPath>`; return name → external dir for references
   whose dir ≠ buildDir. Called when opening the Shared dialog.

3. Dialog — `SharedAssembliesViewModel` / `AssemblyItem` / `SharedAssembliesWindow`
   [DevReload]
   List external references as candidates too (badge/column: "external @ Appload").
   `AssemblyItem` gains `ExternalDir` (null = local). On save, write
   `assemblyLocations` for ticked external items.

4. Load resolution — `SharedAssemblyPreloader.Preload` [shared]
   `dir = config.AssemblyLocations.GetValueOrDefault(name) ?? buildDir;`
   then `Path.Combine(dir, name + ".dll")`. Guard + mixed-mode handling unchanged.

5. NSLOAD parity — `NSLOAD.PluginManager.LoadCore` [Autocad-Civil3d-Tools/NSLOAD]
   Mirror change 4 AND add the missing `IsLoadedInDefaultAlc` guard.

6. IU csproj — `NorsynObjectsInterop` `<Private>True</Private>` → `False`;
   remove the `Ijwhost.dll` `<None>` copy (Ijwhost lives in Appload beside the
   interop). Keep the `<Reference>` (compile-time). Result: no NetloadV2 copy;
   both dev and prod load the interop from Appload — one source of truth.

Cross-machine note: Appload is the shared `X:\` drive with an identical path on
every machine, so a recorded absolute Appload dir resolves for all users. If a
future external ref lived at a per-machine path this would need a variable — out
of scope now.
</proposed-design>

<resolved-decisions>
Decided by the user 2026-07-10 (revdiff):
1. Schema — `assemblyLocations` name→dir map (backward-compatible). CONFIRMED.
2. Discovery — csproj `<Reference><HintPath>` XML parse. CONFIRMED.
3. NSLOAD — KEEP SEPARATE. Mirror the DevReload logic into NSLOAD; do NOT dedupe
   onto shared BuildCore. (Accepts the drift risk in exchange for repo independence.)
4. Scope — GENERALIZE: the dialog surfaces ANY external (HintPath) reference as a
   loadable candidate, not just NorsynObjectsInterop.
5. Sequencing — make DevReload fully working FIRST; only then mirror into NSLOAD and
   flip the IU csproj (so production isn't broken before its loader can resolve from
   Appload).
</resolved-decisions>

<separate-issues-found>
  - [FIXED 2026-07-10] IU csproj `<Import>` used a relative path that resolved to a
    missing `H:\GitHub\DamgaardRI\...`; the projitems had also moved+renamed to
    `X:\GitHub\DamgaardRI\NorsynDrawingTools\managed\NorsynOnDemandLoadingSHARED\NorsynOnDemandLoadingSHARED.projitems`.
    Repointed to that absolute path (cross-drive ⇒ can't be relative). Verified via
    `dotnet msbuild -getProperty:TargetPath`. Same stale-path smell as `build.bat`;
    consider a shared `$(NorsynDrawingToolsRoot)` property to stop scattering it.
  - [FIXED 2026-07-10] `NSLOAD.PluginManager.LoadCore` was a drifted duplicate of
    `SharedAssemblyPreloader` missing the default-ALC guard — the guard was added as
    part of Phase 2 (kept a separate mirror per the decision, not deduped).
</separate-issues-found>

<implementation-status>
All code implemented + compiles (2026-07-10):
  - Phase 1 (DevReload): `SharedAssembliesFile` (+AssemblyLocations), new
    `CsprojReferenceScanner`, `SharedAssemblyPreloader` (external resolution),
    `SharedAssembliesViewModel`/`AssemblyItem` (external candidates + GetAssemblyLocations),
    `SharedAssembliesWindow.xaml` (⧉ external badge), `DevReloadViewModel`
    (scan + persist in dialog + PushToProduction), projitems registration.
    Builds Debug|x64: 0 warn / 0 err.
  - Phase 2 (NSLOAD): `SharedAssembliesConfig` (+AssemblyLocations),
    `PluginManager.LoadCore` (external resolution + IsLoadedInDefaultAlc guard).
    Builds: 0 err.
  - Phase 3 (IU): `IntersectUtilities.csproj` NorsynObjectsInterop Private=False,
    Ijwhost `<None>` removed. Builds; stale interop/Ijwhost purged from bin. Prod
    config `NetloadV2\...\IntersectUtilities\SharedAssemblies.Config.json` given the
    `assemblyLocations` → Appload entry.

REMAINING (needs live AutoCAD; do in THIS ORDER to avoid breaking production):
  1. Build+deploy new DevReload; in DevReload open IU → Shared, confirm
     NorsynObjectsInterop shows as ⧉ external, tick + C++/CLI, Save (writes the DEV
     SharedAssemblies.Config.json with assemblyLocations). Reload IU in dev → must
     load clean from Appload.
  2. Build+deploy new NSLOAD to production (it now understands assemblyLocations).
  3. ONLY THEN rebuild+redeploy IU with Private=False (removes the NetloadV2 private
     copy). Order matters: a no-copy IU under the OLD NSLOAD would fail to find the
     interop. With the prod config entry already in place, the NEW NSLOAD loads from
     Appload regardless.
</implementation-status>

<validation>
Runtime-validated 2026-07-10 in a live Civil 3D instance via the ACD-MCP C# REPL
(no production deploy):
  - FOUNDATION: `Assembly.LoadFrom(Appload\NorsynObjectsInterop.dll)` into the default
    ALC succeeds (no "procedure imported" error) and `NorsynContainer` resolves — the
    exact operation that failed this morning, now clean as a first-load.
  - DISCOVERY DEFECT FOUND + FIXED: the raw scanner surfaced ALL 22 HintPath refs,
    incl. 19 AutoCAD SDK framework DLLs (acdbmgd, AeccDbMgd, AcMPolygonMGD…) — dialog
    clutter + a footgun. First attempt (exclude already-loaded-in-default-ALC) proved
    UNRELIABLE (SDK assemblies demand-load lazily, so 5 slipped through). FIX:
    `DevReloadViewModel.SharedAssemblies` now drops refs under the AutoCAD install dir
    (`typeof(...Application).Assembly.Location` root) — deterministic. Re-validated:
    keeps exactly {DarkUI, NorsynObjectsInterop, NorsynProjectionProfileLabelInterop},
    drops 19 framework refs. DevReload rebuilds 0/0.

  END-TO-END PROVEN (2026-07-10): new DevReload + NSLOAD Release bundles deployed LOCALLY
  to %APPDATA%\Autodesk\ApplicationPlugins (per-user, this machine). Fresh Civil 3D →
  `INTERSECTUTIL` (NSLOAD load command) → NSLOAD preloaded NorsynObjectsInterop from
  `X:\...\Appload\2025\NorsynObjectsInterop.dll` (via the config's assemblyLocations) into
  the default ALC; IU loaded into ALC 'PluginIsolated' and bound to it; NorsynContainer
  resolves; NO "procedure imported" error; NO private-copy dependency. Feature works.

REMAINING = FLEET ROLLOUT ONLY (operational, needs Intune — not a unilateral copy):
  1. Distribute the new NSLOAD bundle to all users (Intune → their %APPDATA%\ApplicationPlugins).
  2. AFTER fleet NSLOAD is live, deploy the new IU (Private=False) to shared
     X:\...\NetloadV2\2025\IntersectUtilities and drop its local interop copy there.
  Order is load-bearing: shared IU-without-copy under an un-updated fleet NSLOAD breaks.
</validation>
