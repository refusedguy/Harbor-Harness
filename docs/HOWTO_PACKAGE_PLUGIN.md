# Packaging and sharing a plugin

> How to hand a working plugin to another person: a single `.cs` file, or a
> package served by the out-of-process plugin host.

Harbor has two supported ways to ship a plugin. Both use the extension axes
that already exist (tool, provider, agent, TUI panel); neither needs a new
axis. Pick the lane that matches how much you need to control at runtime.

| Lane | You ship | Runs where | Best for |
| --- | --- | --- | --- |
| [Single `.cs` file](#single-cs-file) | one source file | compiled at plugin load time | sharing a small tool with a gist or a repo |
| [Out-of-process host](#out-of-process-host) | a DLL, or source the host compiles | a separate `harbor-plugins-host` process | plugins that must not put Roslyn in the CLI, or that carry a manifest |

## Single `.cs` file

A CS-source plugin is one public class with a parameterless constructor that
implements `IPlugin` plus one of the `I*Plugin` interfaces. The host compiles
it and registers the axes it declares. The [HelloWorld sample](../samples/plugins-cs/HelloWorldPlugin.cs)
is the smallest starting point.

Publish the file anywhere that serves plain text. A gist is the common case:

```bash
# Author publishes one file
gh gist create myplugin.cs --public

# User installs it and lets Harbor compile it at startup
curl -fsSL https://gist.githubusercontent.com/user/myplugin.cs/raw > ~/.harbor/plugins/myplugin.cs
harbor
```

Drop `.cs` files into `~/.harbor/plugins/` (global) or `<project>/.harbor/plugins/`
(project-local). Set `HARBOR_PLUGINS_DIR` to move the global directory. The
compiled output is cached by source SHA-256 under `~/.harbor/plugins/cache/`,
so an unchanged file is not recompiled; a changed file gets a new hash and a
fresh compile. See [Plugin development](./PLUGIN_DEVELOPMENT.md) for the
plugin contract and [Plugin system](./PLUGIN_SYSTEM.md) for the load pipeline.

Verify the install from the REPL:

```
harbor> /plugins
  myplugin  v1.0.0
```

A plugin that fails to compile is reported there with the compiler error, not
silently skipped. For a one-command install of a ready-made single-file
plugin, see the [Echo showcase](../samples/plugins/Harbor.Plugin.Echo/README.md)
(`install.sh` copies `EchoPlugin.cs` into the plugins directory).

## Out-of-process host

Since `#1055` the CLI does not compile plugins itself. By default CS-source
plugins are served out-of-process by `harbor-plugins-host`, registered as the
`harbor-csharp-plugins` MCP server when it ships next to the CLI. The CLI
lists and calls plugin tools over MCP stdio. This is the only lane that works
under NativeAOT: Roslyn needs a JIT, so compilation belongs in the host
process rather than in the AOT CLI.

Package for this lane in one of two ways:

- **DLL plugin.** Build a class library that references `Harbor.Abstractions`,
  implement `IPlugin` and the axis interfaces, and place the assembly in the
  plugins directory. The [Echo sample project](../samples/plugins/Harbor.Plugin.Echo/README.md)
  shows the same source built as a DLL.
- **Source with a manifest.** Ship the `.cs` file plus a plugin manifest that
  declares version, declared dependencies, and capabilities. The host reads
  the manifest before it initializes the plugin and refuses an incompatible
  `RequiredHarborVersion`. The manifest is part of the plugin's content, so
  version the two together.

What crosses the process boundary is declared once in the route axis registry.
The tool axis is carried; provider, agent, and TUI axes are refused with a
reason, and `ITuiPlugin` is a closed seam everywhere. A plugin that contributes
only refused axes is reported as not loaded rather than as loaded and useless.

Verify which route a plugin took from the startup log: it names the plugin
route and each loaded plugin with its version and source file. The CLI then
surfaces plugin tools through MCP, so a tool-only plugin behaves the same to
the user regardless of lane. The [Pomodoro showcase](../samples/plugins/Harbor.Plugin.Pomodoro/README.md)
uses this route and installs with one `install.sh`.

## Choosing a lane

Use a single `.cs` file when the plugin is a tool you want someone to try by
copying one file, and you are happy for Harbor to compile it at load time. Use
the out-of-process lane when you need a DLL dependency, a manifest with
declared capabilities, or an AOT CLI that never loads Roslyn. In both cases
the extension axes are the same; only the packaging and the compile location
change.

## See also

- [Plugin development](./PLUGIN_DEVELOPMENT.md) — writing a plugin and the
  distribution section this how-to expands on.
- [Plugin system](./PLUGIN_SYSTEM.md) — full reference for the load pipeline,
  manifests, capabilities, and lifecycle.
- [Getting started](./GETTING_STARTED.md) — first build and where plugins live.
