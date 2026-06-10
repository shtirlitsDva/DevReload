<overview>
RevitDevReload — the DevReload lifecycle (build-first-then-swap, stream-loaded plugins, palette
UI, agent pipe control) ported to Revit 2022–2025, merging the proven pieces of
chuongmep/RevitAddInManager (per-version multi-targeting, captured-ExternalCommandData
invocation, log window) with DevReload's resident plugin model.

Written autonomously by Claude (Fable 5) on 2026-06-10; reviewed design constraints come from
the conversation with the user that day.
</overview>

<version-strategy>
Installed Revits: 2022, 2023, 2024, 2025.

| Revit | TFM            | Loader                                            |
|-------|----------------|---------------------------------------------------|
| 2022  | net48          | LegacyPluginLoader (byte-load, no unload, leak-on-reload) |
| 2023  | net48          | LegacyPluginLoader                                |
| 2024  | net48          | LegacyPluginLoader                                |
| 2025  | net8.0-windows | AlcPluginLoader (collectible ALC, true unload)    |

Per-version SDK-style csproj `RevitDevReload.R22/23/24/25` + one shared project
`RevitDevReload.Shared.shproj` holding ALL code (the Revit-PCF-Exporter pattern, modernised:
SDK-style csproj so `dotnet build` works for every version). Defines: `REVIT2022`… plus
the loader switch is `#if NET8_0_OR_GREATER` (no custom define needed).
RevitAPI refs: `C:\Program Files\Autodesk\Revit {year}` overridable via `RevitPath{year}`
msbuild property.
</version-strategy>

<loaders>
AlcPluginLoader (net8): reuses the DevReload approach verbatim — collectible ALC, byte-load
DLL+PDB (no file locks), `AssemblyDependencyResolver` + plugin-dir fallback, shared-assembly
fall-through to default ALC reading `SharedAssemblies.Config.json`, unload + GC drain on
TearDown. RevitAPI*/AdWindows always fall through.

LegacyPluginLoader (net48): byte-load (`Assembly.Load(byte[])`) so files are never locked —
the stream-load idea applied to the framework runtime. Dependencies resolved via an
`AppDomain.AssemblyResolve` hook that byte-loads from the plugin's build dir. No unload
exists on net48: reload loads a fresh copy, old image stays resident (same trade
RevitAddInManager ships with). Old command instances are dropped; the registry always points
at the newest assembly.

Both implement `IPluginLoader { Load(dllPath) -> LoadedPlugin; TearDown(LoadedPlugin); }`
selected at compile time — one code path per runtime, no runtime branching.
</loaders>

<invocation-model>
Revit has no command registry (no Utils.AddCommand equivalent) and `ExternalCommandData` has
an internal ctor, so:

- The host registers ONE ribbon button (Add-Ins tab) at `IExternalApplication.OnStartup` →
  `DevReloadCommand : IExternalCommand` opens the manager window and captures its
  `ExternalCommandData`.
- Plugin commands (`IExternalCommand` implementations discovered by reflection in the loaded
  assembly) are listed per plugin card in the UI and invoked through a single
  `ExternalEvent`/`IExternalEventHandler` using the captured ExternalCommandData —
  the RevitAddInManager-proven pattern. Execution happens in valid API context.
- Optional resident hook: if the plugin assembly contains an `IExternalApplication`, its
  OnStartup/OnShutdown are invoked with the `UIControlledApplication` captured at host
  startup (best effort; ribbon/DockablePane registration from late-loaded plugins is a Revit
  startup-only restriction and will throw — documented limitation).
- No NoCommands marker needed: Revit never auto-registers commands on assembly load.
</invocation-model>

<shared-code>
New `src/Shared/DevReload.BuildCore` shproj — sources compiled into BOTH the AutoCAD plugin
and all Revit projects (and net48-compatible):

- `BuildService` (was DevReloadService): `dotnet build` for SDK-style csproj; falls back to
  vswhere-located `MSBuild.exe` for old-style csproj (the user's existing Revit plugins, e.g.
  revit-pcf-exporter-2022, are old-style). TargetPath query likewise.
- `GitWorktreeService` — unchanged, host-agnostic.
- `SharedAssembliesFile` — unchanged.
- `IsolatedPluginContext` — unchanged mechanics; shared-name fall-through is constructor
  input (compiled only into net8 hosts via `#if NET8_0_OR_GREATER` guard in the file).

Theme.xaml moves to WpfSHARED so the Revit window and the AutoCAD palette share one theme.
AutoCAD call sites updated to the new names — no aliases, no duplicate paths.
</shared-code>

<config>
`%APPDATA%\RevitDevReload\plugins.R{2022|2023|2024|2025}.json` — one file per Revit major
version because plugin builds are per-version (per-version csprojs are the norm in the
user's repos). Entry: name, projectFilePath, dllPath, buildConfiguration,
activeWorktreePath, loadOnStartup. Loader: `RevitPluginConfig` (TDD).
</config>

<ui>
One modeless WPF window (Revit-owned via WindowInteropHelper), DevReload palette look
(same Theme.xaml, same card layout): per-plugin card = name + load state dot, DBG/config
toggle, Reload split-button, Unload, worktree combo, Auto-load checkbox, expandable
command list (click runs command via ExternalEvent — replaces AutoCAD's command line).
Bottom: collapsible log pane (RevitAddInManager's log window idea) fed by an in-memory
log sink that also backs the pipe `get_log` call. MVVM via CommunityToolkit.Mvvm
(netstandard2.0 — works on net48).
</ui>

<pipe-rpc>
In-process named-pipe server `RevitDevReload.{pid}` speaking newline-delimited JSON
(request: `{"id":1,"cmd":"...","args":{...}}`; response `{"id":1,"ok":true,...}`), running on
a background thread, dispatching mutating ops to the UI thread + ExternalEvent. Commands:
`ping`, `get_state`, `register_plugin`, `load`, `unload`, `reload`, `run_command`,
`get_log`, `quit`. Implemented in the shproj (net48-compatible: System.Text.Json NuGet).
This is intentionally a minimal line protocol, not the full MCP host (Acad.Rpc.Core is
net8-only and lives in the bridge/CLI layer instead).
</pipe-rpc>

<cli>
`src/Revit/Revit.Cli` (net8 exe) — the test/agent driver, mirroring acad_start/send ideas:
- `list-installs` — HKLM\SOFTWARE\Autodesk\Revit enumeration + Program Files probe
- `deploy --rvt 2024 [--config Debug]` — writes the .addin manifest into
  %APPDATA%\Autodesk\Revit\Addins\{year} pointing at the repo build output
- `start --rvt 2024 [--watch-dialogs]` — launches Revit.exe, optional UIA watcher
- `wait-pipe --rvt 2024 [--timeout 300]` — polls for the plugin pipe of any Revit pid
- `send --rvt 2024 --cmd '{...}'` — one-shot pipe request, prints JSON response
- `stop --rvt 2024` — pipe `quit`, falls back to process kill
Dialog watcher: FlaUI.UIA3 — finds Revit-pid-owned dialogs whose buttons match
"Always Load"/"Load Once"/"Load"/"Yes" for the unsigned-add-in security prompt and trusted
location prompts, clicks the allow option, logs every click. UIA element discovery (the
AutoHotkey way), not pixel vision.
</cli>

<testing>
`tests/RevitDevReload.Tests` (net8.0-windows;net48 dual-target, xunit):
- RevitPluginConfig round-trip + per-version file isolation (both TFMs)
- AddinManifestWriter output shape (both TFMs)
- Command discovery against a synthetic assembly (compiled test fixture)
- Wire protocol framing/dispatch of the pipe server (loopback pipe, no Revit)
- net8 only: AlcPluginLoader load/unload collectibility on a fixture DLL
- net48 only: LegacyPluginLoader byte-load + AssemblyResolve of fixture dependency
E2E (manual-ish, scripted via Revit.Cli): deploy → start → wait-pipe → register example
plugin → load → run_command (writes marker file) → reload → unload (R25) → quit. Example
plugin: `example/Revit.Example.Plugin` (R24 + R25 csprojs).
</testing>
