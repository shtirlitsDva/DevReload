<mcp-server-tools-drop-diagnosis>

Date: 2026-06-23. Investigator: Claude (external NorsynDrawingTools session).
DevReload plugin v2.1.0. Bridge: `dotnet .../server/Acad.Rpc.Bridge.dll` (stdio MCP).

<symptom>
Mid-session, the whole `mcp__plugin_devreload_devreload__*` tool surface (all 46:
`acad_*`, `devreload_*`, `ui_*`) vanishes from the agent's catalog. Recurring,
across many days and projects.
</symptom>

<verdict>
HIGH CONFIDENCE: **The DevReload bridge is NOT crashing. The MCP CLIENT (Claude
Code) terminates the bridge's process tree, and the running agent session is
never re-populated with the respawned server's tools.** This is a client-side
lifecycle / multi-session issue, not a bug in the bridge C#. There are
DevReload-side mitigations that make the respawn invisible (below), but they do
not stop Claude Code from killing the server.
</verdict>

<evidence>
1. Bridge architecture (`BridgeRpcHost.cs`, `InstanceConnection.cs`): `acad_*`
   are LOCAL tools (always in `tools/list`); `devreload_*`/`ui_*` are REMOTE
   (forwarded from the in-AutoCAD pipe). A dead AutoCAD instance only triggers
   `ReaderLoopAsync` -> `Disconnect()` -> a `tools/list_changed`; the local
   `acad_*` survive. So losing EVEN the local `acad_*` => the whole stdio server
   went away (bridge process died or transport closed), not a remote refresh.

2. Client-initiated termination, not a crash. The reporting session's MCP log
   (`%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project>\mcp-logs-plugin-devreload-devreload\2026-06-23T08-26-38Z.jsonl`)
   ends with:
     - 09:02:55  devreload_load_plugin completed (last devreload call)
     - ...24 min of only local (non-devreload) work...
     - 09:26:23  "Terminating MCP server process tree"   <- the disconnect
   No stderr error precedes it; the bridge connected cleanly and every tool call
   succeeded.

3. Recurring + paired. Across 92 devreload MCP log files (all projects),
   "Terminating MCP server process tree" appears 20x this month, frequently in
   pairs minutes apart (2026-06-23 09:26:23 AND 09:31:21; 2026-06-11 08:20:53 +
   08:21:50; 2026-06-16 12:35:30 + 12:52:59). Pairing = session/reconnect churn.

4. No crashes in any log. Grep of all 92 logs for `"error"` returns only benign
   `AutoAttach: bound to pid ...` / `skipped - no acad.exe` info and ordinary
   per-call tool errors (`No pid: pass pid...`, `COM unreachable for pid...`).
   Nothing fatal.

5. Two live bridge processes at investigation time (pids 42784, 43512),
   belonging to DIFFERENT Claude sessions/projects. devreload is a globally-
   installed plugin => every Claude session in every project spawns its own
   bridge. Multiple sessions => teardown/respawn churn.

6. Respawn loses the binding. The new bridge (started 09:33:35) logged
   `AutoAttach: skipped - no acad.exe processes running`; the AutoCAD instance
   came up ~20 s LATER. `AutoAttach` (`AutoAttach.cs`, called once in
   `Program.cs`) runs at startup ONLY (no retry), so it misses an instance that
   appears seconds later, and only binds when that instance's DevReload pipe is
   already up. Net: even after the client respawns the bridge, it's unbound and
   the tools look offline until the agent calls `acad_attach`.
</evidence>

<why-claude-code-terminates-it>
To CONFIRM against the Claude Code side — strong correlation, not proven from CC
source:
- Leading hypothesis: MULTIPLE concurrent Claude Code sessions/windows loading
  the global devreload plugin (observed: two sessions on the SAME project plus a
  third on the DevReload repo). Opening / reconnecting / closing sessions appears
  to tear down and respawn the per-project server -> the paired terminations.
- Weaker: idle reaping (a 24-min no-call gap preceded the 09:26 kill).
</why-claude-code-terminates-it>

<fix-directions>
A. Client-side (most impactful; not bridge code)
   - Avoid >1 concurrent Claude session loading devreload, especially two on the
     same project. Test: with a single session, do the paired terminations stop?
   - Review Claude Code MCP lifecycle (version, idle timeout, per-project
     singleton restart-on-attach). Consider scoping devreload per-project rather
     than global so unrelated sessions don't spawn/kill it.
   - In-session recovery after a drop: `/mcp` -> reconnect `devreload`, then
     `acad_attach <pid>` to rebind the running AutoCAD.

B. DevReload-side mitigations (make the inevitable respawn invisible)
   - Make AutoAttach a CONTINUOUS background retry (~60-120 s after startup),
     not one-shot, so an AutoCAD that appears shortly after respawn is rebound
     automatically. (`Program.cs` calls `AutoAttach.TryAttach` exactly once.)
   - Persist the last-bound pid (state file in %LOCALAPPDATA%/%TEMP%); on respawn
     prefer re-attaching to that pid when its pipe reappears — even when several
     AutoCADs run (today's "ambiguous" case currently stays unbound).
   - Decouple launched AutoCAD from the bridge's process tree. VERIFY
     `Controller.Launch` (Acad.Process): if `acad_start` launches acad.exe as a
     CHILD of the bridge, a client "terminate process TREE" will KILL the user's
     AutoCAD too. Launch detached (DETACHED_PROCESS / CREATE_BREAKAWAY_FROM_JOB /
     its own job object) so a bridge teardown never takes AutoCAD with it. (In
     the observed incident AutoCAD survived only because it was user-launched +
     attached, not acad_start-ed.)

C. Diagnostics for the next occurrence
   - Add a bridge startup stderr line with bridge pid + resolved bound pid.
   - Correlate "Terminating MCP server process tree" timestamps in the jsonl
     logs against session open/close (`claude --debug`).
</fix-directions>

<key-files>
- `src/Autocad/Acad.Rpc.Bridge/Program.cs` — startup; one-shot AutoAttach.
- `src/Autocad/Acad.Rpc.Bridge/AutoAttach.cs` — one-shot bind policy (make it retry).
- `src/Autocad/Acad.Rpc.Bridge/AcadProcessTools.cs` — Start/Quit (check Launch child-process behavior).
- `src/Autocad/Acad.Rpc.Bridge/BridgeRpcHost.cs`, `InstanceConnection.cs`,
  `ForwarderPool.cs` — transport + pipe lifecycle (already robust; not the cause).
- Logs: `%LOCALAPPDATA%\claude-cli-nodejs\Cache\<project>\mcp-logs-plugin-devreload-devreload\*.jsonl`.
</key-files>

</mcp-server-tools-drop-diagnosis>
