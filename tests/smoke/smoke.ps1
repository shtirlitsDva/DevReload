# Autonomous smoke test for DevReload's MCP tool surface.
#
# - Backs up %APPDATA%\DevReload\plugins.json (the smoke registers a
#   temporary plugin and must restore on exit).
# - Launches AutoCAD 2025 with a startup script that NETLOADs the
#   worktree's Deploy/DevReload.bundle.
# - Polls for the \\.\pipe\acad-rpc-<pid> pipe.
# - Drives each of the 19 DevReload tools and the example plugin's
#   tools end-to-end, capturing structured responses.
# - Writes results to tests/smoke/results.json.
# - Restores plugins.json and (optionally) closes AutoCAD.

[CmdletBinding()]
param(
    [string]$WorktreeRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$AcadExe = 'C:\Program Files\Autodesk\AutoCAD 2025\acad.exe',
    [int]$PipeTimeoutSeconds = 120,
    [int]$ToolCallTimeoutMs = 30000,
    [switch]$KeepAcadOpen
)

$ErrorActionPreference = 'Stop'
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Sequence = 1

$bundleDll = Join-Path $WorktreeRoot 'Deploy\DevReload.bundle\Contents\Win64\DevReload.dll'
if (-not (Test-Path $bundleDll)) {
    throw "DevReload.dll not found at $bundleDll. Build Release|x64 first."
}
$pluginsJson = Join-Path $env:APPDATA 'DevReload\plugins.json'
$pluginsJsonBackup = "$pluginsJson.smokebak"

# ── helpers ───────────────────────────────────────────────────────

function Backup-PluginsJson {
    if (Test-Path $pluginsJson) {
        Copy-Item $pluginsJson $pluginsJsonBackup -Force
        Write-Host "Backed up plugins.json -> $pluginsJsonBackup"
    }
}

function Restore-PluginsJson {
    if (Test-Path $pluginsJsonBackup) {
        Copy-Item $pluginsJsonBackup $pluginsJson -Force
        Remove-Item $pluginsJsonBackup -Force
        Write-Host "Restored plugins.json"
    }
}

function Start-AcadWithDevReload {
    $startupScr = Join-Path $env:TEMP "devreload-smoke-startup.scr"
    @"
FILEDIA
0
NETLOAD
$bundleDll
FILEDIA
1
"@ | Set-Content -Path $startupScr -Encoding ASCII
    Write-Host "Launching AutoCAD with startup script $startupScr"
    $proc = Start-Process -FilePath $AcadExe -ArgumentList @('/b', $startupScr, '/nologo') -PassThru
    return $proc
}

function Wait-RpcPipe {
    param([int]$AcadPid, [int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $pipeName = "acad-rpc-$AcadPid"
    Write-Host "Polling for pipe $pipeName (timeout ${TimeoutSeconds}s)..."
    while ((Get-Date) -lt $deadline) {
        $pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -match 'acad-rpc-\d+$' }
        foreach ($p in $pipes) {
            if ($p -match "acad-rpc-(\d+)$" -and [int]$matches[1] -eq $AcadPid) {
                Write-Host "Found pipe: $p"
                return $pipeName
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Pipe acad-rpc-$AcadPid did not appear within ${TimeoutSeconds}s"
}

function Connect-Pipe {
    param([string]$PipeName)
    $client = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $client.Connect(5000)
    $script:Reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8, $false, 8192, $true)
    $script:Writer = New-Object System.IO.StreamWriter($client, [System.Text.UTF8Encoding]::new($false, $true), 8192, $true)
    $script:Writer.NewLine = "`n"
    $script:Writer.AutoFlush = $true
    $script:PipeClient = $client
}

function Send-Json {
    param([hashtable]$Msg)
    $line = $Msg | ConvertTo-Json -Depth 20 -Compress
    $script:Writer.WriteLine($line) | Out-Null
}

function Read-JsonResponse {
    param([int]$ExpectedId, [int]$TimeoutMs)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $remainingMs = [int]($deadline - (Get-Date)).TotalMilliseconds
        if ($remainingMs -le 0) { break }
        $readTask = $script:Reader.ReadLineAsync()
        if (-not $readTask.Wait($remainingMs)) {
            throw "Timed out waiting for response to id=$ExpectedId"
        }
        $line = $readTask.Result
        if ($null -eq $line) { throw "Pipe closed waiting for id=$ExpectedId" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $obj = $line | ConvertFrom-Json -Depth 50 } catch { continue }
        if ($obj.PSObject.Properties.Name -contains 'method') {
            # Server notification (tools/list_changed etc.) — skip.
            continue
        }
        if ($obj.id -eq $ExpectedId) { return $obj }
    }
    throw "No response for id=$ExpectedId within ${TimeoutMs}ms"
}

function Invoke-Tool {
    param(
        [string]$Name,
        [hashtable]$Arguments = @{},
        [string]$Label = $null
    )
    $id = $script:Sequence
    $script:Sequence++
    $msg = @{
        jsonrpc = '2.0'
        id      = $id
        method  = 'tools/call'
        params  = @{
            name      = $Name
            arguments = $Arguments
        }
    }
    Send-Json $msg
    try {
        $resp = Read-JsonResponse -ExpectedId $id -TimeoutMs $ToolCallTimeoutMs
        $text = $null
        $isError = $false
        if ($resp.error) {
            $isError = $true
            $text = $resp.error.message
        } else {
            $content = $resp.result.content
            if ($content -and $content.Count -gt 0) { $text = $content[0].text }
            $isError = [bool]$resp.result.isError
        }
        $entry = [pscustomobject]@{
            label    = $(if ($Label) { $Label } else { $Name })
            tool     = $Name
            args     = $Arguments
            isError  = $isError
            response = $text
        }
        $script:Results.Add($entry) | Out-Null
        $marker = if ($isError) { '✗' } else { '✓' }
        $preview = if ($text) { ($text.Substring(0, [Math]::Min(80, $text.Length))) -replace "`r?`n", ' / ' } else { '' }
        Write-Host "  $marker $Name $preview"
        return
    } catch {
        $entry = [pscustomobject]@{
            label    = $(if ($Label) { $Label } else { $Name })
            tool     = $Name
            args     = $Arguments
            isError  = $true
            response = "harness exception: $_"
        }
        $script:Results.Add($entry) | Out-Null
        Write-Host "  ✗ $Name harness exception: $_"
        return
    }
}

function Send-Handshake {
    Send-Json @{
        jsonrpc = '2.0'
        id      = 0
        method  = 'initialize'
        params  = @{
            protocolVersion = '2025-03-26'
            capabilities    = @{}
            clientInfo      = @{ name = 'smoke'; version = '1' }
        }
    }
    $initResp = Read-JsonResponse -ExpectedId 0 -TimeoutMs 10000
    Write-Host "initialize -> $($initResp.result.serverInfo.name) v$($initResp.result.serverInfo.version)"
    Send-Json @{
        jsonrpc = '2.0'
        method  = 'notifications/initialized'
    }
}

function Read-PluginsJson {
    if (-not (Test-Path $pluginsJson)) { return $null }
    return (Get-Content $pluginsJson -Raw | ConvertFrom-Json -Depth 50)
}

# Replacement for the dropped is_registered / is_plugin_loaded tools:
# both questions are answered by filtering list_plugins. Sends the
# call, asserts, records a result entry as if it were a tool call.
function Assert-ListPlugins-Predicate {
    param(
        [string]$Label,
        [scriptblock]$Predicate,
        [bool]$ExpectedValue
    )
    $id = $script:Sequence; $script:Sequence++
    Send-Json @{
        jsonrpc = '2.0'; id = $id; method = 'tools/call'
        params  = @{ name = 'devreload_list_plugins'; arguments = @{} }
    }
    $resp = Read-JsonResponse -ExpectedId $id -TimeoutMs $ToolCallTimeoutMs
    $text = $resp.result.content[0].text
    $plugins = $text | ConvertFrom-Json -Depth 50
    $actual = [bool](& $Predicate $plugins)
    $ok = ($actual -eq $ExpectedValue)
    $marker = if ($ok) { '✓' } else { '✗' }
    Write-Host "  $marker [via list_plugins] $Label  expected=$ExpectedValue actual=$actual"
    $script:Results.Add([pscustomobject]@{
        label    = $Label
        tool     = 'list_plugins+filter'
        args     = @{}
        isError  = -not $ok
        response = "actual=$actual expected=$ExpectedValue"
    }) | Out-Null
}

function Assert-Persistence {
    param(
        [string]$Label,
        [bool]$Condition,
        [string]$Expected,
        [string]$Actual
    )
    $marker = if ($Condition) { '✓' } else { '✗' }
    Write-Host "  $marker [persist] $Label  expected=$Expected actual=$Actual"
    $script:Results.Add([pscustomobject]@{
        label    = "persistence: $Label"
        tool     = '(plugins.json check)'
        args     = @{ expected = $Expected; actual = $Actual }
        isError  = -not $Condition
        response = if ($Condition) { 'persisted' } else { "MISMATCH (expected '$Expected', got '$Actual')" }
    }) | Out-Null
}

function Get-ToolsList {
    $id = 9999
    Send-Json @{ jsonrpc = '2.0'; id = $id; method = 'tools/list' }
    $resp = Read-JsonResponse -ExpectedId $id -TimeoutMs 5000
    return $resp.result.tools
}

# ── main ──────────────────────────────────────────────────────────

Backup-PluginsJson
$acadProc = $null
try {
    $acadProc = Start-AcadWithDevReload
    Write-Host "AutoCAD PID = $($acadProc.Id)"
    $pipeName = Wait-RpcPipe -AcadPid $acadProc.Id -TimeoutSeconds $PipeTimeoutSeconds
    Connect-Pipe -PipeName $pipeName

    Send-Handshake

    Write-Host ""
    Write-Host "── Phase 1: tools/list discovery"
    $toolsBefore = Get-ToolsList
    Write-Host ("Total tools before plugin register: {0}" -f $toolsBefore.Count)
    $namesBefore = $toolsBefore | ForEach-Object { $_.name }
    $script:Results.Add([pscustomobject]@{
        label    = 'tools/list (before)'
        tool     = 'tools/list'
        args     = @{}
        isError  = $false
        response = ($namesBefore -join ', ')
    }) | Out-Null

    Write-Host ""
    Write-Host "── Phase 2: query tools (no side effects)"
    Invoke-Tool 'devreload_list_tools'
    Invoke-Tool 'devreload_list_plugins'
    Invoke-Tool 'devreload_get_assembly_info' @{ name = 'Acd.Mcp' } 'get_assembly_info(Acd.Mcp not loaded)'

    Write-Host ""
    Write-Host "── Phase 3: pure-IO tools"
    Invoke-Tool 'devreload_list_worktrees' @{ repoRoot = $WorktreeRoot }
    $testProjectDir = Join-Path $WorktreeRoot 'example\DevReloadTest'
    $testBuildDir = Join-Path $testProjectDir 'bin\Debug'
    Invoke-Tool 'devreload_read_shared_assemblies' @{ buildDir = $testBuildDir }
    Invoke-Tool 'devreload_write_shared_assemblies' @{
        buildDir            = $testBuildDir
        sharedAssemblies    = @('Acad.Rpc.Core')
        mixedModeAssemblies = @()
        streamedAssemblies  = @('Acad.Rpc.Core')
    }
    Invoke-Tool 'devreload_build_project' @{
        csprojPath         = (Join-Path $testProjectDir 'DevReloadTest.csproj')
        buildConfiguration = 'Debug'
    } 'build_project(DevReloadTest Debug)'
    Invoke-Tool 'devreload_get_available_projects'

    Write-Host ""
    Write-Host "── Phase 4: lifecycle on a temp registration"
    $testDllPath = Join-Path $testBuildDir 'DevReloadTest.Core.dll'
    Invoke-Tool 'devreload_register_new_plugin' @{
        name               = 'DevReloadTest'
        projectFilePath    = (Join-Path $testProjectDir 'DevReloadTest.csproj')
        dllPath            = $testDllPath
        buildConfiguration = 'Debug'
        commandPrefix      = 'TESTSMOKE'
        loadOnStartup      = $false
    }
    Assert-ListPlugins-Predicate -Label 'DevReloadTest registered' `
        -Predicate { param($p) ($p | Where-Object { $_.name -eq 'DevReloadTest' }) -ne $null } `
        -ExpectedValue $true
    Invoke-Tool 'devreload_load_plugin' @{ name = 'DevReloadTest' }
    Assert-ListPlugins-Predicate -Label 'DevReloadTest loaded' `
        -Predicate { param($p) ($p | Where-Object { $_.name -eq 'DevReloadTest' }).loaded } `
        -ExpectedValue $true
    Invoke-Tool 'devreload_get_assembly_info' @{ name = 'DevReloadTest' } 'get_assembly_info after load'
    Invoke-Tool 'devreload_list_tools' -Label 'list_tools includes plugin tools'

    Write-Host ""
    Write-Host "── Phase 5: invoke the plugin's own tools"
    $toolsLoaded = Get-ToolsList
    $pluginToolNames = $toolsLoaded | ForEach-Object { $_.name } | Where-Object { $_ -like 'devreloadtest_*' }
    Write-Host ("Plugin tool names: {0}" -f ($pluginToolNames -join ', '))
    foreach ($t in $pluginToolNames) {
        if ($t -like '*echo*') {
            Invoke-Tool $t @{ text = 'hi from smoke' }
        } else {
            Invoke-Tool $t @{}
        }
    }

    Write-Host ""
    Write-Host "── Phase 6: reload / mutations / unregister + unified-path persistence checks"
    Invoke-Tool 'devreload_reload' @{ name = 'DevReloadTest' } 'reload (builds + swaps ALC)'
    Assert-ListPlugins-Predicate -Label 'DevReloadTest loaded after reload' `
        -Predicate { param($p) ($p | Where-Object { $_.name -eq 'DevReloadTest' }).loaded } `
        -ExpectedValue $true

    # Mutate build config via RPC, then read plugins.json directly
    Invoke-Tool 'devreload_update_build_configuration' @{ name = 'DevReloadTest'; buildConfiguration = 'Release' }
    $cfg = Read-PluginsJson
    $entry = $cfg.plugins | Where-Object { $_.name -eq 'DevReloadTest' }
    Assert-Persistence -Label 'update_build_configuration -> plugins.json' `
        -Condition ($entry.buildConfiguration -eq 'Release') `
        -Expected 'Release' -Actual $entry.buildConfiguration

    # Mutate worktree via RPC, then read plugins.json directly
    $fakeWorktree = 'H:\smoke-temp-worktree'
    Invoke-Tool 'devreload_update_active_worktree' @{ name = 'DevReloadTest'; worktreePath = $fakeWorktree }
    $cfg = Read-PluginsJson
    $entry = $cfg.plugins | Where-Object { $_.name -eq 'DevReloadTest' }
    Assert-Persistence -Label 'update_active_worktree -> plugins.json' `
        -Condition ($entry.activeWorktreePath -eq $fakeWorktree) `
        -Expected $fakeWorktree -Actual $entry.activeWorktreePath

    # Clear worktree back
    Invoke-Tool 'devreload_update_active_worktree' @{ name = 'DevReloadTest'; worktreePath = $null }
    $cfg = Read-PluginsJson
    $entry = $cfg.plugins | Where-Object { $_.name -eq 'DevReloadTest' }
    Assert-Persistence -Label 'update_active_worktree(null) -> plugins.json' `
        -Condition ([string]::IsNullOrEmpty($entry.activeWorktreePath)) `
        -Expected '(null)' -Actual ([string]$entry.activeWorktreePath)

    Invoke-Tool 'devreload_unload_plugin' @{ name = 'DevReloadTest' }
    Assert-ListPlugins-Predicate -Label 'DevReloadTest unloaded' `
        -Predicate { param($p) -not ($p | Where-Object { $_.name -eq 'DevReloadTest' }).loaded } `
        -ExpectedValue $true
    Invoke-Tool 'devreload_unload_all' -Label 'unload_all'
    Invoke-Tool 'devreload_unregister' @{ name = 'DevReloadTest' }
    Assert-ListPlugins-Predicate -Label 'DevReloadTest unregistered' `
        -Predicate { param($p) ($p | Where-Object { $_.name -eq 'DevReloadTest' }) -eq $null } `
        -ExpectedValue $true

    # Verify the unregister persisted to plugins.json
    $cfg = Read-PluginsJson
    $stillPresent = $cfg.plugins | Where-Object { $_.name -eq 'DevReloadTest' }
    Assert-Persistence -Label 'unregister -> entry removed from plugins.json' `
        -Condition ($null -eq $stillPresent) `
        -Expected '(absent)' -Actual ($stillPresent | Out-String)
}
finally {
    if ($script:PipeClient) { try { $script:PipeClient.Dispose() } catch {} }
    Restore-PluginsJson
    if ($acadProc -and -not $KeepAcadOpen) {
        Write-Host "Stopping AutoCAD..."
        try { Stop-Process -Id $acadProc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    $outFile = Join-Path $PSScriptRoot 'results.json'
    $script:Results | ConvertTo-Json -Depth 20 | Set-Content -Path $outFile -Encoding UTF8
    Write-Host ""
    Write-Host "Results written to $outFile"
    $passed = ($script:Results | Where-Object { -not $_.isError }).Count
    $failed = ($script:Results | Where-Object { $_.isError }).Count
    Write-Host "Summary: $passed passed, $failed failed (out of $($script:Results.Count))"
}
