<proposal-universal-rpc-surface>

<status>
LOCKED DESIGN — 2026-05-14. Author: Claude (Opus 4.7). This document defines step 1 (the in-AutoCAD plugin surface) as a single decisive design, no alternatives. Step 2 (outer supervisor surviving AutoCAD restarts) is designed in a separate document AFTER step 1 ships. Supersedes `agentic-control-surface.md`.
</status>

<scope-boundary>

**In scope (step 1).** Everything that lives inside the AutoCAD process: a single MCP server hosted by DevReload, plugins contributing tools by attribute scan, runtime register/unregister on plugin load/unload, named-pipe transport with a thin stdio↔pipe bridge for Claude Code.

**Out of scope (step 2, separate proposal).** The agent's durable outer shell: a long-lived supervisor process that survives AutoCAD restarts, can launch/restart AutoCAD, proxies the in-AutoCAD MCP surface, and exposes its own MCP tools for lifecycle control. The bridge exe defined in step 1 is the natural seed of the step-2 supervisor — step 2 evolves it; step 1 ships it minimal.

</scope-boundary>

<the-decision>

**One direction. Locked.**

- **Protocol:** Model Context Protocol (MCP), wire format only. No parallel JSON-RPC surface, no HTTP/SSE, no COM.
- **Library:** Official ModelContextProtocol C# SDK (Microsoft + Anthropic, v1.0, NuGet `ModelContextProtocol`). We do not write our own dispatch, attribute scanner, JSON-Schema emitter, or transport.
- **Topology:** ONE MCP server per AutoCAD process, hosted by `DevReload.dll`. Plugins contribute tools INTO this single host. Plugins never run their own MCP servers.
- **Attribute set:** The SDK's own — `[McpServerToolType]` on classes, `[McpServerTool]` on methods, `[Description]` on methods and parameters. One AutoCAD-specific attribute added: `[RunOnAcadMainThread]`.
- **Discovery:** `WithToolsFromAssembly(asm)` (SDK) scans an assembly at register-time. Adding a tool = adding an attribute next to an existing method. No central registry file.
- **Transport:** Named pipe `\\.\pipe\acad-rpc-<pid>` inside AutoCAD, speaking MCP wire format. A stateless stdio↔pipe bridge exe (`Acad.Rpc.Bridge.exe`) is the Claude Code-facing MCP server.
- **Threading:** `[RunOnAcadMainThread]`-marked methods are dispatched via an `Application.Idle` pump; unmarked methods run on the SDK's worker thread.
- **Shared assembly:** `Acad.Rpc.Core.dll` is loaded once into AutoCAD's default ALC (as a shared/streamed assembly per DevReload's existing `SharedAssemblies.Config.json` mechanism). Every plugin that contributes RPC tools links against it.

Everything below is the implementation of this decision. No rejected alternatives are revisited.

</the-decision>

<architecture>

```
┌──────────────────────────────────────────────────────────────┐
│ Claude Code  (MCP client)                                    │
└─────────────────────┬────────────────────────────────────────┘
                      │ MCP / stdio
┌─────────────────────▼────────────────────────────────────────┐
│ Acad.Rpc.Bridge.exe                                          │
│ Stateless stdio ↔ named-pipe MCP forwarder. ~150 LOC.        │
│ Discovers AutoCAD via pipe enumeration; one PID → one pipe.  │
└─────────────────────┬────────────────────────────────────────┘
                      │ MCP / named pipe
                      │ \\.\pipe\acad-rpc-<acad_pid>
┌─────────────────────▼────────────────────────────────────────┐
│ AutoCAD process                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ Acad.Rpc.Core.dll   (shared/streamed, default ALC)      │ │
│  │   AcadRpcHost.Current  — singleton                      │ │
│  │   IAcadMainThreadDispatcher  (Application.Idle pump)    │ │
│  │   [RunOnAcadMainThread]   attribute + interceptor       │ │
│  │   RegisterAssembly / UnregisterAssembly                 │ │
│  │   Embeds ModelContextProtocol SDK server                │ │
│  │   Owns the named-pipe transport listener                │ │
│  └─────────────────────────────────────────────────────────┘ │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ DevReload.dll   (default ALC, autoload)                 │ │
│  │   Initialize: creates AcadRpcHost, opens pipe, scans    │ │
│  │     own assembly, starts server.                        │ │
│  │   PluginManager methods annotated [McpServerTool] +     │ │
│  │     [RunOnAcadMainThread].                              │ │
│  │   On plugin load: AcadRpcHost.RegisterAssembly(asm).    │ │
│  │   On plugin unload: AcadRpcHost.UnregisterAssembly.     │ │
│  └─────────────────────────────────────────────────────────┘ │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ Plugin X.dll   (isolated collectible ALC, reload-able)  │ │
│  │   References Acad.Rpc.Core (shared, NOT copied local).  │ │
│  │   Annotates own methods [McpServerTool].                │ │
│  │   No explicit registration call needed — DevReload      │ │
│  │     scans the loaded assembly after Load() succeeds.    │ │
│  └─────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

Three new artifacts. Total ~500 LOC of new code outside annotations.

</architecture>

<packages>

<package-acad-rpc-core>
**`Acad.Rpc.Core`** — the entire universal layer. No DevReload knowledge.

Public surface (illustrative, final names settled at API-skeleton review):
```csharp
public static class AcadRpcHost
{
    public static AcadRpcHost Current { get; }                 // singleton
    public static AcadRpcHost Initialize(AcadRpcOptions opts); // called by DevReload once
    public void RegisterAssembly(Assembly asm);
    public void UnregisterAssembly(Assembly asm);
    public string ApiVersion { get; }                          // semver, logged at register
    public IReadOnlyList<RegisteredToolInfo> ListRegisteredTools();
    public Task ShutdownAsync();
}

public sealed class AcadRpcOptions
{
    public string PipeName { get; init; } = $"acad-rpc-{Environment.ProcessId}";
    public IAcadMainThreadDispatcher MainThreadDispatcher { get; init; }
}

public interface IAcadMainThreadDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct);
}

public sealed class AcadIdlePumpDispatcher : IAcadMainThreadDispatcher { /* Application.Idle queue */ }

[AttributeUsage(AttributeTargets.Method)]
public sealed class RunOnAcadMainThreadAttribute : Attribute { }

public sealed record RegisteredToolInfo(string ToolName, string SourceAssembly, string? Description);
```

Internally:
- An MCP server instance from `ModelContextProtocol` SDK.
- A custom tool-invocation filter (registered with the SDK) that inspects each call's `MethodInfo` for `[RunOnAcadMainThread]`; if present, marshals through `IAcadMainThreadDispatcher` before invoking, then unmarshals the result.
- A per-assembly tool-name index `Dictionary<Assembly, List<string>>` so `UnregisterAssembly` knows exactly which tool names to remove from the SDK's collection. Forced `GC.Collect` after removal so the unloaded plugin's collectible ALC can be reclaimed (same pattern `PluginHost.Unload` already uses).
- A pipe listener loop accepting one MCP client at a time. On disconnect, restarts. The pipe IS the MCP server's transport stream.

Out of scope for `Acad.Rpc.Core`: anything DevReload-specific, anything plugin-specific, anything that knows the names of any tools.

</package-acad-rpc-core>

<package-acad-rpc-bridge>
**`Acad.Rpc.Bridge`** — console exe, stateless stdio↔pipe forwarder.

CLI:
```
Acad.Rpc.Bridge.exe [--pid <n>] [--pipe <name>]
```

Behavior:
- If `--pipe` given, opens that pipe directly.
- Otherwise enumerates `\\.\pipe\acad-rpc-*` entries.
  - Zero matches → exit with `MCP error: AutoCAD not running` (returned over MCP so Claude Code sees a proper error).
  - One match → attach.
  - Many matches → require `--pid`; otherwise exit with error listing PIDs.
- Forwards every byte from stdin to pipe and every byte from pipe to stdout. Bidirectional. No parsing. No state.
- On pipe disconnect, exits cleanly (Claude Code observes the MCP server going away — step-2's supervisor will make this graceful).

~150 LOC. No external deps beyond the BCL.

</package-acad-rpc-bridge>

<package-devreload>
**`DevReload.dll`** — existing project, purely additive changes.

Specific edits:
1. Add reference to `ModelContextProtocol` NuGet (and `Acad.Rpc.Core`).
2. `DevReloaderCommands.Initialize`: before processing `plugins.json`, call `AcadRpcHost.Initialize(new AcadRpcOptions { ... })`, then `AcadRpcHost.Current.RegisterAssembly(typeof(PluginManager).Assembly)`. DevReload's own tools register FIRST, before any plugin loads.
3. `DevReloaderCommands.Terminate`: `await AcadRpcHost.Current.ShutdownAsync()`.
4. `PluginManager.LoadCore`: one line added at the end after `Registrar.RegisterFromAssembly` — `AcadRpcHost.Current.RegisterAssembly(reg.Host.LoadedAssembly)`. (AutoCAD will have already invoked the plugin's `Initialize` via its `AssemblyLoad`-event-driven discovery of `[ExtensionApplication]`; DevReload does not call it.)
5. `PluginManager.TearDown`: at the START of the unload sequence, before any teardown work, `AcadRpcHost.Current.UnregisterAssembly(reg.Host.LoadedAssembly)`. Then `Registrar.UnregisterAll`, then `plugin.Terminate()` (existing — AutoCAD never calls Terminate because .NET cannot unload assemblies natively; DevReload calls it because DevReload's collectible ALC actually does unload), then `Host.Unload()`. Tools are removed FIRST so the agent never sees a half-terminated plugin.
6. Annotate methods that should become tools. The list emerges from `<verbs-emerging>` below; the annotations live next to the existing methods, not in a new file.
7. Add `Acad.Rpc.Core` to the bundle output for shared-assembly distribution.

Lines of behavioral change: ~5. Lines of attribute additions: ~30. Total: under 50 LOC of edits.

</package-devreload>

</packages>

<runtime-flows>

<flow-startup>
1. AutoCAD loads `DevReload.dll` via autoload.
2. `DevReloaderCommands.Initialize`:
   a. Creates `AcadRpcHost.Current` with the Idle-pump dispatcher.
   b. Pipe `\\.\pipe\acad-rpc-<pid>` opens; MCP server is listening.
   c. Scans `DevReload.dll` itself — DevReload's own annotated methods become the first tools. **DevReload is a participant in this system, not just the host.** Its `Reload`, `Unload`, `ListRegisteredPlugins`, `BuildProject`, etc. are MCP tools from the moment the host starts, before any external plugin loads.
   d. Processes `plugins.json`: registers plugin entries, auto-loads those marked `loadOnStartup: true`.
   e. For each successfully auto-loaded plugin, the lifecycle is: `Host.Load` (bytes into collectible ALC; AutoCAD's `AssemblyLoad` handler observes the new assembly and invokes the plugin's `Initialize` itself) → `AcadRpcHost.RegisterAssembly(loadedAssembly)` (DevReload's added step). The plugin's annotated methods become tools and `tools/list_changed` fires.
3. When the user (or step-2 supervisor) starts the bridge exe and Claude Code attaches: `tools/list` returns the unified set; the agent sees DevReload's tools and every loaded plugin's tools, named by source assembly prefix.
</flow-startup>

<flow-plugin-reload>
1. Agent calls `tools/call` → `devreload_reload(name: "Acd.Mcp")`.
2. SDK dispatches; the `[RunOnAcadMainThread]` filter queues the call onto the Idle pump.
3. Idle pump runs `PluginManager.DevReload("Acd.Mcp")`. Lifecycle order:
   - **Teardown phase:** `AcadRpcHost.UnregisterAssembly(oldAssembly)` first — tools disappear from the SDK's collection; `tools/list_changed` fires. Then `Registrar.UnregisterAll` (AutoCAD commands). Then DevReload calls `plugin.Terminate()` (AutoCAD does not). Then `Host.Unload()` (ALC collected).
   - **Build phase:** `dotnet build` runs (main thread freezes for the build duration — acceptable, identical to existing palette button behavior).
   - **Reload phase:** new bytes streamed into a fresh collectible ALC. AutoCAD's `AssemblyLoad` handler observes the new assembly and invokes the new plugin instance's `Initialize`. DevReload then runs `Registrar.RegisterFromAssembly` (AutoCAD commands), then `AcadRpcHost.RegisterAssembly(newAssembly)` — rebuilt plugin's tools, possibly including new methods the developer just added, appear in the SDK's collection. `tools/list_changed` fires again.
4. Tool call returns `{ ok: true, registered: 7 }`.
5. Claude Code refreshes its tool list on the change notification. The agent sees the new methods immediately, no `/reload-plugins`, no manual step.

The agent never observes a half-terminated plugin: RPC tools are unregistered first, so any inbound RPC call lands after the SDK has already removed the tool. The plugin's `Terminate` then runs with no inbound RPC traffic possible.
</flow-plugin-reload>

<flow-plugin-shipping-tools>
A plugin author who wants their plugin's surface exposed to the agent:

```csharp
[McpServerToolType]
public static class ExampleTools
{
    [McpServerTool, Description("Returns the active document's filename.")]
    [RunOnAcadMainThread]
    public static string GetActiveDocumentName()
        => Application.DocumentManager.MdiActiveDocument?.Name ?? "";
}
```

Nothing else. No `IExtensionApplication` change, no register call, no manifest. DevReload's auto-scan on load handles it.

</flow-plugin-shipping-tools>

</runtime-flows>

<locked-details>

<shared-assembly-inclusion>
`Acad.Rpc.Core.dll` MUST resolve to the SAME loaded instance for DevReload and every plugin that uses it. Therefore:
- `Acad.Rpc.Core.dll` ships in the DevReload bundle alongside `DevReload.dll`.
- Loaded into AutoCAD's default ALC by DevReload's bootstrap via `LoadSharedFromStream` (same path DevReload already uses for streamed-shared assemblies — avoids file lock during `Acad.Rpc.Core` hot-iteration).
- DevReload's bootstrap auto-injects `"Acad.Rpc.Core"` into every plugin's effective shared-streamed list. The plugin author does not configure it; DevReload guarantees it.
- `Acad.Rpc.Core` API is versioned (`AcadRpcHost.ApiVersion`). On `RegisterAssembly`, the host logs the consuming plugin's resolved API version so version skew is visible.
</shared-assembly-inclusion>

<tool-naming>
Tool names are auto-prefixed with the source assembly's simple name (lowercase, dot→underscore): `Acd.Mcp.dll`'s `ExecuteCSharp` → `acd_mcp_execute_csharp`. The prefix is part of the tool's identity; collisions across plugins are impossible by construction. Override per tool with `[McpServerTool(Name = "...")]` if a plugin author has a strong reason — discouraged.
</tool-naming>

<multiple-autocad-instances>
Pipe name carries the PID; multiple AutoCAD instances coexist trivially. The bridge enumerates `acad-rpc-*` pipes:
- Zero pipes → MCP error response `AutoCAD not running`.
- One pipe → attach.
- Multiple pipes → require `--pid`; otherwise MCP error listing candidate PIDs.

The bridge's `.mcp.json` entry passes `--pid` if the user runs multiple AutoCADs in parallel; for the common single-AutoCAD case, no argument is needed.
</multiple-autocad-instances>

<security>
Named pipes default to allowing only the creating process's user. No token auth. No localhost-bound socket. No firewall prompt. Other users on the same machine cannot connect.
</security>

<lifecycle-contract>
Tool registrations follow plugin lifetime strictly. There are no orphans. The exact ordering for any plugin (including DevReload itself):

**On load:**
1. Bytes loaded into ALC (collectible for plugins; default for DevReload).
2. AutoCAD `[CommandMethod]` registration via `Utils.AddCommand`.
3. `plugin.Initialize()` invoked — plugin's pre-RPC setup.
4. `AcadRpcHost.RegisterAssembly(loadedAssembly)` — scans for `[McpServerToolType]` / `[McpServerTool]`, adds tools to the SDK collection, fires `tools/list_changed`.

**On unload:**
1. `AcadRpcHost.UnregisterAssembly(loadedAssembly)` — removes tools, fires `tools/list_changed`. Agent traffic to those tools stops here.
2. AutoCAD command unregistration via `Utils.RemoveCommand`.
3. `plugin.Terminate()` invoked — plugin's post-RPC cleanup runs with the RPC surface already retired.
4. ALC `Unload` + forced GC for plugins (no-op for DevReload itself, which never unloads).

This applies symmetrically to DevReload: at AutoCAD shutdown, `DevReloaderCommands.Terminate` unregisters DevReload's own tools before tearing down the host. There is no asymmetry between "the framework" and "plugins"; DevReload's tools live and die under the same rules as any plugin's tools.

Failure handling: if `plugin.Initialize()` throws, the assembly is NOT registered with the RPC host, the ALC is torn down, the load is reported as failed via the existing `PluginManager.Load` error path. The half-loaded plugin never appears in the agent's tool list.

</lifecycle-contract>

<collectible-alc-handling>
`AcadRpcHost` tracks `Dictionary<Assembly, List<string>> _toolsByAssembly` itself. On `UnregisterAssembly(asm)`:
1. Look up the tool-name list for `asm`.
2. Call the SDK's per-tool removal for each.
3. Drop the dictionary entry.
4. `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();` — same triple as `PluginHost.Unload`.
5. Probe with a `WeakReference` to the plugin assembly; if still alive after a 2-second poll, log a warning. (The plugin reload will still proceed; DevReload's existing logic handles this.)

This is the highest-risk piece of `Acad.Rpc.Core`. Step-1 acceptance gate: a smoke test reloads an example plugin ten times in a row and confirms the collectible ALC is reclaimed each time.
</collectible-alc-handling>

<thread-marshaling-and-blocking>
- `[RunOnAcadMainThread]` methods run synchronously on the Idle pump. Tool calls block the SDK worker until the main thread completes the work.
- AutoCAD UI remains responsive between Idle ticks; long-running marked tools (e.g. a build) freeze the UI for that duration — same as existing palette button behavior.
- Cancellation: a `CancellationToken` parameter on a tool method is honored by the SDK; the Idle-pump dispatcher checks it before invoking. Builds and other long ops are encouraged to accept a `CancellationToken`.
- Concurrency: pipe listener accepts one client at a time. Tool calls from that client are serialized by the SDK. No reentrancy.
</thread-marshaling-and-blocking>

<bridge-distribution>
`Acad.Rpc.Bridge.exe` ships as a Claude Code plugin in this repo at `plugins/devreload-mcp/`. The folder contains the bridge binary and a `.mcp.json` pointing at it. User installs via `claude code plugin install <path>` once; thereafter the agent has a `devreload-mcp` MCP server entry.
</bridge-distribution>

<error-mapping>
SDK maps C# exceptions to MCP errors. Convention:
- `ArgumentException` family → `InvalidParams`.
- DevReload-specific failures (build failure, unknown plugin name) → typed `RpcException` subclasses we define in `Acad.Rpc.Core`, mapped to MCP `InternalError` with structured `data` carrying `{ code, details }`.
- Unhandled exception → `InternalError` with redacted message (no stack traces over the wire).
</error-mapping>

</locked-details>

<verbs-emerging>

**DevReload itself exposes tools.** It is the first participant in the system, registered at host startup before any external plugin loads. The MCP surface includes DevReload's own management verbs as first-class tools; the agent uses them to manage every other plugin. There is no "framework vs. plugin" distinction at the MCP layer.

These methods exist today. They become RPC tools by annotation. The list is not a design output; it is the natural consequence of decorating DevReload's existing functional API.

Lifecycle (annotated on `PluginManager`, `[RunOnAcadMainThread]`):
- `Load(name)`, `Unload(name)`, `DevReload(name)`, `UnloadAll()`, `IsLoaded(name)`, `IsRegistered(name)`, `GetRegisteredPluginNames()`, `Unregister(name)`.

Build/config (annotated where the method exists):
- `DevReloadService.BuildProject(csproj, config)`.
- `DevReloadService.GetAvailableProjects()` (for the `register` flow).
- `PluginManager.UpdateBuildConfiguration(name, config)`.
- `PluginManager.UpdateActiveWorktree(name, path)` — paired with a small `GitWorktreeService.ListWorktrees(repoRoot)` accessor to satisfy the user's `select_branch` use case.
- `SharedAssembliesFile.Read(buildDir)` / `SharedAssembliesFile.Write(...)` — the `config_plugin` surface.

New thin wrappers (added only where the existing UI orchestration is too low-level for a clean tool):
- `PluginConfigLoader.RegisterNewPlugin(...)` — one method consolidating the "add plugin" flow currently spread across `DevReloadService.GetAvailableProjects` + `DevReloaderCommands.RegisterFromConfig`. Annotated as the `register` tool.
- `PluginManager.GetAssemblyInfo(name)` — returns `{ assemblyName, location, lastWriteUtc, version, isLoaded }`. The "is my new code running?" probe.

NOT exposed (per user direction): "push to production" stays user-only.

Help: replaced entirely by MCP standard `tools/list` plus per-tool/parameter `[Description]`.

</verbs-emerging>

<step-1-deliverables>

A concrete checklist. Each item is a discrete commit/PR.

1. **`Acad.Rpc.Core` project skeleton.** Public surface only — interfaces, attributes, host shell, options class. Compiles. No implementation. Reviewable as a design contract before any logic is written.
2. **`Acad.Rpc.Core` implementation.** Idle-pump dispatcher, attribute interceptor, register/unregister, MCP SDK wiring, pipe transport. Unit-tested where possible (host can be exercised in-proc against an in-memory MCP client).
3. **`Acad.Rpc.Bridge` project.** Console exe, pipe enumeration, stdio↔pipe loop. Manual smoke-tested.
4. **DevReload integration.** Bootstrap in `Initialize`/`Terminate`. Register/unregister hooks in `LoadCore`/`TearDown`. Method annotations. Bundle includes `Acad.Rpc.Core.dll`.
5. **Auto-injection of `Acad.Rpc.Core` as a streamed shared assembly** for every registered plugin's effective config.
6. **Smoke test.** Launch AutoCAD; bridge connects; `tools/list` returns DevReload's tools; reload the example plugin ten times; confirm tool list deltas fire and collectible ALC reclaims each time.
7. **Claude Code plugin package** at `plugins/devreload-mcp/`. Installable via `claude code plugin install`.

Estimated effort: 4–6 days. The risk concentration is item 2's collectible-ALC unregister correctness — flagged by item 6's smoke test.

</step-1-deliverables>

<step-2-preview>

NOT designed here. Mentioned only so step-1 deliverables don't accidentally block step 2.

The supervisor (working name) is a long-lived process that:
- Is Claude Code's actual MCP server endpoint.
- Owns AutoCAD's process lifecycle: can launch AutoCAD, can restart AutoCAD, can detect AutoCAD crashed.
- Proxies the in-AutoCAD MCP tool list when AutoCAD is up; exposes a degraded tool set (its own lifecycle tools only) when AutoCAD is down.
- Buffers and replays `tools/list_changed` notifications across AutoCAD restarts so the agent sees one continuous MCP session.
- Exposes its own tools: `restart_autocad`, `launch_autocad`, `is_autocad_alive`, `tail_autocad_log`, etc.

Why MCP makes this clean: the SDK's notification model (`tools/list_changed`) is the contract the supervisor needs to do this proxy. We are setting up step 2 by choosing MCP in step 1.

Step 1's bridge exe is the structural seed of step 2's supervisor: step 2 replaces the bridge with a richer process that has the same client-facing shape.

A separate proposal will design step 2 in detail after step 1 is shipped.

</step-2-preview>

<explicitly-rejected>
The following are NOT in the design, and not "could be added later" — they are rejected, to prevent context rot and re-litigation:

- A plain JSON-RPC server. No.
- A `StreamJsonRpc` transport in parallel to MCP. No.
- An HTTP/SSE transport in parallel to the pipe. No.
- A COM/IDispatch surface. No.
- A central `Verbs/` folder, manifest file, or registration table. No.
- A handwritten attribute set duplicating the SDK's. No.
- One MCP server per plugin. No.
- A custom `IRpcHandler<T>` interface mirroring MediatR. No.
- Source-generator-based attribute scan. Not in step 1. Reconsider only if runtime reflection becomes a measured problem.
- Token authentication on the pipe. No.
- Exposing "push to production" as a tool. No.
- Surfacing arbitrary AutoCAD command-line execution as a tool. No — that's a plugin-side concern (e.g. ACD-MCP's `execute_csharp`), not DevReload's.

</explicitly-rejected>

<references>
- ModelContextProtocol C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- `[McpServerTool]` / `WithToolsFromAssembly`: https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/
- MCP transport-agnostic spec: https://modelcontextprotocol.io/specification/2025-03-26/basic/transports
- Reference doc (computer-use pattern that motivated this work): `H:\GitHub\shtirlitsDva\ACD-MCP\docs\computer-use-from-claude-code.md`
- Superseded prior proposal: `docs/proposals/agentic-control-surface.md`
</references>

</proposal-universal-rpc-surface>
