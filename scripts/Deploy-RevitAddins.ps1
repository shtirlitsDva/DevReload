# Deploy-RevitAddins.ps1 — build the RevitDevReload hosts (Release) and
# install them the standard Revit way: binaries copied to
#   %APPDATA%\Autodesk\Revit\Addins\<year>\RevitDevReload\
# plus a RevitDevReload.addin manifest with a RELATIVE assembly path.
#
# This is the user-facing install. The dev loop (manifest pointing at the
# repo's Debug bin so a rebuild + Revit restart picks up changes) stays in
# revit-cli's `deploy` command.
#
# Usage:
#   pwsh scripts\Deploy-RevitAddins.ps1                 # all installed Revits
#   pwsh scripts\Deploy-RevitAddins.ps1 -RevitYears 2025
#   pwsh scripts\Deploy-RevitAddins.ps1 -Remove

param(
    [int[]]$RevitYears,
    [string]$Configuration = 'Release',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$addinId = 'cab77f49-ae73-4f4e-a014-39b717f0691b'

if (-not $RevitYears) {
    # Default to every installed Revit we ship a host for.
    $RevitYears = 2022..2030 | Where-Object {
        (Test-Path "$env:ProgramFiles\Autodesk\Revit $_\Revit.exe") -and
        (Test-Path "$repoRoot\src\Revit\RevitDevReload.R$($_.ToString().Substring(2))")
    }
    if (-not $RevitYears) { throw 'No installed Revit with a matching RevitDevReload host found.' }
}

foreach ($year in $RevitYears) {
    $yy = $year.ToString().Substring(2)
    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    $targetDir = Join-Path $addinsDir 'RevitDevReload'
    $manifest  = Join-Path $addinsDir 'RevitDevReload.addin'

    if ($Remove) {
        if (Test-Path $manifest)  { Remove-Item $manifest;              Write-Host "removed $manifest" }
        if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force; Write-Host "removed $targetDir" }
        continue
    }

    $csproj = Join-Path $repoRoot "src\Revit\RevitDevReload.R$yy\RevitDevReload.R$yy.csproj"
    if (-not (Test-Path $csproj)) { Write-Warning "no host project for Revit $year — skipped"; continue }

    Write-Host "== Revit ${year}: building RevitDevReload.R$yy ($Configuration)"
    dotnet build $csproj -c $Configuration -p:Platform=x64 --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed for Revit $year" }

    $buildOut = Join-Path $repoRoot "src\Revit\RevitDevReload.R$yy\bin\$Configuration"
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Force $targetDir | Out-Null
    Copy-Item "$buildOut\*" $targetDir -Recurse

    @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
    <AddIn Type="Application">
        <Name>RevitDevReload</Name>
        <Assembly>RevitDevReload/RevitDevReload.R$yy.dll</Assembly>
        <FullClassName>RevitDevReload.RevitDevReloadApp</FullClassName>
        <AddInId>$addinId</AddInId>
        <VendorId>DVRL</VendorId>
        <VendorDescription>DevReload, https://github.com/shtirlitsDva/DevReload</VendorDescription>
    </AddIn>
</RevitAddIns>
"@ | Set-Content $manifest -Encoding utf8

    Write-Host "   installed -> $targetDir"
    Write-Host "   manifest  -> $manifest"
}
