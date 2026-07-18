# Plugin Development Guide

> How to write plugins for Harbor — tools, providers, agents, TUI panels.
>
> **TL;DR**: Drop a `.cs` file in `~/.harbor/plugins/`, restart Harbor. That's it.

Связанные документы:
- [EXAMPLES.md §Plugins](./EXAMPLES.md#plugins) — короткие рецепты.
- [DEVELOPMENT.md §Workflow: contribute a plugin](./DEVELOPMENT.md#workflow-contribute-a-plugin).
- [specs/02-plugins.md](../specs/02-plugins.md) — design rationale.
- [SCRIPTING.md](./SCRIPTING.md) — scripting alternative (planned by subagent #5).

---

## Quickstart

Создай файл `~/.harbor/plugins/hello.cs`:

```csharp
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

public sealed class HelloPlugin : IToolPlugin
{
    public string Name => "hello";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Says hello";

    public void Initialize(PluginContext context) { }
    public void RegisterTools(IToolRegistryBuilder b) => b.AddTool<HelloTool>();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HelloTool : ITool
{
    public ToolName Name => ToolName.Create("hello");
    public string DisplayName => "Hello";
    public string Description => "Returns a hello message";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "hello: Say hello";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
    public Result ValidateArguments(JsonElement args) => Result.Success();
    public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "world" : "world";
        return Task.FromResult(ToolResult.Success($"Hello, {name}!"));
    }
}
```

Запусти Harbor:

```bash
$ dotnet run --project src/Harbor.Cli
harbor> /plugins
  hello  v1.0.0  Says hello

harbor> ask "Say hello to Alice"
[tool_execution_start] id=tc_1 tool=hello args={"name":"Alice"}
[tool_execution_end]   id=tc_1 ok=true
Hello, Alice!
```

Что произошло:
1. **CsPluginLoader** нашёл `~/.harbor/plugins/hello.cs` при запуске.
2. **Roslyn** скомпилировал его в in-memory assembly (JIT mode).
3. Loader нашёл тип `HelloPlugin`, реализующий `IToolPlugin`.
4. Вызвал `Initialize(context)` → `RegisterTools(builder)`.
5. `HelloTool` зарегистрирован в `ToolRegistry`.
6. LLM увидел `hello` в tool definitions и смог его вызвать.

> **TODO: confirm with subagent #1** — exact Roslyn API surface (`CsPluginLoader`
> is being built). The plugin contract above is the expected API; the loader
> implementation may differ slightly.

---

## Anatomy of a plugin

Все плагины реализуют базовый `IPlugin` + один или несколько специализированных
интерфейсов:

### `IPlugin` (base contract)

```csharp
public interface IPlugin
{
    string Name { get; }                       // stable lowercase id, e.g. "hello"
    Version Version { get; }                   // semantic version
    Version RequiredHarborVersion { get; }     // minimum Harbor version
    string Description { get; }                // shown in /plugins

    void Initialize(PluginContext context);   // called once on load
    Task ShutdownAsync(CancellationToken ct = default);  // called once on unload
}
```

### `IToolPlugin` — adds tools

```csharp
public interface IToolPlugin : IPlugin
{
    void RegisterTools(IToolRegistryBuilder builder);
}
```

### `IProviderPlugin` — adds LLM providers

```csharp
public interface IProviderPlugin : IPlugin
{
    void RegisterProviders(IProviderRegistryBuilder builder);
}
```

### `IAgentPlugin` — adds agent definitions

```csharp
public interface IAgentPlugin : IPlugin
{
    void RegisterAgents(IAgentRegistryBuilder builder);
}
```

### `ITuiPlugin` — adds TUI views + view models

```csharp
public interface ITuiPlugin : IPlugin
{
    void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels);
}
```

### `ITuiPanelPlugin` (new, subagent #2) — adds a dockable TUI panel

> **TODO: confirm with subagent #2** — `ITuiPanelPlugin` API is being designed
> for the panel system. Expected contract:

```csharp
public interface ITuiPanelPlugin : IPlugin
{
    TuiPanelDescriptor CreatePanel();
}

public sealed record TuiPanelDescriptor(
    string Id,
    string DisplayName,
    TuiPanelPlacement Placement,             // Left | Right | Bottom | Floating
    Func<ITuiViewModel> ViewModelFactory,
    Func<ITuiView> ViewFactory);
```

---

## Plugin context — what can plugins call?

`PluginContext` gives plugins access to the host's services:

```csharp
public sealed class PluginContext
{
    public IServiceCollection Services { get; }       // register DI services
    public IConfiguration Configuration { get; }      // read app config
    public ILoggerFactory LoggerFactory { get; }      // create loggers
    public IEventBus EventBus { get; }                 // subscribe to events
    public string PluginDirectory { get; }             // plugin's on-disk dir (read-only)
    public string DataDirectory { get; }               // plugin's data dir (read-write, persisted)
    public Version HarborVersion { get; }              // current Harbor version

    public ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
}
```

### Available APIs (in `Harbor.Abstractions`)

| Namespace | What's there |
|---|---|
| `Harbor.Abstractions.Models` | `Session`, `AgentMessage`, `AssistantMessage`, `UserMessage`, `ToolResultMessage`, `Usage`, `SessionMetadata` |
| `Harbor.Abstractions.Models.Identifiers` | `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` |
| `Harbor.Abstractions.Tools` | `ITool`, `IToolRegistry`, `ToolContext`, `ToolResult`, `ExecutionMode`, `ToolDescriptor` |
| `Harbor.Abstractions.Providers` | `ILlmClient`, `IProviderRegistry`, `LlmRequest`, `LlmEvent` (12 variants) |
| `Harbor.Abstractions.Events` | `IEventBus`, `AgentEvent` (13 variants) |
| `Harbor.Abstractions.Sessions` | `ISessionStore`, `ISessionContext`, `ICompactionService` |
| `Harbor.Abstractions.Permissions` | `PermissionRuleset`, `PermissionRule`, `PermissionAction`, `PermissionRequest`, `PermissionResponse` |
| `Harbor.Abstractions.Agents` | `IAgent`, `AgentDefinition` |
| `Harbor.Abstractions.Plugins` | `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `PluginContext` |
| `Harbor.Tui.Abstractions.Plugins` | `ITuiPlugin` |
| `Harbor.Tui.Abstractions` | `ViewRegistry`, `ViewModelRegistry`, `ITuiView`, `ITuiViewModel`, `ITuiRenderContext` |

### Via `IServiceProvider` (from `ToolContext.Services`)

Tools get DI access via `context.Services.GetRequiredService<T>()`:

```csharp
public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
{
    var providerRegistry = ctx.Services.GetRequiredService<IProviderRegistry>();
    var sessionStore = ctx.Services.GetRequiredService<ISessionStore>();
    // ...
}
```

### Forbidden APIs

| ❌ Forbidden | Why | ✅ Use instead |
|---|---|---|
| `Harbor.Core.*` (from TUI plugins) | Couples TUI to Core, breaks AOT | `Harbor.Abstractions.Events` |
| `Newtonsoft.Json` | AOT-incompatible | `System.Text.Json` |
| `Assembly.Load` / `AssemblyLoadContext` collectible | AOT-incompatible | Pre-loaded assemblies |
| `Type.GetProperties()` reflection | AOT-incompatible | Pattern match on known types |
| `unsafe` code | Forbidden project-wide | Safe code only |

---

## Limitations of the Roslyn .cs plugin system

### In-process, full-trust, no isolation

Plugins run in the same process as Harbor, with full trust. A misbehaving plugin
can crash the host, leak memory, or call `File.Delete("C:/Windows/System32")`.

**Tradeoffs**:
- ✅ Plugins can call any .NET API (no sandboxing limitations).
- ✅ No IPC overhead.
- ❌ No isolation — buggy plugin = buggy Harbor.
- ❌ Cannot unload a plugin without restarting Harbor.

**Future**: out-of-process plugin host (v0.7+) for untrusted plugins.

### No hot reload

Editing `~/.harbor/plugins/hello.cs` doesn't reload it. Restart Harbor to pick
up changes.

```bash
# After editing a plugin
$ dotnet run --project src/Harbor.Cli
```

### NativeAOT limitations

When Harbor is published as NativeAOT (v0.8+), Roslyn in-process compilation
**does not work** (Roslyn requires JIT). Options:

1. **JIT mode (default)** — full plugin support, all features work.
2. **AOT mode** — plugins must be pre-compiled DLLs, loaded via `AssemblyLoadFrom`
   (no collectible ALC). Or use the out-of-process plugin host.

### Multi-type .cs files

A single `.cs` file can contain multiple types (plugin + tool + view model).
`CsPluginLoader` scans all public types implementing `IPlugin`.

### `using` directives

The loader auto-injects common usings:

```csharp
global using System;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Collections.Generic;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Abstractions.Plugins;
global using Harbor.Abstractions.Tools;
global using CSharpFunctionalExtensions;
```

You can omit these in your plugin file.

---

## Debugging: reading compilation errors

If your plugin fails to compile, `CsPluginLoader` logs the errors:

```bash
$ tail -100 ~/.harbor/harbor.log | grep -A 5 "CsPluginLoader"
warn: Harbor.Core.Plugins.CsPluginLoader[0]
      Failed to compile ~/.harbor/plugins/webhook.cs:
      (12, 17): error CS0103: The name 'HttpClient' does not exist in the current context
      (15, 32): error CS0246: The type or namespace name 'JsonDocument' could not be found
      (22, 5):  error CS0161: 'WebhookTool.ExecuteAsync': not all code paths return a value
```

Each error line: `(line, column): error CSxxxx: message`.

### Common errors

#### CS0103: "The name 'X' does not exist in the current context"

Missing `using`. Add:

```csharp
using System.Net.Http;       // for HttpClient
using System.Text.Json;      // for JsonDocument
using Microsoft.Extensions.Logging;  // for LogInformation
```

#### CS0246: "The type 'X' could not be found"

Missing assembly reference. Plugin can only reference assemblies already loaded
by Harbor (i.e. `Harbor.Abstractions`, `Harbor.Tui.Abstractions`, `System.Text.Json`,
`CSharpFunctionalExtensions`, `Microsoft.Extensions.*`). For anything else, use
a DLL plugin.

#### CS0161: "Not all code paths return a value"

```csharp
// ❌ WRONG
public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct)
{
    if (a.TryGetProperty("name", out var n))
        return Task.FromResult(ToolResult.Success($"Hello, {n}!"));
    // ← missing return at end
}

// ✅ RIGHT
public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct)
{
    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "world" : "world";
    return Task.FromResult(ToolResult.Success($"Hello, {name}!"));
}
```

#### CS1729: "Constructor cannot take N arguments"

You're using an old API. Check the current `PluginContext` signature in
`src/Harbor.Abstractions/Plugins/IPlugin.cs`.

---

## Full example: `TodoWritePlugin` walkthrough

Полный разбор `samples/plugins/Harbor.Plugin.TodoWrite/TodoWritePlugin.cs` (170 строк).

### Plugin class (lines 15-29)

```csharp
public sealed class TodoWritePlugin : IToolPlugin
{
    internal static readonly ConcurrentDictionary<string, List<TodoItem>> TodosBySession = new();

    public string Name => "todowrite";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Todo list management for agents";

    public void Initialize(PluginContext context)
        => context.CreateLogger<TodoWritePlugin>().LogInformation("TodoWrite plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<TodoWriteTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

What's happening:
- `TodosBySession` — shared state keyed by `SessionId`. Per-session todo list.
- `Initialize` — just logs.
- `RegisterTools` — registers `TodoWriteTool` (the tool class below).
- `ShutdownAsync` — no cleanup (state is in-memory, lost on shutdown).

### Tool class (lines 31-162)

```csharp
public sealed class TodoWriteTool : ITool
{
    public ToolName Name => ToolName.Create("todo");
    public string DisplayName => "Todo";
    public string Description => "Manage a todo list... (add, update, list, complete, clear)";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;   // ← side effects, run sequentially
    public string? PromptSnippet => "todo: Manage task list (add/update/complete/list)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `todo` to track progress on multi-step tasks",
        "Add items at the start, mark in_progress when working on them, complete when done",
        "Helps maintain context across long tasks"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["add", "update", "list", "complete", "clear"] },
            "content": { "type": "string" },
            "id": { "type": "string" },
            "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
          },
          "required": ["action"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'action' argument.");
        string? action = actionEl.GetString();
        if (action is not ("add" or "update" or "list" or "complete" or "clear"))
            return Result.Failure($"Unknown action: '{action}'.");
        return Result.Success();
    }
}
```

Key choices:
- `ExecutionMode.Sequential` — tool mutates state, don't run in parallel with itself.
- `PromptGuidelines` — 3 short rules injected into system prompt. Helps LLM use tool correctly.
- `ParameterSchema` — JSON Schema with `enum` constraints. LLM sees valid actions.

### Execute (lines 78-161)

```csharp
public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
{
    string action = args.GetProperty("action").GetString()!;
    var todos = TodoWritePlugin.TodosBySession.GetOrAdd(context.SessionId, _ => new List<TodoItem>());

    lock (todos)   // ← mutate under lock, multiple tool calls per turn could race
    {
        switch (action)
        {
            case "add":
                string content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()! : "";
                if (string.IsNullOrEmpty(content))
                    return Task.FromResult(ToolResult.Error("'content' required for add action."));
                var item = new TodoItem(Guid.NewGuid().ToString("N"), content, TodoStatus.Pending);
                todos.Add(item);
                return Task.FromResult(ToolResult.Success($"Added todo: {item.Id} — {content}", new { id = item.Id }));

            case "list":
                if (todos.Count == 0)
                    return Task.FromResult(ToolResult.Success("No todos. Use action=add to create one."));
                var sb = new StringBuilder();
                sb.AppendLine($"Todos ({todos.Count}):");
                foreach (var t in todos.OrderBy(t => t.Status))
                {
                    string icon = t.Status switch
                    {
                        TodoStatus.Pending => "[ ]",
                        TodoStatus.InProgress => "[~]",
                        TodoStatus.Completed => "[x]",
                        _ => "[?]"
                    };
                    sb.AppendLine($"  {icon} {t.Id} — {t.Content}");
                }
                return Task.FromResult(ToolResult.Success(sb.ToString(), new { count = todos.Count }));

            // ... update / complete / clear
        }
    }
}
```

Key choices:
- `lock (todos)` — the `List<TodoItem>` isn't thread-safe; lock around all mutations.
- `GetOrAdd` — idempotent session creation.
- `OrderBy(t => t.Status)` — deterministic ordering for test stability.
- `ToolResult.Success(message, metadata)` — message goes to LLM, metadata is for
  the host (e.g. UI can show `count`).

### Records + enums (lines 164-171)

```csharp
public sealed record TodoItem(string Id, string Content, TodoStatus Status);

public enum TodoStatus { Pending, InProgress, Completed }
```

`record` for value equality (so two `TodoItem`s with the same `Id` are equal).
`enum` so the LLM can't pass arbitrary strings.

---

## 5 complete plugin examples (30-50 lines each)

### Example 1: `HelloTool` — minimal

Самый простой plugin: один tool, никаких побочных эффектов.

```csharp
// ~/.harbor/plugins/hello.cs
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

public sealed class HelloPlugin : IToolPlugin
{
    public string Name => "hello";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Says hello";
    public void Initialize(PluginContext context) { }
    public void RegisterTools(IToolRegistryBuilder b) => b.AddTool<HelloTool>();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HelloTool : ITool
{
    public ToolName Name => ToolName.Create("hello");
    public string DisplayName => "Hello";
    public string Description => "Returns a hello message";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "hello: Say hello";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
    public Result ValidateArguments(JsonElement args) => Result.Success();
    public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "world" : "world";
        return Task.FromResult(ToolResult.Success($"Hello, {name}!"));
    }
}
```

### Example 2: `FsharpTool` — runs `fsi`

Tool, который запускает F# interpreter (`fsi`) на пользователском коде.

```csharp
// ~/.harbor/plugins/fsharp.cs
using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

public sealed class FsharpPlugin : IToolPlugin
{
    public string Name => "fsharp";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Run F# code via fsi";
    public void Initialize(PluginContext c) { }
    public void RegisterTools(IToolRegistryBuilder b) => b.AddTool<FsharpTool>();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FsharpTool : ITool
{
    public ToolName Name => ToolName.Create("fsharp");
    public string DisplayName => "F#";
    public string Description => "Execute F# code via the fsi interpreter";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "fsharp: Run F# code (fsi)";
    public IReadOnlyList<string> PromptGuidelines => new[] { "Use for F# code execution" };
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}""");

    public Result ValidateArguments(JsonElement a)
        => a.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? Result.Success()
            : Result.Failure("Missing 'code'.");

    public async Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
    {
        var code = a.GetProperty("code").GetString()!;
        using var tmp = new TempFile(".fsx");
        await File.WriteAllTextAsync(tmp.Path, code, ct);

        var psi = new ProcessStartInfo
        {
            FileName = "fsi",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(tmp.Path);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("fsi not found");
        await proc.WaitForExitAsync(ct);
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);

        return proc.ExitCode == 0
            ? ToolResult.Success(stdout)
            : ToolResult.Error($"fsi exited {proc.ExitCode}:\n{stderr}");
    }
}

file sealed class TempFile : IDisposable
{
    public string Path { get; }
    public TempFile(string ext) { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext); }
    public void Dispose() { try { File.Delete(Path); } catch { } }
}
```

### Example 3: `WebhookTool` — fires HTTP webhook on events

Plugin подписывается на `MessageEndEvent` и POST'ит summary на webhook URL.

```csharp
// ~/.harbor/plugins/webhook.cs
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Plugins;

public sealed class WebhookPlugin : IPlugin
{
    public string Name => "webhook";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Fires HTTP webhook on message_end";
    private HttpClient? _http;
    private string? _url;

    public void Initialize(PluginContext context)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _url = context.Configuration["HARBOR_WEBHOOK_URL"];
        if (string.IsNullOrEmpty(_url))
        {
            context.CreateLogger<WebhookPlugin>().LogWarning("HARBOR_WEBHOOK_URL not set — webhook disabled");
            return;
        }
        context.EventBus.Subscribe(OnEvent);
    }

    private async Task OnEvent(AgentEvent e, CancellationToken ct)
    {
        if (e is not MessageEndEvent me) return;
        if (_url is null || _http is null) return;

        var payload = JsonSerializer.Serialize(new
        {
            session_id = me.Message.SessionId,
            message_id = me.Message.Id,
            text = me.Message.GetText(),
            timestamp = e.Timestamp
        });

        try
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_url, content, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Don't crash the host — log and move on.
            Console.Error.WriteLine($"[webhook] {ex.Message}");
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_http is not null) _http.Dispose();
        await Task.CompletedTask;
    }
}
```

> **Note**: This is `IPlugin` (not `IToolPlugin`) — it doesn't add tools, only
> subscribes to events. Still loaded the same way (drop in `~/.harbor/plugins/`).

### Example 4: `AutoLintTool` — runs linter after edit

Plugin подписывается на `ToolExecutionEndEvent` для tool `edit`, запускает
`dotnet format` на отредактированном файле.

```csharp
// ~/.harbor/plugins/autolint.cs
using System.Diagnostics;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Plugins;

public sealed class AutoLintPlugin : IPlugin
{
    public string Name => "autolint";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Runs 'dotnet format' after edit tool";

    public void Initialize(PluginContext context)
        => context.EventBus.Subscribe(OnEvent);

    private static async Task OnEvent(AgentEvent e, CancellationToken ct)
    {
        if (e is not ToolExecutionEndEvent tee) return;
        if (tee.ToolName != "edit" || tee.IsError) return;

        // Extract path from tool result output (heuristic — the edit tool prints "Edited: <path>")
        string? path = ExtractPath(tee.Result.Output);
        if (path is null) return;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("format");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc is null) return;
        await proc.WaitForExitAsync(ct);
    }

    private static string? ExtractPath(string output)
    {
        // "Edited: src/Harbor.Core/Agents/AgentLoop.cs"
        var idx = output.IndexOf("Edited: ");
        if (idx < 0) return null;
        var start = idx + "Edited: ".Length;
        var end = output.IndexOf('\n', start);
        return end < 0 ? output[start..] : output[start..end];
    }

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

### Example 5: `LspDiagnosticsPanel` — TUI panel showing LSP diagnostics

> **TODO: confirm with subagent #2** — `ITuiPanelPlugin` API.

```csharp
// ~/.harbor/plugins/lsp_diag.cs
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.Plugins;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
using Harbor.Tui.Abstractions.Views;

public sealed class LspDiagnosticsPlugin : ITuiPanelPlugin
{
    public string Name => "lsp-diag";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "LSP diagnostics panel";
    public void Initialize(PluginContext c) { }

    public TuiPanelDescriptor CreatePanel() => new(
        Id: "lsp-diag",
        DisplayName: "Diagnostics",
        Placement: TuiPanelPlacement.Bottom,
        ViewModelFactory: () => new LspDiagnosticsViewModel(),
        ViewFactory: () => new LspDiagnosticsView());

    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed partial class LspDiagnosticsViewModel : ObservableObject, ITuiViewModel
{
    public string Id => "lsp-diag";

    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private string _latest = "";

    public Task UpdateFromEventAsync(AgentEvent e, CancellationToken ct = default)
    {
        // Hook into tool execution end (after edit) to refresh diagnostics
        if (e is ToolExecutionEndEvent tee && tee.ToolName == "edit")
        {
            Latest = $"Last edit: {tee.Result.Output.Split('\n')[0]}";
        }
        return Task.CompletedTask;
    }
}

public sealed class LspDiagnosticsView : TuiViewBase<LspDiagnosticsViewModel>
{
    public override string Id => "lsp-diag";
    public override string DisplayName => "Diagnostics";
    public override TuiViewPlacement Placement => TuiViewPlacement.Bottom;

    public override Task RenderAsync(ITuiRenderContext ctx, CancellationToken ct = default)
    {
        if (ViewModel is null) return Task.CompletedTask;
        ctx.WriteColored($"Errors: {ViewModel.ErrorCount}  Warnings: {ViewModel.WarningCount}", TuiColor.Yellow);
        if (!string.IsNullOrEmpty(ViewModel.Latest))
        {
            ctx.WriteLine();
            ctx.WriteColored(ViewModel.Latest, TuiColor.DarkGray);
        }
        return Task.CompletedTask;
    }
}
```

---

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

# 4. Copy DLL (+ dependencies) to ~/.harbor/plugins/
$ cp samples/plugins/Harbor.Plugin.MyPlugin/bin/Release/net10.0/Harbor.Plugin.MyPlugin.dll ~/.harbor/plugins/
$ cp samples/plugins/Harbor.Plugin.MyPlugin/bin/Release/net10.0/*.dll ~/.harbor/plugins/  # deps

# 5. Run Harbor
$ dotnet run --project src/Harbor.Cli
```

### After (Roslyn .cs plugin)

```bash
# 1. Drop a single .cs file in ~/.harbor/plugins/
$ cat > ~/.harbor/plugins/myplugin.cs << 'EOF'
public sealed class MyPlugin : IToolPlugin { /* ... */ }
public sealed class MyTool : ITool { /* ... */ }
EOF

# 2. Run Harbor — CsPluginLoader compiles it on startup
$ dotnet run --project src/Harbor.Cli
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

---

## Scripting alternative

If you don't need full .NET API access, you can also extend Harbor with
scripts:

- **TypeScript / JavaScript** (`.ts` / `.js`) — the default Harbor-native
  scripting path. SharpTS is the default engine (runs scripts as a
  subprocess via the `sharpts` dotnet tool, with native TS interpretation);
  Jint is the in-process fallback when `sharpts` is not installed.
- **F# scripts** (`.fsx`) — via the `FsharpTool` plugin above.

See [SCRIPTING.md](./SCRIPTING.md) for the layered architecture (Engines /
Storage / Compilation / Hosting / Bridge), the four interfaces
(`IScriptEngine`, `IScriptStore`, `IScriptCompiler`, `ScriptGlobals`), and
script authoring examples.

---

## MCP alternative

> **TODO: confirm with subagent #4** — `McpToolTool` is being built (v0.4+).

If your tool already exists as an MCP server (e.g. `@modelcontextprotocol/server-filesystem`),
Harbor will wrap it as a native tool:

```bash
# Planned API:
$ harbor mcp add filesystem -- npx -y @modelcontextprotocol/server-filesystem /tmp
$ harbor
harbor> /tools
  read_file (mcp:filesystem)   Read a file from /tmp
  write_file (mcp:filesystem)  Write a file to /tmp
  list_files (mcp:filesystem)  List files in /tmp
```

No plugin code needed — MCP servers expose their own tool schemas.

See [specs/06-mcp.md](../specs/06-mcp.md) for the design.

---

## Testing your plugin

### Unit tests for the tool

```csharp
using Harbor.Plugin.TodoWrite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

public class TodoWriteToolTests
{
    [Test]
    public async Task Add_Creates_Todo_With_Id()
    {
        var tool = new TodoWriteTool();
        var args = JsonDocument.Parse("""{"action":"add","content":"write tests"}""").RootElement;
        var ctx = MakeContext("test-session");

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Added todo:");
        // cleanup
        TodoWritePlugin.TodosBySession.TryRemove("test-session", out _);
    }

    private static ToolContext MakeContext(string sessionId) => new(
        SessionId: sessionId, MessageId: "m1", CallId: "tc1", Agent: "code",
        Abort: CancellationToken.None, Messages: Array.Empty<AgentMessage>(),
        ReportProgress: (_, _) => Task.CompletedTask,
        Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        Services: null!);
}
```

### E2E test (against real provider)

```csharp
[Test]
public async Task Plugin_Tool_IsCallable_By_LLM()
{
    if (Environment.GetEnvironmentVariable("HARBOR_E2E") is null) return;   // skip in CI

    // 1. Set up Harbor with the plugin loaded
    // 2. Run `harbor ask "Add a todo item 'write tests' and list them"`
    // 3. Assert the output contains "Todos (1)" and "write tests"
}
```

### Manual smoke test

```bash
$ dotnet run --project src/Harbor.Cli
harbor> /plugins
  todowrite  v1.0.0  Todo list management for agents

harbor> Add a todo item 'write tests' and list all todos
[tool_execution_start] id=tc_1 tool=todo args={"action":"add","content":"write tests"}
[tool_execution_end]   id=tc_1 ok=true
[tool_execution_start] id=tc_2 tool=todo args={"action":"list"}
[tool_execution_end]   id=tc_2 ok=true
Todos (1):
  [ ] abc123 — write tests
```

---

## Distribution

### Sharing a single .cs file

```bash
# Author publishes to gist
$ gh gist create myplugin.cs --public

# User installs
$ curl -fsSL https://gist.githubusercontent.com/user/myplugin.cs/raw > ~/.harbor/plugins/myplugin.cs
$ dotnet run --project src/Harbor.Cli
harbor> /plugins
  myplugin  v1.0.0
```

### Future: `harbor plugin install` (v0.5+)

```bash
$ harbor plugin install https://gist.github.com/user/myplugin.cs
$ harbor plugin install nuget:Harbor.Plugin.MyPlugin    # DLL plugin
$ harbor plugin list
$ harbor plugin uninstall myplugin
```

---

## Gotchas

### `using Harbor.Abstractions.Models.Identifiers`

`ToolName` lives in `Harbor.Abstractions.Models.Identifiers`, not in `Tools`.

```csharp
// ❌ WRONG — won't compile
using Harbor.Abstractions.Tools;
public ToolName Name => ToolName.Create("foo");   // CS0246: ToolName not found

// ✅ RIGHT
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
public ToolName Name => ToolName.Create("foo");
```

For `.cs` plugins, the loader auto-injects this using — but DLL plugins need it
explicitly.

### `using Microsoft.Extensions.Logging`

`LogInformation` is an extension method, not on `ILogger` itself.

```csharp
// ❌ WRONG — won't compile
var logger = context.CreateLogger<MyPlugin>();
logger.LogInformation("foo");   // CS1061: ILogger doesn't have LogInformation

// ✅ RIGHT
using Microsoft.Extensions.Logging;
logger.LogInformation("foo");
```

### `HttpClient` — static, not per-call

```csharp
// ❌ WRONG — creates new HttpClient per call (socket exhaustion)
public async Task<ToolResult> ExecuteAsync(...)
{
    using var client = new HttpClient();   // ← BAD
    var resp = await client.GetAsync(url, ct);
    // ...
}

// ✅ RIGHT — static singleton
private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

public async Task<ToolResult> ExecuteAsync(...)
{
    var resp = await Client.GetAsync(url, ct);
    // ...
}
```

### Plugin state survives across tool calls but NOT across Harbor restarts

```csharp
// Todos are lost when Harbor restarts:
internal static readonly ConcurrentDictionary<string, List<TodoItem>> TodosBySession = new();
```

If you need persistence, write to `context.DataDirectory`:

```csharp
public void Initialize(PluginContext context)
{
    _dataPath = Path.Combine(context.DataDirectory, "todos.json");
    if (File.Exists(_dataPath))
        _todos = JsonSerializer.Deserialize<...>(File.ReadAllText(_dataPath));
}
```

### Plugins can't reference `Harbor.Core`

TUI plugins must use only `Harbor.Abstractions` + `Harbor.Tui.Abstractions`.
Referencing `Harbor.Core` couples TUI to Core, breaks AOT.

```csharp
// ❌ WRONG — TUI plugin referencing Core
using Harbor.Core.Sessions;   // ← will fail in AOT mode

// ✅ RIGHT — TUI plugin uses only Abstractions
using Harbor.Abstractions.Events;   // AgentEvent comes from here
```

---

## See also

- [EXAMPLES.md §Plugins](./EXAMPLES.md#plugins) — short recipes.
- [DEVELOPMENT.md §Workflow: contribute a plugin](./DEVELOPMENT.md#workflow-contribute-a-plugin).
- [ARCHITECTURE.md §Plugin contract](./ARCHITECTURE.md#8-plugin-contract).
- [specs/02-plugins.md](../specs/02-plugins.md) — design rationale.
- [SCRIPTING.md](./SCRIPTING.md) — scripting alternative (planned).
- [specs/06-mcp.md](../specs/06-mcp.md) — MCP server integration.
