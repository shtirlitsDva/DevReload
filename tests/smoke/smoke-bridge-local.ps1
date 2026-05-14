# Bridge-only smoke: drives the bridge over stdio with NO AutoCAD running.
# Validates that local (acad_*) tools work without a pipe, and that
# remote-tool calls fail cleanly with the expected error.

[CmdletBinding()]
param(
    [string]$WorktreeRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$bridgeExe = Join-Path $WorktreeRoot "src\Acad.Rpc.Bridge\bin\$Configuration\Acad.Rpc.Bridge.exe"
if (-not (Test-Path $bridgeExe)) {
    throw "Bridge exe not found at $bridgeExe. Build $Configuration|x64 first."
}

Write-Host "Bridge: $bridgeExe"

# ── stdio bridge driver ───────────────────────────────────────────

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
$script:LineQueue = New-Object 'System.Collections.Concurrent.BlockingCollection[string]'

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
# Hook stdout BEFORE Start. The handler runs on a thread-pool thread for
# each completed line, so we just enqueue and let Send-Request consume.
$null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived `
    -MessageData $script:LineQueue -Action {
        if ($null -ne $EventArgs.Data) { $Event.MessageData.Add($EventArgs.Data) }
    }
$null = $proc.Start()
$proc.BeginOutputReadLine()
Write-Host "Bridge started, pid $($proc.Id)"

function Send-Request {
    param([string]$Method, [hashtable]$Params = $null, [int]$TimeoutMs = 30000)
    $id = $script:Sequence
    $script:Sequence++
    $msg = @{ jsonrpc = '2.0'; id = $id; method = $Method }
    if ($Params -ne $null) { $msg.params = $Params }
    $line = $msg | ConvertTo-Json -Compress -Depth 8
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()

    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $remaining = [int](($deadline - (Get-Date)).TotalMilliseconds)
        if ($remaining -le 0) { break }
        $resp = $null
        $got = $script:LineQueue.TryTake([ref]$resp, [Math]::Min($remaining, 500))
        if (-not $got) {
            if ($script:LineQueue.IsCompleted) { throw "Bridge closed stdout while waiting for id $id" }
            continue
        }
        if ([string]::IsNullOrWhiteSpace($resp)) { continue }
        try { $obj = $resp | ConvertFrom-Json -ErrorAction Stop } catch {
            Write-Host "  <skipped non-JSON line> $($resp.Substring(0, [Math]::Min(60, $resp.Length)))" -ForegroundColor DarkGray
            continue
        }
        if ($obj.id -eq $id) { return $obj }
        if ($obj.method) { Write-Host "  <notification> $($obj.method)" -ForegroundColor DarkGray }
    }
    throw "Timed out waiting for id $id (method=$Method)"
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
    # 1. initialize
    Write-Host "`n[1] initialize"
    $r = Send-Request 'initialize'
    Assert "initialize returns serverInfo.name='acad-agent'" `
        ($r.result.serverInfo.name -eq 'acad-agent') `
        "got '$($r.result.serverInfo.name)'"

    # 2. tools/list — local-only mode (no AutoCAD running)
    Write-Host "`n[2] tools/list (no AutoCAD, local only)"
    $r = Send-Request 'tools/list'
    $names = $r.result.tools | ForEach-Object { $_.name }
    Write-Host "  Got $($names.Count) tools: $($names -join ', ')"
    Assert "exactly 14 acad_* tools returned" `
        ($names.Count -eq 14) `
        "expected 14, got $($names.Count)"
    Assert "all returned tools start with 'acad_'" `
        (-not ($names | Where-Object { -not $_.StartsWith('acad_') }))
    Assert "acad_start present" ($names -contains 'acad_start')
    Assert "acad_quit present" ($names -contains 'acad_quit')
    Assert "acad_wait_quiescent present" ($names -contains 'acad_wait_quiescent')
    Assert "acad_send_command present" ($names -contains 'acad_send_command')
    Assert "acad_post_command present" ($names -contains 'acad_post_command')

    # 3. tools/call acad_locate_install
    Write-Host "`n[3] tools/call acad_locate_install"
    $r = Send-Request 'tools/call' @{ name = 'acad_locate_install'; arguments = @{} }
    Assert "locate_install returned content" ($null -ne $r.result.content)
    $body = ($r.result.content[0].text | ConvertFrom-Json)
    Write-Host "  Found $($body.Count) install(s):"
    foreach ($i in $body) {
        Write-Host "    - $($i.ProductName) ($($i.ReleaseKey), $($i.Flavor)) at $($i.ExePath)"
    }
    Assert "at least one install discovered" ($body.Count -ge 1)
    Assert "Civil3D present in discovered installs" (
        ($body | Where-Object { $_.Flavor -eq 'Civil3D' -or $_.Flavor -eq 2 }).Count -ge 1
    )

    # 4. tools/call acad_list_instances — no AutoCAD running, should be empty (or zero acad procs)
    Write-Host "`n[4] tools/call acad_list_instances"
    $r = Send-Request 'tools/call' @{ name = 'acad_list_instances'; arguments = @{} }
    $body = ($r.result.content[0].text | ConvertFrom-Json)
    Write-Host "  $($body.Count) running instance(s)"
    Assert "list_instances returns an array" ($body -is [array] -or $body.Count -ge 0)

    # 5. tools/call acad_get_state without bound pid → must error
    Write-Host "`n[5] tools/call acad_get_state (no bind, no pid) — expect error"
    $r = Send-Request 'tools/call' @{ name = 'acad_get_state'; arguments = @{} }
    Assert "get_state without bind returns isError=true" `
        ($r.result.isError -eq $true) `
        "isError=$($r.result.isError), body=$($r.result.content[0].text)"
    Assert "error mentions binding/start guidance" `
        ($r.result.content[0].text -match 'bind|attach|acad_start') `
        "body=$($r.result.content[0].text)"

    # 6. tools/call for an unknown remote tool — pipe down
    Write-Host "`n[6] tools/call devreload_list_plugins (no pipe) — expect 'not running' error"
    $r = Send-Request 'tools/call' @{ name = 'devreload_list_plugins'; arguments = @{} }
    Assert "remote tool with no pipe returns isError=true" `
        ($r.result.isError -eq $true)
    Assert "error mentions acad_start" `
        ($r.result.content[0].text -match 'acad_start')

    # 7. tools/call with unknown name
    Write-Host "`n[7] tools/call totally_unknown_tool — expect generic error"
    $r = Send-Request 'tools/call' @{ name = 'totally_unknown_tool'; arguments = @{} }
    Assert "unknown tool returns isError=true" ($r.result.isError -eq $true)

    # 8. ping
    Write-Host "`n[8] ping"
    $r = Send-Request 'ping'
    Assert "ping returns {} result" ($null -ne $r.result)

    Write-Host "`nSummary: $script:Pass passed, $script:Fail failed (out of $($script:Pass + $script:Fail))" -ForegroundColor Cyan
}
finally {
    try { $script:ReaderCts.Cancel() } catch {}
    try { $proc.StandardInput.Close() } catch {}
    try { $proc.WaitForExit(5000) | Out-Null } catch {}
    if (-not $proc.HasExited) {
        Write-Host "Bridge did not exit cleanly, killing" -ForegroundColor Yellow
        $proc.Kill()
    }
    # Drain any stderr the bridge emitted (it logs there).
    $stderr = $proc.StandardError.ReadToEnd()
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host "`nBridge stderr:" -ForegroundColor DarkGray
        $stderr -split "`n" | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    }
}

if ($script:Fail -gt 0) { exit 1 }
