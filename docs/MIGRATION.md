# Migration notes

> How to move an existing Harbor setup or plugin to a newer form. The index
> names the boundary and the action; the section below carries the detail.

## Version → action

| From | To | Action |
| --- | --- | --- |
| DLL plugin (`samples/plugins/*.csproj`) | Roslyn `.cs` plugin | move the tool into `~/.harbor/plugins/*.cs` and drop the `.csproj`; see below |

For upgrading the CLI itself, not migrating a plugin, see
[Upgrading Harbor](./UPGRADE.md).

## Migration: from `samples/plugins/*.csproj` (DLL) to `plugins/*.cs` (Roslyn)

### Before (DLL plugin)

```bash
# 1. Create .csproj
$ cat > samples/plugins/Harbor.Plugin.MyPlugin/Harbor.Plugin.MyPlugin.csproj << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../../src/Harbor.Abstractions/Harbor.Abstractions.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0" />
  </ItemGroup>
</Project>
EOF

# 2. Write plugin + tool class
$ cat > samples/plugins/Harbor.Plugin.MyPlugin/MyPlugin.cs << 'EOF'
namespace Harbor.Plugin.MyPlugin;
public sealed class MyPlugin : IToolPlugin { /* ... */ }
public sealed class MyTool : ITool { /* ... */ }
EOF

# 3. Build
$ dotnet build samples/plugins/Harbor.Plugin.MyPlugin -c Release

# 4. DLL plugins are NOT file-drop loaded: ~/.harbor/plugins/ only scans *.cs
#    (see FileSystemPluginSource). A compiled plugin must be registered by host
#    code (IPluginRegistrar) or served out-of-process via src/Harbor.Plugins.Host.

# 5. Run Harbor
$ dotnet run --project apps/Harbor.App.Cli
```

### After (Roslyn .cs plugin)

```bash
# 1. Drop a single .cs file in ~/.harbor/plugins/
$ cat > ~/.harbor/plugins/myplugin.cs << 'EOF'
public sealed class MyPlugin : IToolPlugin { /* ... */ }
public sealed class MyTool : ITool { /* ... */ }
EOF

# 2. Run Harbor — the plugin runtime (PluginHost + Roslyn compiler) compiles it on startup
$ dotnet run --project apps/Harbor.App.Cli
```

### What you lose going to .cs

- ❌ External NuGet packages (only assemblies already loaded by Harbor are visible).
- ❌ Multi-file projects (one .cs = one plugin).
- ❌ Strong naming / signing.
- ❌ Build-time analyzers (Roslynator, Sonar) — no `.editorconfig` enforcement.

### What you gain

- ✅ No `.csproj`, no `dotnet build`, no copy step.
- ✅ Single file = single plugin.
- ✅ Auto-injected common `using`s.
- ✅ Easy to share (gist, curl).

### When to use which

| Use DLL plugin | Use .cs plugin |
|---|---|
| Need external NuGet packages | Just need `Harbor.Abstractions` types |
| Multi-file project (split tool + view model) | Single tool or simple plugin |
| Want strong naming / signing | Sharing via gist / curl |
| Need build-time analyzers | Quick prototyping |
| Production deployment | Personal customization |

## See also

- [Plugin development](./PLUGIN_DEVELOPMENT.md) — the plugin seams and the
  file-drop contract.
- [Upgrading Harbor](./UPGRADE.md) — update commands for each install lane.
