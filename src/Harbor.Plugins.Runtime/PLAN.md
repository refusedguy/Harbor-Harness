# Plan — Harbor.Plugins.Runtime

## Status: Maintenance

## Done

- [x] CsPluginLoader wraps PluginHostBuilder
- [x] Legacy CompiledPlugin type preserved
- [x] [Obsolete] attribute with migration hint

## TODO

- [ ] Remove in v0.5 (see docs/PLUGIN_SYSTEM.md)
- [ ] Update all callers to use PluginHostBuilder directly

## Known issues

- Will be removed in v0.5.

## Next priorities

1. **P0**: Audit all callers, migrate to PluginHostBuilder
2. **P1**: Remove facade in v0.5
