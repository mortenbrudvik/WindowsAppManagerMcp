# Shared helpers for the release scripts in this folder. Dot-source it:
#   . (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
# Works on Windows PowerShell 5.1 and PowerShell 7+.

$script:PluginName = 'windows-app-manager'
$script:ExeName = 'WindowsAppManagerMcp.exe'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

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
# is unaffected and the next start uses the new file. Leftovers from earlier runs are removed
# once nothing runs them any more.
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
    Write-Warning "$Target was in use, so it was moved to $aside. Restart the MCP client (Claude Code) to load the new build."
    Copy-Item -LiteralPath $Source -Destination $Target -Force
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
