# Plugin Development Guide

> How to write plugins for Harbor — tools, providers, agents, and commands.

## Overview

Harbor plugins extend the agent with:
- **Tools** — new actions the LLM can call (e.g. `websearch`, `git`, `tree`).
- **Providers** — custom LLM clients (e.g. Bedrock, Azure, custom API).
- **Agents** — new modes with specific permissions (e.g. `debugger`, `reviewer`).
- **Commands** — slash-commands in the REPL.

Plugins are .NET assemblies (DLLs) that implement `IPlugin` and are loaded at startup.

## Plugin contract

```csharp
public interface IPlugin
{
    string Name { get; }
    Version Version { get; }
    Version RequiredHarborVersion { get; }
    string Description { get; }

    void Initialize(PluginContext context);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

// Pick one or more of these:
public interface IToolPlugin : IPlugin
{
    void RegisterTools(IToolRegistryBuilder builder);
}

public interface IProviderPlugin : IPlugin
{
    void RegisterProviders(IProviderRegistryBuilder builder);
}

public interface IAgentPlugin : IPlugin
{
    void RegisterAgents(IAgentRegistryBuilder builder);
}
```

## Creating a tool plugin

### 1. Create the project

```bash
mkdir MyPlugin
cd MyPlugin
dotnet new classlib -n MyPlugin.HarborPlugin
```

Edit `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path/to/Harbor.Abstractions.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0" />
  </ItemGroup>
</Project>
```

### 2. Implement the tool

```csharp
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace MyPlugin.HarborPlugin;

public sealed class MyPlugin : IToolPlugin
{
    public string Name => "myplugin";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "My custom tool";

    public void Initialize(PluginContext context)
    {
        context.CreateLogger<MyPlugin>().LogInformation("MyPlugin initialized");
    }

    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<MyTool>();
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class MyTool : ITool
{
    public ToolName Name => ToolName.Create("my_tool");
    public string DisplayName => "My Tool";
    public string Description => "Does something useful";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "my_tool: Does something useful";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public JsonDocument ParameterSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "input": { "type": "string", "description": "Input value" }
          },
          "required": ["input"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'input' argument.");
        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var input = args.GetProperty("input").GetString()!;
        return Task.FromResult(ToolResult.Success($"Processed: {input}"));
    }
}
```

### 3. Build and install

```bash
dotnet build
cp bin/Debug/net10.0/MyPlugin.HarborPlugin.dll ~/.harbor/plugins/
```

Harbor auto-discovers plugins in `~/.harbor/plugins/`.

## Sample plugins

See `samples/plugins/` for working examples:

| Plugin | What it demonstrates |
|---|---|
| [Harbor.Plugin.WebSearch](../samples/plugins/Harbor.Plugin.WebSearch/) | HTTP-based tool (DuckDuckGo, no API key) |
| [Harbor.Plugin.TodoWrite](../samples/plugins/Harbor.Plugin.TodoWrite/) | Stateful tool (per-session todo list) |
| [Harbor.Plugin.GitTools](../samples/plugins/Harbor.Plugin.GitTools/) | Wrapping shell commands safely |
| [Harbor.Plugin.FileTree](../samples/plugins/Harbor.Plugin.FileTree/) | Read-only filesystem tool with custom output |

## TUI plugins

TUI plugins extend the terminal UI with custom views, view models, and overlays. They
implement `ITuiPlugin` from `Harbor.Tui.Abstractions.Plugins`.

### Contract

```csharp
public interface ITuiPlugin
{
    string Name { get; }
    Version Version { get; }
    string Description { get; }

    void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels);
}
```

### What you can do

- **Register a new view** — append a custom panel to any `TuiViewPlacement` (status bar,
  chat history, sidebar, overlay, …). The renderer repaints it on the events selected by
  `BaseTuiRenderer.ShouldRenderPlacement`.
- **Override a builtin view** — register a view with the same id as a builtin
  (`"status-bar"`, `"chat-history"`, `"input"`, `"diff-preview"`) before
  `BaseTuiRenderer.InitializeAsync` runs. Builtin registration is skipped when an id is
  already taken (override-before-builtin).
- **Register a custom view model** — add state holders that views bind to by id. The
  `ViewModelRegistry` auto-binds view ↔ view model by matching `ITuiView.Id` to
  `ITuiViewModel.Id`.

### Decoupling contract

TUI plugins MUST NOT reference `Harbor.Core`. All agent state flows in through
`AgentEvent` (from `Harbor.Abstractions.Events`); all rendering goes through
`ITuiRenderContext` (from `Harbor.Tui.Abstractions.Renderers`).

### Minimal example: a clock sidebar

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Plugins;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
using Harbor.Tui.Abstractions.Views;

public sealed class ClockPlugin : ITuiPlugin
{
    public string Name => "clock";
    public Version Version => new(1, 0, 0);
    public string Description => "Shows a live clock in the right sidebar";

    public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels)
    {
        viewModels.Register(new ClockViewModel());
        views.Register(new ClockView());
    }
}

public sealed partial class ClockViewModel : ObservableObject, ITuiViewModel
{
    [ObservableProperty] private string _time = DateTime.Now.ToString("HH:mm:ss");
    public string Id => "clock";
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        Time = DateTime.Now.ToString("HH:mm:ss");
        return Task.CompletedTask;
    }
}

public sealed class ClockView : TuiViewBase<ClockViewModel>
{
    public override string Id => "clock";
    public override string DisplayName => "Clock";
    public override TuiViewPlacement Placement => TuiViewPlacement.SidebarRight;

    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
    {
        var vm = ViewModel;
        if (vm is null) return Task.CompletedTask;
        context.WriteColored($"[{vm.Time}]", TuiColor.DarkGray);
        context.WriteLine();
        return Task.CompletedTask;
    }
}
```

### Builtin views reference

| Id | View | Placement | View model |
|---|---|---|---|
| `status-bar` | `StatusBarView` | `StatusBar` | `StatusBarViewModel` |
| `chat-history` | `ChatHistoryView` | `ChatHistory` | `ChatHistoryViewModel` |
| `input` | `InputView` | `Input` | `InputViewModel` |
| `diff-preview` | `DiffPreviewView` | `Overlay` | `DiffPreviewViewModel` |

### Placement rendering

`BaseTuiRenderer.ShouldRenderPlacement` decides which placements are repainted for a given
event. The default policy:

| Placement | Repainted on |
|---|---|
| `StatusBar` | `AgentStartEvent`, `AgentEndEvent`, `AgentErrorEvent`, `Compaction*`, `StepFinishEvent` |
| `ChatHistory` | `AgentStartEvent`, `MessageEndEvent`, `ToolExecutionEndEvent` |
| `Input` | `AgentStartEvent`, `AgentEndEvent` |
| `Overlay` | `ToolExecutionEndEvent` |

Streaming renderers (`AnsiTuiRenderer`, `PlainTuiRenderer`) emit token deltas directly
and rely on placement-driven repaints only for state transitions. Full-screen renderers
can override `ShouldRenderPlacement` to repaint more aggressively.

## Tool design patterns

### Pattern: Read-only tool

For tools that don't modify state (e.g. `read`, `grep`, `tree`):
- Set `ExecutionMode = ExecutionMode.Parallel`.
- Use default permission rules: `Allow` for `*`.

### Pattern: Write tool

For tools that modify state (e.g. `write`, `edit`, `bash`):
- Set `ExecutionMode = ExecutionMode.Sequential`.
- Use `PermissionRuleset` to require `Ask` for sensitive operations.

### Pattern: Stateful tool

For tools that maintain state across calls (e.g. `TodoWrite`):
- Use `ConcurrentDictionary<sessionId, List<T>>` keyed by `context.SessionId`.
- Lock when modifying state.
- Clean up on session end (subscribe to `SessionEndEvent` via `IEventBus`).

### Pattern: HTTP-based tool

For tools that call external APIs:
- Use `HttpClient` (static, reused).
- Set reasonable timeout (15-30s).
- Handle `HttpRequestException` gracefully.
- Return `ToolResult.Error` on failure, `ToolResult.Success` on success.

### Pattern: Process-wrapping tool

For tools that wrap shell commands (e.g. `GitTool`):
- Use `ProcessStartInfo` with `ArgumentList` (safe quoting).
- Validate args before execution.
- Block dangerous commands (e.g. `git push --force`, `rm -rf /`).
- Capture stdout + stderr + exit code.

## ToolContext

The `ToolContext` provides access to:

```csharp
public sealed record ToolContext(
    string SessionId,
    string MessageId,
    string? CallId,
    string Agent,
    CancellationToken Abort,
    IReadOnlyList<AgentMessage> Messages,    // full session history
    Func<ToolProgressUpdate, CancellationToken, Task> ReportProgress,
    Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> Ask,
    IServiceProvider Services);               // DI access
```

Use `context.Services.GetRequiredService<T>()` to access other services (e.g. `IProviderRegistry`).

## Permission integration

Tools can declare permission requirements via `PermissionRuleset`:

```csharp
public static PermissionRuleset ForMyTool => new(new[]
{
    new PermissionRule("my_tool", "sensitive/*", PermissionAction.Ask),
    new PermissionRule("my_tool", "*", PermissionAction.Allow),
});
```

Add to agent config in `~/.harbor/config.json`:

```jsonc
{
  "agents": {
    "code": {
      "permission": {
        "my_tool": { "sensitive/*": "ask", "*": "allow" }
      }
    }
  }
}
```

## Plugin lifecycle

```
1. Harbor starts
2. Scans ~/.harbor/plugins/*.dll and ./providers/*.dll
3. Loads each assembly, finds types implementing IPlugin
4. Instantiates each plugin
5. Calls plugin.Initialize(context) — register tools/providers/agents here
6. Plugin is ready — agent can use its tools
7. On shutdown: plugin.ShutdownAsync() — clean up resources
```

## Testing your plugin

Create a test project:

```bash
dotnet new tunit -n MyPlugin.Tests
```

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

public class MyToolTests
{
    [Test]
    public async Task Execute_Returns_Success()
    {
        var tool = new MyTool();
        var args = JsonDocument.Parse("""{"input": "test"}""").RootElement;
        var ctx = CreateTestContext();

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("test");
    }

    private static ToolContext CreateTestContext() => new(
        SessionId: "test",
        MessageId: "test",
        CallId: "test",
        Agent: "code",
        Abort: CancellationToken.None,
        Messages: Array.Empty<AgentMessage>(),
        ReportProgress: (_, _) => Task.CompletedTask,
        Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        Services: null!);
}
```

## Distribution

### As a DLL

```bash
dotnet build -c Release
cp bin/Release/net10.0/MyPlugin.dll ~/.harbor/plugins/
```

### As a NuGet package

```bash
dotnet pack -c Release
dotnet nuget push bin/Release/MyPlugin.1.0.0.nupkg --api-key YOUR_KEY --source https://api.nuget.org/v3/index.json
```

Users install:
```bash
harbor plugin install MyPlugin
```

(Future feature — for now, manual DLL copy.)

## Gotchas

### NativeAOT compatibility

If you want your plugin to work with NativeAOT-compiled Harbor:
- No `AssemblyLoadContext` collectible — plugin is loaded statically.
- No reflection emit.
- Use `System.Text.Json` source-gen for serialization.
- Test with `<PublishAot>true</PublishAot>` in your test project.

For JIT-compiled Harbor (default), none of these restrictions apply.

### Dependency conflicts

Plugins share the host's dependency tree. If your plugin needs a different version of a library:
- Use `<PrivateAssets>all</PrivateAssets>` on PackageReference.
- Or isolate via `AssemblyLoadContext` (JIT mode only).

### HttpClient

Don't create `new HttpClient()` per call — use a static instance:
```csharp
private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
```

Or inject `IHttpClientFactory` via `context.Services`.

## Examples from the wild

Ideas for plugins (from similar ecosystems):

| Plugin idea | Inspiration |
|---|---|
| Database query tool | kilocode's postgres MCP |
| Slack integration | MCP Slack server |
| GitHub operations | gh CLI wrapper |
| Docker management | docker CLI wrapper |
| Code search | Sourcegraph API |
| Documentation fetch | ReadTheDocs / DevDocs API |
| Image generation | OpenAI DALL-E, Stable Diffusion |
| Speech-to-text | OpenAI Whisper |
| Vector memory | ChromaDB, LanceDB |
| Browser automation | Playwright |
| File watcher | `FileSystemWatcher`-based reactive tool |
| Test runner | `dotnet test` / `pytest` / `jest` wrapper |
| LSP integration | LSP client for code intelligence |

See [specs/10-repo-analysis.md](../specs/10-repo-analysis.md) for more ideas from kilocode/opencode/pi-agent/crush.
