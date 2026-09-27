<groups>
A group is an ordered set of native `.dbx`/`.arx` modules built from one `.sln` in one `msbuild -m` run. Reload = unload → build → load: a mapped module locks its file, so the build cannot run first. A failed build leaves the group unloaded.
</groups>

<profiles>
A group has profiles. Profile = name + one absolute worktree folder + modules (`.vcxproj` paths relative to the folder, in load order: `.dbx` before the `.arx` that uses it) + MSBuild properties + companion DLLs. Load and Reload build the active profile. Profiles are per folder.

- `oarx_list_plugins`: `configPending` = a change applied at the next load/reload.
- Group-level settings (`oarx_update_plugin`) and per-folder content (profiles) are separate.
- The active profile cannot be deleted. On a loaded group, `oarx_activate_profile` applies at the next reload.
</profiles>

<your-worktree>
In your own worktree, publish a profile yourself: `oarx_publish_profile(name, worktreePath=<worktree>, copyFrom=<main profile>, projectFilePaths=[...])`. `copyFrom` applies on create; change only what differs. Modules and the solution must be inside the folder.

Publishing does not activate. The active profile decides what the user's AutoCAD builds; activate only when the user asks. Delete your profile (`oarx_delete_profile`) when the worktree is removed.
</your-worktree>

<prebuilt-payloads>
`oarx_reload_payload(name, payloadDir)` loads compiled modules with no build and no profile — for a tester machine that receives a payload. `payloadDir` holds `payload.json`; the tool description gives its format.

- The previous payload of the same `name` is unloaded first. Nothing goes to `plugins.json` or the palette; the state ends with the AutoCAD process.
- Read `success`, then `restartRequired`:
  - `success:false`, `restartRequired:true` — only a new AutoCAD can take this payload. `acad_quit`, `acad_start`, call again.
  - `success:false`, `restartRequired:false` — the payload is defective (manifest, file-name clash, module refused). `file` names the file. A restart does not help.
- `loaded` lists what this `name` has mapped now, with file version and sha256. Compare the sha256 values with the payload you sent.
</prebuilt-payloads>
