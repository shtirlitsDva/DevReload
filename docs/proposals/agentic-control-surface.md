<proposal-agentic-control-surface>

<status>
SUPERSEDED — 2026-05-14. This proposal scoped a DevReload-specific named-pipe RPC. After user feedback, the scope expanded to a universal, attribute-driven, app-agnostic system that all our AutoCAD plugins will use. See `universal-rpc-surface.md` for the current proposal. Kept for context.
</status>

<problem-statement>
Today, an autonomous agent (e.g. Claude Code) iterating on an AutoCAD plugin has to drive DevReload's reload cycle through AutoCAD's command line. That means the agent needs a way to type `<PREFIX>UNLOAD` / `<PREFIX>LOAD` into AutoCAD — either via the plugin's own RPC channel (ACD-MCP's REPL pipe), via UI automation, or via COM `SendCommand`. Each path has a sharp edge:

- **Plugin's own pipe.** Works for everything *until* the plugin you're iterating on IS the pipe-hosting plugin. Reloading ACD-MCP through ACD-MCP's pipe kills the pipe mid-call. Chicken-and-egg.
- **COM `SendCommand` via ROT.** Civil 3D launched with `/Automation` against an unsaved drawing doesn't register in the ROT (documented in `H:\GitHub\shtirlitsDva\ACD-MCP\docs\computer-use-from-claude-code.md` → `<step-3-listener-auto-start>`). Path is documented but unreliable in headless launches.
- **UI Automation.** AutoCAD's command-line edit control is a WPF `AutoCompleteEdit_1` with no native HWND that accepts `WM_CHAR`. Documented dead end (same doc, same section).
- **Win32 `SendMessage`.** Same dead end as UIA for the CLI control.

The result: the agent has no first-class way to talk to DevReload from outside AutoCAD. The workarounds it does have (queue an AutoCAD command via the plugin's own RPC; flip `loadOnStartup` and rely on autoload on the next AutoCAD launch) are all coupled to a plugin-side channel being present and healthy. For the self-reload case — where the plugin being reloaded IS the plugin hosting the agent's RPC — the channel dies mid-cycle and the agent is stuck. None of this needs AutoCAD to restart; DevReload already does the runtime swap. What's missing is a stable outside-AutoCAD verb: "reload this plugin and tell me the new build is live."

DevReload sits at the architecturally correct layer to solve this. Its lifetime is the AutoCAD process lifetime, it lives in the default ALC, it owns the collectible ALCs that hold plugins, and it already owns the verbs (`Load`, `Unload`, `DevReload`, `UnloadAll`, the registration registry). What it lacks is an out-of-process control channel.

</problem-statement>

<goals-and-non-goals>

<goals>
1. An autonomous agent — running outside AutoCAD — can call `reload(pluginName)` and get back a structured result: success / build errors / load errors / "is alive" probe.
2. The channel survives the plugin reload itself. Reloading plugin X must NOT kill the agent's control channel.
3. The channel survives bootstrap: it's open before any plugin loads, so it works for first-load, autoload-on-startup config, and recovery from an unloaded state.
4. Per-AutoCAD-process isolation. Multiple AutoCAD instances can coexist without port/file collisions.
5. No admin rights, no firewall prompts, no global registration, no installer steps beyond what DevReload already requires.
6. Local-only (security): the channel must not be reachable from another machine or another OS user.
7. Cheap to implement: weeks-of-work, not months. Single-developer scope.
</goals>

<non-goals>
1. NOT a replacement for plugin-side RPC (ACD-MCP's REPL). DevReload manages **lifecycle**; the plugin's own surface manages **behavior**. They compose.
2. NOT a generic AutoCAD command runner. The pipe should expose DevReload's verbs only. If the agent needs `Doc.Name`, that's what ACD-MCP is for.
3. NOT cross-platform. We're already Windows-only.
4. NOT a feature flag system or remote configuration store.
</non-goals>

</goals-and-non-goals>

<approach-comparison>

A control channel for an in-process .NET component is a well-trodden pattern. Six realistic transports, ranked across the criteria above:

<approach-a-named-pipe>

**Shape.** DevReload's `Initialize()` opens `\\.\pipe\devreload-<acad_pid>` via `NamedPipeServerStream`. A line-delimited JSON-RPC framing handles request/response. Work items are marshalled to AutoCAD's main thread via an `Application.Idle` pump.

**Client.** PowerShell helper script `Invoke-DevReloadPipe.ps1` that mirrors the shape of `Invoke-AcdMcpPipe.ps1` already in the ACD-MCP repo. One call from the agent's shell.

**Pros.**
- Zero new dependencies. `System.IO.Pipes` is in the BCL.
- Sub-millisecond round trip after the first call.
- Per-user ACLs by default. No firewall prompt. No admin. No port allocation.
- Multiple AutoCAD instances coexist trivially — pipe name includes the PID.
- Lives in default ALC alongside DevReload itself, so it survives every plugin ALC unload by construction.
- The "discover the PID" step is one PowerShell call (`Get-Process acad`). The agent already does it for ACD-MCP.

**Cons.**
- Windows-only. (Not a real con — we're Windows-only by AutoCAD requirement.)
- PowerShell-shaped, not MCP-shaped. The agent calls the pipe via a script wrapper, not via a typed MCP tool. Some agents may prefer the latter.

**Effort.** ~1–2 days. Half a day for the pipe server + Idle pump, half a day for the JSON-RPC dispatch + verb implementations, half a day for the PowerShell client + smoke test.

**Verdict.** Strongest candidate. This is the core layer.

</approach-a-named-pipe>

<approach-b-mcp-bridge>

**Shape.** A separate `DevReload.Bridge.exe` speaks MCP stdio to Claude Code (or any MCP-capable client). It connects to DevReload's named pipe (Approach A) internally and exposes the same verbs as typed MCP tools (`mcp__devreload__reload`, `mcp__devreload__list_plugins`, etc.).

**Pros.**
- Agent calls typed tools, no script shim, no PID-lookup boilerplate.
- Discoverable in the Claude Code tool list automatically.
- Tools can have rich JSON schemas → agent gets autocompletion / parameter docs.

**Cons.**
- Another process to ship, version, and resolve.
- Bridge can die independently — and per the reference doc (`<gotcha id="killing-bridge-disconnects-session">`), Claude Code does NOT auto-respawn MCP bridges. If the bridge dies, the agent loses the tools until `/reload-plugins`. An autonomous loop can't trigger that.
- Distribution: needs a `.mcp.json` and a Claude Code plugin install step, OR the user manually wires it.

**Mitigation.** The bridge is purely a stdio<->pipe shim — keep it stateless. It can be killed and restarted from outside cheaply, and the user can drive the pipe directly as a fallback. This is the same pattern ACD-MCP already uses.

**Effort.** ~2–3 days on top of Approach A.

**Verdict.** Strong second layer. Build on top of A, don't replace A.

</approach-b-mcp-bridge>

<approach-c-http-rest>

**Shape.** DevReload starts an `HttpListener` (or embedded Kestrel) bound to `127.0.0.1:NNNN`. REST endpoints: `POST /plugins/<name>/reload`, `GET /plugins`, etc.

**Pros.**
- Most language-agnostic. Any HTTP client, including `curl` for ad-hoc debugging.
- Browsable: a `GET /` index page can be served as a built-in diagnostic.

**Cons.**
- Port allocation problem with multiple AutoCAD instances. Need either a "first free port from a range" with discovery file, or a fixed port per PID derived from a hash — both ugly.
- Windows Defender Firewall may prompt the user on first launch when an unknown process opens a listening socket. (Even bound to 127.0.0.1 — the prompt happens at bind time, not connect time.)
- Larger attack surface. Localhost-bound is correct but other-user-on-same-machine can still connect unless we add a token.
- Token / auth handling needed → more code.

**Effort.** ~3–4 days, much of it on the port-and-discovery bookkeeping.

**Verdict.** Not worth it. Pipes win on every axis that matters for the actual use case.

</approach-c-http-rest>

<approach-d-com-idispatch>

**Shape.** Decorate a `DevReloadManager` class with `[ComVisible(true)]` + `[Guid(...)]`, register a type library on install. Agent calls via `New-Object -ComObject DevReload.Manager.1`.

**Pros.**
- Idiomatic Win32. Used by AutoCAD itself.
- Strongly-typed surface from any COM-aware host.

**Cons.**
- Registration requires admin OR per-user `HKCU\Software\Classes` writes. Adds installer complexity.
- COM + collectible ALC + WPF in the same process is a known footgun pile. Proxies, marshaling, apartment threading — every one of these has bitten this stack before.
- ROT-based discovery has the same "no entry for `/Automation` + unsaved drawing" bug that motivated this whole proposal.

**Effort.** ~5+ days; high regression risk.

**Verdict.** Reject. We are not solving 2010's problem.

</approach-d-com-idispatch>

<approach-e-file-watcher>

**Shape.** Agent drops a file: `%APPDATA%\DevReload\inbox\Acd.Mcp.reload`. DevReload's `FileSystemWatcher` picks it up, executes, writes a response file `Acd.Mcp.reload.result`.

**Pros.**
- Trivial. ~50 LOC.
- Zero protocol design. Useful as a panic-button fallback.

**Cons.**
- Async response over the file system is a race-condition factory.
- No structured error envelope without rebuilding what JSON-RPC already gives us for free.
- Awkward for "tell me when you're done" — the agent has to poll.

**Effort.** ~half a day.

**Verdict.** Don't ship as the primary channel. May be worth keeping in mind as a recovery hatch ("the pipe is wedged — drop a file to force a self-restart"). Not in v1 scope.

</approach-e-file-watcher>

<approach-f-wm-copydata>

**Shape.** Agent sends a `WM_COPYDATA` Win32 message to a hidden window DevReload registers. Payload is a UTF-8 JSON request; a small shared-memory region holds the response.

**Pros.**
- Fast.
- Plays nice with AutoCAD's main thread (the message arrives on the UI thread for free).

**Cons.**
- P/Invoke gymnastics on the client side. Awkward from PowerShell, painful from agent scripts.
- No streaming of progress (e.g. build output) without a side channel.
- No reasonable client story without a wrapper exe — which puts us back at Approach B but with worse mechanics.

**Effort.** ~2 days; payoff is questionable.

**Verdict.** Reject.

</approach-f-wm-copydata>

</approach-comparison>

<recommendation>

**Build Approach A as the core. Build Approach B on top of it later if/when the MCP shape pays off.**

The named-pipe server is the structurally correct answer: it solves the stated problem with no new dependencies, doesn't touch any of the load-bearing parts of DevReload (ALC management, command registration, build pipeline), and survives the exact bootstrap problem that motivates this work. The MCP bridge is a UX wrapper around it for one specific class of clients (Claude Code) — useful, but optional.

The decision tree we resolve with this:

| Concern | Resolution |
|---|---|
| Agent needs to reload a plugin without typing in AutoCAD's command line | Pipe call → DevReload's existing `PluginManager.DevReload(name)` runs on main thread via Idle pump |
| Agent is iterating on the plugin that hosts the agent's RPC (the ACD-MCP self-reload problem) | Pipe lives in DevReload's default ALC, not the plugin's collectible ALC → unaffected by the plugin reload |
| Multiple AutoCAD instances open at once | Pipe name carries the PID → trivial isolation |
| Agent needs to know "did my reload actually land" | Pipe verb `get_loaded_assembly_info(name)` returns AssemblyName + Location + LastWriteTime |
| Agent needs to recover from a wedged state | Pipe verb `unload_all` + `unregister(name)` + `reload_config()` reads `plugins.json` and re-bootstraps |
| Security / multi-user | Default-ACL named pipe restricts to the AutoCAD process's user |
| Firewall prompts | None — pipes don't bind sockets |

</recommendation>

<verb-surface-v1>

Concrete verb list for v1. Names are illustrative; final naming during design review.

<lifecycle-verbs>
- `ping()` → `{ pid, version, uptimeSec }`. Liveness probe.
- `list_plugins()` → `[{ name, loaded, dllPath, projectFilePath, buildConfiguration, commandPrefix, activeWorktreePath, loadOnStartup }]`.
- `is_loaded(name)` → `bool`.
- `load(name)` → `{ ok, error?, commandsRegistered? }`.
- `unload(name)` → `{ ok, error?, alcCollected? }`. Returns `alcCollected=true` only if the collectible ALC was confirmed GC'd within a short wait window (see `<alc-collection-confirmation>` below).
- `reload(name, { build: bool = true, configuration?: "Debug"|"Release" })` → `{ ok, buildSummary?, error? }`. The headline verb.
- `unload_all()` → `{ unloaded: number }`.
</lifecycle-verbs>

<build-and-config-verbs>
- `build(name, configuration?)` → `{ ok, dllPath?, buildSummary?, error? }`. Builds without reloading. Useful for "did my edit compile?" checks.
- `set_build_configuration(name, "Debug"|"Release")` → `{ ok }`.
- `set_worktree(name, path?: string|null)` → `{ ok }`. `null` clears.
- `set_load_on_startup(name, value: bool)` → `{ ok }`. Critical for autonomous bootstrap of the next AutoCAD session.
- `get_assembly_info(name)` → `{ assemblyName, location, lastWriteUtc, version, isLoaded }`. The "is my new code actually running" probe.
</build-and-config-verbs>

<registry-verbs>
- `reload_config()` → re-reads `plugins.json`, registers any new entries, unregisters removed ones. Returns delta.
- `get_config_path()` → `%APPDATA%\DevReload\plugins.json`.
</registry-verbs>

<diagnostic-verbs>
- `tail_log(maxLines: int)` → returns last N lines of DevReload's recent activity (build output, load errors). Implies adding a small in-memory ring buffer; agent can `tail_log(50)` after a failing `reload` to get context.
</diagnostic-verbs>

<deliberately-excluded>
- No "run arbitrary AutoCAD command" verb. That's plugin-side concern (ACD-MCP).
- No "invoke method on loaded plugin via reflection" verb. Same reason.
- No "modify SharedAssemblies.Config.json" verb in v1. Config edits go through the existing palette UI. Revisit if real demand emerges.
</deliberately-excluded>

</verb-surface-v1>

<implementation-sketch>

Pure design sketch — not a commitment to specific code. Surfaces the structural decisions for review.

<thread-marshaling>
The pipe server runs on a worker thread. Every verb that touches AutoCAD APIs must run on the main thread. Implementation:

```
private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
// In Initialize(): Application.Idle += DrainQueue;
private static void DrainQueue(object? s, EventArgs e) {
    while (_mainThreadQueue.TryDequeue(out var work)) {
        try { work(); } catch { /* logged, never bubbles into Idle */ }
    }
}
// Verb handler:
private Task<TResult> OnMainThread<TResult>(Func<TResult> work) {
    var tcs = new TaskCompletionSource<TResult>();
    _mainThreadQueue.Enqueue(() => {
        try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); }
    });
    return tcs.Task;
}
```

This is the same pattern ACD-MCP uses. No new invention.

</thread-marshaling>

<json-rpc-framing>
Single-line JSON per message, newline-delimited (no length prefix). Request: `{ "id": <int>, "method": "<verb>", "params": { ... } }`. Response: `{ "id": <int>, "result": ... }` or `{ "id": <int>, "error": { "code", "message" } }`.

Why line-delimited and not JSON-RPC 2.0 over Content-Length frames: smaller surface, easier to debug with `Get-Content` on the pipe-side log, easier to call from PowerShell with `StreamReader.ReadLine()`. The same protocol ACD-MCP's `Invoke-AcdMcpPipe.ps1` uses.

</json-rpc-framing>

<pipe-lifecycle>
- Server starts in `DevReloaderCommands.Initialize()`, before any plugin auto-load. This means even `loadOnStartup=true` plugins are reachable; if they fail to load, the pipe is still up to report it.
- Server stops in `DevReloaderCommands.Terminate()`.
- Stale pipes from a previous AutoCAD process don't conflict because the PID is in the name. No cleanup needed.
- Pipe accepts one connection at a time (`PipeOptions.Asynchronous` + a connection loop). Agent's requests are inherently serialized — exactly what AutoCAD's main thread wants.
</pipe-lifecycle>

<alc-collection-confirmation>
A latent reliability issue called out in the reference doc (`<reload-the-plugin-procedure>` → "AutoCAD held a FileShare.Read lock on Acd.Mcp.dll long enough to fail an immediate `dotnet build`"). After `_context.Unload()`, the collectible ALC is unloaded *asynchronously*. The DLL on disk may stay locked until GC finalizes the ALC. DevReload already does `GC.Collect()` x2 in `PluginHost.Unload()` — sometimes that's not enough.

Proposed enhancement: `PluginHost.Unload()` keeps a `WeakReference` to the unloaded ALC. The new pipe verb `unload(name)` polls the WeakReference for up to ~2 seconds, returning `alcCollected: true/false`. The agent then knows whether to retry `build` or wait.

This is a small, contained, separate enhancement. I am calling it out per CLAUDE.md rule-3 (report code smells / latent issues) and rule-7 (no silent scope creep): it is **not** included in the v1 scope above, but should be the second item shipped because it directly affects the agent's reliability on the reload→build cycle the proposal is meant to enable.

</alc-collection-confirmation>

<command-trigger-side-channel>
The reference doc points at one more concrete agent pain point: after a successful reload, ACD-MCP needs `ACDMCP_START` to wake its pipe. The agent can't type that command. Two ways to address this from DevReload's surface:

1. **Optional `postLoadCommand` in `plugins.json`.** When `load`/`reload` succeeds, DevReload queues an `[CommandMethod]` invocation on the main thread (via `Document.SendStringToExecute` against the active document). Set once in config, automatic forever.

2. **Pipe verb `invoke_command(name)`.** Agent explicitly fires a registered AutoCAD command. Maximum flexibility.

Recommend: both. Option 1 is the right default for production-style autoload. Option 2 is the lower-level escape hatch for the agentic loop. The two are cheap and don't conflict.

This too is a **separate item** — calling it out for the same rule-3 / rule-7 reasons. The v1 lifecycle verbs above are sufficient for the headline goal; these are quality-of-life adds for the second iteration.

</command-trigger-side-channel>

</implementation-sketch>

<risks-and-open-questions>

<risk-1-deadlocks>
The Idle-pump pattern serializes all pipe-triggered work behind AutoCAD's command loop. If a verb handler blocks (e.g. `build`, which takes seconds to minutes), the AutoCAD UI is responsive but no other agent verbs can run until it returns. For long-running verbs, decide: run synchronously on the main thread (simple, blocking) or run async on a worker thread and only marshal the final state-mutation to the main thread (complex, non-blocking).

Recommend: synchronous in v1. The agent expects `reload` to be a single round-trip; "blocking the UI for a 5-second build" is the existing behavior of every AutoCAD `[CommandMethod]` already.
</risk-1-deadlocks>

<risk-2-reentrancy>
What happens if `reload(A)` is called while `reload(B)` is mid-build? Today: nothing, because the agent serializes from one shell. But a misbehaving agent could fire two requests on two pipe connections. The single-connection-at-a-time pipe server eliminates the race at the transport level. Document this so a future "accept multiple connections" change doesn't silently break the invariant.
</risk-2-reentrancy>

<risk-3-reflection-stability>
This isn't a DevReload risk per se, but a related one in the reference doc: the agent reflects into ACD-MCP's private fields (`_batchRpc._uiState`). A rename refactor silently breaks the harness. Since the new DevReload pipe gives the agent a **stable, typed** way to drive the lifecycle, DevReload itself is no longer subject to this risk. But the broader pattern — agents reflecting into plugins — remains. Worth noting in the proposal so future plugin authors who want agent-driven testing have a model to copy: expose a typed verb surface, don't expect agents to reflect into private state.
</risk-3-reflection-stability>

<open-question-1-distribution>
The pipe lives in `DevReload.dll`, which AutoCAD already auto-loads via `acad2025.lsp`. So nothing new ships at install time. But if we also add the MCP bridge (Approach B), that's a new exe + a `.mcp.json` entry the user installs via `claude code plugin install`. Acceptable, but a decision for "later".
</open-question-1-distribution>

<open-question-2-shipping-the-helper-script>
The PowerShell client (`Invoke-DevReloadPipe.ps1`) is the path of least resistance for the agent. Where does it ship? Options: in the repo's `scripts/` dir, deployed alongside the bundle, or generated on demand. Recommend `scripts/` in the repo; the agent's project setup CLAUDE.md can reference it.
</open-question-2-shipping-the-helper-script>

<open-question-3-naming>
Pipe naming: `\\.\pipe\devreload-<pid>` is a defensible default. Open question: do we ALSO write a discovery file at `%APPDATA%\DevReload\pipe.txt` so an agent doesn't have to look up the AutoCAD PID? Or does the agent always know the PID anyway (since it launched AutoCAD)? Probably yes for the autonomous-bootstrap scenario; "no" for the user's-AutoCAD-is-already-open scenario. A small `pipes.json` listing `[{ pid, pipeName, startedUtc }]` covers both — DevReload writes its own entry on Initialize, removes it on Terminate.
</open-question-3-naming>

</risks-and-open-questions>

<scope-and-effort>

<v1-scope>
1. `NamedPipeServer` in `DevReload.dll`, started in `Initialize()`, stopped in `Terminate()`.
2. JSON-RPC line-delimited framing.
3. Lifecycle verbs: `ping`, `list_plugins`, `is_loaded`, `load`, `unload`, `reload`, `unload_all`.
4. Build/config verbs: `build`, `set_build_configuration`, `set_worktree`, `set_load_on_startup`, `get_assembly_info`.
5. Registry verbs: `reload_config`, `get_config_path`.
6. Diagnostic verb: `tail_log` (plus the in-memory ring buffer that backs it).
7. `Invoke-DevReloadPipe.ps1` helper in `scripts/`.
8. Discovery file: `%APPDATA%\DevReload\pipes.json` (per `<open-question-3-naming>`).
9. Smoke-test script that exercises every verb against a real AutoCAD instance and an example plugin.

**Estimated effort:** 2–3 days of focused work for one developer who knows the codebase. A small, well-bounded module — `DevReload.Rpc/` namespace, ~600 LOC counting tests.

</v1-scope>

<v2-followups>
1. `WeakReference`-based ALC collection confirmation in `PluginHost.Unload()` (per `<alc-collection-confirmation>`).
2. `postLoadCommand` config field + main-thread `SendStringToExecute` (per `<command-trigger-side-channel>`).
3. `invoke_command(name)` pipe verb (same section).
4. `DevReload.Bridge.exe` MCP shim wrapping the pipe (Approach B).

Each is independent and individually justifiable. Ship in this order.

</v2-followups>

<not-doing>
- COM/IDispatch surface.
- HTTP/REST surface.
- File-watcher channel as the primary path.
- WM_COPYDATA.
- Token-based auth (per-user pipe ACL is sufficient).
- Cross-machine remote control.
- Modifying `SharedAssemblies.Config.json` via the pipe.
</not-doing>

</scope-and-effort>

<how-this-changes-the-acd-mcp-iteration-loop>

For concreteness, here's the agent's iteration cycle today vs after this proposal lands. Source for "today": `H:\GitHub\shtirlitsDva\ACD-MCP\docs\computer-use-from-claude-code.md` → `<reload-the-plugin-procedure>`.

<today>
DevReload reloads the plugin at runtime — AutoCAD stays up the whole time. That part already works. The pain is in **how the agent triggers the reload from outside the AutoCAD window**.

For a plugin that is NOT the agent's RPC host (the easy case):
1. Edit source.
2. Via the plugin-side RPC (e.g. ACD-MCP's REPL): `Doc.SendStringToExecute("OTHERPLUGINUNLOAD ", true, false, true);` — queues the AutoCAD command.
3. `dotnet build ...`. May fail because AutoCAD holds a `FileShare.Read` on the old DLL until the collectible ALC is finalized. Workaround per the ref doc: `mv X.dll X.dll.old; dotnet build`.
4. Via the same RPC: `Doc.SendStringToExecute("OTHERPLUGINLOAD ", true, false, true);`.
5. Optionally invoke the plugin's `[CommandMethod]` warm-up command the same way.

Workable, but coupled to a *separate* plugin (ACD-MCP) being present and healthy. If ACD-MCP isn't loaded, the agent has no on-ramp.

For a plugin that IS the agent's RPC host (the ACD-MCP self-reload case):
1. Edit source.
2. Via ACD-MCP's REPL: queue `ACDMCPUNLOAD`. The command executes → the collectible ALC tears down → the pipe dies as part of the teardown. Agent loses its channel mid-cycle.
3. Agent now has no way to send `ACDMCPLOAD`. The reference doc's `<autonomous-bootstrap>` chain (flip `loadOnStartup`, restart Civil 3D, `Application.Idle` hook to auto-start the pipe) exists exactly because the agent has no way to recover without restarting AutoCAD.

This second case — the bootstrap / self-reload problem — is the load-bearing pain point this proposal targets. The first case is also improved (no dependency on ACD-MCP being the on-ramp) but is not on fire today.

</today>

<after>
1. Edit source.
2. Agent: `Invoke-DevReloadPipe.ps1 -Method reload -Params @{ name='Acd.Mcp' }`. DevReload builds, unloads, waits for ALC collection, reloads, returns `{ ok: true, buildSummary: ..., commandsRegistered: 7 }`. The pipe is hosted by DevReload (not the plugin), so this works even when reloading the plugin that normally hosts the agent's RPC.
3. Agent: `Invoke-DevReloadPipe.ps1 -Method invoke_command -Params @{ name='ACDMCP_START' }` (or eliminated entirely if `postLoadCommand` is configured in `plugins.json`).
4. Agent continues via the plugin's own RPC as before.

**Wall-clock per cycle:** the build cost (~5–15s) plus the ALC unload settle (~0.5s) plus the plugin's own warm-up. AutoCAD never restarts. No human at the keyboard.

</after>

The headline win is that the self-reload bootstrap problem disappears: the channel that triggers the reload is not the channel that gets torn down by the reload.

</how-this-changes-the-acd-mcp-iteration-loop>

<approval-asks>

Before any code lands, three concrete decisions for the user:

1. **Approve v1 scope as listed in `<v1-scope>`.** Or strike specific verbs / add others.
2. **Approve the v2 follow-up order in `<v2-followups>`.** Or reorder. The ALC-collection enhancement in particular is the one I'd argue most strongly for shipping early.
3. **Decide on the MCP-bridge question.** Build it eventually (`<v2-followups>`)? Don't bother (PowerShell shim is good enough)? Build it first instead of the pipe (unlikely — the pipe is a precondition)?

Once these are settled, I'd propose to persist the agreed shape into `docs/shared-understanding/agentic-control-surface.md` (per the AI-driven-development rules in CLAUDE.md) and start cutting code.

</approval-asks>

</proposal-agentic-control-surface>
