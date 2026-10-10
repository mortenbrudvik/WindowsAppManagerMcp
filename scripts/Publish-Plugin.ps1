<#
.SYNOPSIS
    Publishes the plugin executable to bin/, the only path .claude-plugin/plugin.json runs,
    and verifies that it carries the expected version.

.DESCRIPTION
    1. Checks that Directory.Build.props, plugin.json and marketplace.json declare the same version.
    2. Refuses to build when a build input has uncommitted changes: src/, config/ (the sample layout
       presets embedded in the exe), the MSBuild, NuGet and SDK files at the repository root
       (modified, untracked or ignored), and Directory.Build.props beyond the <Version> that
       Set-Version.ps1 changes ($BuildInputPaths in scripts/ReleaseCommon.ps1 lists them). The exe
       embeds the HEAD commit SHA in its ProductVersion, and CI (scripts/Test-PluginBinary.ps1) uses
       that SHA to prove the committed binary matches its build inputs. Use -AllowDirty only for
       local, uncommitted test builds.
    3. Runs dotnet publish (Release, win-x64, framework-dependent single file) into a staging folder
       under artifacts/ (gitignored).
    4. Checks the publish produced only WindowsAppManagerMcp.exe (plus symbols), because that single
       file is all the plugin ships.
    5. Checks the exe's ProductVersion is "<version>+<HEAD sha>".
    6. Copies WindowsAppManagerMcp.exe into -OutputDirectory (default: <repo>/bin).

    The exe is framework-dependent: it needs the .NET 8 Desktop Runtime (x64) on the machine.

.PARAMETER OutputDirectory
    Where to put WindowsAppManagerMcp.exe. Defaults to <repo>/bin. /update-plugin passes the
    installed plugin's bin folder so a local test build does not touch the committed exe.

.PARAMETER AllowDirty
    Build even if a build input has uncommitted changes. The exe's commit SHA will then not
    describe what it was built from, so never commit a binary built this way.

.EXAMPLE
    ./scripts/Publish-Plugin.ps1

.EXAMPLE
    ./scripts/Publish-Plugin.ps1 -OutputDirectory "$env:USERPROFILE\.claude\plugins\cache\...\bin" -AllowDirty
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$root = Get-RepoRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'bin'
}

# 1. Versions agree
$version = Get-PropsVersion $root
$problems = Test-VersionConsistency $root
if ($problems.Count -gt 0) {
    throw ("Version mismatch:`n  " + ($problems -join "`n  "))
}

# 2. Build inputs are committed
$head = Invoke-Git $root @('rev-parse', 'HEAD')
if ($head.ExitCode -ne 0) {
    throw "Could not read the HEAD commit in $root. Publish-Plugin.ps1 must run inside the git repository."
}
$headSha = ([string]$head.Output).Trim()

$dirty = @(Get-UncommittedBuildInputs $root)
if ($dirty.Count -gt 0) {
    $message = "Build inputs have uncommitted changes, so the exe's commit SHA ($headSha) would not describe what it was built from:`n  " + ($dirty -join "`n  ")
    if (-not $AllowDirty) {
        throw "$message`nCommit them first, or pass -AllowDirty for a local test build that must not be committed."
    }
    Write-Warning $message
}

# 3. Publish into a clean staging folder
$project = Join-Path $root 'src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj'
$staging = Join-Path $root 'artifacts/plugin-publish'
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

Write-Host "Publishing $($script:ExeName) $version from $headSha ..."
& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    --output $staging `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# 4. Only the exe ships
$exe = Join-Path $staging $script:ExeName
if (-not (Test-Path -LiteralPath $exe)) {
    throw "dotnet publish did not produce $($script:ExeName) in $staging."
}
$extra = @(Get-ChildItem -LiteralPath $staging -Recurse -File |
    Where-Object { $_.Name -ne $script:ExeName -and $_.Extension -ne '.pdb' })
if ($extra.Count -gt 0) {
    throw ("The publish produced files besides $($script:ExeName), but the plugin only ships that one file:`n  " +
        (($extra | ForEach-Object { $_.FullName.Substring($staging.Length + 1) }) -join "`n  "))
}

# 5. The exe carries the expected version and commit
$build = Get-ExeBuildInfo $exe
if ($build.Version -ne $version) {
    throw "The published exe reports ProductVersion '$($build.ProductVersion)', expected $version (from Directory.Build.props)."
}
if (-not $build.CommitSha) {
    throw "The published exe reports ProductVersion '$($build.ProductVersion)' without a commit SHA, so CI cannot verify it. Expected '$version+$headSha'."
}
if ($build.CommitSha -ne $headSha) {
    throw "The published exe reports commit $($build.CommitSha), expected HEAD $headSha."
}

# 6. Copy into place
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Join-Path $OutputDirectory $script:ExeName
Copy-ExeReplacingRunning -Source $exe -Target $target

$written = Get-ExeBuildInfo $target
if ($written.ProductVersion -ne $build.ProductVersion) {
    throw "$target reports '$($written.ProductVersion)' after the copy, expected '$($build.ProductVersion)'."
}

Write-Host "Published $target"
Write-Host "  ProductVersion $($written.ProductVersion)"
Write-Host "  Size           $((Get-Item -LiteralPath $target).Length) bytes"
