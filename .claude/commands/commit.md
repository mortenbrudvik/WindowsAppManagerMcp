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
  exe is the only binary in git, and `scripts/Publish-Plugin.ps1` is the only way to refresh it.
  There is no `dist/` folder any more.
- `bin/` matches the `[Bb]in/` rule in `.gitignore`, but `bin/WindowsAppManagerMcp.exe` is tracked
  anyway. Git still reports changes to it, but a plain `git add` of that path fails (see step 6).
  `git check-ignore -v --no-index bin/WindowsAppManagerMcp.exe` shows the rule; without
  `--no-index`, `git check-ignore` prints nothing for a tracked file.
- When the `v<version>` tag is pushed, the Build workflow runs `scripts/Test-PluginBinary.ps1`. It
  fails, and no GitHub release is created, unless the committed exe has the tagged version and was
  built from the tagged build inputs: `src/`, `config/` (the sample layout presets embedded in the
  exe), the MSBuild, NuGet and SDK files at the repository root, and `Directory.Build.props` apart
  from its `<Version>`. `$BuildInputPaths` in `scripts/ReleaseCommon.ps1` lists them.
- The GitHub release asset is **not** the committed exe. The tag build runs its own
  `dotnet publish` on the CI runner and the release job zips that output. It is built from the same
  sources with the same version, but it is a different binary: it reports
  `<version>+<sha of the tagged release commit>`, while the plugin exe reports
  `<version>+<sha of the commit before it>` (the commit `Publish-Plugin.ps1` built from).

## Process

When invoked, perform these steps from the repository root (`git rev-parse --show-toplevel`).
The scripts are PowerShell; from bash, run them as `pwsh -File scripts/<name>.ps1 ...`. They also
run on Windows PowerShell 5.1, whose default execution policy blocks scripts, so without `pwsh` use
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/<name>.ps1 ...`.

### 1. Gather Information

If version or message not provided as arguments, ask the user:
- **Version**: What version number? (e.g., 1.2.0)
- **Message**: Brief description of the changes (1-2 sentences)

### 2. Make Sure the Sources Are Committed

Run `git status --short`. The exe records the HEAD commit it was built from, so
`Publish-Plugin.ps1` refuses to build while a build input has uncommitted changes: `src/`,
`config/`, a root build file such as `global.json` or `NuGet.config` (also when it is untracked or
ignored), or `Directory.Build.props` beyond the `<Version>` that step 3 sets. If there are such
changes, ask the user to commit them first (normal conventional commits), then continue.

Make the release commit on the branch that gets pushed and tagged, normally `main` after the
source changes have landed there. That commit, and the commit before it (which the exe was built
from), must reach the remote unchanged. Do not squash or rebase them: CI looks up the exe's build
commit in the tag's history, and a rewritten commit fails the tag build with "not in this
repository's history". If you make the release commit on a worktree branch, merge it with a
fast-forward or a merge commit.

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

Stage only the release files (not `git add -A`). `bin/` is gitignored, so naming the exe in a
plain `git add` prints "The following paths are ignored by one of your .gitignore files" and exits
with code 1, even though the file is tracked. If that `git add` is chained with `&&`, the commit
never runs. Stage the exe in its own command with `-f`, and check that exactly the four release
files are staged:

```bash
git add Directory.Build.props .claude-plugin/plugin.json .claude-plugin/marketplace.json
git add -f -- bin/WindowsAppManagerMcp.exe

# Expect exactly these four paths
git diff --cached --name-only
```

Stop if the list is different, for example because `bin/WindowsAppManagerMcp.exe` is missing.
Then commit and tag:

```bash
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
  `WindowsAppManagerMcp-v<version>-win-x64.zip`, a CI rebuild of the tagged sources rather than
  the committed plugin exe, see "How releases work"):
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
- Pushed to origin; CI checks the committed exe, rebuilds the exe from the tag and publishes that
  rebuild as the GitHub release
