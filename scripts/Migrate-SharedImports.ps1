# Migrate-SharedImports.ps1 — one-time migration for plugin repos that import
# DevReload's shared projects (EventManager, WpfSHARED) by marketplace path.
#
# The 2026-06 repo restructure moved:
#   src\EventManager  -> src\Autocad\EventManager
#   src\WpfSHARED     -> src\Shared\WpfSHARED
#
# Run this ONCE, right after updating the devreload Claude plugin (which
# refreshes %USERPROFILE%\.claude\plugins\marketplaces\devreload to the new
# layout). It rewrites the <Import> paths in every consumer csproj it finds.
#
# Usage:
#   pwsh scripts\Migrate-SharedImports.ps1                  # scan default roots
#   pwsh scripts\Migrate-SharedImports.ps1 -Roots X:\GitHub # explicit roots

param(
    [string[]]$Roots = @('X:\GitHub', "$env:USERPROFILE\Desktop\GitHub")
)

$ErrorActionPreference = 'Stop'

$rewrites = @(
    @{ Old = 'src\EventManager\EventManager.projitems'; New = 'src\Autocad\EventManager\EventManager.projitems' },
    @{ Old = 'src/EventManager/EventManager.projitems'; New = 'src/Autocad/EventManager/EventManager.projitems' },
    @{ Old = 'src\WpfSHARED\WpfSHARED.projitems';       New = 'src\Shared\WpfSHARED\WpfSHARED.projitems' },
    @{ Old = 'src/WpfSHARED/WpfSHARED.projitems';       New = 'src/Shared/WpfSHARED/WpfSHARED.projitems' }
)

$changed = @()
foreach ($root in $Roots) {
    if (-not (Test-Path $root)) { continue }
    $csprojs = Get-ChildItem $root -Recurse -Filter *.csproj -ErrorAction SilentlyContinue -Depth 6
    foreach ($file in $csprojs) {
        $text = Get-Content $file.FullName -Raw
        $new = $text
        foreach ($rw in $rewrites) {
            # Only touch devreload marketplace/repo imports, not unrelated
            # projects that happen to have a src\EventManager of their own.
            $new = $new -replace ('(devreload[^"'']*)' + [regex]::Escape($rw.Old)), ('$1' + $rw.New.Replace('\', '\\'))
        }
        if ($new -ne $text) {
            Set-Content $file.FullName $new -NoNewline
            $changed += $file.FullName
        }
    }
}

if ($changed.Count -eq 0) {
    Write-Host 'No consumer csproj needed migration.'
} else {
    Write-Host "Migrated $($changed.Count) file(s):"
    $changed | ForEach-Object { Write-Host "  $_" }
    Write-Host 'Remember to commit these changes in the affected repos.'
}
