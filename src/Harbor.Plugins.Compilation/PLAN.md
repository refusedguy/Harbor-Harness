# Plan — Harbor.Plugins.Compilation

## Status: Stable

## Done

- [x] Roslyn in-memory compilation
- [x] Disk cache decorator (source hash key)
- [x] Assembly reference snapshot
- [x] Compilation diagnostics surfaced as Result failure

## TODO

- [ ] Source generator support
- [ ] Multi-target framework compilation
- [ ] PDB emission for debugging

## Known issues

- Roslyn cold start adds ~1s to first plugin load (cached after).
- No source generator support yet.

## Next priorities

1. **P0**: Surface compile errors with file:line diagnostics
2. **P1**: Source generator support
3. **P2**: Multi-target framework
