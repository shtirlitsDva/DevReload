<title>Proposal: Seamless multi-instance AutoCAD/Civil 3D control</title>

<status>
Implemented and validated, 2026-06-15. The pipe-first control plane below is in the
tree and deployed: per-pid routing drives several Civil 3D instances at once with no
COM, `acad_wait_quiescent` no longer hangs on a 2nd instance, and the plugin-load /
command / state / document operations are isolated per pid. COM is removed from the
control path (`AcadComClient` deleted). See `<validation>` at the end.
</status>

<problem>
DevReload and ACD-MCP work beautifully against ONE AutoCAD. The moment a second
instance is running — whether one agent drives two, or two agents each drive their
own — control breaks in ways that look like "the tools are broken" or "AutoCAD is
hung," when in fact the instance is healthy.

Empirically reproduced (two concurrent Civil 3D 2025 instances, A started first,
B second):

| Operation | Transport | Instance A (1st) | Instance B (2nd) |
|---|---|---|---|
| `acad_wait_pipe` | pipe | ✅ | ✅ |
| `devreload_*` (reload/load/list) | pipe | ✅ | ✅ |
| acd-mcp `autocad_script_*` | pipe | ✅ | ✅ (if loaded in only one instance) |
| `acad_send_command` | COM | ✅ (occasionally flaky) | ❌ "COM unreachable" |
| `acad_wait_quiescent` | COM | ✅ | ❌ **hangs the full timeout** (90 s measured) |
| `acad_get_state` | COM | ✅ | ❌ "COM unreachable" |

Plus two acd-mcp discovery failures with multiple instances:
- `AMBIGUOUS_AUTOCADS` — transient, while the target's acd-mcp pipe is still coming up.
- `MULTIPLE_AUTOCAD_PLUGINS` — permanent, when acd-mcp is loaded in 2+ instances; every
  script call fails because the bridge cannot pick one and the MCP config cannot pass a
  runtime `--pid`.

These are the two user-reported symptoms: "agents hang on wait-for-idle" and
"agents insist acd-mcp tools don't exist."
</problem>

<root-cause>
**AutoCAD registers exactly one `AcadApplication` in the Windows Running Object Table per machine — effectively the first-started instance.** Every COM-by-pid lookup the bridge performs (`AcadComClient.AttachByPid(pid)`) can only resolve that one instance. The 2nd+ instance is COM-invisible, so every COM-backed tool either throws "COM unreachable" or, worse, polls COM forever (`acad_wait_quiescent`) and burns its whole timeout — the observed "hang."

The named pipe each instance opens (`acad-rpc-<pid>`, and `acd-mcp-<pid>`) does NOT have this limitation: it is keyed by pid and always reachable. Everything that already runs over the pipe (`devreload_*`, acd-mcp scripts) works on every instance. **The fault is entirely in choosing COM as the transport for process/document control.**

A secondary issue, the acd-mcp ambiguity, is the same disease in a different organ:
acd-mcp's bridge auto-*discovers* one AutoCAD instead of being told which pid to talk to.
</root-cause>

<design-goals>
1. **Pid is the unit of addressing.** Any per-instance operation targets a pid; the bridge routes it to that pid's pipe. No global "the AutoCAD."
2. **No COM on the hot path.** COM/ROT is demoted to an optional fallback used only for an AutoCAD that does NOT have the DevReload plugin loaded (rare, since DevReload autoloads).
3. **Same tool names and signatures.** `acad_send_command`, `acad_wait_quiescent`, etc. keep their shape (they already take an optional `pid`). Only the transport underneath changes. Agents and the existing skill keep working.
4. **One agent ↔ one instance per pipe stays enforced** (the single-connection pipe is good — it prevents two agents corrupting one drawing). Multi-instance means one bridge holding several *distinct* pipe connections, which is fine.
5. **acd-mcp gets pid-addressable too**, so a multi-instance session can target a specific drawing for script execution.
</design-goals>

<proposed-architecture>
A **pipe-first control plane**: the in-AutoCAD plugin owns all per-instance operations; the bridge is a thin per-pid router.

<part-1-move-process-control-into-the-plugin>
Today the in-AutoCAD `DevReload` plugin hosts only `devreload_*` (build/load lifecycle) over its `AcadRpcHost`. Add a second `[AcadRpcSurface(Group = "acad")]` inside the plugin — call it `AcadControlTools` — exposing the operations that are currently COM-only in the bridge:

- `send_command` / `post_command` — via `Document.SendStringToExecute` / `Editor` on the main thread (acd-mcp already proves this works on a COM-invisible instance).
- `get_state` — `IsQuiescent`, `HasActiveDocument`, `ActiveDocumentName`, `Visible` read directly from `Application`/`DocumentManager` in-process.
- `wait_quiescent` — trivial and instant in-process: the plugin already knows its own quiescence; no polling, no COM. **This alone eliminates the reported hang.**
- `open_drawing` / `new_drawing` / `close_active_drawing` / `activate_document` / `list_open_documents` — `DocumentManager` calls in-process.
- `quit` — `Application.Quit()` from inside (graceful, no ROT), with the bridge's `Process.Kill(pid)` as the only fallback.

These run on the AutoCAD main thread exactly like the existing `RunOnAcadMainThread` `devreload_*` tools, so the mechanism is proven.

The plugin is the same DLL in every instance, so every instance answers these over its own pipe. Pid-invisibility via COM becomes irrelevant.
</part-1-move-process-control-into-the-plugin>

<part-2-bridge-becomes-a-per-pid-router>
Replace the single-connection `PipeForwarder` with a **`PipePool`**: a small map of `pid → live pipe connection`, each connection identical to today's forwarder (request-id correlation, reader loop, reconnect-on-binding/appearance). Behaviour:

- `acad_*(pid)` and `devreload_*(pid?)` resolve a pid (explicit, else the default/bound pid) and forward to that pid's pooled connection, opening it lazily.
- A connection is evicted when its process exits (the forwarder already learned to stop retrying on process-exit as of the 2026-06-15 fix).
- "Binding" (`acad_attach`) degrades from a hard single-connection constraint to a **default-pid** convenience for when `pid` is omitted. You can still attach/`detach`; it just selects the default route.

Tool-catalog stability: `acad_*` are bridge-native; `devreload_*` are the same set regardless of how many instances are connected, so they are merged once. The catalog no longer flickers as instances come and go (only truly going to zero connected instances removes `devreload_*`).
</part-2-bridge-becomes-a-per-pid-router>

<part-3-what-stays-in-the-bridge-natively>
These need no instance and therefore no pipe/COM:
- `acad_locate_install` — registry read.
- `acad_start` — process launch; auto-adds the new pid to the pool and sets it default.
- `acad_list_instances` — OS process enumeration + pipe-existence probe. **Drop the `comAvailable` column** (or relabel it "pluginPipeUp"); COM-availability is no longer meaningful or trustworthy.
- `acad_wait_pipe` — pipe existence; already correct.
- `acad_attach` — set default pid + ensure a pooled connection.
</part-3-what-stays-in-the-bridge-natively>

<part-4-acd-mcp-multi-instance>
Two options, smallest-first:

- **A (pragmatic): pid parameter + routing.** Give `autocad_script_execute` / `autocad_script_propose` / batch tools an optional `pid`, and have the acd-mcp bridge route to `acd-mcp-<pid>`. When omitted, default to the single instance that has the acd-mcp pipe, or to a pid shared from DevReload's binding. This directly removes `MULTIPLE_AUTOCAD_PLUGINS` as a dead-end.
- **C (north star): one pipe, one router.** Fold acd-mcp's script surface into the same in-AutoCAD RPC host as `devreload_*`/`acad_*`, so each instance exposes a single `acad-rpc-<pid>` pipe carrying every tool, and a single bridge routes everything by pid. Eliminates the separate acd-mcp bridge, its discovery logic, and the whole ambiguity class. Larger change, cross-repo (ACD-MCP is a sibling product), but it is the clean end state.

Until either ships, the operational rule (now in the skill) is: load `Acd.Mcp` in exactly one instance.
</part-4-acd-mcp-multi-instance>

<part-5-com-as-fallback-only>
Keep `AcadComClient` for one case: an AutoCAD that is running but does NOT have the DevReload plugin loaded (so no pipe). Since DevReload autoloads via the ApplicationPlugins bundle, this is the exception, not the rule. Mark every COM path "fallback; first instance only; may be flaky" and prefer the pipe whenever the plugin pipe is up.
</part-5-com-as-fallback-only>
</proposed-architecture>

<module-and-interface-changes>
- **`src/Autocad/DevReload` (in-AutoCAD plugin):** NEW `AcadControlTools` RPC surface (Group "acad"), main-thread, mirroring the bridge's process/document/command operations in-process. Reuses the existing `AcadRpcHost` + `RunOnAcadMainThread` machinery.
- **`src/Autocad/Acad.Rpc.Bridge/PipeForwarder.cs` → `PipePool`:** from one connection to a pid-keyed pool; per-pid lazy connect, reconnect, process-exit eviction. (The 2026-06-15 "retry-until-pipe-or-exit" fix is the per-connection foundation.)
- **`src/Autocad/Acad.Rpc.Bridge/AcadProcessTools.cs`:** `send_command`/`post_command`/`get_state`/`wait_quiescent`/document ops forward through the pool to the in-plugin surface instead of `AcadComClient`. `quit` asks the plugin to `Application.Quit()`, else `Process.Kill`. Launch/discovery unchanged.
- **`src/Autocad/Acad.Rpc.Bridge/AcadInstanceBinding.cs`:** "binding" becomes "default pid"; allow it to coexist with other pooled connections.
- **`AcadComClient` (Acad.Process):** demoted to fallback; callers prefer the pipe.
- **ACD-MCP (sibling repo):** option A (`pid` param + routing) now; option C (fold into the unified pipe) later.
</module-and-interface-changes>

<what-this-fixes>
- **wait-for-idle hang** → gone: quiescence is answered in-process per pid, instantly.
- **"acd-mcp tools don't exist"** → gone: pid-addressable acd-mcp (A), or unified pipe (C).
- **`acad_send_command` dead on 2nd instance** → gone: runs over the pid's pipe.
- **Two agents launching different instances colliding via ROT** → gone: no global COM lookup; each agent's bridge routes to its own pids' pipes.
- **`acad_quit` always kill-fallback → Drawing Recovery next start** → reduced: graceful in-process `Application.Quit()` avoids the kill in the common case.
</what-this-fixes>

<risks-and-tradeoffs>
- **Main-thread process control.** `Quit`/document switches from inside the plugin are delicate; needs care so a quit doesn't deadlock the pipe reader. Mitigated by the acd-mcp precedent (heavy main-thread work over a pipe already works) and by keeping `Process.Kill` as the hard fallback.
- **Pre-plugin window.** Between process launch and plugin load there is no pipe. `acad_start` already returns immediately and `acad_wait_pipe` gates readiness; the COM fallback covers any "no-DevReload" instance.
- **Pool lifecycle complexity.** More connections to manage than one forwarder. Bounded by the number of instances an agent runs (typically 1–3); per-pid reconnect/eviction logic is small and already prototyped in the single forwarder.
- **acd-mcp option C is cross-repo** and the larger lift; option A is a safe incremental step that removes the dead-end immediately.
- **Behaviour change in `acad_list_instances`** (drop/relabel `comAvailable`). Low risk; it was misleading anyway.
</risks-and-tradeoffs>

<phasing>
1. **Plugin `AcadControlTools` + bridge routes `get_state`/`wait_quiescent` over the pipe.** Kills the hang and the "COM unreachable" on state reads — highest user value, smallest surface.
2. **Route `send_command`/`post_command`/document ops over the pipe; `PipePool`.** Full multi-instance command control.
3. **acd-mcp option A (pid param).** Removes `MULTIPLE_AUTOCAD_PLUGINS` dead-end.
4. **`quit` graceful-in-process; demote COM to fallback; relabel `list_instances`.** Cleanup.
5. **(Later) acd-mcp option C: unify onto one pipe + one router.**
</phasing>

<as-built-deltas>
Where the shipped implementation differs from the proposal above:
- **COM is removed, not kept as a fallback.** `AcadComClient` is deleted and the
  control path is pipe-only — one mode of operation. An instance without the
  DevReload plugin is simply not drivable; launch one with `acad_start`.
- **`acad_quit` ends the process** rather than calling `Application.Quit()` in-process.
  Graceful in-process quit (to avoid the Drawing Recovery palette on the next start)
  remains a future refinement.
- **`acad_list_instances` reports `pipeAvailable`**, and the COM-availability column
  is gone.
- **acd-mcp now takes a per-call `pid` (option A done):** `autocad_script_*` /
  `autocad_batch_*` accept an optional `pid` and the bridge routes to `acd-mcp-<pid>`
  (per-call pid wins over the `--pid` flag). Several instances can each have `Acd.Mcp`
  loaded and be targeted independently; omit `pid` with one, pass it with several.
  Option C (folding acd-mcp onto the unified DevReload pipe) remains future.
</as-built-deltas>

<validation>
Exercised live against two concurrent Civil 3D 2025 instances from one bridge:
- `acad_get_state` on both pids returned instantly and independently — no COM, no hang.
- `acad_wait_quiescent` on the 2nd instance returned immediately (the reported hang is gone).
- `acad_send_command("._CIRCLE 0,0 5", pid=B)` drew the circle; acd-mcp confirmed `circles=1 r=5` in B; A was untouched.
- `acad_new_drawing(pid=A)` raised A's document count to 2 while B stayed at 1 — per-pid isolation.
- `devreload_reload("ThrowawayPlugin", pid=A)` loaded it in A only; B's registry showed it unloaded — per-pid plugin lifecycle isolation.
- Reload result on success no longer carries the full build log (summary only), so it stays under the MCP result-size cap.

Not yet exercised: two separate bridges (agents) driving the SAME instance at once — the
in-AutoCAD pipe server now accepts up to 16 concurrent connections, but this path has
only been run single-bridge.
</validation>
