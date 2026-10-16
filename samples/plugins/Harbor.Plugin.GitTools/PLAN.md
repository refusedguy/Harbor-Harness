# Plan — Harbor.Plugin.GitTools

## Status: Showcase slice-2 (#1434)

Showcase plugin — demonstrates `IToolPlugin` + `ITuiPanelPlugin` (tools + panel only, no new axes).

## Done

- [x] Implements `IToolPlugin` + `ITuiPanelPlugin`
- [x] Plugin manifest (name, version)
- [x] Initialize / Shutdown lifecycle
- [x] Works with the default plugin pipeline (`PluginHostBuilder`)
- [x] Structured read-only facts (`git_status` with `status` / `branch` / `pr` actions)
- [x] Best-effort PR status via `gh` (degrades without `gh`, never fails the call)
- [x] Read-only `git-status` panel (text rows, daemon `IGitQuery` + PR cache, no per-frame spawn)
- [x] One-command install (`install.sh`) + 60-second demo (`demo.sh`)
- [x] Showcase doc page (`docs/PLUGIN_SHOWCASE_GITTOOLS.md`)

## TODO

- [ ] Add unit tests
- [ ] Error handling polish
- [ ] Use LibGit2Sharp instead of shelling out

## Known issues

- Requires `git` on PATH (`gh` optional, PR line only).
- Shelling out is slower than LibGit2Sharp.
- The panel PR line refreshes on `git_status pr` tool calls, not per frame (by design — no per-frame spawn).
