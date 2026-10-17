<#
.SYNOPSIS
    Starts a built server exe, lists its tools, resources and prompts over MCP (stdio) and compares them with
    the checked-in tools/list and resources snapshots.

.DESCRIPTION
    The snapshot (tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json) is what the sources serve:
    ToolSurfaceSnapshotTests keeps it equal to the tools/list of an in-process server with the production
    registrations. This script checks that an exe serves the same: it starts the exe with stdin and stdout
    redirected, sends initialize, notifications/initialized and tools/list (following nextCursor), closes
    stdin so the server exits, and compares every tool object with the snapshot (names, descriptions,
    annotations, input and output schemas, member order included). A version or SHA check cannot catch an exe
    whose tools differ from its sources; this does.

    It then lists the resources, resource templates and prompts (resources/list, resources/templates/list,
    prompts/list, roadmap M6.9) and compares them, item by item, with
    tests/WindowsAppManagerMcp.Tests/Contract/resources.snapshot.json, which ToolSurfaceSnapshotTests keeps equal to
    the lists of an in-process server, so an exe that serves stale resources or prompts fails too.

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

.PARAMETER ResourcesSnapshotPath
    The resources and prompts snapshot. Defaults to tests/WindowsAppManagerMcp.Tests/Contract/resources.snapshot.json.

.PARAMETER OnlyIfCurrent
    Skip (exit 0 with a notice) unless the exe passes the checks of Test-PluginBinary.ps1: its version is the
    current one and no build input changed since the commit it was built from.

.PARAMETER TimeoutSeconds
    How long to wait for each response and for the server to exit. Default 60.

.PARAMETER ProtocolVersion
    The MCP revision the initialize request asks for. Default: the newest revision with the initialize handshake that
    the server's SDK speaks, which stdio clients such as Claude Code use (ProtocolRevisionContractTests pins it).

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
    [string]$ResourcesSnapshotPath,
    [switch]$OnlyIfCurrent,
    [int]$TimeoutSeconds = 60,
    [string]$ProtocolVersion = '2025-11-25'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$root = Get-RepoRoot
# Relative paths are relative to the PowerShell location (Set-Location), which .NET's current directory does not follow
if ($SnapshotPath) {
    $SnapshotPath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($SnapshotPath)
}
else {
    $SnapshotPath = Join-Path $root 'tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json'
}
if ($ResourcesSnapshotPath) {
    $ResourcesSnapshotPath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($ResourcesSnapshotPath)
}
else {
    $ResourcesSnapshotPath = Join-Path $root 'tests/WindowsAppManagerMcp.Tests/Contract/resources.snapshot.json'
}
$ExePath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($ExePath)
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

foreach ($path in @($SnapshotPath, $ResourcesSnapshotPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Outcome 'error' "The snapshot $path does not exist."
        exit 1
    }
}

# The lists of the resources snapshot (roadmap M6.9): the member that holds each, the method that lists it, and the key
# of its items
$resourceLists = @(
    @{ Member = 'resources'; Method = 'resources/list'; Key = 'uri' }
    @{ Member = 'resourceTemplates'; Method = 'resources/templates/list'; Key = 'uriTemplate' }
    @{ Member = 'prompts'; Method = 'prompts/list'; Key = 'name' }
)

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
# The snapshot is the whole tool surface: every toolset, nothing excluded, not read-only, whatever the caller's settings
$startInfo.Environment['WAM_TOOLSETS'] = 'all'
[void]$startInfo.Environment.Remove('WAM_EXCLUDE_TOOLS')
[void]$startInfo.Environment.Remove('WAM_READ_ONLY')
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

# Every item of a paged list method, following nextCursor; $script:id numbers the requests
function Get-AllItems([string]$Method, [string]$Member) {
    $items = [System.Collections.Generic.List[object]]::new()
    $cursor = $null
    do {
        $request = @{ jsonrpc = '2.0'; id = $script:id; method = $Method }
        if ($cursor) {
            $request.params = @{ cursor = $cursor }
        }
        Send-Message $request
        $page = Receive-Response $script:id
        # An absent or null member is an empty page: @($null) would be one item that is $null
        if ($null -ne $page.$Member) {
            foreach ($item in @($page.$Member)) {
                $items.Add($item)
            }
        }
        $cursor = $(if ($page.PSObject.Properties['nextCursor']) { $page.nextCursor } else { $null })
        $script:id++
    } while ($cursor)
    return , $items
}

$liveTools = [System.Collections.Generic.List[object]]::new()
$liveResources = @{}
$failure = $null
try {
    Send-Message @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params  = @{
            protocolVersion = $ProtocolVersion
            capabilities    = @{}
            clientInfo      = @{ name = 'tools-snapshot-check'; version = '1.0.0' }
        }
    }
    $initialized = Receive-Response 1
    Write-Host "Started $exeLabel`: $($initialized.serverInfo.name) $($initialized.serverInfo.version), protocol $($initialized.protocolVersion)."
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    $script:id = 2
    $liveTools = Get-AllItems 'tools/list' 'tools'
    foreach ($list in $resourceLists) {
        $liveResources[$list.Member] = Get-AllItems $list.Method $list.Member
    }
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
    Write-Outcome 'error' "Listing the tools, resources and prompts of $exeLabel failed: $failure"
    exit 1
}

# The same JSON text on both sides when the tool objects are equal: ConvertTo-Json writes members in the order
# they were read, so a reordered parameter list is a difference, as in the snapshot test
function ConvertTo-Canonical($Node) {
    $Node | ConvertTo-Json -Depth 100 -Compress
}

# What differs between the items of a snapshot list and the live list, one line per item, keyed by $Key; the first
# differing items with both texts. A null list or item is no item: an empty list in the snapshot or on the wire (a
# member that is null, absent or []) compares equal to any other empty list
function Compare-Items($Expected, $Live, [string]$Key, [string]$Label) {
    $before = [ordered]@{}
    foreach ($item in @($Expected | Where-Object { $null -ne $_ })) {
        $before[[string]$item.$Key] = ConvertTo-Canonical $item
    }
    $after = [ordered]@{}
    foreach ($item in @($Live | Where-Object { $null -ne $_ })) {
        $after[[string]$item.$Key] = ConvertTo-Canonical $item
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $shown = 0
    foreach ($name in $after.Keys) {
        if (-not $before.Contains($name)) {
            $lines.Add("served, not in the snapshot: $Label $name")
        }
        elseif ($before[$name] -cne $after[$name]) {
            $lines.Add("differs from the snapshot: $Label $name")
            if ($shown -lt 3) {
                $lines.Add("  ${name}, snapshot: $($before[$name])")
                $lines.Add("  ${name}, served:   $($after[$name])")
                $shown++
            }
        }
    }
    foreach ($name in $before.Keys) {
        if (-not $after.Contains($name)) {
            $lines.Add("in the snapshot, not served: $Label $name")
        }
    }
    return , $lines
}

$snapshotTools = @((Get-Content -LiteralPath $SnapshotPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100).tools)
$differences = Compare-Items $snapshotTools $liveTools 'name' 'tool'

$resourcesSnapshot = Get-Content -LiteralPath $ResourcesSnapshotPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100
$resourceDifferences = [System.Collections.Generic.List[string]]::new()
$resourceCount = 0
foreach ($list in $resourceLists) {
    $resourceCount += $liveResources[$list.Member].Count
    foreach ($line in (Compare-Items $resourcesSnapshot.($list.Member) $liveResources[$list.Member] $list.Key ($list.Method -replace '/list$', ''))) {
        $resourceDifferences.Add($line)
    }
}

$resourcesLabel = Get-RepoRelativeLabel $root $ResourcesSnapshotPath
if ($differences.Count -eq 0 -and $resourceDifferences.Count -eq 0) {
    Write-Host "OK: the $($liveTools.Count) tools of $exeLabel match $(Get-RepoRelativeLabel $root $SnapshotPath), and its $resourceCount resources, resource templates and prompts match $resourcesLabel."
    exit 0
}

foreach ($difference in @($differences) + @($resourceDifferences)) {
    Write-Host "  $difference"
}
if ($differences.Count -gt 0) {
    Write-Outcome 'error' "The tools/list of $exeLabel differs from the snapshot. If the sources changed the tools, run the tests with UPDATE_SNAPSHOTS=1 and commit the snapshot; if only the exe differs, rebuild it (scripts/Publish-Plugin.ps1)."
}
if ($resourceDifferences.Count -gt 0) {
    Write-Outcome 'error' "The resources, resource templates or prompts of $exeLabel differ from $resourcesLabel. If the sources changed them, run the tests with UPDATE_SNAPSHOTS=1 and commit the snapshot; if only the exe differs, rebuild it (scripts/Publish-Plugin.ps1)."
}
exit 1
