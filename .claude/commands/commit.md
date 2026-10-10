# Commit

Create a versioned release commit with tag for the Windows App Manager plugin.

## Usage

```
/commit [version] [message]
```

- `version` (optional): Version number (e.g., `1.2.0`). If not provided, will prompt for it.
- `message` (optional): Brief description of changes. If not provided, will prompt for it.

## How releases work

- The version has one source: `<Version>` in `Directory.Build.props`. `scripts/Set-Version.ps1`
  copies it into `.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json`, and tests fail
  if they drift. The server reports it (plus the commit SHA) as `serverInfo.version`.
- The plugin runs `bin/WindowsAppManagerMcp.exe` (see `.claude-plugin/plugin.json`). That committed
  exe is the only binary that ships, and `scripts/Publish-Plugin.ps1` is the only way to refresh it.
  There is no `dist/` folder any more.
- When the `v<version>` tag is pushed, the Build workflow runs `scripts/Test-PluginBinary.ps1`. It
  fails, and no GitHub release is created, unless the committed exe has the tagged version and was
  built from the tagged `src/`.

## Process

When invoked, perform these steps from the repository root (`git rev-parse --show-toplevel`).
The scripts are PowerShell; from bash, run them as `pwsh -File scripts/<name>.ps1 ...`.

### 1. Gather Information

If version or message not provided as arguments, ask the user:
- **Version**: What version number? (e.g., 1.2.0)
- **Message**: Brief description of the changes (1-2 sentences)

### 2. Make Sure the Sources Are Committed

Run `git status --short`. The exe records the HEAD commit it was built from, so
`Publish-Plugin.ps1` refuses to build while `src/` has uncommitted changes. If there are source
changes, ask the user to commit them first (normal conventional commits), then continue.

### 3. Update Version Numbers

```powershell
./scripts/Set-Version.ps1 -Version <version>
```

This updates `Directory.Build.props`, `.claude-plugin/plugin.json` and the plugin entry in
`.claude-plugin/marketplace.json`.

### 4. Build Release Binary

```powershell
./scripts/Publish-Plugin.ps1
./scripts/Test-PluginBinary.ps1 -Tag v<version>
```

`Publish-Plugin.ps1` publishes the framework-dependent single-file exe to `bin/` and verifies its
ProductVersion is `<version>+<HEAD sha>`. `Test-PluginBinary.ps1` runs the same check CI runs on
the tag. Stop if either fails.

### 5. Review Changes

Run `git status` and `git diff --stat` to show the user what will be committed. Expect exactly
`Directory.Build.props`, `.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` and
`bin/WindowsAppManagerMcp.exe`.

### 6. Commit and Tag

Stage only the release files (not `git add -A`):

```bash
git add Directory.Build.props .claude-plugin/plugin.json .claude-plugin/marketplace.json bin/WindowsAppManagerMcp.exe

git commit -m "chore: Release v<version>

<message>

- Bump version to <version>
- Rebuild bin/WindowsAppManagerMcp.exe"

# Create annotated tag
git tag -a v<version> -m "Release v<version> - <message>"
```

### 7. Push

```bash
git push && git push origin v<version>
```

### 8. Confirm

Display:
- Commit hash
- Tag name
- Build workflow run for the tag: `gh run list --workflow=build.yml --limit 3`
- GitHub release URL (exists once that run succeeds; asset
  `WindowsAppManagerMcp-v<version>-win-x64.zip`):
  `https://github.com/mortenbrudvik/WindowsAppManagerMcp/releases/tag/v<version>`

## Example

```
/commit 1.2.0 "Add window grouping feature"
```

Results in:
- Version 1.2.0 in `Directory.Build.props` and the plugin manifests
- `bin/WindowsAppManagerMcp.exe` rebuilt, reporting ProductVersion `1.2.0+<sha>`
- Commit: `chore: Release v1.2.0`
- Tag: `v1.2.0`
- Pushed to origin; CI publishes the GitHub release
