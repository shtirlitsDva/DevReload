# Full end-to-end agent smoke. Drives the bridge over stdio to:
#   1. Launch Civil 3D from a cold start.
#   2. Wait for it to be quiescent.
#   3. Open a new drawing (start-screen → active-doc).
#   4. NETLOAD the worktree's DevReload bundle.
#   5. Wait for the in-AutoCAD RPC pipe to come up.
#   6. Verify tools/list now merges acad_* + devreload_*.
#   7. Drive a devreload tool through the bridge forwarder.
#   8. Quit AutoCAD gracefully.
#   9. Verify the pid is gone.
#
# Backs up plugins.json on entry; restores on exit (regardless of success).
# Civil 3D startup is slow on a cold cache — total runtime can exceed 3
# minutes. Adjust timeouts via parameters if needed.

[CmdletBinding()]
param(
    [string]$WorktreeRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Configuration = 'Release',
    [int]$StartupTimeoutSeconds = 300,
    [int]$PipeWaitSeconds = 60
)

$ErrorActionPreference = 'Stop'

$bridgeExe = Join-Path $WorktreeRoot "src\Acad.Rpc.Bridge\bin\$Configuration\Acad.Rpc.Bridge.exe"
$bundleDll = Join-Path $WorktreeRoot "Deploy\DevReload.bundle\Contents\Win64\DevReload.dll"
$pluginsJson = Join-Path $env:APPDATA 'DevReload\plugins.json'
$pluginsJsonBackup = "$pluginsJson.smokebak"

if (-not (Test-Path $bridgeExe)) { throw "Bridge exe not found: $bridgeExe" }
if (-not (Test-Path $bundleDll)) { throw "Bundle DLL not found: $bundleDll. Build Release|x64 of DevReload first." }

# ── plugins.json backup/restore ───────────────────────────────────

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

# ── bridge driver ─────────────────────────────────────────────────

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $bridgeExe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$script:Sequence = 1
$script:Pass = 0
$script:Fail = 0
$script:AcadPid = $null
$proc = $null
$script:LineQueue = $null

function Send-Request {
    param([string]$Method, [hashtable]$Params = $null, [int]$TimeoutMs = 60000)
    $id = $script:Sequence; $script:Sequence++
    $msg = @{ jsonrpc = '2.0'; id = $id; method = $Method }
    if ($Params -ne $null) { $msg.params = $Params }
    $line = $msg | ConvertTo-Json -Compress -Depth 10
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()

    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $remaining = [int](($deadline - (Get-Date)).TotalMilliseconds)
        if ($remaining -le 0) { break }
        $resp = $null
        $got = $script:LineQueue.TryTake([ref]$resp, [Math]::Min($remaining, 500))
        if (-not $got) {
            if ($script:LineQueue.IsCompleted) {
                throw "Bridge closed stdout while waiting for id $id (method=$Method)"
            }
            continue
        }
        if ([string]::IsNullOrWhiteSpace($resp)) { continue }
        # Skip non-JSON lines gracefully — partial-line flushes during
        # process shutdown can land in the queue alongside real responses.
        try { $obj = $resp | ConvertFrom-Json -ErrorAction Stop } catch {
            Write-Host "  <skipped non-JSON line> $($resp.Substring(0, [Math]::Min(60, $resp.Length)))" -ForegroundColor DarkGray
            continue
        }
        if ($obj.id -eq $id) { return $obj }
        if ($obj.method) { Write-Host "  <notification> $($obj.method)" -ForegroundColor DarkGray }
    }
    throw "Timed out waiting for response to id $id (method=$Method)"
}

function Assert {
    param([string]$Label, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        Write-Host "  PASS  $Label" -ForegroundColor Green
        $script:Pass++
    } else {
        Write-Host "  FAIL  $Label  $Detail" -ForegroundColor Red
        $script:Fail++
    }
}

try {
    Backup-PluginsJson

    $script:LineQueue = New-Object 'System.Collections.Concurrent.BlockingCollection[string]'
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    # OutputDataReceived event fires per stdout line on a thread-pool
    # thread. We enqueue and let Send-Request consume — this avoids the
    # "stream is currently in use" error you get from issuing multiple
    # ReadLineAsync calls in parallel on a single StreamReader.
    $null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived `
        -MessageData $script:LineQueue -Action {
            if ($null -ne $EventArgs.Data) { $Event.MessageData.Add($EventArgs.Data) }
        }
    $null = $proc.Start()
    $proc.BeginOutputReadLine()
    Write-Host "Bridge started, pid $($proc.Id)"

    # 1. initialize + initial tools/list
    Write-Host "`n[1] initialize"
    $r = Send-Request 'initialize'
    Assert "server is acad-agent" ($r.result.serverInfo.name -eq 'acad-agent')

    Write-Host "`n[2] tools/list (no AutoCAD)"
    $r = Send-Request 'tools/list'
    $initialCount = $r.result.tools.Count
    Write-Host "  Initial tool count: $initialCount"
    Assert "initial list is acad_* only" (
        ($r.result.tools | Where-Object { -not $_.name.StartsWith('acad_') }).Count -eq 0)

    # 2. Launch Civil 3D with startupCommands that NETLOAD DevReload.
    # This is the canonical pattern: AutoCAD opens Drawing1.dwg + runs the
    # script, which NETLOADs DevReload, which opens its pipe. No COM/ROT
    # involved — saves us from the unsaved-Drawing1 ROT trap.
    Write-Host "`n[3] acad_start Civil3D with NETLOAD-at-boot"
    $netloadScript = "FILEDIA`n0`nNETLOAD`n$bundleDll`nFILEDIA`n1`n"
    $r = Send-Request 'tools/call' @{
        name = 'acad_start'
        arguments = @{ flavor = 'Civil3D'; startupCommands = $netloadScript }
    } -TimeoutMs 30000
    Assert "acad_start succeeded" ($r.result.isError -ne $true) `
        "body=$($r.result.content[0].text)"
    if ($r.result.isError -ne $true) {
        $startResult = ($r.result.content[0].text | ConvertFrom-Json)
        $script:AcadPid = $startResult.Pid
        Write-Host "  AutoCAD pid: $script:AcadPid, pipe: $($startResult.PipeName)"
        Assert "pid is positive" ($script:AcadPid -gt 0)
    }

    # 3. Wait for DevReload's pipe to come up. Pipe-based readiness is
    # COM-independent — works regardless of ROT state.
    Write-Host "`n[4] acad_wait_pipe (DevReload pipe)"
    $r = Send-Request 'tools/call' @{
        name = 'acad_wait_pipe'
        arguments = @{ timeoutSeconds = $StartupTimeoutSeconds }
    } -TimeoutMs (($StartupTimeoutSeconds + 30) * 1000)
    $waitResult = ($r.result.content[0].text | ConvertFrom-Json)
    Write-Host "  Wait elapsed: $($waitResult.ElapsedSeconds)s, succeeded=$($waitResult.Succeeded)"
    Assert "wait_pipe succeeded" ($waitResult.Succeeded -eq $true) `
        "reason=$($waitResult.Reason)"

    # 4. Verify merged tools/list now contains both acad_* and devreload_*.
    # Bridge's pipe forwarder auto-connects when the pipe appears; the
    # next tools/list call merges the remote catalogue in. A small wait
    # absorbs the forwarder's connect latency.
    Write-Host "`n[5] tools/list merged (acad_* + devreload_*)"
    $merged = $null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $r = Send-Request 'tools/list'
        if (($r.result.tools | Where-Object { $_.name.StartsWith('devreload_') }).Count -gt 0) {
            $merged = $r.result.tools
            break
        }
    }
    Assert "merged tools/list contains devreload_*" ($null -ne $merged) `
        "no devreload_ tools after 15s of polling"
    if ($merged) {
        $acadCount = ($merged | Where-Object { $_.name.StartsWith('acad_') }).Count
        $devCount = ($merged | Where-Object { $_.name.StartsWith('devreload_') }).Count
        Write-Host "  acad_* tools: $acadCount, devreload_* tools: $devCount, total: $($merged.Count)"
        Assert "14 acad_* tools" ($acadCount -eq 14)
        Assert "at least 15 devreload_* tools" ($devCount -ge 15)
    }

    # 5. Drive a forwarded devreload tool
    Write-Host "`n[6] tools/call devreload_list_plugins (forwarded over pipe)"
    $r = Send-Request 'tools/call' @{ name = 'devreload_list_plugins'; arguments = @{} } -TimeoutMs 30000
    Assert "devreload_list_plugins succeeded via forwarder" ($r.result.isError -ne $true) `
        "body=$($r.result.content[0].text)"

    # 6. acad_quit — graceful via COM if available, kill fallback either way.
    # ROT may be empty (unsaved Drawing1) — that just routes us to the
    # Kill path. The outcome flag distinguishes Graceful vs Killed.
    Write-Host "`n[7] acad_quit"
    $r = Send-Request 'tools/call' @{
        name = 'acad_quit'
        arguments = @{ saveChanges = $false; timeoutSeconds = 15 }
    } -TimeoutMs 30000
    Assert "quit returned" ($r.result.isError -ne $true) `
        "body=$($r.result.content[0].text)"
    $quitResult = ($r.result.content[0].text | ConvertFrom-Json)
    Write-Host "  Quit outcome: $($quitResult.Outcome)"
    Assert "quit outcome is Graceful or Killed" `
        ($quitResult.Outcome -eq 'Graceful' -or $quitResult.Outcome -eq 'Killed' -or $quitResult.Outcome -eq 0 -or $quitResult.Outcome -eq 1)

    Start-Sleep -Seconds 2
    $stillRunning = $null
    try { $stillRunning = Get-Process -Id $script:AcadPid -ErrorAction SilentlyContinue } catch {}
    Assert "AutoCAD pid $script:AcadPid is gone" ($null -eq $stillRunning)

    Write-Host "`nSummary: $script:Pass passed, $script:Fail failed (out of $($script:Pass + $script:Fail))" `
        -ForegroundColor Cyan
}
finally {
    try { if ($proc -and -not $proc.HasExited) { $proc.StandardInput.Close() } } catch {}
    try { $proc.WaitForExit(5000) | Out-Null } catch {}
    if ($proc -and -not $proc.HasExited) {
        Write-Host "Bridge did not exit cleanly, killing" -ForegroundColor Yellow
        try { $proc.Kill() } catch {}
    }
    # Belt-and-braces: if AutoCAD is still up, kill it.
    if ($script:AcadPid) {
        try {
            $stillRunning = Get-Process -Id $script:AcadPid -ErrorAction SilentlyContinue
            if ($stillRunning) {
                Write-Host "AutoCAD pid $script:AcadPid still alive — killing" -ForegroundColor Yellow
                Stop-Process -Id $script:AcadPid -Force -ErrorAction SilentlyContinue
            }
        } catch {}
    }
    Restore-PluginsJson
    try {
        $stderr = $proc.StandardError.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Host "`nBridge stderr (tail):" -ForegroundColor DarkGray
            ($stderr -split "`n" | Select-Object -Last 25) | ForEach-Object {
                Write-Host "  $_" -ForegroundColor DarkGray
            }
        }
    } catch {}
}

if ($script:Fail -gt 0) { exit 1 }
