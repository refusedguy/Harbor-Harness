# Plugin showcase slice-2: GitTools done right

Branch/PR status facts plus a read-only TUI panel. Built on the existing
axes only: tools (`git` + `git_status`) + `ITuiPanelPlugin` (`git-status`
panel). No new axes per the plugin-axis freeze.

## What it shows

- Daemon data over IPC: the panel reads branch facts through
  `PanelServices.Git` — the daemon's `IGitQuery` port, which is daemon data
  over IPC in the two-process layout. It never forks `git` per frame (the
  same discipline the jump palette keeps since #666); without a registered
  git query it degrades to placeholders and never throws.
- Structured facts: `git_status` with `status` / `branch` / `pr` actions
  complements the free-form `git` tool (kept verbatim for compatibility)
  with parsed output the model can rely on.
- Best-effort PR status: the `pr` action runs `gh pr status --json`;
  a missing `gh`, a non-repo, or no PR all degrade to a plain success
  message. Every `pr` run refreshes `GitPrCache`, which is the panel's PR
  line — so the PR row updates on tool calls, not on frames (by design).

## Install (one command)

```bash
bash samples/plugins/Harbor.Plugin.GitTools/install.sh
```

This copies the single-file plugin to `~/.harbor/plugins/gittools.cs`
(or `$HARBOR_PLUGINS_DIR`). Restart Harbor; Roslyn compiles it at startup.
Set `HARBOR_PLUGINS_DIR` to override the destination.

## 60-second demo

```bash
bash samples/plugins/Harbor.Plugin.GitTools/demo.sh
```

The script walks install, branch, working-tree status, PR status, and the
panel registration check. It is hermetic (no dotnet, no network) and
finishes well under 60 seconds.

## Tool schema

Tool `git` (unchanged): free-form `args` + optional `cwd`, sequential
execution, `Command("args")` safety profile; still rejects `push --force`
without `--force-with-lease` and `reset --hard`.

Tool `git_status`, parallel execution, opaque safety profile.
Actions: `status`, `branch`, `pr`. Optional `cwd` (defaults to the
session worktree, then the process directory).

## Panel contract

`GitToolsPlugin` implements `IToolPlugin` and `ITuiPanelPlugin`,
registering `GitStatusPanel` (`git-status`, Bottom dock, size 8). The panel
reads a `GitWorkspaceStatus` snapshot plus the `GitPrCache` line and
returns `IReadOnlyList<string>` rows only, clipped to the dock geometry.
`WorktreeDirectory` defaults to the process directory and may be rebound
per session by the host (same pattern as the jump palette).

## Sources

- [GitToolsPlugin.cs](../samples/plugins/Harbor.Plugin.GitTools/GitToolsPlugin.cs)
- [install.sh](../samples/plugins/Harbor.Plugin.GitTools/install.sh)
- [demo.sh](../samples/plugins/Harbor.Plugin.GitTools/demo.sh)
- [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md)
- [PLUGIN_SYSTEM.md](./PLUGIN_SYSTEM.md)
