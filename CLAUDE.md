# CLAUDE.md

Project-specific instructions for Claude Code.

## Commands

- **"list tasks"** - List tasks from BACKLOG.md and BACKLOG-TECHNICAL.md

## Git worktrees

- **Always use a git worktree when implementing a task**, whether it's a feature, bug fix,
  refactor, or backlog item. Never edit code directly in the main checkout on `main`.
- Create worktrees under `.worktrees/` (already gitignored), on a new branch from `main`:
  `git worktree add .worktrees/<branch-name> -b <branch-name> main`
- Do all edits, builds, tests, and commits inside the worktree. Merge or open a PR from that branch.
- Once the branch is merged, remove the worktree with `git worktree remove .worktrees/<branch-name>`.
- Exceptions: read-only work (questions, exploration, reviews), or when the user explicitly
  says to work directly on `main`.
