# Test-RevitE2E.ps1 — end-to-end smoke test for RevitDevReload.
#
# Flow: deploy host -> start Revit (auto-clicking security dialogs) ->
# register + load + reload the example plugin over the pipe -> assert the
# discovered commands -> unload -> quit Revit.
#
# Usage: pwsh scripts\Test-RevitE2E.ps1 -RevitYear 2025 [-SkipDeploy] [-KeepRevit]
#
# NOTE: run_command (executing a plugin IExternalCommand) needs
# ExternalCommandData, captured when the DevReload ribbon button is first
# clicked — that part stays manual/UIA-assisted and is not asserted here.

param(
    [Parameter(Mandatory)] [int]$RevitYear,
    [switch]$SkipDeploy,
    [switch]$KeepRevit
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repoRoot 'src\Revit\Revit.Cli\bin\Debug\revit-cli.exe'
if (-not (Test-Path $cli)) {
    throw "revit-cli not built. Run: dotnet build src\Revit\Revit.Cli -c Debug -p:Platform=x64"
}

$exampleYear = if ($RevitYear -ge 2025) { 2025 } else { 2024 }
$exampleCsproj = Join-Path $repoRoot "example\Revit.Example.Plugin\Revit.Example.Plugin.$exampleYear\Revit.Example.Plugin.$exampleYear.csproj"

function Send-Cmd([string]$cmd, [string]$argsJson = 'null') {
    $out = & $cli send --cmd $cmd --args $argsJson
    Write-Host "  $cmd -> $out"
    if ($LASTEXITCODE -ne 0) { throw "pipe command '$cmd' failed: $out" }
    return $out
}

$failures = @()

try {
    if (-not $SkipDeploy) {
        Write-Host "== deploy host for Revit $RevitYear"
        & $cli deploy --rvt $RevitYear
        if ($LASTEXITCODE -ne 0) { throw 'deploy failed' }
    }

    Write-Host "== start Revit $RevitYear (watching security dialogs)"
    & $cli start --rvt $RevitYear --watch-dialogs --wait-pipe 300
    if ($LASTEXITCODE -ne 0) { throw 'Revit started but the DevReload pipe never came up' }

    Write-Host '== ping'
    $ping = Send-Cmd 'ping'
    if ($ping -notmatch "\"revitVersion\":$RevitYear") { $failures += "ping reports wrong version: $ping" }

    Write-Host '== register example plugin'
    Send-Cmd 'register_plugin' ('{"projectFilePath":"' + $exampleCsproj.Replace('\', '\\') + '"}') | Out-Null

    Write-Host '== load (builds first)'
    $load = Send-Cmd 'load' ('{"name":"Revit.Example.Plugin.' + $exampleYear + '"}')
    if ($load -notmatch '"loaded":true') { $failures += "load did not report loaded: $load" }
    if ($load -notmatch '"commandCount":2') { $failures += "expected 2 discovered commands: $load" }

    Write-Host '== reload (build-first-then-swap)'
    $reload = Send-Cmd 'reload' ('{"name":"Revit.Example.Plugin.' + $exampleYear + '"}')
    if ($reload -notmatch '"loaded":true') { $failures += "reload did not report loaded: $reload" }

    Write-Host '== get_state'
    $state = Send-Cmd 'get_state'
    if ($state -notmatch 'WriteMarkerCommand') { $failures += "state does not list WriteMarkerCommand: $state" }

    Write-Host '== unload'
    $unload = Send-Cmd 'unload' ('{"name":"Revit.Example.Plugin.' + $exampleYear + '"}')
    if ($unload -notmatch '"success":true') { $failures += "unload failed: $unload" }

    Write-Host '== unregister'
    Send-Cmd 'unregister_plugin' ('{"name":"Revit.Example.Plugin.' + $exampleYear + '"}') | Out-Null
}
finally {
    if (-not $KeepRevit) {
        Write-Host '== stop Revit'
        & $cli stop
    }
}

if ($failures.Count -gt 0) {
    Write-Host "`nE2E FAILED:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nE2E PASSED for Revit $RevitYear" -ForegroundColor Green
exit 0
