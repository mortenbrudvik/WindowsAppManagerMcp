# Update Plugin

Update the installed Windows App Manager plugin from development source.

## Usage

```
/update-plugin
```

## Process

When invoked, perform these steps in PowerShell (`pwsh`; in Windows PowerShell 5.1 the default
execution policy blocks the script in step 2, so run it with
`powershell -NoProfile -ExecutionPolicy Bypass -File ...`). Derive every path; never hard-code the
repository location or the user name:

```powershell
$repo = git rev-parse --show-toplevel
$version = (Get-Content "$repo\.claude-plugin\plugin.json" -Raw | ConvertFrom-Json).version
$dest = Join-Path $env:USERPROFILE ".claude\plugins\cache\windows-app-manager-marketplace\windows-app-manager\$version"
```

The installed plugin runs `${CLAUDE_PLUGIN_ROOT}/bin/WindowsAppManagerMcp.exe`, which is
`$dest\bin\WindowsAppManagerMcp.exe`.

### 1. Compare Versions

Read and display:
- **Development version**: `$version` (from `.claude-plugin/plugin.json`)
- **Installed version**: from `~/.claude/plugins/installed_plugins.json`. In that file (format
  `"version": 2`), `plugins` maps `"<plugin>@<marketplace>"` keys to arrays of install entries. Use
  the `version` of the entry under `plugins["windows-app-manager@windows-app-manager-marketplace"]`
  (the one with `"scope": "user"` if there are several).

If that key is missing, the plugin is not installed. Stop and ask the user to install it first
(`/plugin marketplace add mortenbrudvik/WindowsAppManagerMcp`, then
`/plugin install windows-app-manager@windows-app-manager-marketplace`). This command only updates an
existing install.

Show the user the version comparison before proceeding.

### 2. Build the Binary Into the Installed Plugin

```powershell
& "$repo\scripts\Publish-Plugin.ps1" -OutputDirectory "$dest\bin" -AllowDirty
```

This publishes straight to `$dest\bin`, the path the plugin runs, and leaves the committed
`bin/WindowsAppManagerMcp.exe` in the repository untouched (only `/commit` refreshes that one).
`-AllowDirty` allows testing uncommitted changes; the script warns when `src/` is dirty. If the
installed exe is running (this session's MCP server), the script moves it aside and installs the
new one; the new build is used after a restart.

Verify the build succeeded and note the ProductVersion it prints before proceeding.

### 3. Copy Plugin Files

```powershell
New-Item -ItemType Directory -Force -Path "$dest\.claude-plugin", "$dest\skills", "$dest\.claude\commands" | Out-Null

# Plugin config
Copy-Item -Force "$repo\.claude-plugin\plugin.json", "$repo\.claude-plugin\marketplace.json" "$dest\.claude-plugin\"

# Skills
Copy-Item -Recurse -Force "$repo\skills\*" "$dest\skills\"

# Commands
Copy-Item -Recurse -Force "$repo\.claude\commands\*" "$dest\.claude\commands\"

# Earlier versions of this command copied an old exe to .claude-plugin\bin, which the plugin never runs
if (Test-Path "$dest\.claude-plugin\bin") { Remove-Item -Recurse -Force "$dest\.claude-plugin\bin" }
```

### 4. Update installed_plugins.json

Read `~/.claude/plugins/installed_plugins.json` and update the entry found in step 1, under
`plugins["windows-app-manager@windows-app-manager-marketplace"]`:

- `version`: `$version`
- `installPath`: `$dest`
- `lastUpdated`: current ISO 8601 timestamp (e.g., `2026-01-16T12:00:00.000Z`)
- `gitCommitSha`: output of `git rev-parse HEAD`

Leave the other fields (`scope`, `installedAt`, ...) and the other plugins' entries unchanged, and
write the updated JSON back to the file.

### 5. Confirm Update

Display a summary:
- Previous version -> New version
- ProductVersion of `$dest\bin\WindowsAppManagerMcp.exe`
- Files copied
- Reminder: "Restart Claude Code to use the updated plugin"

## Example Output

```
=== Plugin Update ===

Versions:
  Development: 1.12.0
  Installed:   1.11.0

Building release binary...
✓ Published ...\windows-app-manager\1.12.0\bin\WindowsAppManagerMcp.exe (1.12.0+3f2a9c1...)

Copying files to cache...
✓ .claude-plugin/ copied
✓ skills/ copied
✓ .claude/commands/ copied

Updating installed_plugins.json...
✓ Version updated: 1.11.0 -> 1.12.0

Done! Restart Claude Code to use the updated plugin.
```
