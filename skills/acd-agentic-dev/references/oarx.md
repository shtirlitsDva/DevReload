<groups>
A group is an ordered set of native `.dbx`/`.arx` modules built from one `.sln` in one `msbuild -m` run. Reload = unload → build → load: a mapped module locks its file, so the build cannot run first. A failed build leaves the group unloaded.
</groups>

<profiles>
A group has profiles. Profile = name + one absolute worktree folder + modules (`.vcxproj` paths relative to the folder, in load order: `.dbx` before the `.arx` that uses it) + MSBuild properties + companion DLLs. Load and Reload build the active profile. Profiles are per folder.

- `oarx_list_plugins`: `configPending` = a change applied at the next load/reload.
- Group-level settings (`oarx_update_plugin`) and per-folder content (profiles) are separate.
- The active profile cannot be deleted. On a loaded group, `oarx_activate_profile` applies at the next reload.
</profiles>

<build-folder>
A profile can have a `buildFolder` (relative to the worktree, or absolute). DevReload builds into it and loads the modules from it. Use one when the profile's MSBuild properties differ from the repo's other builds: with a shared output folder, each switch between the two builds recompiles everything.

- DevReload passes the folder to MSBuild as `DevReloadBuildFolder=<absolute folder>`, on both the TargetPath query and the build.
- The repo must route `OutDir` and `IntDir` from that property. It must do so before TargetPath is derived, so for C++ use `ForceImportBeforeCppTargets`, not `Directory.Build.targets`.
- DevReload refuses a module that resolves or lands outside the folder. The error names the project.
- Set it with `oarx_publish_profile(..., buildFolder="x64\\DevReload")`. `""` clears it. Do not set `DevReloadBuildFolder` as an MSBuild property.
</build-folder>

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

<may-unload-export>
Before DevReload unloads ANY module of a group or a payload (reload, unload, a payload's rollback), it asks every module in the run whether it may unload now. One no and NOTHING is unloaded: the .arx stays loaded and its commands keep working. The response carries the module's own sentence plus "Nothing was unloaded"; `restartRequired` is false. Remove the cause and call again.

Any native module may answer through one optional C export (2.9.0):

```cpp
extern "C" __declspec(dllexport) int DevReloadMayUnload_v1(wchar_t* reason, int reasonChars);
```

- Return 1 = may unload now, 0 = refuse. Any other value, or a call that faults, holds the unload.
- On a refusal, write ONE sentence the user can act on into `reason`, NUL-ended, at most `reasonChars` characters with the NUL (DevReload passes 1024). Say why and what to do ("37 NDH objects are alive in open drawings. Close those drawings, then unload.").
- No export = asked nothing, counts as yes. A module that is not mapped is not asked.
- DevReload finds it with `GetModuleHandle(<file name>)` + `GetProcAddress`, and calls it on the host's MAIN thread (every `oarx_*` tool and `{PREFIX}` command runs there). Keep it cheap and side-effect free; it must not throw (an exception leaving an `extern "C"` function is undefined behaviour).
- Make it the SAME decision as the module's `kUnloadAppMsg` refusal, so the question and the handler never disagree. The handler's refusal stays as the last guard.
- Why: a refusal from `kUnloadAppMsg` cannot always be recovered. BricsCAD V26 drops the refusing module from its list, keeps it mapped, and never calls its unload handler again, so the session is stuck without the modules that did come out (the .arx). Asking first prevents it.
- The group cycle asks after it has closed the named drawings (BricsCAD), so a dbx that counts its live objects answers about what is left open.
</may-unload-export>
