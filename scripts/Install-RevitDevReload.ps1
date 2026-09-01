# Install-RevitDevReload.ps1 — interactive installer.
#
# Pops up a WinForms dialog with one checkbox per installed Revit that has a
# matching RevitDevReload host, lets you tick the ones to install, then hands
# the chosen years to Deploy-RevitAddins.ps1 (the single install code path:
# build -> copy binaries -> write manifest).
#
# Usage:
#   pwsh scripts\Install-RevitDevReload.ps1                 # pick versions in a dialog
#   pwsh scripts\Install-RevitDevReload.ps1 -Configuration Debug
#
# This is only the picker UI. All real work lives in Deploy-RevitAddins.ps1.

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $PSScriptRoot
$deployScript = Join-Path $PSScriptRoot 'Deploy-RevitAddins.ps1'

if (-not (Test-Path $deployScript)) {
    throw "Deploy-RevitAddins.ps1 not found next to this script ($deployScript)."
}

# --- Discover installable versions: Revit installed AND we ship a host for it.
$candidates = 2022..2030 | Where-Object {
    (Test-Path "$env:ProgramFiles\Autodesk\Revit $_\Revit.exe") -and
    (Test-Path "$repoRoot\src\Revit\RevitDevReload.R$($_.ToString().Substring(2))")
}

if (-not $candidates) {
    Write-Warning 'No installed Revit with a matching RevitDevReload host was found. Nothing to install.'
    return
}

# --- Build the checkbox dialog.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text          = 'Install RevitDevReload'
$form.FormBorderStyle = 'FixedDialog'
$form.StartPosition = 'CenterScreen'
$form.MaximizeBox   = $false
$form.MinimizeBox   = $false
$form.ClientSize    = New-Object System.Drawing.Size(320, (70 + $candidates.Count * 28 + 50))

$label = New-Object System.Windows.Forms.Label
$label.Text     = "Select the Revit versions to install to ($Configuration):"
$label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point(15, 15)
$form.Controls.Add($label)

$checkBoxes = @()
$y = 45
foreach ($year in $candidates) {
    $cb = New-Object System.Windows.Forms.CheckBox
    $cb.Text     = "Revit $year"
    $cb.Tag      = $year
    $cb.Checked  = $true
    $cb.AutoSize = $true
    $cb.Location = New-Object System.Drawing.Point(25, $y)
    $form.Controls.Add($cb)
    $checkBoxes += $cb
    $y += 28
}

$okButton = New-Object System.Windows.Forms.Button
$okButton.Text         = 'Install'
$okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
$okButton.Location     = New-Object System.Drawing.Point(140, ($y + 10))
$okButton.Size         = New-Object System.Drawing.Size(80, 28)
$form.Controls.Add($okButton)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text         = 'Cancel'
$cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$cancelButton.Location     = New-Object System.Drawing.Point(228, ($y + 10))
$cancelButton.Size         = New-Object System.Drawing.Size(80, 28)
$form.Controls.Add($cancelButton)

$form.AcceptButton = $okButton
$form.CancelButton = $cancelButton

$result = $form.ShowDialog()

if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
    Write-Host 'Cancelled — nothing installed.'
    return
}

$selectedYears = @($checkBoxes | Where-Object { $_.Checked } | ForEach-Object { [int]$_.Tag })

if (-not $selectedYears) {
    Write-Warning 'No versions ticked — nothing installed.'
    return
}

Write-Host "Installing RevitDevReload ($Configuration) to: $($selectedYears -join ', ')"
& $deployScript -RevitYears $selectedYears -Configuration $Configuration
