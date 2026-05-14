# Pack-Plugin.ps1 — build the Acad.Rpc.Bridge MCP server for distribution
# as part of the DevReload Claude Code / Codex plugin.
#
# Output: <repo>\server\Acad.Rpc.Bridge.dll + dependencies + runtimeconfig.
# This path is what .claude-plugin\plugin.json references via
# ${CLAUDE_PLUGIN_ROOT}\server\Acad.Rpc.Bridge.dll, and what
# Install-Codex.ps1 wires into ~/.codex/config.toml.
#
# Framework-dependent publish — requires .NET 8 runtime on the user's
# machine. Sized ~5 MB; self-contained would be ~60 MB. Switch via the
# -SelfContained flag if you want the bigger artefact.

param(
    [switch]$SelfContained,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bridgeCsproj = Join-Path $repoRoot 'src\Acad.Rpc.Bridge\Acad.Rpc.Bridge.csproj'
$serverDir = Join-Path $repoRoot 'server'

if (-not (Test-Path $bridgeCsproj)) {
    throw "Bridge csproj not found at $bridgeCsproj — run from a checked-out DevReload repo."
}

if (Test-Path $serverDir) {
    Remove-Item -Recurse -Force $serverDir
}

$publishArgs = @(
    'publish', $bridgeCsproj,
    '-c', $Configuration,
    '-p:Platform=x64',
    '--nologo',
    '-o', $serverDir
)
if ($SelfContained) {
    $publishArgs += @('--self-contained=true', '-r', 'win-x64')
} else {
    $publishArgs += '--self-contained=false'
}

Write-Host "Packing Acad.Rpc.Bridge ($Configuration, self-contained=$SelfContained)..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$bridgeDll = Join-Path $serverDir 'Acad.Rpc.Bridge.dll'
if (-not (Test-Path $bridgeDll)) {
    throw "Publish completed but Acad.Rpc.Bridge.dll missing at $bridgeDll"
}

Write-Host ""
Write-Host "Packed: $bridgeDll" -ForegroundColor Green
Write-Host ""
Write-Host "Install in Claude Code (local marketplace):" -ForegroundColor Cyan
Write-Host "  /plugin marketplace add `"$repoRoot`""
Write-Host "  /plugin install devreload"
Write-Host ""
Write-Host "Install in Codex:" -ForegroundColor Cyan
Write-Host "  .\scripts\Install-Codex.ps1"
