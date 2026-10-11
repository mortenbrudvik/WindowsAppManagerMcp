<#
.SYNOPSIS
    Checks that the committed bin/WindowsAppManagerMcp.exe (the exe the plugin runs) was built
    from the current sources with the current version.

.DESCRIPTION
    Reads the exe's file metadata only; it never runs the exe. Checks:
      1. Directory.Build.props, plugin.json and marketplace.json declare the same version.
      2. With -Tag: the tag is v<version>.
      3. The exe's ProductVersion is <version>+<commit sha>, and its bytes carry the PerMonitorV2
         dpiAwareness of src/WindowsAppManagerMcp/app.manifest.
      4. That commit exists and no build input changed between it and HEAD. The build inputs are
         listed once, in $BuildInputPaths in scripts/ReleaseCommon.ps1: src/, config/ (the sample
         layout presets embedded in the exe), the MSBuild, NuGet and SDK files at the repository
         root, and Directory.Build.props apart from its <Version> line, which check 3 covers.
         (scripts/Publish-Plugin.ps1 refuses to build while a build input has uncommitted
         changes, so the SHA always describes what the exe was built from.)

    Needs the full git history (in GitHub Actions: actions/checkout with fetch-depth: 0).

    The build workflow runs this strictly on v* tags, so a release cannot ship a stale exe, and on
    pushes and pull requests that change bin/WindowsAppManagerMcp.exe (Test-PluginBinaryChanged in
    scripts/ReleaseCommon.ps1 decides), so a stale or hand-built exe fails when it is committed.
    On other pushes and pull requests it runs with -WarnOnly, because a stale exe is expected
    there until the next release.

.PARAMETER Tag
    The release tag being built (for example v1.12.0). Must equal v<version>.

.PARAMETER ExePath
    The exe to check. Defaults to <repo>/bin/WindowsAppManagerMcp.exe.

.PARAMETER WarnOnly
    Report problems as warnings and exit 0.

.EXAMPLE
    ./scripts/Test-PluginBinary.ps1

.EXAMPLE
    ./scripts/Test-PluginBinary.ps1 -Tag v1.12.0
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$ExePath,
    [switch]$WarnOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$root = Get-RepoRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root "bin/$($script:ExeName)"
}
$exeLabel = Get-RepoRelativeLabel $root $ExePath
$version = Get-PropsVersion $root
$problems = Get-PluginBinaryProblems $root $ExePath $Tag

$inActions = $env:GITHUB_ACTIONS -eq 'true'
if ($problems.Count -eq 0) {
    Write-Host "OK: $exeLabel is $version and was built from the current build inputs (src/, config/ and the root build files)."
    exit 0
}

foreach ($problem in $problems) {
    if ($inActions) {
        $level = $(if ($WarnOnly) { 'warning' } else { 'error' })
        Write-Host "::$level title=Plugin binary check::$problem"
    }
    elseif ($WarnOnly) {
        Write-Warning $problem
    }
    else {
        Write-Host "ERROR: $problem" -ForegroundColor Red
    }
}

if ($WarnOnly) {
    exit 0
}
exit 1
