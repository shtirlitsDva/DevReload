---
name: acd-agentic-dev
description: Agentic development loop for AutoCAD/Civil 3D .NET and ObjectARX plugins with DevReload hot-reload (`acad_*`, `devreload_*`, `oarx_*`, `ui_*` tools) and ACD-MCP live assertions (`autocad_*`). Use to fix, refactor, extend, or debug a plugin; to reproduce a command defect in a live AutoCAD/Civil 3D; to test a palette, jig, or dialog; or for .arx/.dbx work. Not for drawing queries or edits with no plugin code change — use /acd-mcp:script for those.
---

<surfaces>
- **DevReload** — AutoCAD process control and plugin lifecycle: `acad_*`, `devreload_*`, `oarx_*` (native groups), `ui_*` (UI automation). One bridge.
- **ACD-MCP** — a live C# script session inside the same AutoCAD: `autocad_*`. It observes and changes the drawing. It is the DevReload plugin `Acd.Mcp`.

Loop: DevReload reloads your code → ACD-MCP verifies what the code did.
</surfaces>

<tool-surface-comes-up-in-phases>
| Phase | Trigger | Tools that work |
|---|---|---|
| 0 — cold | session start | bridge-local: `acad_start`, `acad_wait_pipe`, `acad_attach`, `acad_detach`, `acad_list_instances`, `acad_locate_install`, `acad_quit`. Other tools are listed (bridge cache, or the acd-mcp server) and fail at call time. |
| 1 — pipe up | `acad_start` → `acad_wait_pipe` succeeds | `devreload_*`, `oarx_*`, `ui_*`, and the other `acad_*` (send_command, get_state, drawings). |
| 2 — Acd.Mcp loaded | Acd.Mcp is loaded in the instance | `autocad_*`. |

After `acad_start`/`acad_attach`, the bridge retries the pipe until it connects or the process exits, then sends `tools/list_changed`. `acad_wait_pipe` succeeded but tools are missing from your catalog: the client did not refetch. Call `acad_detach`, then `acad_attach <pid>`. The tool surface is not broken; do not make copies of it.
</tool-surface-comes-up-in-phases>

<the-loop>
1. **Bring up.** `acad_start` → `acad_wait_pipe`. Check Acd.Mcp: `devreload_list_plugins` → `Acd.Mcp` `loaded`. Not loaded: `devreload_load_plugin("Acd.Mcp")`. Not registered: stop and tell the user. Confirm with `autocad_script_execute("Doc.Name")`.
2. **State the success criterion** in one sentence that `autocad_script_execute` can query, e.g. "after `MYCMD` on `crashtest-01.dwg`, modelspace contains exactly one Polyline with 4 vertices". Without it, "done" drifts.
3. **Reproduce and analyze.** Reproduce the defect live before you edit: a guessed cause gives a fix that hides the real defect. Read the cited code. If it does not match the brief, restate the brief before you plan.
4. **Plan in modules.** Name the files and module boundaries that change. Report an unexpected boundary crossing to the user before you edit.
5. **Red.** Write the failing test first. It must fail on the behavior; a compile failure on a missing type proves nothing.
   - Pure .NET logic: an xUnit test in the plugin repo's test project (none: ask the user). Link-include the source (`<Compile Include="..\..\src\Foo\Bar.cs" Link="Sut\Bar.cs" />`) so the test build has no AutoCAD references.
   - AutoCAD-bound behavior: a live test — `acad_send_command` + `autocad_script_execute` assertions. Save reusable ones as `%APPDATA%\Acd.Mcp\scripts\script\<name>.csx`.
6. **Green.** Smallest change that passes. No refactor.
7. **Live-verify.** `devreload_reload(name)` → read `success` → drive the code → assert the criterion with `autocad_script_execute`. Assertion fails: find out whether the test or the fix is wrong.
8. **Refactor.** After each refactor: `devreload_reload` + re-assert.
9. **Stop** when the criterion holds. Report side-quests to the user; do not fix them — the user owns scope.

If the brief's cause hypothesis is wrong, go back to step 3 with the new evidence.
</the-loop>

<result-contracts>
DevReload tools return a JSON object in `structuredContent`; the text block carries the same JSON. Lists are wrapped: `{"items":[...]}`.

- A refused call is `isError: true`. Its text names the defect; a bad argument names the argument and the valid values. Nothing was done.
- A failed build is a normal result: `success:false` and the full `build.log`. `devreload_reload` keeps the previous build loaded; `oarx_reload` leaves the group unloaded. Read `success` on every build and load result, including `oarx_reload_payload`.
</result-contracts>

<references>
Read the file when the task needs it:

| File | Read when |
|---|---|
| [references/acd-mcp.md](references/acd-mcp.md) | Before you write assertion snippets, or when `autocad_*` calls fail. |
| [references/plugin-shape.md](references/plugin-shape.md) | New plugin project, or the first reload of an existing plugin: audit its entry class, because a `Terminate()` defect corrupts every later reload and hides the real bug. |
| [references/authoring-mcp-tools.md](references/authoring-mcp-tools.md) | The plugin publishes `[AcadRpcTool]` methods. |
| [references/ui-automation.md](references/ui-automation.md) | Testing a WPF palette, a native dialog, a jig, or taking screenshots (`ui_*`). |
| [references/oarx.md](references/oarx.md) | Native `.dbx`/`.arx` work (`oarx_*`), or loading a prebuilt payload (`oarx_reload_payload`). |
| [references/instances-and-recovery.md](references/instances-and-recovery.md) | `acad_wait_pipe` does not succeed, tools fail after a restart or crash, or more than one AutoCAD runs. |
</references>

<gotchas>
1. **`acad_wait_pipe` returns `succeeded:false`:** read `reason`, then call again. A client timeout with no result: call again. Cold Civil 3D takes 1–3 min. Do not start a second instance or quit the loading one. Idle process and still no pipe: `references/instances-and-recovery.md`.
2. **`acad_send_command` takes tokens:** one per argument, split on whitespace (`"._CIRCLE 0,0 5"`). It cannot send a bare Enter. A command that needs one: `acad_post_command` with the raw string and every `\n`, then `acad_wait_quiescent`. Neither returns command-line text; assert with `autocad_script_execute`.
3. **`Assembly.Location` is `""` under stream-load.** `Path.GetDirectoryName(typeof(X).Assembly.Location)` → NRE. Use `AppDomain.BaseDirectory`, or store the path at load time.
4. **XAML resolves types in the default ALC.** Custom controls, converters, and template targets used from XAML go into shared assemblies (`devreload_write_shared_assemblies`). Symptom: `XamlParseException` on a type that compiles.
5. **`[CommandMethod]` needs a public type.** `commandPrefix` names only the generated `{prefix}LOAD/DEV/UNLOAD` commands.
6. **A reload seems to miss an edit:** `devreload_get_assembly_info` gives the file the loaded bytes came from and its last-write time at load.
7. **Clean up:** `devreload_unregister` throwaway plugins, `acad_quit` at the end.
</gotchas>
