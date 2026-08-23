# Lab: can DevReload stop AutoCAD auto-registering commands from a plugin
# that carries no NoCommands marker?
#
# Builds LabPlugin (the unprepared plugin) and LabProbe (the harness), starts
# AutoCAD on a script that NETLOADs the probe and runs LABRUN, then prints the
# log the probe wrote.

param(
    [string]$AutoCADPath = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

dotnet build "$here\LabPlugin\LabPlugin.csproj"             -c Debug -p:Platform=x64 -v q --nologo
dotnet build "$here\LabPluginMarked\LabPluginMarked.csproj" -c Debug -p:Platform=x64 -v q --nologo
dotnet build "$here\LabProbe\LabProbe.csproj"               -c Debug -p:Platform=x64 -v q --nologo

# The probe loads plugins by name out of one directory, so stage them together.
$stage = "$here\stage"
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$here\LabPlugin\bin\x64\Debug\LabPlugin.*"             $stage -Force
Copy-Item "$here\LabPluginMarked\bin\x64\Debug\LabPluginMarked.*" $stage -Force

$probe = "$here\LabProbe\bin\x64\Debug\LabProbe.dll"
foreach ($p in @("$stage\LabPlugin.dll", "$stage\LabPluginMarked.dll", $probe)) {
    if (-not (Test-Path $p)) { throw "build output missing: $p" }
}

$log = "$here\lab.log"
if (Test-Path $log) { Remove-Item $log }

$scr = "$here\lab.scr"
@"
(setvar "FILEDIA" 0)
(setvar "SECURELOAD" 0)
(command "_.NETLOAD" "$($probe -replace '\\','/')")
(command "_.LABRUN")
(command "_.QUIT")
"@ | Set-Content -Path $scr -Encoding ascii

$env:DEVRELOAD_LAB_LOG = $log
$env:DEVRELOAD_LAB_DIR = $stage

$proc = Start-Process -FilePath "$AutoCADPath\acad.exe" `
    -ArgumentList @('/nologo', '/b', "`"$scr`"") -PassThru

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "AutoCAD (pid $($proc.Id)) did not exit within $TimeoutSeconds s; killing."
    $proc.Kill()
    $proc.WaitForExit()
}

if (Test-Path $log) { Get-Content $log } else { Write-Host 'NO LOG WRITTEN' }
