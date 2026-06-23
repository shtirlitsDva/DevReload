---
name: acd-agentic-dev
description: Agentic development loop for AutoCAD/Civil 3D .NET plugins, driven entirely through DevReload's MCP tools plus the ACD-MCP script surface. Activates when the agent is asked to fix, refactor, or extend an AutoCAD/Civil 3D plugin AND the `acad_*` / `devreload_*` MCP tool surfaces are reachable. The skill is the LOOP, the TOOL INVENTORY, the TOOL-SURFACE LIFECYCLE, and how DevReload and ACD-MCP fit together — nothing else.
when_to_use: User asks the agent to do development work on an AutoCAD/Civil 3D plugin, and the MCP host (Acad.Rpc.Bridge → the in-AutoCAD pipe) is connected. The agent owns the full cycle: spin up AutoCAD, load DevReload + the ACD-MCP plugin, register/load the plugin under development, write a failing test, ship the smallest fix, live-verify with `acad_send_command` + `autocad_script_execute`, iterate until a stated success criterion holds. No human in the loop.
---

<read-this-first>
Two MCP surfaces cooperate in this loop. Confusing them is the #1 reason agents go blind:

- **DevReload** drives the AutoCAD *process* and the *plugin lifecycle*: start AutoCAD, register/build/hot-reload your plugin, load/unload it. Tools: `acad_*` and `devreload_*`.
- **ACD-MCP** is a separate MCP server that runs a **live C# script session inside the same AutoCAD**. It is your *eyes and hands inside the drawing*: create test entities, query model state, assert on results. Tools: `autocad_script_execute`, `autocad_script_propose`, `autocad_batch_*`. **It is itself a DevReload-managed plugin (`Acd.Mcp`) that you must LOAD before its tools work** — see `<acd-mcp-is-your-eyes-and-hands>`.

The loop is: **DevReload reloads your code → ACD-MCP verifies what the code did.** You need both. If you only know DevReload, you can build and load but cannot *observe* the drawing; if you only know ACD-MCP, you cannot reload the plugin you are editing.

- **UI automation (`ui_*`)** is part of the DevReload host (same bridge), for when the thing under test is a *UI*, not just drawing state: see/drive WPF palettes at the ViewModel level, OK native modal dialogs, synthesize mouse for jigs/grips, and take inline screenshots. See `<ui-group>`.

**Before you do anything, read `<tool-surface-comes-up-in-phases>`.** Your tool catalog at session start is INCOMPLETE on purpose — `devreload_*` tools do not exist until AutoCAD is up. Agents that don't know this conclude "the MCP is broken" or invent throwaway copies. Don't.
</read-this-first>

<tool-surface-comes-up-in-phases>
**Your MCP tool catalog grows in three phases. Do not panic when a tool "isn't there yet" — bring up the phase that publishes it.**

| Phase | Trigger | Tools that become available |
|---|---|---|
| 0 — cold | session start, no AutoCAD running | `acad_*` (DevReload bridge, process control) + the static `acd-mcp` server tools (`autocad_script_*`, `autocad_batch_*`) appear in the catalog but **fail at call time** until phase 2. |
| 1 — pipe up | `acad_start` → DevReload loads in AutoCAD → `acad_wait_pipe` succeeds | `devreload_*` (reload, register, list_plugins, …) — published dynamically by the bridge once it connects to the in-AutoCAD pipe. |
| 2 — plugin loaded | `devreload_load_plugin("Acd.Mcp")` (+ a pump, see gotcha) | `autocad_script_execute` & friends actually *execute* against the live drawing. |

**Why `devreload_*` is missing at phase 0.** The DevReload bridge exposes a *static* set of `acad_*` process-control tools always. The `devreload_*` plugin-lifecycle tools live *inside AutoCAD* and are forwarded to your client only after the bridge connects to the in-AutoCAD RPC pipe. With no AutoCAD running, there is no pipe, so there are no `devreload_*` tools. This is expected, not a fault.

**The bridge waits for the pipe for as long as it takes.** Once `acad_start`/`acad_attach` binds an instance, the bridge keeps retrying the pipe connection until it succeeds (or the process exits) — a cold Civil 3D or a startup dialog that delays the pipe by minutes is fine; when the pipe finally appears the bridge connects and publishes `devreload_*` automatically. You should NOT have to do anything special: `acad_start` → `acad_wait_pipe` → the `devreload_*` tools show up.

> **If the tools still aren't in your catalog a few seconds after `acad_wait_pipe` succeeded**, the gap is on the MCP-client side — it hasn't refetched after the bridge's `tools/list_changed` notification. `acad_detach` then `acad_attach <pid>` nudges the client to refresh. (This is a client-refresh quirk, not the bridge giving up.)
</tool-surface-comes-up-in-phases>

<the-loop>
One pass per problem. Each step is a checkpoint — never skip diagnosis to rush to a fix.

0. **Bring the pipe up.** `acad_start(flavor)` → `acad_wait_pipe`. Confirm `devreload_*` tools are now in your catalog (if not, re-bind — see `<tool-surface-comes-up-in-phases>`). Then `devreload_load_plugin("Acd.Mcp")` so you can verify your work, and sanity-check it with `autocad_script_execute("Doc.Name")`.
1. **State the success criterion in ONE sentence** before touching code. Something observable through `acad_get_state` or — far more powerful — an `autocad_script_execute` query ("after `MYCMD` on `crashtest-01.dwg`, modelspace contains exactly one Polyline with 4 vertices"). Without a concrete, queryable criterion, "done" drifts.
2. **Analyze.** Read the cited code at the cited line numbers. Verify the code still matches the brief — codebases drift between issue-filing and issue-fixing. If it doesn't match, restate the brief in your own words before planning.
3. **Plan in modules.** Name the file(s) and module boundaries you'll change. If the right change crosses a boundary you didn't expect, surface it before editing (rule 7).
4. **TDD — red.** Write the failing test FIRST.
   - Pure-.NET logic → an xUnit test in a `tests/<Project>.Tests/` project that link-includes the source files (`<Compile Include="..\..\src\Foo\Bar.cs" Link="Sut\Bar.cs" />`) to avoid pulling AutoCAD references into the test build.
   - AutoCAD-bound behaviour → a "live test": a deterministic sequence of `acad_send_command` + `autocad_script_execute` calls that exercises the command and asserts on drawing state. Live tests are first-class regression artifacts; save reusable ones as ACD-MCP scripts (`%APPDATA%\Acd.Mcp\scripts\script\<name>.csx`) and/or commit them.
   Run the test, confirm it fails for the RIGHT REASON. A test that fails on missing types is not a red test.
5. **TDD — green.** Smallest change that makes the test pass. Do not refactor in this step.
6. **Live-verify.** `devreload_reload(name)` to build + hot-reload, then drive the new code (`acad_send_command(...)` for commands), then **assert with `autocad_script_execute(...)`** that the success criterion from step 1 holds.
   - `devreload_reload` returns the **entire `dotnet build` log** even on success — it can be tens of KB and may trip your client's result-size cap (see gotcha 11). Read only `success`, `loaded`, `commandCount`. The `build.log` matters **only when `build.success == false`** — then read it and fix the build before continuing.
   - If the command runs but the `autocad_script_execute` assertion fails, the test from step 4 is wrong OR the fix is wrong. Diagnose which.
7. **Refactor.** Tests still green. Restructure for clarity. After each meaningful refactor: `devreload_reload` + re-assert. The reload is sub-10-second — abuse it.
8. **Stop on the criterion.** When step 1's criterion is observably met AND no new `EXCEPTION`-class log entries appeared, stop. Do NOT bundle "while I'm here" cleanups — surface them as code-smell reports (rule 3).

If at any step the proximate-cause hypothesis from the brief proves wrong, return to step 2 with the new evidence (rule 4).
</the-loop>

<acd-mcp-is-your-eyes-and-hands>
**ACD-MCP is how you observe and manipulate the drawing. In this loop it is not optional — it is how you write tests and verify fixes.**

**What it is.** A separate MCP server (`Acd.Mcp.Bridge.exe`, stdio) that talks over its own named pipe (`acd-mcp-<pid>`) to a plugin (`Acd.Mcp.dll`) running inside AutoCAD. `autocad_script_execute` compiles a C# snippet via Roslyn and runs it on AutoCAD's main thread under `Doc.LockDocument()`. State persists between calls — it's a session, not a one-shot. Globals: `Doc`, `Db`, `Ed`, `CivilDoc` (or null), `Acd`.

**It must be LOADED, and you load it through DevReload.** `Acd.Mcp` is registered as a DevReload plugin but is **not** autoloaded. Its `autocad_script_*` tools appear in your catalog from phase 0 (the stdio server is always there) but they **fail at call time until the plugin is loaded inside AutoCAD**:

```
devreload_load_plugin("Acd.Mcp")          # one line — this is the integral step
autocad_script_execute("Doc.Name")        # confirm it answers
```

If `Acd.Mcp` is not in `devreload_list_plugins`, register it once with `devreload_register_new_plugin(projectFilePath="...\\Acd.Mcp.csproj", commandPrefix="ACDMCP")`.

**Load the ACD-MCP flavor skill before writing snippets.** Run `/acd-mcp:script` (single drawing) or `/acd-mcp:batch` (many .dwg files) FIRST. Those skills carry conventions that prevent silent failures you *will* hit otherwise:
- **Block-form `using` only.** Top-level `using var tr = ...;` is parsed as a using-*directive* and fails to compile. Write `using (var tr = ...) { ... }`.
- Trailing-expression return + auto-return gotchas, return-value serialization etiquette, propose-vs-execute, the mirror-before-propose rule, `{"$unsupported":"T"}` → `/acd-mcp:add-dto`.

**Returned values are auto-projected through DTOs — return entities, don't hand-roll JSON.** ACD-MCP serializes every value you return through a registered DTO, and ships maintained DTOs for the common AutoCAD types (`Line`, `Circle`, `Arc`, `Polyline`, `BlockReference`, `MText`, `DBText`, `Hatch`, `DBPoint`, … plus `Point3d`/`Vector3d`/`ObjectId`/`Handle`/`Extents3d`). When you assert on drawing state, **return the entity (or a `List<T>`/`IEnumerable<T>` of them) directly** and read the rich, consistent JSON in `returnValueJson` — do NOT type `new { ... }` anonymous objects to re-implement a projection the DTO already gives you (that shape drifts call-to-call and makes assertions brittle). A `{"$unsupported":"T"}` marker means no DTO exists for type `T`; author one via `/acd-mcp:add-dto` if you'll query it more than once. Full contract: `/acd-mcp:script` `<serialization-etiquette>`.

**Use it for both halves of TDD:**
- *Arrange* a test fixture: `autocad_script_execute` to draw the entities your command consumes, set layers, etc.
- *Assert*: `autocad_script_execute` to count/inspect what your command produced (the radius-5-circle-at-origin check, the "exactly one Polyline" check).

**Concurrent connections, like DevReload.** The in-AutoCAD ACD-MCP pipe accepts several bridge connections at once; each snippet still runs serialized on AutoCAD's main thread under `Doc.LockDocument()`, so concurrent callers queue rather than collide. If `autocad_script_execute` misbehaves, read the `acd-mcp://status` resource for a health snapshot — it answers even when tool calls don't, because it's served bridge-side.

**Verification surfaces, in order of power:** `autocad_script_execute` (arbitrary query — default) > `acad_get_state` (quiescent/active-doc only) > command-line output (you cannot read it back through `acad_send_command`; don't rely on it for assertions).
</acd-mcp-is-your-eyes-and-hands>

<scaffolding-a-new-plugin-project>
When the task is a brand-new plugin (or a throwaway repro) at an arbitrary path — not an existing project in the DevReload solution — you must set up its `.csproj` yourself. DevReload does not template this. Minimal reload-ready csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Platforms>x64</Platforms>
    <Nullable>enable</Nullable>
    <DebugType>portable</DebugType>      <!-- line-accurate stack traces under stream-load (gotcha 6) -->
    <DebugSymbols>true</DebugSymbols>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
    <AutoCADPath Condition="'$(AutoCADPath)' == ''">C:\Program Files\Autodesk\AutoCAD 2025</AutoCADPath>
  </PropertyGroup>
  <ItemGroup>
    <!-- Provided by the host process — Private=False so they're never copied to output. -->
    <Reference Include="accoremgd"><HintPath>$(AutoCADPath)\accoremgd.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="acdbmgd"><HintPath>$(AutoCADPath)\acdbmgd.dll</HintPath><Private>False</Private></Reference>
    <Reference Include="acmgd"><HintPath>$(AutoCADPath)\acmgd.dll</HintPath><Private>False</Private></Reference>
    <!-- Civil 3D types: add AecBaseMgd / AeccDbMgd / AeccPressurePipesMgd from $(AutoCADPath)\C3D as needed. -->
  </ItemGroup>
</Project>
```

The code shape (NoCommands marker, `IExtensionApplication`, removable commands) is in `<reload-safe-plugin-shape>` — follow it from the first commit.

**Then register by csproj, and let DevReload derive the rest.** `devreload_register_new_plugin(projectFilePath="...\\Foo.csproj")`. The plugin **name is the csproj filename** and the **dllPath is resolved via MSBuild `TargetPath`** — you do NOT pass either. Build the project once first (or just call `devreload_reload`, which builds). Do not hardcode an output path: with `-p:Platform=x64` and no explicit `OutputPath`, output lands in `bin\x64\Debug\`, not `bin\Debug\` — another reason to let `register`/`reload` resolve it.
</scaffolding-a-new-plugin-project>

<mcp-tools>
The tools the two MCP surfaces expose. These are the agent's primary interface — more reliable and reproducible than asking the user to click in the palette.

<acad-group>
Lifecycle and command surface for the AutoCAD process. `acad_*` prefix. **Available from phase 0** (static bridge tools).

| Tool | When to call |
|---|---|
| `acad_locate_install` | First call when flavor/install isn't known. Returns AutoCAD/Civil3D/Plant3D/etc. installs discovered from the registry. |
| `acad_start(flavor, startupCommands?, drawingPath?, ...)` | Spin up a fresh AutoCAD/Civil 3D. Auto-binds the bridge to the launched pid. **`startupCommands` is usually unnecessary**: if DevReload is installed as an autoload bundle (`%APPDATA%\Autodesk\ApplicationPlugins\DevReload.bundle`, the normal setup) it loads on every start and the pipe comes up on its own. Pass `startupCommands="FILEDIA\n0\nNETLOAD\n<bundle-path>\nFILEDIA\n1\n"` ONLY when the bundle is not installed. |
| `acad_wait_pipe(timeoutSeconds=120, pid?)` | PRIMARY readiness gate. Returns when the instance's RPC pipe is listening — pid-specific, works with no document open. Gate every new instance on its own pipe. If it times out, see `<startup-can-stall>`. Cap at 120 s — a longer wait with no progress is a hang, not patience. |
| `acad_wait_quiescent(pid?)` | Return once the target instance is quiescent. Pid-routable; returns promptly on every instance. For cold-start readiness gate on `acad_wait_pipe` instead. |
| `acad_list_instances` | See running AutoCAD processes, each one's pipe availability, and which one the bridge is bound to. Cheap first probe whenever tools seem offline. |
| `acad_attach(pid)` / `acad_detach` | Bind / release. **`detach` then `attach <pid>` is the canonical recovery to force the bridge to (re)connect the pipe and re-publish `devreload_*` tools** (see `<tool-surface-comes-up-in-phases>`). |
| `acad_get_state(pid?)` | State snapshot of the target instance: `IsQuiescent`, `HasActiveDocument`, `ActiveDocumentName`, open-document count. Pid-routable. |
| `acad_send_command(cmd, pid?)` | BLOCKING — runs an AutoCAD command on the target instance and returns when it finishes. Pid-routable. Tokens split on whitespace/newlines (e.g. `"TWCIRCLE"` or `"._CIRCLE 0,0 5"`). |
| `acad_post_command(cmd)` | NON-BLOCKING — queues for the next pump. Only when fire-and-forget is required; always pair with `acad_wait_quiescent` if you need to observe the result. |
| `acad_open_drawing(path, readOnly?)` | Open a .dwg. |
| `acad_new_drawing(templatePath?)` | Empty drawing for test runs. |
| `acad_close_active_drawing(saveChanges=false)` | Default `false` — agentic test drives should not persist drawing state. |
| `acad_quit(pid?)` | End an instance's process. **Call at end of session** to free resources. A Drawing Recovery palette on the *next* start is the normal aftermath of a process-end — routine, not a failure; clear it per `<startup-can-stall>`. |
</acad-group>

<devreload-group>
Plugin lifecycle, build, and configuration. `devreload_*` prefix. **Available only from phase 1** (after the pipe is up and the bridge has connected — re-bind if missing).

| Tool | When to call |
|---|---|
| `devreload_list_plugins` | THE single source of truth for what's registered and what's loaded. Filter the response — do not use three separate query tools. |
| `devreload_register_new_plugin(projectFilePath, buildConfiguration?, commandPrefix?, loadOnStartup?)` | One-time: add a plugin to `plugins.json`. **No `name` and no `dllPath` parameters** — name = csproj filename, dllPath = MSBuild `TargetPath`. Project must have been restored/built at least once (or just call `devreload_reload`, which builds). `commandPrefix` generates the `{prefix}LOAD/DEV/UNLOAD` AutoCAD commands; it does NOT prefix your plugin's own `[CommandMethod]` names. |
| `devreload_reload(name)` | THE inner-loop tool. Builds via `dotnet build`, then stream-loads the new DLL into a fresh collectible ALC. Returns `success`/`loaded`/`commandCount` plus a full `build` log. Build-first-then-swap: the old plugin keeps running if the build fails. **On success ignore the log; on `build.success == false` read it.** |
| `devreload_load_plugin(name)` | Load without rebuilding. **This is how you bring up `Acd.Mcp`.** Use otherwise only when the DLL on disk is known to be the desired one. |
| `devreload_unload_plugin(name)` / `devreload_unload_all` | Tear down one / every loaded plugin. `unload_all` is the recovery for a wedged state. |
| `devreload_unregister(name)` | Permanently remove from `plugins.json`. Use to clean up throwaway plugins you registered. |
| `devreload_get_assembly_info(name)` | Confirm which DLL is actually loaded (path, version, last-write). Use when a reload doesn't seem to have picked up an edit. |
| `devreload_list_tools` | Every MCP tool currently registered with the in-AutoCAD host, grouped by source assembly. Run after `devreload_reload` to verify new `[AcadRpcTool]` methods are exposed. |
| `devreload_list_configurations(name)` / `devreload_update_build_configuration(name, cfg)` | Discover the project's declared configurations (Debug/Release/custom like IALCD/IALCR), then switch which one the next reload builds. Persisted. |
| `devreload_update_active_worktree(name, worktreePath?)` | Point a registered plugin at a worktree. Pass `null` to revert to the main checkout. |
| `devreload_build_project(csprojPath, config)` | Direct `dotnet build`, no load step. Useful for CI-style verification or sanity-building dependencies. |
| `devreload_list_worktrees(repoRoot)` | Enumerate git worktrees + branches. |
| `devreload_read_shared_assemblies(buildDir)` / `devreload_write_shared_assemblies(...)` | Read or write `SharedAssemblies.Config.json` — controls which DLLs load into the default ALC (required for WPF XAML type resolution and any type that must cross the plugin/host boundary). |
</devreload-group>

<acd-mcp-group>
The script/verification surface. Separate MCP server; tools present from phase 0 but only **execute** after `devreload_load_plugin("Acd.Mcp")` (phase 2). Load `/acd-mcp:script` or `/acd-mcp:batch` for the full contract before calling.

| Tool | When to call |
|---|---|
| `autocad_script_execute(code, timeout_ms?)` | Run a C# snippet against the active drawing NOW. Your default for arranging fixtures and asserting results. |
| `autocad_script_propose(code)` | Stage a snippet into the in-AutoCAD SCRIPT editor for the user to review/run (auto-opens the palette). |
| `autocad_batch_propose_script` / `autocad_batch_run_test` / `autocad_batch_list_files` | Multi-file edits across a folder of .dwg. See `/acd-mcp:batch`. |
| `autocad_get_selection` | Read the current pickfirst selection. |
| resource `acd-mcp://status` | Health snapshot (per-handler ready/degraded). Answers even when tool calls fail — use it to triage. |
</acd-mcp-group>

<ui-group>
UI-automation surface for testing plugin UIs and driving native dialogs. `ui_*` prefix, group `"ui"`. Compiled INTO the DevReload host (`DevReload.dll`), so — like `devreload_*` — these appear from **phase 1** (once the in-AutoCAD pipe is up) and are **pid-addressable** (optional `pid`, omit for the bound default). They exist because `acad_send_command` can drive a *command* but cannot *see* a WPF palette, OK a native modal dialog with no .NET API, synthesize a jig drag, or screenshot a region.

**Capability 1 — see & interact with WPF plugin UI, ViewModel-aware.** `[RunOnAcadMainThread]`.
| Tool | When to call |
|---|---|
| `ui_list_surfaces` | List WPF surfaces (palettes/windows) hosted in the process: hwnd, root type, size. |
| `ui_snapshot(hwnd?, includeViewModel?, maxDepth?)` | Element tree (type, x:Name, AutomationId, text, value, enabled, visible, screen-px bounds) PLUS a reflection dump of the bound ViewModel — assert on the ViewModel, not pixels. Each node's `id` (`"0/3/1"`) is the element ref. Re-read after every action; the tree mutates. |
| `ui_invoke / ui_set_value / ui_toggle / ui_select(elementRef, hwnd?)` | Drive a control via its UI Automation peer (Invoke / Value / Toggle / SelectionItem). `elementRef` = tree-path id, x:Name, or AutomationId. |

**Capability 2 — drive native (Win32/MFC) dialogs with no .NET API** (e.g. COGO-point projection). Run OFF the main thread on purpose, so they work WHILE a modal stalls the idle pump.
| Tool | When to call |
|---|---|
| `ui_list_windows` | Top-level windows (main frame + any modal): hwnd, title, class, bounds, visible, enabled. A modal disables the main frame — `enabled:false` on it is how you spot one. |
| `ui_dialog_buttons(hwnd)` | Enumerate a dialog's buttons recursively (finds nested file-dialog Open/Cancel too) with labels + bounds. |
| `ui_dialog_click(hwnd, label)` | **Headless** click — posts `BM_CLICK` to the button: no cursor, no foreground, works on a background instance, multi-instance safe. Prefer this to dismiss any dialog. |
| `ui_press_key(key, hwnd?)` | enter/escape/tab/space/yes/no. Pass the dialog `hwnd` to foreground+focus it first (a real keystroke needs focus). |

**Capability 3 — synthesize mouse input** for jigs / grips / OSNAP / real-time drag. SendInput-based.
| Tool | When to call |
|---|---|
| `ui_mouse_move / ui_click / ui_drag` | Physical-pixel cursor move / click / button-held drag. |
| `ui_canvas_capture_view(pid?)` | Capture the live view (center, height, twist, drawing-area screen rect) so WCS gesture tools can map WCS→pixel. Call while quiescent, BEFORE a jig. **Required before the canvas tools below.** |
| `ui_canvas_click(wcsX, wcsY) / ui_canvas_drag(fromWcs, toWcs, ...)` | Click / button-held drag at WCS points; OSNAP still snaps to real geometry. |

**Capability 4 — granular vision.** Screenshots return the PNG **inline** (no temp file) plus size metadata.
| Tool | When to call |
|---|---|
| `ui_screenshot_window(hwnd)` / `ui_screenshot_region(x,y,w,h)` / `ui_screenshot_element(elementRef)` / `ui_screenshot_wcs_box(min/max + padding)` | Capture a window (PrintWindow — occlusion-proof, renders even when background), an arbitrary region, a WPF element's bounds, or a canvas WCS box (needs a prior `ui_canvas_capture_view`). |
| `ui_canvas_drag_capture(fromWcs, toWcs, steps?, stepDelayMs?, captureStride?)` | Drive a canvas drag while capturing a frame every `captureStride` samples — watch a jig animate frame by frame; returns the ordered frames inline. |

**Two behavioural classes — know which you're in:**
- **In-process (cap 1, cap 4, `ui_list_windows`, `ui_dialog_*`) — per-instance, parallel-safe.** They act inside the targeted process: its WPF tree, its HWNDs, its PrintWindow render. PrintWindow captures occluded/background windows, so you can snapshot instance B while A is foreground. Fully concurrent across pids; `ui_dialog_click` is headless.
- **Synthetic input (cap 3 + `ui_mouse_*`/`ui_click`/`ui_drag` + `ui_press_key`) — pid-addressable but serialized on ONE shared cursor.** SendInput/keybd_event are session-global: one cursor, one foreground per desktop. `pid` selects which instance to foreground + compute coordinates for (`ui_canvas_*` call `Foreground.Ensure`), but two instances cannot receive synthetic input at once, and it cannot be confined to a background instance or another virtual desktop. Drive canvas gestures one instance at a time. (True parallelism here needs OS-level session isolation — separate logged-in Windows/RDP sessions — not the plugin.)

**Gotchas specific to this surface:**
- **A modal dialog blocks the cap-1 main-thread tools.** `ui_snapshot` et al. dispatch onto the idle pump, which a modal stalls. Rather than hang, the dispatcher detects the modal (main frame disabled) and **fails fast** with a message pointing you at the off-thread `ui_list_windows` / `ui_dialog_*` tools. Dismiss the dialog (`ui_dialog_click "Cancel"`), then retry the WPF tool.
- **Point-acquisition jigs (`ed.Drag` + `AcquirePoint`) vs the drag tools.** `ui_canvas_drag`/`_drag_capture` hold the button DOWN for the whole gesture — right for grips, window-select, and real-time drag-move, but a point jig commits on the first button event, killing the animation. Drive a point jig with `ui_mouse_move` ×N (to animate/stream coords) + `ui_canvas_click` (to commit). A jig that streams coordinates into a bound WPF control updates the palette live (it repaints on AutoCAD's jig message pump) — a good end-to-end interactive-dev regression target.
- **Inline screenshots can be large.** A full-canvas frame is a big base64 blob. Prefer `ui_screenshot_wcs_box`/`ui_screenshot_region`/a small window, and keep `ui_canvas_drag_capture` frame counts low (high `captureStride`, few `steps`).
</ui-group>
</mcp-tools>

<startup-can-stall>
**`acad_wait_pipe` timed out — but the process is alive. Before concluding anything is broken, check for a blocking startup UI.**

The DevReload pipe comes up when DevReload's `Initialize()` runs on AutoCAD's first idle. A modal dialog or a focus-stealing startup palette can suppress that idle, so the pipe never appears — `acad_wait_pipe` just times out, with no error.

The one that bites repeatedly: the **Drawing Recovery Manager** palette, shown when AutoCAD's previous run didn't shut down cleanly (e.g. a prior instance was killed, or the `acad_quit` fallback had to Process.Kill). It holds the pump and stalls the whole bring-up.

Triage when `acad_wait_pipe` times out:
1. `acad_list_instances` — is the pid alive with `pipeAvailable:false`? Process is up but the pipe never came up.
2. Check whether the process is *busy* (still loading) or *idle* (blocked): a fully-idle, responsive process that still has no pipe is blocked on UI, not loading. (Cold Civil 3D legitimately churns for 1–3 minutes first — give it that.)
3. Enumerate its top-level windows; if you see "Drawing Recovery" (or any dialog), close it (post `WM_CLOSE`, or have the user click it away).
4. After the blocker clears, the pipe appears within seconds and the bridge connects on its own (it never stops waiting for the pipe). If the `devreload_*` tools don't refresh into your client catalog, nudge it with `acad_detach`/`acad_attach` (see `<tool-surface-comes-up-in-phases>`).

`acad_quit` ends the process, so a Drawing Recovery palette on the next start is the norm, not a failure — just clear it per the steps above.
</startup-can-stall>

<driving-multiple-instances>
**Run as many Civil 3D instances as you like and target any of them by pid.** All control flows over each instance's own pipe (`acad-rpc-<pid>`), so every `acad_*` and `devreload_*` tool works on every instance, simultaneously and independently.

**Targeting:** every `acad_*` and `devreload_*` tool takes an optional `pid`.
- Pass `pid` to act on a specific instance: `acad_send_command("TWCIRCLE", pid=65072)`, `acad_get_state(pid=62392)`, `devreload_reload("MyPlugin", pid=62392)`.
- Omit `pid` to use the **bound default** instance — the one `acad_start` last launched, or the one you set with `acad_attach <pid>`.
- `acad_wait_pipe(pid=…)` is the readiness gate for each instance; gate every new instance on its own pipe before driving it.

**Isolation is per pid.** A command, a new drawing, or a plugin load/reload affects only the targeted instance; the others are untouched. Plugin load state is per instance too — `MyPlugin` can be loaded in pid A and not in pid B at the same time.

**A typical two-instance session:**
```
acad_start(Civil3D)               # → pid A, auto-bound
acad_wait_pipe(pid=A)
acad_start(Civil3D)               # → pid B, now bound
acad_wait_pipe(pid=B)
devreload_reload("MyPlugin", pid=A)
acad_send_command("MYCMD", pid=A)         # runs in A, blocks until done
acad_get_state(pid=B)                     # reads B independently — no hang
```

**Multiple agents:** each agent runs its own bridge. The in-AutoCAD pipe accepts several concurrent connections, so two agents can even drive the SAME instance — though the cleaner pattern is one instance per agent. There is no machine-wide single-owner limit; pid is the unit of addressing.

**acd-mcp is pid-addressable too.** Its `autocad_script_*` / `autocad_batch_*` tools take an optional `pid` — pass it to target a specific instance, omit it when only one instance has `Acd.Mcp` loaded. Read the `acd-mcp://status` resource if its tools misbehave:
- `PIPE_NOT_LISTENING` — the target's `acd-mcp-<pid>` pipe hasn't finished coming up yet (a few-second window right after `devreload_load_plugin("Acd.Mcp")`); retry once.
- `MULTIPLE_AUTOCAD_PLUGINS` — `Acd.Mcp` is loaded in two or more instances and you didn't pass `pid`; pass it (pids from `acad_list_instances`).
</driving-multiple-instances>

<reload-safe-plugin-shape>
A plugin that DevReload can reload cleanly has THREE invariants: command registration is removable, the assembly is unpinned by `Terminate()`, and cleanup state survives the dual-instance lifecycle. Miss any one and `devreload_reload` either throws `eDuplicateKey`, leaks the old DLL (old commands keep firing alongside the new ones), or silently keeps stale event handlers wired up.

When extending or fixing an existing plugin, AUDIT the plugin's entry class against this section before the first reload — a buggy `Terminate()` will silently corrupt every subsequent iteration of `<the-loop>` and make diagnosis hard. When generating a new plugin, follow this shape from the first commit.

<suppress-autocad-command-scan>
AutoCAD's `ExtensionLoader` permanently registers every `[CommandMethod]` it discovers on assembly load — there is no public API to unregister, so the second reload throws `eDuplicateKey`. Block the scan by pointing it at an empty marker class (canonical name: `NoCommands`):

```csharp
[assembly: CommandClass(typeof(MyNamespace.NoCommands))]

namespace MyNamespace
{
    public class NoCommands { }
}
```

With this assembly attribute present, AutoCAD scans ONLY `NoCommands` and finds zero commands. DevReload's `CommandRegistrar` then enumerates the assembly's exported types itself and registers commands via the removable `Utils.AddCommand` path. Apply this unconditionally — plugins built for this system are always loaded through DevReload (both during development and at deployment); there is no `NETLOAD`-only release configuration to keep working.

Symptom of missing suppression: first `devreload_reload` works; second one fails with `eDuplicateKey` naming one of the plugin's own commands.
</suppress-autocad-command-scan>

<terminate-must-unpin-everything>
The collectible ALC unloads only when nothing in the default ALC still references it. Anything the plugin handed to AutoCAD — palette windows, event handlers, overrules, transient graphics, document-level idle/quiescent hooks — roots the assembly and prevents unload. The OLD plugin then keeps running alongside the NEW one: every event fires through both old and new delegates, palettes and overrules from the old build stay alive, and any timer/callback the old code armed keeps firing into dead objects.

`Terminate()` MUST release every such reference. Concrete checklist:

- **PaletteSets**: `Close()` then `Dispose()` then null out the static field. A live `PaletteSet` is rooted by AutoCAD's window manager.
- **Event subscriptions**: every `+=` needs the matching `-=` before `Terminate()` returns. Use `AcadEventManager` (see below) — never rely on manual unsubscribe lists.
- **Overrules** (`DrawableOverrule`, `ObjectOverrule`, etc.): call `Overrule.RemoveOverrule(...)` then `Dispose()`.
- **Transient graphics**: `TransientManager.CurrentTransientManager.EraseTransient(...)` for every entity you added.
- **Static caches holding `DBObject`/`Document`/`Editor` references**: clear them.

When in doubt whether something pins the assembly: ask "does AutoCAD hold a delegate or COM reference to anything in my DLL after Initialize returns?" If yes, that reference must be released in `Terminate()`.

**Why commands are NOT a useful symptom.** AutoCAD's command dispatcher resolves a command name to its LATEST registration — load a second assembly with the same `[CommandMethod("MYCMD")]` and `MYCMD` runs the new one, regardless of whether the old DLL is still pinned. So if you ship a code change and `acad_send_command("MYCMD\n")` produces the new behaviour, that proves NOTHING about whether `Terminate()` unpinned the old assembly. Use events, palette visibility, and memory growth as the diagnostic signals instead. Confirm the loaded DLL via `devreload_get_assembly_info`; if its timestamp matches the just-built one, the bug is in `Terminate()`, not in the build.
</terminate-must-unpin-everything>

<use-static-fields-for-cleanup-state>
On assembly load, AutoCAD's `ExtensionLoader` scans for `IExtensionApplication` implementations, instantiates each, and calls `Initialize()`. DevReload, on the unload path, holds its own instance of the same class (created via `Activator.CreateInstance` so it can hold a typed reference) and calls `Terminate()` on THAT one. **The two instances are different objects** — instance fields written in `Initialize()` are invisible to `Terminate()`. (`[assembly: ExtensionApplication]` is optional; AutoCAD finds the type via the interface scan regardless.)

The workaround is mechanical: put every piece of cleanup state in a `static` field, and `Initialize()` / `Terminate()` can do as much wiring/teardown as the plugin needs.

```csharp
public class MyPlugin : IExtensionApplication
{
    private static PaletteSet? _palette;
    internal static AcadEventManager? Events { get; private set; }

    public void Initialize()
    {
        Events = new AcadEventManager();
        // ...wire services, register lifecycle hooks, hand
        // dependencies to your AppContext / DI root, etc.
    }

    public void Terminate()
    {
        Events?.Dispose();
        Events = null;

        if (_palette != null)
        {
            _palette.Close();
            _palette.Dispose();
            _palette = null;
        }
        // ...null any other app-scope static slots the plugin populated.
    }
}
```

Rich `Initialize()` / `Terminate()` bodies are the norm in production plugins (palettes, lifecycle hooks, dependency-injection root wiring, MessagePack/QuestPDF setup, service registrations). The dual-instance constraint just means **every named slot the teardown touches must be `static`** — nothing more.

Symptom of an instance-field mistake: `Terminate()` runs (you see the log line) but the palette stays open and the events keep firing, because the fields it nulled out were on the wrong instance.
</use-static-fields-for-cleanup-state>

<event-subscriptions-via-acadeventmanager>
DevReload ships `src/Autocad/EventManager/AcadEventManager.cs` as a **shared project** — import it via `<Import Project="..\..\src\Autocad\EventManager\EventManager.projitems" />` (or equivalent). It compiles into the plugin DLL, no NuGet, no extra dependency.

It exists because naive event cleanup breaks in two ways that are silent in Release and lethal in Debug:

1. You captured `Application.DocumentManager.MdiActiveDocument` to unsubscribe from in `Terminate()` — the user switched documents in the meantime, so `MdiActiveDocument -= handler` targets the wrong doc and the original handler stays bound across reloads.
2. You stored the original `Document` reference — but the user closed that document, so the stored reference is dead and `-=` is a silent no-op.

`AcadEventManager` records an unsubscribe `Action` per `Document`, auto-cleans on `DocumentToBeDestroyed`, and bulk-cleans on `Dispose()`. Pattern:

```csharp
var doc = Application.DocumentManager.MdiActiveDocument;
doc.CommandEnded += OnCommandEnded;
MyPlugin.Events!.Track(doc, () => doc.CommandEnded -= OnCommandEnded);

// In Terminate():
_events?.Dispose();   // unsubscribes every tracked handler across every document
```

Use it for every event subscription touching AutoCAD's `Application.*`, `DocumentManager.*`, or per-`Document` events. If you find yourself writing a manual `List<Action> _unsubscribes` field, stop — that's what `AcadEventManager` is.
</event-subscriptions-via-acadeventmanager>
</reload-safe-plugin-shape>

<gotchas>
Lessons that bite repeatedly. Each is directly actionable from this skill.

1. **The bring-up sequence is `acad_start` → `acad_wait_pipe` → (re-bind if `devreload_*` missing) → `devreload_load_plugin("Acd.Mcp")` → `devreload_register_new_plugin` (first time only) → `devreload_reload` → `acad_send_command` → `autocad_script_execute` (assert).** Memorise it; deviating wastes round-trips.
2. **`devreload_*` tools are absent until AutoCAD's pipe is up; then they appear automatically.** The bridge waits for the pipe however long it takes. Do NOT conclude the MCP is broken or start inventing throwaway copies — just `acad_wait_pipe`. Only if the catalog doesn't refresh client-side, nudge it with `acad_detach`/`acad_attach <pid>`. See `<tool-surface-comes-up-in-phases>`. **If your own `acad_wait_pipe` call returns early or times out before the pipe appears (a client-side call cap, not the bridge giving up), just call it again — it is idempotent. Never `acad_start` a second instance or `acad_quit` the loading one because one wait was cut short; cold Civil 3D legitimately takes 1–3 min.**
3. **`autocad_script_execute` fails until `Acd.Mcp` is loaded.** The tool is in your catalog from the start but the plugin behind it isn't running. `devreload_load_plugin("Acd.Mcp")` first.
4. **ACD-MCP snippets: block-form `using` only.** `using (var tr = ...) { ... }`, never top-level `using var tr = ...;` (parsed as a using-directive → compile error). Load `/acd-mcp:script` for the rest.
5. **Commands MUST register via `Utils.AddCommand`, not `CommandClass.AddCommand`,** and plugin assemblies MUST suppress AutoCAD's own scan with `NoCommands` — see `<reload-safe-plugin-shape>`. Symptom of missing suppression: second `devreload_reload` fails with `eDuplicateKey`.
6. **`Assembly.Location` is empty under stream-loading.** Code that reads sidecar files via `Path.GetDirectoryName(typeof(X).Assembly.Location)` returns `""` then NREs. Use `AppDomain.BaseDirectory` or store the path at load time via assembly metadata.
7. **WPF XAML resolves types in the DEFAULT ALC, not the plugin ALC.** Anything referenced from XAML (custom controls, converters, value-template targets) MUST be in shared-assemblies via `devreload_write_shared_assemblies`. Symptom: `XamlParseException` naming a type that compiles fine.
8. **`Database.Dispose()` does NOT synchronously release the OS file handle** (finalizer-driven). Open-dispose-then-reopen-for-write races the OS share rules. Use `FileShare.ReadWrite` when a `SaveAs` is in the future. `FileShare.Read` BLOCKS a future writer — it is NOT "the safe default."
9. **PDB stream-load is what gives line-accurate stack traces.** Confirm `<DebugType>portable</DebugType>` and `<DebugSymbols>true</DebugSymbols>` in the plugin csproj. Hex offsets in exceptions = missing/broken PDB emission.
10. **After `devreload_reload`, call `devreload_list_tools`** if you added or renamed `[AcadRpcTool]` methods. The attribute scan happens on assembly load; a build that succeeds but doesn't expose your new method means the attribute is wrong (typically: class not `public`, or method signature unsupported).
11. **`devreload_reload` returns the FULL build log even on success** — tens of KB, often dominated by MSB3277 reference-version warnings, and it may exceed your client's result-size limit and surface as a scary error on a reload that actually SUCCEEDED. Read `success`/`loaded`/`commandCount`; consult `build.log` only when `build.success == false`. (DevReload code smell: it should omit/truncate the log on success — report it.)
12. **`[CommandMethod]` only scans public types.** Class registering commands must be `public`. `commandPrefix` does not rename your commands — it only names the generated `{prefix}LOAD/DEV/UNLOAD`.
13. **`acad_send_command` strings need every terminator.** Native AutoCAD commands often want an extra `\n` to commit. If a command doesn't run, mentally type it into the command line — every keystroke you'd press is a character in the string. You cannot read command-line text back; assert with `autocad_script_execute` instead.
14. **Cold Civil 3D startup is slow** (1–3 min). Gate on `acad_wait_pipe(120)` — it returns the instant the instance's plugin pipe is live. If `wait_pipe` stalls past ~120 s on an idle, responsive process, suspect a blocking dialog — see `<startup-can-stall>`.
15. **Clean up after throwaway work.** `devreload_unregister(name)` the throwaway plugin and `acad_quit` at the end, so `plugins.json` stays tidy and the next start is clean.
16. **Multiple AutoCADs: target each by `pid`.** Every `acad_*`/`devreload_*` tool takes an optional `pid`; omit for the bound default. acd-mcp's `autocad_*` tools take `pid` too. All control is pipe-based and runs on every instance at once, independently. See `<driving-multiple-instances>`.
</gotchas>

<multiple-agents-and-instances>
**Pid is the unit of addressing.** One agent's bridge drives any number of instances by passing `pid`; each agent runs its own bridge. Both the DevReload pipe and the acd-mcp pipe accept several concurrent connections, so two agents can even drive the same instance — though one-instance-per-agent stays the cleaner pattern (a shared instance means a shared drawing and a shared ACD-MCP script session).

**acd-mcp picks its target by `pid`, not by elimination.** Its `autocad_script_*` / `autocad_batch_*` tools take an optional `pid`; pass it to pick an instance when `Acd.Mcp` is loaded in more than one. You do NOT unload it from the others.
- `MULTIPLE_AUTOCAD_PLUGINS` — `Acd.Mcp` is loaded in two or more instances and you didn't pass `pid`. Pass it (pids from `acad_list_instances`).
- When its tools misbehave, read the `acd-mcp://status` resource — it answers even when tool calls don't.

For any "no AutoCAD bound / pipe not up" error from a `devreload_*` call, the fix is to bring up and bind an instance: `acad_start` (or `acad_attach <pid>`), then `acad_wait_pipe`.
</multiple-agents-and-instances>

<binding-lifecycle>
The bridge process holds **at most one bound AutoCAD pid at a time**. The binding is in-memory — it does NOT persist across bridge restarts. Restarts happen any time you:

- Resume a previous session (`claude -r`).
- Run `/reload-plugins` or update the devreload plugin via `/plugin`.
- Kill the bridge directly (or it crashes).

After a restart, the bridge starts fresh:

1. **Single running AutoCAD with the DevReload pipe up** → bridge **auto-attaches** to it; `devreload_*` tools come back online once the forwarder connects (sub-second). This is `AutoAttach` in `Acad.Rpc.Bridge\AutoAttach.cs`. The pipe accepts concurrent connections, so a second bridge can attach to the same instance too.
2. **Zero running AutoCADs** → bridge stays unbound. Call `acad_start` to launch one.
3. **Multiple running AutoCADs** → bridge stays unbound (ambiguous). Run `acad_list_instances`, then `acad_attach <pid>` to pick.
4. **AutoCAD running but pipe not up** (DevReload not loaded yet, or stalled on a dialog) → bridge stays unbound. Clear any blocker (`<startup-can-stall>`), then `acad_attach`, or `acad_quit` and `acad_start` fresh.

**If `devreload_*` tools show "offline" after a restart:** the bridge is unbound. Run `acad_list_instances`. If exactly one healthy instance shows `isBound=false`, call `acad_attach <pid>`; if it shows bound but the tools are still missing client-side, re-bind (`acad_detach` then `acad_attach <pid>`) to nudge the catalog refresh.

**Tool catalog does not survive a session resume verbatim. When in doubt, `acad_list_instances` is the cheap first probe.**

Remote `devreload_*`/`ui_*` stay listed (from cache) while unbound; calls error until you rebind. Unbound signal = "tools present, calls fail", not "tools gone". See `<recover-from-a-civil-crash>`.
</binding-lifecycle>

<recover-from-a-civil-crash>
A crashed Civil 3D instance does NOT kill the bridge — only that instance is lost.

State after a crash:
- `devreload_*`/`ui_*` stay in the catalog (cached); calls error `no target / pipe not up`.
- Dead binding self-clears; `acad_get_state` → "no instance bound".

Recover (one step):
- Drive a surviving instance: pass its `pid` on the call — no rebind needed.
- Else `acad_attach <pid>` (`acad_list_instances` for pids), or `acad_start` a fresh one.

Frozen-but-alive (a "has stopped working" dialog, process not yet exited) can hang an in-flight call → `acad_quit <pid>`, then `acad_start`.
</recover-from-a-civil-crash>

<engineering-rules-anchored>
The user's global rules (`~/.claude/CLAUDE.md` `<engineering-rules-strict>`) apply with extra force here:

- **Rule 4 (no rushing to fix bugs):** Verify the proximate-cause hypothesis with a live `acad_send_command` repro + `autocad_script_execute` assertion BEFORE editing code.
- **Rule 1 (no overengineering):** A one-line OS-level fix earns a one-line OS-level fix + a test that pins it. Resist symbolic tests that don't observably exercise the fix.
- **Rule 7 (no silent scope creep):** When `devreload_reload` is sub-10-second, the temptation to "while I'm here, clean up X" is huge. Resist. Surface side-quests as code-smell reports.
- **Rule 9 (push back on subpar requests):** When the issue's "prefer X" guidance conflicts with the evidence, name the conflict and propose the better path.
- **Rule 11 (mark dead code):** Phantom `.sln` entries, unused shared-assembly entries, orphaned `[AcadRpcTool]` methods nothing calls — fix or delete, never silently tolerate.
</engineering-rules-anchored>
