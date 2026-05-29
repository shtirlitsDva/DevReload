<worktree-shared-config-ux>

Making the `Shared` assemblies workflow usable on a freshly-selected
worktree/branch that has not been built yet. Implemented 2026-05-29.

<problem>
A plugin loads fine on `master` with a shared-DLL configuration
(`SharedAssemblies.Config.json`). A feature is developed in a git worktree; the
user switches the dropdown to the worktree branch and:

1. `Shared` fails — the worktree has no build folder yet, so the dialog refuses
   to open.
2. The user can't "just build", because the only build trigger (`Reload`) also
   *loads*, and loading with an empty/wrong shared config half-initialises the
   plugin → the session is wedged → AutoCAD restart.
</problem>

<root-cause>
Three defects behind the one symptom:

1. `SharedAssemblies()` hard-blocked on `Directory.Exists(buildDir)`.
2. The old resolver queried MSBuild on the (un-restored) worktree csproj and, on
   failure, **silently fell back to the main repo's build dir** — so a naive fix
   would let the user edit master's config believing it was the worktree's.
3. No "build without load" path existed to produce the worktree's DLLs safely.
</root-cause>

<decisions-locked>
Confirmed with the user via revdiff annotations 2026-05-29:

<d1-no-fallbacks>
NO FALLBACK PATHS. The silent fall-back-to-master-dir is deleted. Path resolution
either succeeds or fails loudly. Codified as rule-12 in global CLAUDE.md.
</d1-no-fallbacks>

<d2-build-first>
Force build-first. If the selected branch/config build dir is missing or empty,
`Shared` tells the user to build first via the Reload flyout's "Build only", then
returns. Because build-first guarantees a restored, built worktree, MSBuild
resolves the build dir reliably with no fallback needed.
</d2-build-first>

<d3-reload-split-button>
`Reload` is a split-button: click = Reload (build+load); `▾` flyout = "Build only"
(build, no load). "Build only" produces the DLLs so `Shared` can be configured.
</d3-reload-split-button>

<d4-copy-from-checks-presence>
`Copy from <branch>` copies only the config (the JSON selection), and applies
only entries whose DLL actually exists in THIS worktree's build dir. Missing ones
are reported in the dialog, not silently dropped.
</d4-copy-from-checks-presence>

<d5-no-load-guard-green-indicator>
The load path is NOT guarded — loading without a shared config is legitimate
(most plugins need none, and DevReload can't know per-branch requirements). The
knowledge stays with the user; the `Shared` button is **green-tinted** when the
current branch+config build dir has a config file, updating on branch/config
switch.
</d5-no-load-guard-green-indicator>

<d6-theme-extraction>
Option B: a real centralized `Theme.xaml` was extracted, inline styles migrated,
and the duplicated resolver smell consolidated. No smells left behind.
</d6-theme-extraction>
</decisions-locked>

<implementation>

<build-dir-resolution>
`GitWorktreeService.ResolveActiveCsproj(projectFilePath, activeWorktreePath)` —
maps the csproj into the active worktree, or returns it unchanged; THROWS if a
worktree is active but the repo root can't be resolved (no silent fallback).
`PluginManager.GetEffectiveCsprojPath` now delegates to it (smell-2 fixed).

`DevReloadService.ResolveBuildDir(projectFilePath, activeWorktreePath, config)` —
returns the build dir via MSBuild `TargetPath`, or null when unresolvable. NO
fallback. Used by the Shared command, Push-to-Production, and the green indicator.
</build-dir-resolution>

<shared-dialog>
`DevReloadViewModel.SharedAssemblies` resolves the build dir, enforces build-first
(missing/empty → "build first" message), seeds the dialog, and computes copy-from
sources via `DiscoverCopyFromSources` (other worktrees whose build dir holds a
config; derived by swapping the worktree root onto the current build dir — no
extra MSBuild queries).

`SharedAssembliesViewModel` gains `Sources`, `SelectedSource`, `CopyFromCommand`,
and `CopyStatus`. `CopyFrom` resets the list, applies the source config to DLLs
present here, and reports any skipped (missing) DLLs.
</shared-dialog>

<build-only>
`PluginManager.BuildOnly(pluginName)` builds the effective csproj and updates
`reg.DllPath` without `LoadCore`. `DevReloadViewModel.BuildOnlyPlugin` invokes it;
the panel's flyout "Build only" calls it from code-behind (a Popup can't reach the
root VM via FindAncestor) and closes the flyout.
</build-only>

<green-indicator>
`PluginItemViewModel.HasSharedConfig` (bound by `SharedBtn`'s DataTrigger) is
recomputed off the UI thread (MSBuild can spawn dotnet) in the ctor, on real
branch switch, on config toggle, and after a Shared save / Build only. The
worktree-change handler ignores no-op re-selections to avoid redundant queries.
</green-indicator>

<theme>
`src/DevReload/Themes/Theme.xaml` holds all brushes, button/control styles, the
DBG/REL toggle, the status dot, the split-button toggle, and the new
`SharedConfigBrush` + `SharedBtn`. Merged into `DevReloadPanel.xaml` and
`SharedAssembliesWindow.xaml` via a RELATIVE source (`../Themes/Theme.xaml`) —
pack://...;component URIs do not resolve when AutoCAD loads the assembly. Root
backgrounds use `DynamicResource` to avoid a parse-order race.
</theme>
</implementation>

<files-touched>
- NEW `src/DevReload/Themes/Theme.xaml`
- `src/DevReload/GitWorktreeService.cs` — `ResolveActiveCsproj`
- `src/DevReload/DevReloadService.cs` — `ResolveBuildDir`
- `src/DevReload/PluginManager.cs` — `BuildOnly`, `GetEffectiveCsprojPath` delegation
- `src/DevReload/ViewModels/DevReloadViewModel.cs` — Shared/Push rewrite,
  BuildOnly command, DiscoverCopyFromSources, HasSharedConfig wiring
- `src/DevReload/ViewModels/SharedAssembliesViewModel.cs` — sources + CopyFrom
- `src/DevReload/Views/DevReloadPanel.xaml` (+ .cs) — merged theme, split button, tint
- `src/DevReload/Views/SharedAssembliesWindow.xaml` — merged theme, copy-from row
</files-touched>

<verification>
`dotnet build src/DevReload/DevReload.csproj -c Debug -p:Platform=x64` →
0 warnings, 0 errors. Theme.xaml compiles as a Page and the relative
merged-dictionary path validates at markup-compile time. Runtime behaviour inside
AutoCAD (palette rendering, flyout, tint, dialog) still to be exercised live.
</verification>

</worktree-shared-config-ux>
