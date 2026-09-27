<startup-can-stall>
The pipe listens after AutoCAD loads the DevReload bundle. A startup dialog can delay that load; `acad_wait_pipe` then returns `succeeded:false`. Most frequent blocker: the **Drawing Recovery Manager**, shown after a run that did not shut down cleanly — which includes every `acad_quit`.

`acad_wait_pipe` does not succeed:
1. `acad_list_instances`: pid alive with `pipeAvailable:false`?
2. Busy or idle? Cold Civil 3D loads for 1–3 min. Idle, responsive, and no pipe = blocked on UI.
3. `ui_*` needs the pipe, so it is not available. Find the dialog by its top-level window title (e.g. "Drawing Recovery") and post `WM_CLOSE` with a PowerShell script (`FindWindow` + `PostMessage` through `Add-Type`), or ask the user to close it.
4. The pipe comes up seconds later and the bridge connects. Catalog not refreshed: `acad_detach` + `acad_attach <pid>`.
</startup-can-stall>

<multiple-instances>
Each instance has its own pipe `acad-rpc-<pid>`. All tools work on all instances at the same time.

- Pass `pid` to target an instance. Omit it for the bound default: the instance `acad_start` launched last, or the one set with `acad_attach`.
- Gate each new instance with `acad_wait_pipe(pid=…)`.
- Commands, drawings, and plugin load state are per instance.
- `autocad_*` take `pid` too; pass it when Acd.Mcp is loaded in more than one instance.

```
acad_start(Civil3D)               # → pid A, bound
acad_wait_pipe(pid=A)
acad_start(Civil3D)               # → pid B, bound
acad_wait_pipe(pid=B)
devreload_reload("MyPlugin", pid=A)
acad_send_command("MYCMD", pid=A)
acad_get_state(pid=B)
```

Each agent runs its own bridge. The DevReload and ACD-MCP pipes accept several connections, so agents can share an instance; one instance per agent is cleaner (a shared instance shares the drawing and the script session).
</multiple-instances>

<binding-lifecycle>
The bridge binds at most one default pid, in memory. The binding is lost when the bridge restarts: `claude -r`, `/reload-plugins`, `/plugin update`, or a bridge crash.

On start the bridge:
1. One AutoCAD with the pipe up → attaches to it.
2. No AutoCAD → unbound. `acad_start`.
3. Several AutoCADs → unbound. `acad_list_instances`, then `acad_attach <pid>`.
4. AutoCAD running, pipe down → unbound. Clear the blocker (`<startup-can-stall>`), then `acad_attach`; or `acad_quit` + `acad_start`.

Unbound = remote tools listed (cached), calls fail. Probe with `acad_list_instances`: one healthy instance with `isBound:false` → `acad_attach <pid>`.
</binding-lifecycle>

<crash>
A crashed instance does not stop the bridge. The binding to it clears: `acad_list_instances` no longer lists the pid, and pid-less remote calls return an error.

- Another instance is alive: pass its `pid`.
- Otherwise: `acad_attach <pid>` or `acad_start`.
- Frozen but alive (a "has stopped working" dialog) can hang a call: `acad_quit <pid>`, then `acad_start`.
</crash>
