<plugin-release-playbook>

How to ship a DevReload change to a running machine. Read this instead of re-deriving the steps — the friction is that **three independent artifacts** are built and deployed in different ways, and a given change may touch one, two, or all three.

<the-three-artifacts>

1. **In-AutoCAD host bundle** — `DevReload.dll` (+ `Acad.Rpc.Core.dll`, `UiMcp.Core.dll`, deps) loaded *inside* AutoCAD from `%APPDATA%\Autodesk\ApplicationPlugins\DevReload.bundle`. This is what runs the `devreload_*` and `ui_*` tools and the hot-reload engine.
   - Built by: Release build of `DevReload.csproj` (the `CreateBundle` MSBuild target emits `<repo>\Deploy\DevReload.bundle`).
   - Deployed by: copying that bundle over the `%APPDATA%` one.
   - Picked up by: **restarting AutoCAD** (the DLL is loaded at startup and locked while AutoCAD runs).

2. **MCP bridge** — `server\Acad.Rpc.Bridge.dll` (+ `Acad.Rpc.Core.dll`, `Acad.Process.dll`, deps), the stdio MCP server Claude Code / Codex spawns. Serves the static `acad_*` tools and forwards `devreload_*`/`ui_*` to the host pipe.
   - Built by: `scripts\Pack-Plugin.ps1` (publishes the bridge into `<repo>\server`).
   - Deployed by: `/plugin update` (Claude Code copies `server\` from the working tree into the plugin cache).
   - Picked up by: **reconnecting the MCP server** (`/mcp` → devreload → reconnect, or restart Claude Code). The running bridge locks `server\*.dll`, so the swap needs the old one stopped.

3. **Plugin package metadata** — `.claude-plugin\plugin.json` (version + `mcpServers`) and the `skills\` directory (`/acd-agentic-dev` etc.).
   - Built by: nothing — they are source files.
   - Deployed by: `/plugin update` (copies `plugin.json` + `skills\` into the cache; a **version bump in `plugin.json` is what makes the client notice an update**).
   - Picked up by: the next `/acd-agentic-dev` invocation after `/plugin update`.

</the-three-artifacts>

<which-artifacts-does-my-change-touch>

| Changed file(s) | Host bundle | MCP bridge | Plugin metadata |
|---|---|---|---|
| `src/Autocad/DevReload/**` (incl. `Ui/**`) | ✅ | — | — |
| `src/Shared/Acad.Rpc.Core/**` (shared by host + bridge) | ✅ | ✅ | — |
| `src/Autocad/Acad.Rpc.Bridge/**`, `src/Autocad/Acad.Process/**`, `src/Shared/UiMcp.Core/**` | ✅ (if host references it) | ✅ | — |
| `skills/**` or `.claude-plugin/plugin.json` | — | — | ✅ |

If a row is ticked, that artifact must be rebuilt **and** re-picked-up (see the table above for how). When in doubt, do all three — it's cheap.

</which-artifacts-does-my-change-touch>

<the-update-procedure>

Run from the repo root, on the branch you want to ship (normally `master`).

```powershell
# 0. Bump the plugin version so the client detects the update.
#    Edit .claude-plugin\plugin.json  "version": "X.Y.Z"
#    (minor bump for a new tool/feature, patch for a fix). Commit it.

# 1. HOST BUNDLE — only if a host-affecting file changed.
dotnet build src\Autocad\DevReload\DevReload.csproj -c Release -p:Platform=x64
# CreateBundle emits <repo>\Deploy\DevReload.bundle. Deploy it (close AutoCAD first — it locks the DLLs):
$src = "<repo>\Deploy\DevReload.bundle\Contents\Win64"
$dst = "$env:APPDATA\Autodesk\ApplicationPlugins\DevReload.bundle\Contents\Win64"
robocopy $src $dst /MIR     # exit code 0 or 1 = success

# 2. MCP BRIDGE — only if a bridge-affecting file changed.
.\scripts\Pack-Plugin.ps1   # publishes the bridge into <repo>\server

# 3. Push source.
git push origin master
```

Then the **client-side steps you must do by hand** (an agent can't run `/plugin` or `/mcp`):

- **`/plugin update devreload`** — pulls the new `plugin.json` version, `skills\`, and `server\` into the cache. (First time on a machine: `/plugin marketplace add <repo>` then `/plugin install devreload`.)
- **Restart AutoCAD** if the host bundle changed (step 1).
- **`/mcp` → devreload → reconnect** (or restart Claude Code) if the bridge changed (step 2) — this spawns the freshly-packed `server\Acad.Rpc.Bridge.dll`.

</the-update-procedure>

<gotchas>

- **The running bridge locks `server\*.dll`.** `Pack-Plugin.ps1` deletes + republishes `server\`; if a bridge process is alive it'll fail or the swap won't take. Stop the MCP server first (`/mcp` disconnect, or it's already down) — or accept that the new bridge only loads on the next reconnect/restart.
- **A Civil/AutoCAD quit can drop the whole devreload MCP from the client catalog** (transport flap). Reconnect with `/mcp`. The bridge keeps the static `acad_*` published while unbound and auto-attaches a lone running instance, so this is a client-refresh issue, not a bridge fault.
- **The host bundle and the bridge each carry their OWN copy of `Acad.Rpc.Core.dll`** (different processes). A change there means rebuilding BOTH (steps 1 and 2).
- **GitHub distribution (`release` branch).** `marketplace.json` points the `url` source at `ref: release`, and `release` carries a committed `server\` (built artifact) on top of source. For local installs you don't need it. To use that channel: sync `release` to `master`, run `Pack-Plugin.ps1`, commit `server\`, and push `release`. (As of v2.1.0 the `release` branch is stale — local install from the working tree is the live path.)

</gotchas>

</plugin-release-playbook>
