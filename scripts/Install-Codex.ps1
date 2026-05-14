# Install-Codex.ps1 — wire DevReload's MCP bridge + acd-agentic-dev skill
# into a local Codex CLI install.
#
# What it does (idempotent):
#   1. Runs Pack-Plugin.ps1 if .\server\Acad.Rpc.Bridge.dll is missing.
#   2. Copies skills\acd-agentic-dev\ to %USERPROFILE%\.agents\skills\
#      (Codex's user-scope skills directory).
#   3. Registers the MCP server in %USERPROFILE%\.codex\config.toml.
#      Prefers `codex mcp add` if the codex CLI is on PATH; otherwise
#      appends a clearly-marked [mcp_servers.devreload] block (skips
#      if already present).

param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bridgeDll = Join-Path $repoRoot 'server\Acad.Rpc.Bridge.dll'
$skillSrc = Join-Path $repoRoot 'skills\acd-agentic-dev'

# 1. Pack if needed.
if (-not (Test-Path $bridgeDll)) {
    Write-Host "Bridge not yet packed — running Pack-Plugin.ps1..." -ForegroundColor Cyan
    $packArgs = @()
    if ($SelfContained) { $packArgs += '-SelfContained' }
    & (Join-Path $PSScriptRoot 'Pack-Plugin.ps1') @packArgs
    if (-not (Test-Path $bridgeDll)) {
        throw "Pack-Plugin completed but $bridgeDll still missing."
    }
}

# 2. Install the skill globally for Codex.
$skillDst = Join-Path $env:USERPROFILE '.agents\skills\acd-agentic-dev'
$skillDstParent = Split-Path -Parent $skillDst
if (-not (Test-Path $skillDstParent)) {
    New-Item -ItemType Directory -Force -Path $skillDstParent | Out-Null
}
if (Test-Path $skillDst) {
    Remove-Item -Recurse -Force $skillDst
}
Copy-Item -Recurse -Path $skillSrc -Destination $skillDst -Force
Write-Host "Skill installed: $skillDst" -ForegroundColor Green

# 3. Register the MCP server with Codex.
$codexCli = Get-Command codex -ErrorAction SilentlyContinue
$bridgeDllEscaped = $bridgeDll  # Codex CLI handles its own quoting.

if ($codexCli) {
    Write-Host "Codex CLI detected — registering via 'codex mcp add'..." -ForegroundColor Cyan
    # `codex mcp add <name> -- <command> <args...>` overwrites any existing entry with the same name.
    & codex mcp add devreload -- dotnet $bridgeDllEscaped
    if ($LASTEXITCODE -ne 0) {
        throw "codex mcp add failed (exit $LASTEXITCODE). Falling back to manual TOML edit may be required."
    }
    Write-Host "Registered MCP server 'devreload' with Codex." -ForegroundColor Green
} else {
    Write-Host "Codex CLI not found on PATH — appending [mcp_servers.devreload] to config.toml..." -ForegroundColor Yellow
    $codexDir = Join-Path $env:USERPROFILE '.codex'
    $codexConfig = Join-Path $codexDir 'config.toml'
    if (-not (Test-Path $codexDir)) {
        New-Item -ItemType Directory -Force -Path $codexDir | Out-Null
    }

    $sectionHeader = '[mcp_servers.devreload]'
    $existing = if (Test-Path $codexConfig) { Get-Content -LiteralPath $codexConfig -Raw } else { '' }

    if ($existing -match [regex]::Escape($sectionHeader)) {
        Write-Host "Section $sectionHeader already present in $codexConfig — leaving config untouched." -ForegroundColor Yellow
        Write-Host "If you need to repoint at a new bridge path, edit it manually." -ForegroundColor Yellow
    } else {
        # TOML escapes: backslashes must be doubled inside basic strings, OR use a literal (single-quoted) string.
        # Use a literal string to keep Windows paths readable.
        $literalPath = "'" + $bridgeDll + "'"
        $block = @"

# Added by DevReload Install-Codex.ps1
$sectionHeader
command = "dotnet"
args = [$literalPath]
"@
        Add-Content -LiteralPath $codexConfig -Value $block -Encoding UTF8
        Write-Host "Appended MCP server 'devreload' to $codexConfig" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Done. Restart Codex (or reload its config) to pick up the new MCP server." -ForegroundColor Cyan
Write-Host "Skill is available as `$acd-agentic-dev or via /skills inside Codex." -ForegroundColor Cyan
