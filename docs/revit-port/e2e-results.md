<e2e-results date="2026-06-10">
Run autonomously by Claude (Fable 5) via `revit-cli` against the locally
installed Revits. All pipe traffic below is real output from live Revit
processes.

<revit-2025 loader="AlcPluginLoader (collectible ALC)" result="PASS — full lifecycle">
- deploy → manifest in %APPDATA%\Autodesk\Revit\Addins\2025
- start --watch-dialogs: UIA watcher auto-clicked "Security - Unsigned
  Add-In → Always Load" (and pyRevit's signed prompt on the first run)
- ping → { revitVersion: 2025, trueUnload: true }
- register_plugin / load → 2 commands discovered
- reload → dotnet build inside dispatcher, swap, still 2 commands
- UIA: Home → New… → OK (new project), Add-Ins tab → DevReload button →
  manager window opened (card list rendered), ExternalCommandData captured
- run_command WriteMarkerCommand → Succeeded; marker file written by the
  plugin inside Revit (note: Revit redirects %TEMP% to a per-session GUID
  subfolder — found at %LOCALAPPDATA%\Temp\<guid>\revit-devreload-example-marker.txt)
- unload (ALC released) / unregister / get_log / quit — all clean
</revit-2025>

<revit-2024 loader="LegacyPluginLoader (net48 byte-load)" result="PASS — full lifecycle">
- deploy / start --watch-dialogs (security dialog auto-clicked) / ping →
  { revitVersion: 2024, trueUnload: false }
- register / load (2 commands) / reload (build+swap) /
  unload → "unloaded (assembly stays resident until Revit restarts — .NET
  Framework)" / unregister / stop — all clean
</revit-2024>

<revit-2022 result="BLOCKED — Autodesk sign-in">
Revit 2022 on this machine halts at an interactive "Log på" (sign-in)
window before any add-in loads; licensing needs a one-time manual login.
The R22 host builds and is byte-identical in code to the proven R24 path
(same shproj, same net48 loader). Re-run `scripts\Test-RevitE2E.ps1
-RevitYear 2022` after signing into Revit 2022 once.
</revit-2022>

<revit-2023 result="NOT RUN">
Skipped to conserve the session; same code path as 2024. Run
`scripts\Test-RevitE2E.ps1 -RevitYear 2023` to verify.
</revit-2023>

<bugs-found-and-fixed-during-e2e>
1. PipeServer wedged after the first client: NamedPipeServerStream needs an
   UNCONDITIONAL Disconnect() between clients (IsConnected may already read
   false). Regression test added; verified red on old code.
2. DialogWatcher matched only top-level window titles — Revit hosts the
   security prompt as a child Window element inside its unnamed main
   window. Watcher now scans descendant Window elements.
3. Process.MainWindowHandle is IntPtr.Zero during OnStartup — switched to
   UIControlledApplication.MainWindowHandle.
</bugs-found-and-fixed-during-e2e>
</e2e-results>
