# Plan — Harbor.Plugin.GitTools

## Status: Sample

Sample plugin — demonstrates the `IToolPlugin` extension point. Not a production-grade implementation.

## Done

- [x] Implements `IToolPlugin`
- [x] Plugin manifest (name, version)
- [x] Initialize / Shutdown lifecycle
- [x] Works with the default plugin pipeline (`PluginHostBuilder`)

## TODO

- [ ] Add unit tests
- [ ] Add a sample invocation in docs
- [ ] Error handling polish
- [ ] Add `git_commit` tool
- [ ] Add `git_branch` tool
- [ ] Use LibGit2Sharp instead of shelling out

## Known issues

- Requires `git` on PATH.
- Shelling out is slower than LibGit2Sharp.

## Next priorities

1. **P1**: Add unit tests
2. **P2**: Error handling polish
