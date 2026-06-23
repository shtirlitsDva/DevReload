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

# 2. Push source. This is also the BRIDGE release for the GitHub marketplace:
#    .github/workflows/release-plugin.yml fires on every push to master, runs
#    Pack-Plugin.ps1, and force-pushes `release` = master tree + packed server/.
#    So you do NOT pack or touch the release branch by hand for that path.
git push origin master

# 3. MCP BRIDGE (manual) — only for a LOCAL-checkout marketplace or Codex,
#    which read server/ from the working tree rather than the release branch.
.\scripts\Pack-Plugin.ps1   # publishes the bridge into <repo>\server
```

Then the **client-side steps you must do by hand** (an agent can't run `/plugin` or `/mcp`):

- **`/plugin update devreload`** — pulls the new `plugin.json` version, `skills\`, and `server\` into the cache. (First time on a machine: `/plugin marketplace add <repo>` then `/plugin install devreload`.)
- **Restart AutoCAD** if the host bundle changed (step 1).
- **`/mcp` → devreload → reconnect** (or restart Claude Code) if the bridge changed (step 2) — this spawns the freshly-packed `server\Acad.Rpc.Bridge.dll`.

</the-update-procedure>

<gotchas>

- **The running bridge locks `server\*.dll`.** `Pack-Plugin.ps1` deletes + republishes `server\`; if a bridge process is alive it'll fail or the swap won't take. Stop the MCP server first (`/mcp` disconnect, or it's already down) — or accept that the new bridge only loads on the next reconnect/restart.
- **A Civil/AutoCAD crash or quit no longer collapses the catalog (v2.2.0).** The bridge keeps the full surface published while unbound — `acad_*` always, plus `devreload_*`/`ui_*` from a cached snapshot — and auto-clears a dead binding; remote calls just fail with "pass pid / acad_attach" until you rebind. A lone running instance is still auto-attached. If the client itself tears down the server (a true transport flap / respawn), reconnect with `/mcp`.
- **The host bundle and the bridge each carry their OWN copy of `Acad.Rpc.Core.dll`** (different processes). A change there means rebuilding BOTH (steps 1 and 2).
- **GitHub distribution (`release` branch) is automated — don't touch it by hand.** `.github/workflows/release-plugin.yml` runs on every push to `master`: it runs `Pack-Plugin.ps1` and **force-pushes** `release` = the master tree + packed `server\`. `marketplace.json` points `/plugin marketplace add shtirlitsDva/DevReload` at that branch, so a `git push origin master` IS the bridge release for the marketplace path. (`server\` is force-added by the workflow even though it's ignored on master.) A *local* `release` ref in your clone can lag the CI-maintained `origin/release` — `git fetch` before reasoning about it.

</gotchas>

</plugin-release-playbook>
