<#
.SYNOPSIS
    Starts a built server exe, lists its tools over MCP (stdio) and compares them with the checked-in
    tools/list snapshot.

.DESCRIPTION
    The snapshot (tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json) is what the sources serve:
    ToolSurfaceSnapshotTests keeps it equal to the tools/list of an in-process server with the production
    registrations. This script checks that an exe serves the same: it starts the exe with stdin and stdout
    redirected, sends initialize, notifications/initialized and tools/list (following nextCursor), closes
    stdin so the server exits, and compares every tool object with the snapshot (names, descriptions,
    annotations, input and output schemas, member order included). A version or SHA check cannot catch an exe
    whose tools differ from its sources; this does.

    It RUNS the exe. The server starts as it does for an MCP client: it records the foreground window, logs its
    safety settings to stderr and joins the pause shared by the servers on the desktop. The script starts it
    with WAM_PAUSE_HOTKEY=none, so it registers no global hotkey. It calls no tool, so it sends no input and
    changes no window. The build workflow runs it on a GitHub-hosted runner; it is not part of the test suite.

    The build workflow runs it twice: on the exe it has just published from the checked-out sources (always),
    and with -OnlyIfCurrent on the committed bin/WindowsAppManagerMcp.exe, which is compared only when it was
    built from the current build inputs (the checks of scripts/Test-PluginBinary.ps1). A stale committed exe is
    expected between releases and is skipped with a notice.

.PARAMETER ExePath
    The exe to start.

.PARAMETER ArgumentList
    Arguments for the exe. The server takes none; this exists to point the script at a stand-in server.

.PARAMETER SnapshotPath
    The snapshot. Defaults to tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json.

.PARAMETER OnlyIfCurrent
    Skip (exit 0 with a notice) unless the exe passes the checks of Test-PluginBinary.ps1: its version is the
    current one and no build input changed since the commit it was built from.

.PARAMETER TimeoutSeconds
    How long to wait for each response and for the server to exit. Default 60.

.EXAMPLE
    ./scripts/Test-ToolsSnapshot.ps1 -ExePath ./publish/WindowsAppManagerMcp.exe

.EXAMPLE
    ./scripts/Test-ToolsSnapshot.ps1 -ExePath ./bin/WindowsAppManagerMcp.exe -OnlyIfCurrent
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [string[]]$ArgumentList = @(),
    [string]$SnapshotPath,
    [switch]$OnlyIfCurrent,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$root = Get-RepoRoot
if (-not $SnapshotPath) {
    $SnapshotPath = Join-Path $root 'tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json'
}
$ExePath = [System.IO.Path]::GetFullPath($ExePath)
$exeLabel = Get-RepoRelativeLabel $root $ExePath
$inActions = $env:GITHUB_ACTIONS -eq 'true'

function Write-Outcome([string]$Level, [string]$Message) {
    if ($inActions) {
        Write-Host "::$Level title=Tool surface check::$Message"
    }
    elseif ($Level -eq 'error') {
        Write-Host "ERROR: $Message" -ForegroundColor Red
    }
    else {
        Write-Host "$($Level.ToUpperInvariant()): $Message"
    }
}

if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    Write-Outcome 'error' "The snapshot $SnapshotPath does not exist."
    exit 1
}

if ($OnlyIfCurrent) {
    $problems = Get-PluginBinaryProblems $root $ExePath ''
    if ($problems.Count -gt 0) {
        Write-Outcome 'notice' "Skipped the tools/list check of ${exeLabel}: it is not a build of the current sources, so its tools are expected to differ until the next release. $($problems -join ' ')"
        exit 0
    }
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Outcome 'error' "$exeLabel does not exist."
    exit 1
}

# One JSON-RPC message per line, as the MCP stdio transport sends them
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExePath)
foreach ($argument in $ArgumentList) {
    $startInfo.ArgumentList.Add($argument)
}
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Environment['WAM_PAUSE_HOTKEY'] = 'none' # no global hotkey for a server that only lists its tools
$utf8 = [System.Text.UTF8Encoding]::new($false)
$startInfo.StandardInputEncoding = $utf8
$startInfo.StandardOutputEncoding = $utf8
$startInfo.StandardErrorEncoding = $utf8

$timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$process = [System.Diagnostics.Process]::Start($startInfo)
$stderr = $process.StandardError.ReadToEndAsync()
$process.StandardInput.NewLine = "`n"
$process.StandardInput.AutoFlush = $true

function Send-Message([hashtable]$Message) {
    $process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 20 -Compress))
}

function Receive-Response([int]$Id) {
    $deadline = [DateTime]::UtcNow + $timeout
    while ($true) {
        $remaining = $deadline - [DateTime]::UtcNow
        $read = $process.StandardOutput.ReadLineAsync()
        if ($remaining -le [TimeSpan]::Zero -or -not $read.Wait($remaining)) {
            throw "No response to request $Id within $TimeoutSeconds s."
        }
        $line = $read.Result
        if ($null -eq $line) {
            throw "The server closed its output before it answered request $Id (exit code $(if ($process.HasExited) { $process.ExitCode } else { 'none yet' }))."
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $message = $line | ConvertFrom-Json -Depth 100
        # Notifications and requests from the server (no id, or a method) are not the answer
        if ($message.PSObject.Properties['method'] -or -not $message.PSObject.Properties['id'] -or $message.id -ne $Id) {
            continue
        }
        if ($message.PSObject.Properties['error']) {
            throw "Request $Id failed: $($message.error | ConvertTo-Json -Depth 10 -Compress)"
        }
        return $message.result
    }
}

$liveTools = [System.Collections.Generic.List[object]]::new()
$failure = $null
try {
    Send-Message @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params  = @{
            protocolVersion = '2025-06-18'
            capabilities    = @{}
            clientInfo      = @{ name = 'tools-snapshot-check'; version = '1.0.0' }
        }
    }
    $initialized = Receive-Response 1
    Write-Host "Started $exeLabel`: $($initialized.serverInfo.name) $($initialized.serverInfo.version), protocol $($initialized.protocolVersion)."
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    $id = 2
    $cursor = $null
    do {
        $request = @{ jsonrpc = '2.0'; id = $id; method = 'tools/list' }
        if ($cursor) {
            $request.params = @{ cursor = $cursor }
        }
        Send-Message $request
        $page = Receive-Response $id
        foreach ($tool in @($page.tools)) {
            $liveTools.Add($tool)
        }
        $cursor = $(if ($page.PSObject.Properties['nextCursor']) { $page.nextCursor } else { $null })
        $id++
    } while ($cursor)
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # End of stdin ends the session, and the server exits
    $process.StandardInput.Close()
    if (-not $process.WaitForExit([int]$timeout.TotalMilliseconds)) {
        $process.Kill($true)
        $process.WaitForExit()
        Write-Warning "$exeLabel did not exit within $TimeoutSeconds s after its input was closed, so it was stopped."
    }
}

if ($failure) {
    # The server's log (stderr) says why it did not answer, for example an exception at start
    if ($stderr.Wait([TimeSpan]::FromSeconds(5)) -and $stderr.Result) {
        Write-Host "Standard error of ${exeLabel}:"
        Write-Host $stderr.Result
    }
    Write-Outcome 'error' "Listing the tools of $exeLabel failed: $failure"
    exit 1
}

# The same JSON text on both sides when the tool objects are equal: ConvertTo-Json writes members in the order
# they were read, so a reordered parameter list is a difference, as in the snapshot test
function ConvertTo-Canonical($Node) {
    $Node | ConvertTo-Json -Depth 100 -Compress
}

$snapshotTools = @((Get-Content -LiteralPath $SnapshotPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100).tools)
$expected = [ordered]@{}
foreach ($tool in $snapshotTools) {
    $expected[[string]$tool.name] = ConvertTo-Canonical $tool
}
$actual = [ordered]@{}
foreach ($tool in $liveTools) {
    $actual[[string]$tool.name] = ConvertTo-Canonical $tool
}

$differences = [System.Collections.Generic.List[string]]::new()
foreach ($name in $actual.Keys) {
    if (-not $expected.Contains($name)) {
        $differences.Add("served, not in the snapshot: $name")
    }
    elseif ($expected[$name] -cne $actual[$name]) {
        $differences.Add("differs from the snapshot: $name")
    }
}
foreach ($name in $expected.Keys) {
    if (-not $actual.Contains($name)) {
        $differences.Add("in the snapshot, not served: $name")
    }
}

if ($differences.Count -eq 0) {
    Write-Host "OK: the $($actual.Count) tools of $exeLabel match $(Get-RepoRelativeLabel $root ([System.IO.Path]::GetFullPath($SnapshotPath)))."
    exit 0
}

foreach ($difference in $differences) {
    Write-Host "  $difference"
}
foreach ($name in @($actual.Keys) | Where-Object { $expected.Contains($_) -and $expected[$_] -cne $actual[$_] } | Select-Object -First 3) {
    Write-Host "  ${name}, snapshot: $($expected[$name])"
    Write-Host "  ${name}, served:   $($actual[$name])"
}
Write-Outcome 'error' "The tools/list of $exeLabel differs from the snapshot ($($differences.Count) tool(s)). If the sources changed the tools, run the tests with UPDATE_SNAPSHOTS=1 and commit the snapshot; if only the exe differs, rebuild it (scripts/Publish-Plugin.ps1)."
exit 1
