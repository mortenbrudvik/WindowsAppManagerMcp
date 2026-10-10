<#
.SYNOPSIS
    Sets the product version everywhere it is declared.

.DESCRIPTION
    Updates, in one step:
      - <Version> in Directory.Build.props (the single source of truth; stamped into the exe)
      - "version" in .claude-plugin/plugin.json
      - the windows-app-manager plugin entry's "version" in .claude-plugin/marketplace.json

    Only the version strings are rewritten; the rest of each file is left byte-for-byte intact.
    The marketplace's own metadata.version is not a plugin version and is left alone.

    Afterwards, run scripts/Publish-Plugin.ps1 so bin/WindowsAppManagerMcp.exe carries the new version.

.PARAMETER Version
    The new version, as MAJOR.MINOR.PATCH (for example 1.12.0). Release tags are v<Version>.

.EXAMPLE
    ./scripts/Set-Version.ps1 -Version 1.12.0

.EXAMPLE
    ./scripts/Set-Version.ps1 -Version 1.12.0 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$root = Get-RepoRoot

$targets = @(
    @{
        Path    = 'Directory.Build.props'
        Pattern = '(<Version>)[^<]*(</Version>)'
    },
    @{
        # The only "version" key in plugin.json is the top-level plugin version.
        Path    = '.claude-plugin/plugin.json'
        Pattern = '("version"\s*:\s*")[^"]*(")'
    },
    @{
        # The "version" that follows "name": "windows-app-manager" inside the same plugin entry.
        Path    = '.claude-plugin/marketplace.json'
        Pattern = '("name"\s*:\s*"windows-app-manager"\s*,[^{}]*?"version"\s*:\s*")[^"]*(")'
    }
)

$previous = Get-PropsVersion $root

# Validate every file before writing any, so a failure never leaves the versions half-updated.
$updates = foreach ($target in $targets) {
    $text = Read-RepoText $root $target.Path
    $regex = New-Object System.Text.RegularExpressions.Regex($target.Pattern)
    $count = $regex.Matches($text).Count
    if ($count -ne 1) {
        throw "Expected exactly one version to replace in $($target.Path), found $count. Update the file by hand or fix the pattern in Set-Version.ps1."
    }
    $replacement = '${1}' + $Version + '${2}'
    @{ Path = $target.Path; Text = $regex.Replace($text, $replacement) }
}

foreach ($update in $updates) {
    if ($PSCmdlet.ShouldProcess($update.Path, "Set version to $Version")) {
        Write-RepoText $root $update.Path $update.Text
    }
}

if ($WhatIfPreference) {
    return
}

$problems = Test-VersionConsistency $root
if ((Get-PropsVersion $root) -ne $Version) {
    $problems.Add("Directory.Build.props still does not say $Version.")
}
if ($problems.Count -gt 0) {
    throw ("Version update did not take effect:`n  " + ($problems -join "`n  "))
}

Write-Host "Version $previous -> $Version in Directory.Build.props, .claude-plugin/plugin.json and .claude-plugin/marketplace.json."
Write-Host 'Next: ./scripts/Publish-Plugin.ps1 to rebuild bin/WindowsAppManagerMcp.exe with this version.'
