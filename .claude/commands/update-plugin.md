# Update Plugin

Update the installed Windows App Manager plugin from development source.

## Usage

```
/update-plugin
```

## Process

When invoked, perform these steps:

### 1. Compare Versions

Read and display:
- **Development version**: from `.claude-plugin/plugin.json`
- **Installed version**: from `~/.claude/plugins/installed_plugins.json` (look for entry with `name: "windows-app-manager"` and `marketplace: "windows-app-manager-marketplace"`)

Show the user the version comparison before proceeding.

### 2. Build Latest Binary

```bash
dotnet publish src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj -c Release -o dist/
```

Verify the build succeeded before proceeding.

### 3. Copy Plugin Files

Copy from project to installed cache location.

**Source**: `C:\code\projects\WindowsAppManagerMcp`
**Destination**: `C:\Users\morte\.claude\plugins\cache\windows-app-manager-marketplace\windows-app-manager\<version>`

Where `<version>` is the version from `.claude-plugin/plugin.json`.

Use PowerShell to copy files:

```powershell
$version = "<version from plugin.json>"
$dest = "$env:USERPROFILE\.claude\plugins\cache\windows-app-manager-marketplace\windows-app-manager\$version"

# Create destination directories if needed
New-Item -ItemType Directory -Force -Path "$dest\.claude-plugin"
New-Item -ItemType Directory -Force -Path "$dest\.claude-plugin\bin"
New-Item -ItemType Directory -Force -Path "$dest\skills"
New-Item -ItemType Directory -Force -Path "$dest\.claude\commands"

# Copy plugin config and binary
Copy-Item -Force ".claude-plugin\plugin.json" "$dest\.claude-plugin\"
Copy-Item -Force ".claude-plugin\marketplace.json" "$dest\.claude-plugin\"
Copy-Item -Recurse -Force ".claude-plugin\bin\*" "$dest\.claude-plugin\bin\"

# Copy skills
Copy-Item -Recurse -Force "skills\*" "$dest\skills\"

# Copy commands
Copy-Item -Recurse -Force ".claude\commands\*" "$dest\.claude\commands\"
```

### 4. Update installed_plugins.json

Read `~/.claude/plugins/installed_plugins.json` and update the entry for the windows-app-manager plugin:

- `version`: new version string from plugin.json
- `installPath`: `C:\Users\morte\.claude\plugins\cache\windows-app-manager-marketplace\windows-app-manager\<version>`
- `lastUpdated`: current ISO 8601 timestamp (e.g., `2026-01-16T12:00:00.000Z`)
- `gitCommitSha`: output of `git rev-parse HEAD`

Write the updated JSON back to the file.

### 5. Confirm Update

Display a summary:
- Previous version -> New version
- Files copied
- Reminder: "Restart Claude Code to use the updated plugin"

## Example Output

```
=== Plugin Update ===

Versions:
  Development: 1.6.1
  Installed:   1.2.0

Building release binary...
✓ Build succeeded

Copying files to cache...
✓ .claude-plugin/ copied
✓ skills/ copied
✓ .claude/commands/ copied

Updating installed_plugins.json...
✓ Version updated: 1.2.0 -> 1.6.1

Done! Restart Claude Code to use the updated plugin.
```
