---
name: acd-agentic-dev
description: Agentic development loop for AutoCAD/.NET plugins, driven entirely through DevReload's MCP tools. Activates when the agent is asked to fix, refactor, or extend an AutoCAD plugin AND the `acad_*` / `devreload_*` MCP tool surfaces are reachable. The skill is the LOOP plus the TOOL INVENTORY — nothing else.
when_to_use: User asks the agent to do development work on an AutoCAD plugin, and the MCP host (Acad.Rpc.Bridge → the in-AutoCAD pipe) is connected. The agent owns the full cycle: spin up AutoCAD, register/load the plugin, write a failing test, ship the smallest fix, live-verify with `acad_send_command`, iterate until a stated success criterion holds. No human in the loop.
---

<the-loop>
One pass per problem. Each step is a checkpoint — never skip diagnosis to rush to a fix.

1. **State the success criterion in ONE sentence** before touching code. Something observable through `acad_get_state` or `acad_send_command` output ("after `MYCMD` on `crashtest-01.dwg`, the active document's modelspace contains exactly one Polyline"). Without a concrete criterion, "done" drifts.
2. **Analyze.** Read the cited code at the cited line numbers. Verify the code still matches the brief — codebases drift between issue-filing and issue-fixing. If it doesn't match, restate the brief in your own words before planning.
3. **Plan in modules.** Name the file(s) and module boundaries you'll change. If the right change crosses a boundary you didn't expect, surface it before editing (rule 7).
4. **TDD — red.** Write the failing test FIRST.
   - Pure-.NET logic → an xUnit test in a `tests/<Project>.Tests/` project that link-includes the source files (`<Compile Include="..\..\src\Foo\Bar.cs" Link="Sut\Bar.cs" />`) to avoid pulling AutoCAD references into the test build.
   - AutoCAD-bound behaviour → a "live test": a deterministic sequence of MCP calls that exercises the command and asserts on `acad_get_state` / drawing inspection. Live tests are first-class regression artifacts; commit them.
   Run the test, confirm it fails for the RIGHT REASON. A test that fails on missing types is not a red test.
5. **TDD — green.** Smallest change that makes the test pass. Do not refactor in this step.
6. **Live-verify.** `devreload_reload(name)` to build + hot-reload, then `acad_send_command(...)` to drive the new code, then `acad_get_state(...)` (or drawing inspection) to confirm the success criterion from step 1.
   - If `devreload_reload` returns `BuildResult.Success == false`, read the full `Log` and fix the build before continuing. Don't paper over it with a wider build.
   - If `acad_send_command` succeeds but `acad_get_state` does NOT match the criterion, the test from step 4 is wrong OR the fix is wrong. Diagnose which.
7. **Refactor.** Tests still green. Restructure for clarity. After each meaningful refactor: `devreload_reload` + live exercise. The reload is sub-10-second — abuse it.
8. **Stop on the criterion.** When step 1's criterion is observably met AND no new `EXCEPTION`-class log entries appeared, stop. Do NOT bundle "while I'm here" cleanups — surface them as code-smell reports (rule 3).

If at any step the proximate-cause hypothesis from the brief proves wrong, return to step 2 with the new evidence (rule 4).
</the-loop>

<mcp-tools>
The tools DevReload's RPC surface exposes. These are the agent's primary interface — more reliable and reproducible than asking the user to click in the palette.

<acad-group>
Lifecycle and command surface for the AutoCAD process. `acad_*` prefix.

| Tool | When to call |
|---|---|
| `acad_locate_install` | First call when flavor/install isn't known. Returns AutoCAD/Civil3D/Plant3D/etc. installs discovered from the registry. |
| `acad_start(flavor, startupCommands?, ...)` | Spin up a fresh AutoCAD/Civil 3D. Pass `startupCommands` like `"FILEDIA\n0\nNETLOAD\n<bundle-path>\nFILEDIA\n1\n"` to NETLOAD DevReload at boot — the canonical agentic-dev startup pattern. Bridge auto-binds to the launched pid. |
| `acad_wait_pipe(timeoutSeconds=120)` | PRIMARY readiness gate. Returns when the in-AutoCAD RPC pipe is listening — independent of COM/ROT, works with no document open. |
| `acad_wait_quiescent(requireActiveDocument=true, timeoutSeconds=300)` | Secondary readiness gate for code that needs a document. Cold Civil 3D can take minutes — keep the timeout generous. |
| `acad_list_instances` | See running AutoCAD processes, COM/pipe availability, and which one the bridge is bound to. |
| `acad_attach(pid)` | Bind to an already-running AutoCAD. |
| `acad_detach` | Release binding. |
| `acad_get_state` | Snapshot `IsQuiescent`, `HasActiveDocument`, `ActiveDocumentName`, `Visible`. Use after commands to confirm completion. |
| `acad_send_command(cmd)` | BLOCKING — returns when AutoCAD finishes the command. Default choice. Command string must include every terminator (`"\n"` between args, trailing `"\n"` to commit). |
| `acad_post_command(cmd)` | NON-BLOCKING — queues for the next pump. Only when fire-and-forget is required; always pair with `acad_wait_quiescent` if you need to observe the result. |
| `acad_open_drawing(path, readOnly?)` | Open a .dwg. Required after `acad_start` because the default Start tab leaves no active document. |
| `acad_new_drawing(templatePath?)` | Empty drawing for test runs. |
| `acad_close_active_drawing(saveChanges=false)` | Default `false` — agentic test drives should not persist drawing state. |
| `acad_quit(saveChanges=false, timeoutSeconds=10)` | Graceful Quit() via COM with kill fallback. Call at end of session. |
</acad-group>

<devreload-group>
Plugin lifecycle, build, and configuration. `devreload_*` prefix.

| Tool | When to call |
|---|---|
| `devreload_list_plugins` | THE single source of truth for what's registered and what's loaded. Filter the response — do not use three separate query tools. |
| `devreload_register_new_plugin(name, projectFilePath, dllPath, buildConfiguration?, commandPrefix?, loadOnStartup?)` | One-time: add a plugin to `plugins.json`. Persists across AutoCAD restarts. |
| `devreload_reload(name)` | THE inner-loop tool. Builds via `dotnet build`, then stream-loads the new DLL into a fresh collectible ALC. Returns `PluginActionResult` with nested `BuildResult` — inspect `Success` and `Log` before declaring victory. Build-first-then-swap: the old plugin keeps running if the build fails. |
| `devreload_load_plugin(name)` | Load without rebuilding. Use only when the DLL on disk is known to be the desired one. |
| `devreload_unload_plugin(name)` | Tear down the plugin's ALC. |
| `devreload_unload_all` | Recovery: tear down every loaded plugin. |
| `devreload_unregister(name)` | Permanently remove from `plugins.json`. |
| `devreload_get_assembly_info(name)` | Confirm which DLL is actually loaded (path, version, last-write). Use when a reload doesn't seem to have picked up an edit. |
| `devreload_list_tools` | Every MCP tool currently registered with the host, grouped by source assembly. Run after `devreload_reload` to verify new `[AcadRpcTool]` methods on the plugin are exposed. |
| `devreload_update_build_configuration(name, "Debug"\|"Release")` | Switch what the next `devreload_reload` builds. Persisted. |
| `devreload_update_active_worktree(name, worktreePath?)` | Point a registered plugin at a worktree. Pass `null` to revert to the main checkout. |
| `devreload_build_project(csprojPath, config)` | Direct `dotnet build`, no load step. Useful for CI-style verification or sanity-building dependencies. |
| `devreload_list_worktrees(repoRoot)` | Enumerate git worktrees + branches. |
| `devreload_read_shared_assemblies(buildDir)` / `devreload_write_shared_assemblies(...)` | Read or write `SharedAssemblies.Config.json` — controls which DLLs load into the default ALC (required for WPF XAML type resolution and any type that must cross the plugin/host boundary). |
</devreload-group>
</mcp-tools>

<gotchas>
Lessons that bite repeatedly. Each is directly actionable from this skill.

1. **Build/test loop is `acad_start` → `acad_wait_pipe` → `devreload_register_new_plugin` (first time only) → `devreload_reload` → `acad_send_command`.** Memorise this sequence; deviating wastes round-trips.
2. **Commands MUST register via `Utils.AddCommand`, not `CommandClass.AddCommand`.** First is removable; second is permanent for the process and produces `eDuplicateKey` on the second `devreload_reload`. DevReload's own `CommandRegistrar` scans `[CommandMethod]` and uses the removable path — if you hand-roll command registration in a plugin, mirror that.
3. **`Assembly.Location` is empty under stream-loading.** Code that reads sidecar files via `Path.GetDirectoryName(typeof(X).Assembly.Location)` returns `""` then NREs. Use `AppDomain.BaseDirectory` or store the path at load time via assembly metadata.
4. **WPF XAML resolves types in the DEFAULT ALC, not the plugin ALC.** Anything referenced from XAML (custom controls, converters, value-template targets) MUST be in shared-assemblies via `devreload_write_shared_assemblies`. Symptom: `XamlParseException` naming a type that compiles fine.
5. **`Database.Dispose()` does NOT synchronously release the OS file handle** (finalizer-driven). Open-dispose-then-reopen-for-write races the OS share rules. Use `FileShare.ReadWrite` when a `SaveAs` is in the future. `FileShare.Read` BLOCKS a future writer — it is NOT "the safe default."
6. **PDB stream-load is what gives line-accurate stack traces.** Confirm `<DebugType>portable</DebugType>` and `<DebugSymbols>true</DebugSymbols>` in the plugin csproj. Hex offsets in exceptions = missing/broken PDB emission.
7. **After `devreload_reload`, call `devreload_list_tools`** if you added or renamed `[AcadRpcTool]` methods. The attribute scan happens on assembly load; a build that succeeds but doesn't expose your new method means the attribute is wrong (typically: class not `public`, or method signature unsupported).
8. **`[CommandMethod]` only scans public types.** Class registering commands must be `public`.
9. **`acad_send_command` strings need every terminator.** Native AutoCAD commands often want an extra `\n` to commit. If a command doesn't run, mentally type it into the command line — every keystroke you'd press is a character in the string.
10. **Cold Civil 3D startup is slow.** Default `acad_wait_quiescent(300)` is right for first boot. Don't drop it below 60 on any path.
</gotchas>

<binding-lifecycle>
The bridge process holds **at most one bound AutoCAD pid at a time**. The binding is in-memory — it does NOT persist across bridge restarts. Restarts happen any time you:

- Resume a previous session (`claude -r`).
- Run `/reload-plugins` or update the devreload plugin via `/plugin`.
- Kill the bridge directly (or it crashes).

After a restart, the bridge starts fresh:

1. **Single running AutoCAD with the DevReload pipe up** → bridge **auto-attaches** to it silently. No agent action needed; `devreload_*` tools come back online once the pipe forwarder reconnects (sub-second). This is `AutoAttach` in `Acad.Rpc.Bridge\AutoAttach.cs`.
2. **Zero running AutoCADs** → bridge stays unbound. Call `acad_start` to launch one.
3. **Multiple running AutoCADs** → bridge stays unbound (ambiguous). Run `acad_list_instances`, then `acad_attach <pid>` to pick.
4. **AutoCAD running but pipe not up** (DevReload not loaded yet) → bridge stays unbound. Either NETLOAD DevReload manually or `acad_quit` and `acad_start` it fresh.

**If `devreload_*` tools show "offline" after a restart:** the bridge is unbound. This is NOT a broken plugin — it's a missing binding. Run `acad_list_instances`. If exactly one healthy instance shows `isBound=false`, call `acad_attach <pid>`; the tools come back online.

**Never assume your tool catalog survives a session resume verbatim.** When in doubt: `acad_list_instances` is the cheap first probe.
</binding-lifecycle>

<engineering-rules-anchored>
The user's global rules (`~/.claude/CLAUDE.md` `<engineering-rules-strict>`) apply with extra force here:

- **Rule 4 (no rushing to fix bugs):** Verify the proximate-cause hypothesis with a live `acad_send_command` repro BEFORE editing code.
- **Rule 1 (no overengineering):** A one-line OS-level fix earns a one-line OS-level fix + a test that pins it. Resist symbolic tests that don't observably exercise the fix.
- **Rule 7 (no silent scope creep):** When `devreload_reload` is sub-10-second, the temptation to "while I'm here, clean up X" is huge. Resist. Surface side-quests as code-smell reports.
- **Rule 9 (push back on subpar requests):** When the issue's "prefer X" guidance conflicts with the evidence, name the conflict and propose the better path.
- **Rule 11 (mark dead code):** Phantom `.sln` entries, unused shared-assembly entries, orphaned `[AcadRpcTool]` methods nothing calls — fix or delete, never silently tolerate.
</engineering-rules-anchored>
