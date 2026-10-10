# Shared helpers for the release scripts in this folder. Dot-source it:
#   . (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
# Works on Windows PowerShell 5.1 and PowerShell 7+.

$script:PluginName = 'windows-app-manager'
$script:ExeName = 'WindowsAppManagerMcp.exe'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# Everything in the repository that goes into bin/WindowsAppManagerMcp.exe, as git pathspecs relative
# to the repository root. Publish-Plugin.ps1 refuses to build while any of them has uncommitted
# changes, and Test-PluginBinary.ps1 reports the exe as stale when any of them changed after the
# commit the exe was built from.
#   src     the project and its sources
#   config  the sample layout presets the project embeds (config/layouts/*.json, see the
#           EmbeddedResource item in src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj)
#   others  files MSBuild, NuGet and the .NET SDK read from the repository root. Files that do not
#           exist yet are listed too, so adding one later counts as a change.
# Directory.Build.props is a build input as well, but Set-Version.ps1 changes its <Version> on purpose
# right before publishing, so it is checked separately with the <Version> line ignored (the version
# itself is compared with the exe's ProductVersion). When the project starts to read another file from
# outside src/, add it here; ReleaseBuildInputsTests checks the csproj and the root files against this list.
$script:BuildInputPaths = @(
    'src',
    'config',
    'Directory.Build.targets',
    'Directory.Build.rsp',
    'Directory.Packages.props',
    'global.json',
    ':(icase)nuget.config'
)
$script:BuildPropsPath = 'Directory.Build.props'

function Get-RepoRoot {
    (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Read-RepoText([string]$Root, [string]$RelativePath) {
    [System.IO.File]::ReadAllText((Join-Path $Root $RelativePath))
}

function Write-RepoText([string]$Root, [string]$RelativePath, [string]$Text) {
    # UTF-8 without BOM, line endings exactly as given.
    [System.IO.File]::WriteAllText((Join-Path $Root $RelativePath), $Text, $script:Utf8NoBom)
}

# <Version> in Directory.Build.props: the single source of truth for the product version.
function Get-PropsVersion([string]$Root) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.Load((Join-Path $Root 'Directory.Build.props'))
    $node = $xml.SelectSingleNode('//Version')
    if (-not $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw 'Directory.Build.props does not define a <Version>.'
    }
    $node.InnerText.Trim()
}

# Plugin versions declared in .claude-plugin/plugin.json and .claude-plugin/marketplace.json.
function Get-ManifestVersions([string]$Root) {
    $plugin = Read-RepoText $Root '.claude-plugin/plugin.json' | ConvertFrom-Json
    $marketplace = Read-RepoText $Root '.claude-plugin/marketplace.json' | ConvertFrom-Json
    $entry = @($marketplace.plugins | Where-Object { $_.name -eq $script:PluginName })
    if ($entry.Count -ne 1) {
        throw "marketplace.json must contain exactly one plugin named '$($script:PluginName)'."
    }
    [pscustomobject]@{
        PluginJson      = [string]$plugin.version
        MarketplaceJson = [string]$entry[0].version
    }
}

# Returns a list of problems (empty when Directory.Build.props and both manifests agree).
function Test-VersionConsistency([string]$Root) {
    $problems = New-Object System.Collections.Generic.List[string]
    $version = Get-PropsVersion $Root
    $manifests = Get-ManifestVersions $Root
    if ($manifests.PluginJson -ne $version) {
        $problems.Add(".claude-plugin/plugin.json version is '$($manifests.PluginJson)' but Directory.Build.props is '$version'. Run scripts/Set-Version.ps1.")
    }
    if ($manifests.MarketplaceJson -ne $version) {
        $problems.Add(".claude-plugin/marketplace.json plugin version is '$($manifests.MarketplaceJson)' but Directory.Build.props is '$version'. Run scripts/Set-Version.ps1.")
    }
    , $problems
}

# Splits an exe's ProductVersion ("1.11.0+<sha>") into its version and commit SHA (may be empty).
function Get-ExeBuildInfo([string]$ExePath) {
    $productVersion = (Get-Item -LiteralPath $ExePath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        $productVersion = ''
    }
    $parts = $productVersion.Trim().Split('+', 2)
    [pscustomobject]@{
        ProductVersion = $productVersion.Trim()
        Version        = $parts[0]
        CommitSha      = $(if ($parts.Count -gt 1) { $parts[1] } else { '' })
    }
}

# Copies an exe over $Target even while $Target is running (for example the MCP server of the
# Claude Code session that runs /update-plugin). Windows cannot overwrite a running exe but can
# rename it, so the running copy is moved aside to "<name>.old-<timestamp>"; the running process
# is unaffected and the next start uses the new file. If the copy still fails, the original is
# moved back before the error is rethrown, so $Target (the path the plugin runs) is never left
# without an exe. Leftovers from earlier runs are removed once nothing runs them any more.
function Copy-ExeReplacingRunning([string]$Source, [string]$Target) {
    $directory = Split-Path -Parent $Target
    $leaf = Split-Path -Leaf $Target
    Get-ChildItem -LiteralPath $directory -Filter "$leaf.old-*" -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

    try {
        Copy-Item -LiteralPath $Source -Destination $Target -Force
        return
    }
    catch {
        if (-not (Test-Path -LiteralPath $Target)) {
            throw
        }
    }

    $aside = "$Target.old-$(Get-Date -Format 'yyyyMMddHHmmss')"
    try {
        Move-Item -LiteralPath $Target -Destination $aside -Force
    }
    catch {
        throw "Could not replace $Target (in use?) and could not move it aside: $($_.Exception.Message)"
    }
    try {
        Copy-Item -LiteralPath $Source -Destination $Target -Force
    }
    catch {
        $copyError = $_
        try {
            # A failed copy can leave a partial file behind; the original replaces it.
            if (Test-Path -LiteralPath $Target) {
                Remove-Item -LiteralPath $Target -Force
            }
            Move-Item -LiteralPath $aside -Destination $Target -Force
        }
        catch {
            throw "Could not copy $Source to $Target ($($copyError.Exception.Message)), and could not move the original back from $aside ($($_.Exception.Message)). Rename $aside to $Target by hand."
        }
        Write-Warning "Could not copy $Source to $Target, so the original exe was moved back."
        throw $copyError
    }
    Write-Warning "$Target was in use, so it was moved to $aside. Restart the MCP client (Claude Code) to load the new build."
}

function Invoke-Git([string]$Root, [string[]]$Arguments) {
    # Windows PowerShell 5.1 turns redirected native stderr into errors, which 'Stop' would make fatal.
    $ErrorActionPreference = 'Continue'
    $output = & git -C $Root @Arguments 2>$null
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = $output
    }
}

# True when Directory.Build.props changed in more than its <Version> line. $DiffArguments selects
# what git diff compares: @('HEAD') for the working tree and index against HEAD, @($sha, 'HEAD') for
# two commits.
function Test-BuildPropsChangedBeyondVersion([string]$Root, [string[]]$DiffArguments) {
    $diff = Invoke-Git $Root (@('diff', '--no-color', '--no-ext-diff', '-U0') + $DiffArguments + @('--', $script:BuildPropsPath))
    if ($diff.ExitCode -ne 0) {
        throw "git diff $($DiffArguments -join ' ') -- $($script:BuildPropsPath) failed."
    }
    foreach ($line in @($diff.Output)) {
        $text = [string]$line
        if ($text.StartsWith('+++') -or $text.StartsWith('---')) {
            continue
        }
        if (($text.StartsWith('+') -or $text.StartsWith('-')) -and
            $text -notmatch '^[+-]\s*<Version>[^<]*</Version>\s*$') {
            return $true
        }
    }
    $false
}

# Build inputs (see $BuildInputPaths) that changed between $FromCommit and HEAD. Throws when git fails.
function Get-ChangedBuildInputs([string]$Root, [string]$FromCommit) {
    $changed = Invoke-Git $Root (@('diff', '--name-only', $FromCommit, 'HEAD', '--') + $script:BuildInputPaths)
    if ($changed.ExitCode -ne 0) {
        throw "git diff between $FromCommit and HEAD failed."
    }
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($file in @($changed.Output | Where-Object { $_ })) {
        $files.Add([string]$file)
    }
    if (Test-BuildPropsChangedBeyondVersion $Root @($FromCommit, 'HEAD')) {
        $files.Add("$($script:BuildPropsPath) (more than its <Version>)")
    }
    # Unrolled: wrap the call in @() to get an array, also when nothing changed.
    $files.ToArray()
}

# $Path relative to $Root with forward slashes (bin/WindowsAppManagerMcp.exe) when it is inside $Root, for messages;
# otherwise $Path as it is.
function Get-RepoRelativeLabel([string]$Root, [string]$Path) {
    if ($Path.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($Root.Length).TrimStart([char]'\', [char]'/').Replace([char]'\', [char]'/')
    }
    $Path
}

# Why $ExePath is not a current build of the plugin exe (empty when it is): the version manifests agree, the
# exe's ProductVersion is <version>+<commit sha>, that commit is in this clone, and no build input (see
# $BuildInputPaths) changed between it and HEAD. With $Tag, the tag must also be v<version>. Reads the exe's file
# metadata only; it never runs the exe. Test-PluginBinary.ps1 reports these problems, and Test-ToolsSnapshot.ps1
# -OnlyIfCurrent uses them to decide whether the exe's tools/list is worth comparing.
function Get-PluginBinaryProblems([string]$Root, [string]$ExePath, [string]$Tag) {
    $exeLabel = Get-RepoRelativeLabel $Root $ExePath
    $version = Get-PropsVersion $Root
    $problems = Test-VersionConsistency $Root

    if ($Tag -and $Tag -ne "v$version") {
        $problems.Add("Tag '$Tag' does not match the version in Directory.Build.props (expected tag v$version).")
    }

    if (-not (Test-Path -LiteralPath $ExePath)) {
        $problems.Add("$exeLabel does not exist. Run scripts/Publish-Plugin.ps1.")
        return , $problems
    }

    $build = Get-ExeBuildInfo $ExePath
    $republish = 'Run scripts/Publish-Plugin.ps1 and commit the exe.'

    if ($build.Version -ne $version) {
        $problems.Add("$exeLabel reports version '$($build.ProductVersion)', but Directory.Build.props is $version. $republish")
    }

    if (-not $build.CommitSha) {
        $problems.Add("$exeLabel has no commit SHA in its ProductVersion ('$($build.ProductVersion)'), so its sources cannot be verified. $republish")
    }
    elseif ((Invoke-Git $Root @('cat-file', '-e', "$($build.CommitSha)^{commit}")).ExitCode -ne 0) {
        $problems.Add("$exeLabel was built from commit $($build.CommitSha), which is not in this repository's history (shallow clone, or built from an unpushed commit). $republish")
    }
    else {
        $files = $null
        try {
            $files = @(Get-ChangedBuildInputs $Root $build.CommitSha)
        }
        catch {
            $problems.Add("Comparing the build inputs of $($build.CommitSha) and HEAD failed: $($_.Exception.Message)")
        }
        if ($files -and $files.Count -gt 0) {
            $shown = ($files | Select-Object -First 10) -join ', '
            $more = $(if ($files.Count -gt 10) { " and $($files.Count - 10) more" } else { '' })
            $problems.Add("$exeLabel is stale: built from $($build.CommitSha.Substring(0, [Math]::Min(12, $build.CommitSha.Length))), and $($files.Count) build input(s) changed since then ($shown$more). $republish")
        }
    }

    , $problems
}

function New-PluginBinaryChange([bool]$Changed, [string]$Reason) {
    [pscustomobject]@{ Changed = $Changed; Reason = $Reason }
}

# Whether a push or pull request changes the committed plugin exe (bin/WindowsAppManagerMcp.exe).
# The Build workflow checks the exe strictly when it does, and only warns otherwise. Compares $Base
# with $Head (default HEAD); with -SinceMergeBase it compares the merge base of the two with $Head,
# which is what a pull request itself changes. When the answer is unknown, Changed is $true, so the
# check errs on the strict side: $Base is empty, or it is not in this clone (a force push). An
# all-zero $Base (GitHub's "before" for a newly created branch) means everything in $Head is new,
# so the exe counts as changed when $Head contains it.
# Returns [pscustomobject]@{ Changed = [bool]; Reason = [string] }.
function Test-PluginBinaryChanged([string]$Root, [string]$Base, [string]$Head, [switch]$SinceMergeBase) {
    $exePath = "bin/$($script:ExeName)"
    if ([string]::IsNullOrWhiteSpace($Head)) {
        $Head = 'HEAD'
    }
    if ([string]::IsNullOrWhiteSpace($Base)) {
        return New-PluginBinaryChange $true "No base commit to compare $Head with, so $exePath is checked strictly."
    }
    if ($Base -match '^0+$') {
        if ((Invoke-Git $Root @('cat-file', '-e', "$($Head):$exePath")).ExitCode -eq 0) {
            return New-PluginBinaryChange $true "$Head is a new branch that contains $exePath, so it is checked strictly."
        }
        return New-PluginBinaryChange $false "$Head is a new branch without $exePath."
    }
    foreach ($commit in @($Base, $Head)) {
        if ((Invoke-Git $Root @('cat-file', '-e', "$commit^{commit}")).ExitCode -ne 0) {
            return New-PluginBinaryChange $true "Commit $commit is not in this clone (force push?), so whether $exePath changed is unknown and it is checked strictly."
        }
    }

    $range = $(if ($SinceMergeBase) { @("$Base...$Head") } else { @($Base, $Head) })
    $diff = Invoke-Git $Root (@('diff', '--name-only') + $range + @('--', $exePath))
    if ($diff.ExitCode -ne 0) {
        return New-PluginBinaryChange $true "git diff $($range -join ' ') failed, so whether $exePath changed is unknown and it is checked strictly."
    }
    if (@($diff.Output | Where-Object { $_ }).Count -gt 0) {
        return New-PluginBinaryChange $true "$exePath changed ($($range -join ' ')), so it is checked strictly."
    }
    New-PluginBinaryChange $false "$exePath did not change ($($range -join ' ')); a stale exe is only a warning until the next release."
}

# Uncommitted build inputs (see $BuildInputPaths) in the working tree, as git status --porcelain
# lines: modified, staged, untracked and ignored files. Ignored files count because MSBuild reads
# them all the same: a *.csproj.user, a Directory.Build.rsp, or a source file that a .gitignore
# pattern happens to match is compiled in like any other. Only the build output is left out: the
# bin/ and obj/ folders next to each project file under src/. Throws when git fails.
function Get-UncommittedBuildInputs([string]$Root) {
    $entries = New-Object System.Collections.Generic.List[string]

    $status = Invoke-Git $Root (@('status', '--porcelain', '--ignored', '--') + $script:BuildInputPaths)
    $projects = Invoke-Git $Root @('ls-files', '--', ':(glob)src/**/*.*proj')
    if ($status.ExitCode -ne 0 -or $projects.ExitCode -ne 0) {
        throw "git status for the build inputs failed in $Root."
    }

    # "src/WindowsAppManagerMcp/bin/", "src/WindowsAppManagerMcp/obj/", ...
    $buildOutput = New-Object System.Collections.Generic.List[string]
    foreach ($project in @($projects.Output | Where-Object { $_ })) {
        $directory = ([string]$project).Trim('"')
        $slash = $directory.LastIndexOf('/')
        $directory = $(if ($slash -ge 0) { $directory.Substring(0, $slash + 1) } else { '' })
        $buildOutput.Add("${directory}bin/")
        $buildOutput.Add("${directory}obj/")
    }

    foreach ($line in @($status.Output | Where-Object { $_ })) {
        $text = [string]$line
        if ($text.StartsWith('!! ')) {
            $path = $text.Substring(3).Trim('"')
            $isBuildOutput = @($buildOutput | Where-Object {
                    $path.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
                }).Count -gt 0
            if ($isBuildOutput) {
                continue
            }
        }
        $entries.Add($text)
    }
    if (Test-BuildPropsChangedBeyondVersion $Root @('HEAD')) {
        $entries.Add(" M $($script:BuildPropsPath) (more than its <Version>)")
    }
    # Unrolled: wrap the call in @() to get an array, also when nothing is uncommitted.
    $entries.ToArray()
}
