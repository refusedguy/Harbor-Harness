# Plan — Harbor.Plugins.Compilation

## Status: Stable

## Done

- [x] Roslyn in-memory compilation (`RoslynPluginCompiler` — ctor takes `PluginAssemblyReferences`)
- [x] Disk cache decorator (source hash key) (`CachingCompiler(inner, cacheDir, logger)`)
- [x] Assembly reference snapshot (`PluginAssemblyReferences`)
- [x] Compilation diagnostics surfaced as Result failure
- [x] Compile errors surfaced with file:line diagnostics
      (`RoslynPluginCompiler.cs:87-92` — `[severity] path(line,col): id — message`)

## TODO

- [ ] Source generator support
- [ ] Multi-target framework compilation
- [ ] PDB emission for debugging

## Known issues

- Roslyn cold start adds ~1s to first plugin load (cached after).
- No source generator support yet.

## Next priorities

1. **P1**: Source generator support
2. **P2**: Multi-target framework
3. **P2**: PDB emission for debugging loaded plugin sources
