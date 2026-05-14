<feature>Agent-driven AutoCAD/Civil 3D control surface</feature>

<status>
LOCKED — design approved on 2026-05-14 with corrections folded in. Implementation in progress per the execution order at the bottom of this file.
</status>

<purpose>
DevReload today lets a human (or an agent already connected to AutoCAD) develop a plugin without restarting AutoCAD. The remaining gap for **fully autonomous** plugin development is that the agent cannot:

1. Start AutoCAD/Civil 3D when it is not running.
2. Wait for AutoCAD to reach a quiescent state.
3. Open a test drawing (most plugins assume an active doc).
4. Send native AutoCAD commands (some plugin flows must be invoked via the command line).
5. Cleanly stop AutoCAD between iterations.

These are *process-level* concerns — they exist BEFORE and AROUND the in-process plugin RPC. Mixing them into DevReload would couple the plugin lifecycle with the host lifecycle. So: a new module, exposed through the same MCP the agent already uses.

The user's framing: *"this autocad module must be a separate module so we don't mix devreload and autoacad control, but ONE mcp."*
</purpose>

<naming>
Names matter — the user noted the current "devreload mcp" branding is too narrow for the broader vision.

<terminology-proposal>
- **MCP server name** (what the agent sees in its client config): `acad-agent` (or `civil3d-agent`). Generic enough to host devreload + process-control + any future module.
- **New .NET project**: `Acad.Process` (lives under `src/`). Exposes process lifecycle + COM-level controls. Contributes tools to the same `Acad.Rpc.Core` registry with `[AcadRpcSurface(Group = "acad")]`, so tools appear as `acad_start`, `acad_is_quiescent`, etc.
- **Tool prefix**: `acad_*` for process-control, distinct from `devreload_*`.
</terminology-proposal>
</naming>

<core-architectural-tension>
**Where do the process-control tools live?**

Today the architecture is:
- Inside AutoCAD: `Acad.Rpc.Core` hosts a named pipe `\\.\pipe\acad-rpc-<pid>` with all tools.
- Outside AutoCAD: `Acad.Rpc.Bridge` is a stateless byte forwarder (stdin↔pipe).

This works perfectly when AutoCAD is already running. But process-control tools must be callable BEFORE AutoCAD exists. So they cannot live inside the AutoCAD process. They must live in (or be hosted by) the bridge.

This is the central design decision. Two viable approaches:

<decision>
The bridge becomes JSON-RPC aware. It hosts its own tool registry — `Acad.Process` tools auto-discovered the same way DevReload's surface is discovered. It exposes a unified `tools/list` that merges:
- Its local catalogue (always available, even when no AutoCAD is running)
- The in-AutoCAD pipe's catalogue (when a pipe is bound)

For `tools/call`: dispatches locally if the tool is in the local catalogue; otherwise forwards to the bound pipe. If no pipe is bound and the tool is remote, returns a structured "AutoCAD not running" error.

Cleanly enabled by a three-layer split of `Acad.Rpc.Core`:
- **`RpcCore`** — registry + JSON-RPC dispatch + attribute scanning + auto-discovery. Knows nothing about transports.
- **`IAcadRpcTransport`** + `NamedPipeTransport` / `StdioTransport` — transport-specific I/O loops.
- **`AcadRpcHost`** (in-AutoCAD, singleton facade) composes `RpcCore` + `NamedPipeTransport`. Public API unchanged.
- **`BridgeRpcHost`** (in-bridge) composes `RpcCore` + `StdioTransport` + `PipeForwarder` for unknown tool names.

`list_changed` is published by `RpcCore` as an event the transport(s) subscribe to.

Why this split is the right answer: one registration mechanism, one attribute set, one schema builder, one set of error codes. Plugin authors write `[AcadRpcSurface]` types and the SAME contract works inside AutoCAD AND in the bridge. Any future surface (websocket transport, HTTP transport, embedded test transport) drops onto the same `RpcCore`. This is the deep-module / well-bounded-boundary principle applied at the protocol level.
</decision>

<module-boundary>
**`Acad.Process`** = the new project. Pure C#. No reference to AutoCAD .NET assemblies. References `Acad.Rpc.Core` only.

Public concepts (one canonical type each):
- `AcadProcessController` — find/launch/quit AutoCAD instances at the OS level (`Process.Start`, `Process.GetProcessesByName`, registry-driven path discovery).
- `AcadComClient` — late-bound COM wrapper. Attaches to a specific PID via the Running Object Table (mirroring `VsInstanceFinder`'s ROT enumeration pattern). Exposes `IsQuiescent`, `SendCommand`, `OpenDocument`, `NewDocument`, `Quit`, `ActiveDocumentName`, `AcadProductName`.
- `AcadProcessTools` — the `[AcadRpcSurface(Group = "acad")]` catalogue. Thin facade over the two classes above.

Why these split: testability. The COM client and the process controller are testable in isolation (the COM wrapper against a real running AutoCAD; the controller via `Process` abstractions). The tools class is a one-line-per-method facade — no logic, no test value of its own beyond the surface contract test (see Testing).
</module-boundary>

<tool-catalogue>
Locked list. The principle: one rich `list` tool per noun (don't fan out into ten predicates the agent can derive). Same shape as DevReload's surface.

<lifecycle>
- `acad_list_instances` — enumerate running AutoCAD/C3D processes (pid, product name from COM, main-window title, pipe availability).
- `acad_start` — launch a new AutoCAD process. Args: `flavor` (defaults to `civil3d`), `productVersion` (e.g. "25.0", optional — picks newest installed if omitted), `installPath` (optional override), `profile` (optional), `drawingPath` (optional — open this drawing on startup), `visible` (default true). Auto-binds the bridge to the new pid. Returns: pid + estimated time-to-ready + product name.
- `acad_attach` — bind the bridge to an already-running AutoCAD by pid. Args: `pid`. Returns: bound `Instance` state.
- `acad_detach` — release the current binding (no-op if not bound). Pipe forwarder closes.
- `acad_wait_quiescent` — block until `IsQuiescent` AND `HasActiveDocument` are both true, or timeout. Args: `pid` (defaults to bound), `timeoutSeconds` (default 300), `requireActiveDocument` (default true). Returns: actual wait time + final state.
- `acad_quit` — graceful shutdown via COM `Quit()`. Args: `pid` (defaults to bound), `saveChanges` (default false), `timeoutSeconds` (default 10). Falls back to `Process.Kill` if COM doesn't return in budget; result tags `Graceful` vs `Killed`. Auto-detaches on success.
</lifecycle>

<state-query>
- `acad_get_state` — single rich query against a pid: `IsQuiescent`, `HasActiveDocument`, `ActiveDocumentName`, `ProductName`, `WindowState`, `Visible`. One round-trip = full picture.
</state-query>

<commands-and-docs>
- `acad_send_command` — `AcadApplication.ActiveDocument.SendCommand(commandString)`. **Blocking** — the COM call returns after AutoCAD has finished executing the command. Use when the agent needs to know the command completed before the next tool call.
- `acad_post_command` — `AcadApplication.ActiveDocument.PostCommand(commandString)`. **Non-blocking** — queues the command and returns immediately. AutoCAD executes it on the next pump cycle. Use when chaining commands or when the next state read happens after a `acad_wait_quiescent`.
- `acad_open_drawing` — `Documents.Open(path, readOnly)`. Args: `pid`, `path`, `readOnly` (default false).
- `acad_new_drawing` — `Documents.Add(templatePath)`. Args: `pid`, `templatePath` (optional; null = default).
- `acad_close_active_drawing` — `ActiveDocument.Close(saveChanges)`.
</commands-and-docs>

<discovery>
- `acad_locate_install` — read registry, return all installed AutoCAD/C3D versions with their install paths. Helps the agent pick a flavor/version when invoking `acad_start`.
</discovery>

The full picture: ~13 tools on the `acad_*` surface (lifecycle 6 + state 1 + commands/docs 5 + discovery 1). Combined with DevReload's 16, the agent sees ~29 tools — still well under MCP-client tool-list limits.

Out of scope for v1 (deliberate YAGNI): screen captures, drawing-content queries, undo control, profile management, audit/recover, plot. These are plugin-domain concerns — plugins should expose their own RPC surfaces.
</tool-catalogue>

<idle-detection>
COM exposes `AcadApplication.GetAcadState()` which returns an `AcadState` object with `IsQuiescent` (bool) and `Mode` (`acQuiescent` / `acNotQuiescent`). This is the official idle marker.

Quiescent is *necessary but not sufficient* for "ready to drive". AutoCAD can be quiescent during the start screen (no doc open) — most plugins error in that mode. So the agent's typical wait is "quiescent **AND** has an active document". Both flags come from `acad_get_state`; `acad_wait_quiescent` polls them with a budget.

Polling interval: 250ms. (`IsQuiescent` is a fast COM call.) Max budget for cold start: **300s default, configurable per call** (Civil 3D start-up on a slow workstation with a cold cache and a fresh profile can run several minutes; the timeout is generous because a too-short budget masks legitimately-slow but still-progressing startups).

Failure modes the wait must distinguish:
- Timeout (return: still-not-quiescent + last observed state).
- Process exited (Civil 3D crashed during startup — return: dead).
- COM unreachable (the COM object is gone but the process is alive — typically means a modal dialog box is up; return: blocked-by-dialog).
</idle-detection>

<startup-state-model>
Three startup modes the user named:

<mode name="empty-drawing">
AutoCAD opens with a blank `Drawing1.dwg`. `HasActiveDocument = true`. Most plugins work.
</mode>

<mode name="start-screen">
AutoCAD opens to the "Start" tab. `HasActiveDocument = false`. Most plugins crash on first command.
**This is the user's current default.** Therefore: the agent skill must always follow `acad_start` with `acad_open_drawing` (or `acad_new_drawing`) before driving any plugin.
</mode>

<mode name="hidden-no-doc">
The COM-instantiated case (when something creates AutoCAD via `Activator.CreateInstance` on the ProgID). `Visible = false`, no doc. We avoid this mode for agent work — too fragile for plugin testing.
</mode>

The `acad_start` tool's `drawingPath` argument lets the agent collapse start+open into one call when a test drawing path is known. When omitted, the agent gets a started-but-not-ready process and must follow up with explicit drawing setup.
</startup-state-model>

<bridge-protocol-changes>
Concrete shape of the Option-A bridge refactor:

1. **Extract** `Acad.Rpc.Core.RpcDispatcher` from `AcadRpcHost`. It owns: registry, attribute scanning, `DispatchAsync`, `InvokeToolAsync`. Constructor takes `IAcadMainThreadDispatcher` and `Action<string>? log`. Knows nothing about transports.

2. **Keep** `AcadRpcHost` as the public in-AutoCAD entry — it now composes `RpcDispatcher` + the pipe server loop. Public API unchanged. `EnableAutoDiscovery` etc. delegate to the dispatcher.

3. **New** `BridgeRpcHost` in `Acad.Rpc.Bridge`. Composes `RpcDispatcher` + stdio I/O + a `IPipeBackend` that knows how to forward unknown tools to the in-AutoCAD pipe. The dispatcher in the bridge gets a `MainThreadDispatcher` that throws (process-control tools never need an AutoCAD UI thread — they ARE the AutoCAD UI thread's manager).

4. **Bridge dispatch flow** for each agent request:
   - `initialize` → bridge answers itself (server name = `acad-agent`).
   - `tools/list` → bridge merges its local list with the pipe's list (if reachable). When pipe is down, only local tools are returned. When pipe comes up later, bridge sends `notifications/tools/list_changed`.
   - `tools/call` → bridge checks local registry first; if hit, dispatch locally; if miss, forward to pipe; if miss AND pipe down, return structured error: "AutoCAD not running; start it with `acad_start`."

5. **Pipe discovery in the bridge** — today the bridge picks one pipe at startup. New behavior: it can also start with NO pipe and connect lazily once a pid is bound (`acad_start` auto-binds; `acad_attach` binds explicitly). The bridge derives the pipe name (`acad-rpc-<pid>`) and polls until the pipe appears, then publishes `notifications/tools/list_changed` so the agent re-fetches the merged catalogue.

6. **Bridge state model** — a `BoundInstance` record: `{ Pid, ProductName, PipeName, PipeConnected, BoundAt }`. Mutated by `acad_start` / `acad_attach` / `acad_detach` / process-exit watcher. Read by every `acad_*` tool that has a default-pid fallback.
</bridge-protocol-changes>

<com-strategy>
**Late binding via ROT**, not interop assemblies. Reasons:
- We already have a working precedent in the repo (`VsInstanceFinder.cs` enumerates the ROT for VS instances).
- Avoids version-locked PIAs (AutoCAD's COM interop differs per major version).
- One code path works for AutoCAD AND Civil 3D AND verticals.

<rot-moniker-format priority="LOAD-BEARING">
AutoCAD/Civil 3D/verticals register in the COM Running Object Table under their **CLSID**, not under a human-readable name. Verified on Civil 3D 2025 against a normal launch: the ROT entry looks like:

```
!{363E5B47-885D-44C3-89EB-A2AB2129B57E}
```

The CLSID is the same for every AutoCAD-family product on a machine (it resolves to whatever AutoCAD-platform binary is most recently installed). It is **not** `AutoCAD.Application` or `AutoCAD.Application.25` — those are ProgIDs, used to LOOK UP the CLSID via `CLSIDFromProgID`, not to match in the ROT.

This is the single most common stumbling block when porting AutoCAD COM code from .NET Framework to .NET 8: legacy code uses `Marshal.GetActiveObject("AutoCAD.Application")` which is deprecated in .NET 8, and the replacement P/Invoke against `CLSIDFromProgID` + `GetActiveObject(rclsid, …)` requires the CLSID hop. Custom ROT enumeration must therefore match on the CLSID hex string (in braces, uppercase) — NOT on the ProgID string.

**Correction to a misleading claim found elsewhere:** there is a write-up in the sibling ACD-MCP repo (`docs/computer-use-from-claude-code.md`) stating that a fresh AutoCAD launched without a saved drawing path does not register in the ROT. That claim is specific to `/Automation` mode (hidden COM-server launch) and does NOT apply to a normal `acad.exe /product C3D` launch. A normal launch registers in the ROT immediately, with NO drawing required — `IsQuiescent` and `GetAcadState()` are reachable on a fresh Start-tab Civil 3D. The relevant evidence: AcadDiag's GetActiveObject probe against pid 51832 on this machine returned `IsQuiescent=True`, `HWND=593050`, `ProductName='Autodesk Civil 3D 2025'`, with NO drawing open.
</rot-moniker-format>

Attach flow:
1. Resolve CLSID once via `CLSIDFromProgID("AutoCAD.Application")`.
2. `IRunningObjectTable.EnumRunning` → iterate moniker display names.
3. Match `"!{<CLSID-in-braces-uppercase>}"` as a substring.
4. `GetObject` → `IDispatch`.
5. Late-bind property/method calls via `dynamic` (`.HWND`, `.Name`, `.GetAcadState().IsQuiescent`, `.ActiveDocument`, …).
6. Match instance to a pid via `GetWindowThreadProcessId(HWND)`.

Launch flow:
1. Resolve install path (registry / user setting).
2. `Process.Start(acad.exe, args)` with args controlling visibility/profile/file.
3. Poll ROT until the new pid's CLSID moniker appears.

Single-instance fast path (when multi-instance isn't needed): just call `GetActiveObject(rclsid)` directly — it returns the first matching ROT entry. Multi-instance requires the enumeration path above.

This whole layer is one class — `AcadComClient` — with two static factory methods (`AttachByPid`, `EnumerateInstances`) and the operation methods. ~300 lines, no inheritance, no interfaces.
</com-strategy>

<error-model>
Every `acad_*` tool returns either a successful structured result or a typed failure. Failure categories the agent must distinguish:

- `NotRunning` — no AutoCAD process with the given pid (or no AutoCAD at all). Recovery: call `acad_start`.
- `NotResponding` — process alive, COM unreachable. Likely a modal dialog. Recovery: human intervention (or `acad_send_command` "_.CANCEL" if we want to risk it — not in v1).
- `Timeout` — operation exceeded its budget. Includes last observed state.
- `InvalidArgument` — e.g. `acad_open_drawing` with a path that doesn't exist. Returns the offending arg and reason.
- `ComError` — wrapped HRESULT with name + code.

These map onto `CallToolResultText(..., isError: true)` with a structured JSON body the agent can parse.
</error-model>

<testing>
Three layers, matching the existing DevReload pattern:

1. **Unit (Acad.Rpc.Core.Tests)** — new test class for `RpcDispatcher` (post-extraction) verifying registry/auto-discovery behavior survives the refactor unchanged. Surface contract test for `AcadProcessTools` (mirrors `DevReloadSurfaceTests`): load the `Acad.Process.dll`, assert all expected `acad_*` tool names and required input-schema properties exist.

2. **Bridge integration** — new test project `tests/Acad.Rpc.Bridge.Tests`. Spawns the bridge as a child process with a fake pipe backend; drives JSON-RPC on stdin; asserts:
   - `tools/list` returns local tools when pipe is absent.
   - `tools/list` merges remote tools when pipe is present.
   - `tools/call` routes correctly to local vs remote.
   - `list_changed` fires when pipe comes up.

3. **End-to-end smoke (tests/smoke/)** — extend the existing harness with an autonomous test that:
   - Calls `acad_start` from outside (no AutoCAD running).
   - Waits for quiescent + active doc.
   - Drives the existing DevReload tools to register/load the example plugin.
   - Calls a plugin tool through the merged surface.
   - Calls `acad_quit`.
   - Verifies clean exit + no leaked processes.

This is the *real* coverage and the gate for shipping.
</testing>

<skill-outline>
`/acd-agentic-dev` — the user-facing skill that bootstraps an agent for autonomous plugin development. Vendored at `~/.claude/skills/acd-agentic-dev/` (out-of-repo).

What the skill brief contains:
- **Tool inventory** — the `acad_*` and `devreload_*` catalogues, with their input shapes and typical invocation order.
- **Standard workflow** — design → tests → code → start AutoCAD → open test drawing → register plugin → load → drive plugin via its `[AcadRpcSurface]` tools → gather logs → quit → iterate.
- **Drawing-state contract** — most plugins need an active doc; the skill must `acad_open_drawing` after `acad_start`.
- **Failure-recovery patterns** — when `NotResponding`, the skill should not retry indefinitely; it stops and asks the user.
- **Cleanup discipline** — every session ends with `acad_quit` even on failure.

Not in scope for this design doc: the skill's full content. That belongs in a separate skill-authoring task once the tools exist.
</skill-outline>

<locked-decisions>
Decisions made and locked. Implementation proceeds against this list.

1. **Bridge architecture** — `RpcCore` extracted; bridge and AcadRpcHost both compose it.

2. **MCP server name** — `acad-agent`. Branding-neutral across AutoCAD/C3D/verticals.

3. **AutoCAD flavor** — **Civil 3D is the default target**; plain AutoCAD and other verticals (Plant, Mechanical, …) are launchable via the `flavor` argument. Installed flavors are discovered through the registry by `acad_locate_install`.

4. **Install-path discovery** — registry probe (`HKLM:\SOFTWARE\Autodesk\AutoCAD\R<n>.0\<lang>` + the Civil 3D vertical key); env-var override for CI/headless contexts; final fallback to the user-supplied `installPath` argument on `acad_start`.

5. **Default startup composition** — explicit. `acad_start` opens the process; `acad_open_drawing` follows. Each tool does one thing. The `/acd-agentic-dev` skill documents the canonical sequence so the agent doesn't have to reinvent it.

6. **Quit fallback** — `acad_quit` tries COM `Quit()` first, falls back to `Process.Kill` after `timeoutSeconds` (default 10s). The result discriminates `Graceful` vs `Killed` so the agent knows which path was taken.

7. **Multi-instance / multi-agent — DAY ONE.** Real use case: two agents on two worktrees each driving their own AutoCAD. Bridge supports this natively:
   - Each agent runs its own `Acad.Rpc.Bridge` process — they are independent by construction.
   - Each bridge holds a single **bound pid** at any moment. `acad_start` auto-binds the new pid; `acad_attach <pid>` binds to an existing process; `acad_detach` releases.
   - Pipe address derives from the bound pid (`acad-rpc-<pid>`), so pipes are naturally namespaced by process.
   - All `acad_*` tools default to the bound pid; pass `pid` explicitly to override (e.g., `acad_list_instances` + `acad_get_state pid=...` for cross-instance inspection without binding).
   - There is no global lock — two bridges binding to two AutoCADs do not interfere.

8. **Visibility** — `visible=true` is the default and the strongly-recommended path. The user noted plugins with WPF/WinForms UI cannot reliably run headless. `visible=false` is allowed for completeness but the tool description warns it is unsupported for UI plugins.
</locked-decisions>

<execution-order-when-approved>
Once the open questions are resolved, implementation order:

1. Refactor: extract `RpcDispatcher` from `AcadRpcHost`. Confirm all existing tests stay green (no behavior change).
2. Scaffold `Acad.Process` project — controller + COM client + tool surface — with attribute-driven tools, no bridge integration yet. Surface contract test for the catalogue.
3. Stand up `BridgeRpcHost` in `Acad.Rpc.Bridge`. Wire up local catalogue + stdio. Test merges + forwarding.
4. Wire pipe-discovery-after-start in the bridge. Test reconnect + `list_changed`.
5. End-to-end smoke: cold start → drive plugin → quit.
6. Skill content for `/acd-agentic-dev`.
</execution-order-when-approved>
