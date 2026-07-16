# 01 — Архитектура ядра

> Документ: программная архитектура. Слои, assembly layout, pipeline, DI, lifecycle, threading model. Здесь — структурные решения; детали каждого компонента — в специализированных разделах (`02-plugins.md`, `03-providers.md`, и т.д.).

## 1. Слои и сборки

```
Harbor.sln
│
├── src/
│   ├── Harbor.Abstractions/         — контракты, без impl
│   │   ├── IAgent.cs
│   │   ├── IAgentLoop.cs
│   │   ├── ITool.cs
│   │   ├── IToolRegistry.cs
│   │   ├── IProviderRegistry.cs
│   │   ├── ISessionStore.cs
│   │   ├── ICompactionService.cs
│   │   ├── IExtensionHost.cs
│   │   ├── IPermissionService.cs
│   │   ├── ILlmClient.cs            (или Microsoft.Extensions.AI.IChatClient)
│   │   └── Events/                  (LLMEvent, AgentEvent, ToolEvent — discriminated unions)
│   │
│   ├── Harbor.Core/                 — базовые impl (host-agnostic)
│   │   ├── Agent.cs
│   │   ├── AgentLoop.cs
│   │   ├── ToolRegistry.cs
│   │   ├── ProviderRegistry.cs
│   │   ├── SessionManager.cs
│   │   ├── CompactionService.cs
│   │   ├── SystemPromptBuilder.cs
│   │   ├── PermissionService.cs
│   │   ├── TokenEstimator.cs
│   │   └── Json/                    (JsonSerializerContext source-gen)
│   │
│   ├── Harbor.Storage.Sqlite/       — SQLite + Dapper.AOT
│   │   ├── SqliteSessionStore.cs
│   │   ├── Migrations/
│   │   └── Schema.sql
│   │
│   ├── Harbor.Storage.Jsonl/        — JSONL mirror / alternative
│   │   └── JsonlSessionStore.cs
│   │
│   ├── Harbor.Providers.Anthropic/  — провайдеры (отдельные сборки, lazy-load)
│   ├── Harbor.Providers.OpenAI/
│   ├── Harbor.Providers.Google/
│   ├── Harbor.Providers.Ollama/
│   │
│   ├── Harbor.Tools.Builtin/        — read/write/edit/bash/glob/grep/ls
│   │   ├── ReadTool.cs
│   │   ├── WriteTool.cs
│   │   ├── EditTool.cs
│   │   ├── BashTool.cs
│   │   ├── GlobTool.cs
│   │   ├── GrepTool.cs
│   │   └── LsTool.cs
│   │
│   ├── Harbor.Extensions/           — plugin loading, AssemblyLoadContext
│   │   ├── ExtensionHost.cs
│   │   ├── PluginLoader.cs
│   │   └── CollectibleAssemblyLoadContext.cs
│   │
│   ├── Harbor.Mcp/                  — MCP client (опциональный, plugin-style)
│   │   ├── McpClient.cs
│   │   ├── Transports/ (Stdio, Http, Sse)
│   │   └── McpToolAdapter.cs
│   │
│   ├── Harbor.Tui/                  — терминальный UI
│   │   ├── Ansi.cs                  (50-LOC helper)
│   │   ├── StreamingRenderer.cs
│   │   ├── SlashCommandRouter.cs
│   │   ├── Components/ (Header, ChatHistory, Editor, Footer, StatusBar)
│   │   └── Theme.cs
│   │
│   ├── Harbor.Cli/                  — ConsoleAppFramework v5 host
│   │   ├── Commands/ (Run, Serve, Session, Plugin, Models)
│   │   └── Program.cs
│   │
│   └── Harbor.NativeAot/           — AOT-specific dispatch (source-gen)
│       └── PluginDispatch.Generated.cs
│
├── tests/
│   ├── Harbor.Core.Tests/
│   ├── Harbor.Providers.Tests/      (recording/playback via VCR-style)
│   └── Harbor.E2e.Tests/
│
└── samples/
    ├── Harbor.PluginSample/
    └── Harbor.CustomProvider/
```

**Принципы разбиения**:
1. **`Harbor.Abstractions`** — чистые интерфейсы, ноль зависимостей. Можно ссылаться из plugin-проектов без тащения ядра.
2. **Каждый провайдер в своей сборке** — lazy-load через type-name, не тащим все SDK в бинарник.
3. **`Harbor.Tools.Builtin`** — отдельная сборка, чтобы можно было заменить целиком (например, использовать `Harbor.Tools.Minimal` без bash для sandboxed environments).
4. **`Harbor.Mcp`** — опциональная сборка. Не подключается по умолчанию.
5. **`Harbor.NativeAot`** — AOT-specific код (source-gen dispatch). В JIT-билдах не используется.

## 2. Главный пайплайн (high-level)

```
┌────────────────────────────────────────────────────────────────────────────┐
│ User input (CLI/TUI)                                                       │
│  ├─ slash command (/session, /model, /clear, /compact, /plugin, ...)      │
│  └─ prompt text                                                            │
└──────────────────────────────┬─────────────────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ SessionManager                                                             │
│  ├─ resolve current session (or create new)                                │
│  ├─ append UserMessage to session                                          │
│  └─ enqueue PromptRequest to AgentLoop                                     │
└──────────────────────────────┬─────────────────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ AgentLoop.RunAsync(session, cancellationToken)                             │
│                                                                            │
│  while (true) {                                                            │
│    1. Check overflow → if (ShouldCompact) → CompactionService.Run()        │
│    2. Build system prompt (env + tools + skills + context files + mode)    │
│    3. Resolve tools from ToolRegistry (filter by agent.permission)         │
│    4. Resolve provider from ProviderRegistry (lazy-load if not loaded)     │
│    5. Convert AgentMessages → LlmMessages (provider-specific transform)    │
│    6. llmClient.StreamAsync(messages, tools, systemPrompt, ct)             │
│       └─ IAsyncEnumerable<LLMEvent>                                        │
│           ├─ TextDelta → TUI render token                                  │
│           ├─ ToolCall → queue for execution                                │
│           └─ StepFinish → usage tracking, cost                             │
│    7. If no tool calls → break                                             │
│    8. Execute tool calls (parallel by default, sequential per-tool flag)   │
│       ├─ PermissionService.Check(tool, args, agent) → allow|ask|deny       │
│       ├─ tool.Execute(args, ctx)                                           │
│       │   └─ ToolContext { SessionId, Messages, Ask, Cancel }              │
│       └─ ToolResult → append to session                                    │
│    9. Check doom-loop (3 identical tool calls → ask user)                  │
│    10. step++; if (step > agent.MaxSteps) → inject MAX_STEPS prefill       │
│  }                                                                         │
│                                                                            │
│  → finalize: persist session, fire AgentEnd event                          │
└────────────────────────────────────────────────────────────────────────────┘
```

## 3. Lifecycle и DI

### 3.1. Host

Используем `Microsoft.Extensions.Hosting` — стандартный `IHost` с `IHostedService`:

```csharp
// Harbor.Cli/Program.cs
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("~/.harbor/config.json", optional: true)
    .AddEnvironmentVariables("HARBOR_")
    .AddCommandLine(args);

// Core services
builder.Services.AddSingleton<ISessionStore, SqliteSessionStore>();
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
builder.Services.AddSingleton<ICompactionService, CompactionService>();
builder.Services.AddSingleton<IPermissionService, PermissionService>();
builder.Services.AddSingleton<IExtensionHost, ExtensionHost>();
builder.Services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

// Builtin tools (registered via convention)
builder.Services.AddBuiltinTools();
builder.Services.AddBuiltinProviders();
builder.Services.AddBuiltinAgents();

// CLI/TUI
builder.Services.AddSingleton<IStreamingRenderer, StreamingRenderer>();
builder.Services.AddSingleton<ISlashCommandRouter, SlashCommandRouter>();

// IHostedService
builder.Services.AddHostedService<HarborHostedService>();

var host = builder.Build();
await host.RunAsync();
```

### 3.2. `HarborHostedService`

```csharp
public sealed class HarborHostedService : IHostedService
{
    private readonly ISlashCommandRouter _router;
    private readonly IConsoleInput _input;
    private readonly IServiceProvider _services;
    
    public Task StartAsync(CancellationToken ct)
    {
        // 1. Initialize extension host (load plugins from ~/.harbor/plugins/)
        // 2. Initialize session store (run migrations)
        // 3. Start TUI render loop (in background)
        // 4. Start CLI input loop
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken ct)
    {
        // 1. Cancel all in-flight LLM streams
        // 2. Flush session store
        // 3. Unload plugins (if collectible ALC)
        // 4. Restore terminal state (disable raw mode, show cursor)
        return Task.CompletedTask;
    }
}
```

### 3.3. Application lifecycle events

```csharp
public interface IApplicationLifecycle
{
    event Func<CancellationToken, Task> Initializing;   // до DI build
    event Func<CancellationToken, Task> Initialized;    // после DI build, до старта
    event Func<CancellationToken, Task> Started;        // после IHostedService.Start
    event Func<CancellationToken, Task> Stopping;       // перед Stop
    event Func<CancellationToken, Task> Stopped;        // после Stop
}
```

Plugins подписываются через `IExtensionHost.On(event, handler)` (см. `02-plugins.md`).

## 4. Threading model

### 4.1. Принципы

1. **`async/await` везде** — никаких `Thread.Sleep`, никаких `.Result`, никаких `.Wait()`.
2. **`CancellationToken` пробрасывается во все async-методы** — пользователь может прервать в любой момент (Escape в TUI, Ctrl-C в CLI).
3. **`IAsyncEnumerable<T>` для streaming** — LLM-стриминг, tool progress, session events.
4. **`System.Threading.Channels` для очередей** — steering messages, follow-up queue, tool call queue.
5. **`Microsoft.Extensions.Logging` с async sinks** — никаких blocking file writes в hot path.

### 4.2. Single-threaded TUI render loop

TUI — single-threaded (как у crush): все события из ядра (`AgentLoop`, `ToolRegistry`, etc.) маршалируются в один `Channel<object>`, который читается render loop'ом. Это исключает race conditions в ANSI-рендеринге.

```csharp
public sealed class TuiRenderLoop
{
    private readonly Channel<object> _eventChannel = 
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions { 
            SingleReader = true, 
            SingleWriter = false 
        });
    
    public async Task RunAsync(CancellationToken ct)
    {
        // Set terminal raw mode, hide cursor, enter alt screen if needed
        await Terminal.EnterRawModeAsync();
        
        try
        {
            await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct))
            {
                Render(evt);
                Terminal.Flush();
            }
        }
        finally
        {
            await Terminal.ExitRawModeAsync();
        }
    }
    
    // Различные producer'ы пишут в канал
    public void Publish(object evt) => _eventChannel.Writer.TryWrite(evt);
}
```

### 4.3. Background services

| Service | Schedule | Что делает |
|---|---|---|
| `CompactionPruneService` | после каждого turn | Удаляет старые tool outputs (>20K токенов) из активной истории |
| `SessionSnapshotService` | каждые N изменений | Сохраняет snapshot сессии для undo/revert |
| `PluginWatcherService` | FS watch на `~/.harbor/plugins/` | Hot-reload плагинов при изменении (dev mode) |
| `McpReconnectService` | per-MCP retry policy | Переподключение к MCP-серверам при падении |
| `LspManagerService` | on-demand | Spawn/kill LSP-серверов (plugin) |

## 5. Конфигурация

### 5.1. Источники (по приоритету, высокий → низкий)

1. CLI args (`--model claude-opus-4`, `--no-tools bash`)
2. Environment variables (`HARBOR_MODEL=...`, `ANTHROPIC_API_KEY=...`)
3. Project-local config (`.harbor/config.json` in cwd)
4. Global config (`~/.harbor/config.json`)
5. Builtin defaults

### 5.2. Структура `config.json`

```jsonc
{
  "$schema": "https://harbor.sh/schema/config.json",
  
  // Default model & agent
  "model": "anthropic/claude-opus-4",
  "agent": "code",
  
  // Providers config
  "providers": {
    "anthropic": {
      "apiKey": "${ANTHROPIC_API_KEY}",  // env var expansion
      "baseUrl": null                     // null = default
    },
    "openai": { "apiKey": "${OPENAI_API_KEY}" },
    "ollama": { "baseUrl": "http://localhost:11434" }
  },
  
  // Tools (override defaults)
  "tools": {
    "bash": {
      "timeout": 30000,
      "shell": "auto"  // auto | bash | pwsh | cmd
    },
    "edit": { "usePatch": false }  // GPT-5+ prefers apply_patch
  },
  
  // Permissions (per-tool glob patterns)
  "permissions": {
    "bash":    { "*": "ask", "ls *": "allow", "cat *": "allow" },
    "edit":    { "src/*": "allow", "*.env*": "deny", "*": "ask" },
    "write":   { "src/*": "allow", "*": "ask" },
    "webfetch":{ "*": "ask" }
  },
  
  // Agents (modes)
  "agents": {
    "code":    { "model": "anthropic/claude-opus-4", "maxSteps": 50 },
    "plan":    { "model": "anthropic/claude-opus-4", "maxSteps": 100 },
    "explore": { "model": "anthropic/claude-haiku-3.5", "maxSteps": 20 }
  },
  
  // Custom agents (template from builtin)
  "customAgents": [
    {
      "name": "reviewer",
      "displayName": "Code Reviewer",
      "model": "anthropic/claude-opus-4",
      "permission": {
        "bash":  { "*": "deny" },
        "edit":  { "*": "deny" },
        "write": { "*": "deny" },
        "read":  { "*": "allow" },
        "grep":  { "*": "allow" },
        "glob":  { "*": "allow" }
      },
      "systemPromptAppend": "You are a strict code reviewer. Focus on bugs, security, and architectural issues. Do not modify code."
    }
  ],
  
  // Plugins (NuGet package IDs or local paths)
  "plugins": [
    "Harbor.Plugin.WebSearch",
    "Harbor.Plugin.TodoWrite",
    "./local-plugins/my-debug-tool.dll"
  ],
  
  // MCP servers (optional)
  "mcp": {
    "filesystem": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
    },
    "github": {
      "type": "http",
      "url": "https://mcp.github.com/sse",
      "oauth": { /* ... */ }
    }
  },
  
  // Sessions
  "sessions": {
    "storage": "sqlite",  // sqlite | jsonl
    "path": "~/.harbor/sessions.db",
    "compaction": {
      "reserveTokens": 16384,
      "keepRecentTokens": 20000,
      "tailTurns": 2,
      "toolOutputMaxChars": 2000
    }
  },
  
  // TUI
  "tui": {
    "theme": "auto",  // auto | dark | light
    "editor": "vim",  // vim | emacs | default
    "renderMode": "streaming"  // streaming | batch
  },
  
  // Telemetry
  "telemetry": {
    "enabled": false,
    "endpoint": null
  }
}
```

### 5.3. .NET Configuration binding под AOT

`Microsoft.Extensions.Configuration` + `ConfigurationBinder` использует reflection. Под AOT используем **`ConfigurationBinder.SourceGen`** (preview в .NET 10):

```csharp
[ConfigurationSource]
public partial class HarborConfig
{
    public string Model { get; set; } = "anthropic/claude-opus-4";
    public string Agent { get; set; } = "code";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();
    // ...
}
```

Source generator создаёт `HarborConfigBinder` с strongly-typed binding, без reflection.

## 6. Event model

### 6.1. `AgentEvent` discriminated union

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentStartEvent), "agent_start")]
[JsonDerivedType(typeof(TurnStartEvent), "turn_start")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(MessageUpdateEvent), "message_update")]
[JsonDerivedType(typeof(MessageEndEvent), "message_end")]
[JsonDerivedType(typeof(ToolExecutionStartEvent), "tool_execution_start")]
[JsonDerivedType(typeof(ToolExecutionUpdateEvent), "tool_execution_update")]
[JsonDerivedType(typeof(ToolExecutionEndEvent), "tool_execution_end")]
[JsonDerivedType(typeof(TurnEndEvent), "turn_end")]
[JsonDerivedType(typeof(AgentEndEvent), "agent_end")]
[JsonDerivedType(typeof(AgentErrorEvent), "agent_error")]
public abstract record AgentEvent;

public sealed record AgentStartEvent(string SessionId, IReadOnlyList<AgentMessage> Messages) : AgentEvent;
public sealed record TurnStartEvent(int TurnIndex) : AgentEvent;
public sealed record MessageStartEvent(AgentMessage Message) : AgentEvent;
public sealed record MessageUpdateEvent(LLMEvent LlmEvent, AgentMessage Partial) : AgentEvent;
public sealed record MessageEndEvent(AgentMessage Message) : AgentEvent;
public sealed record ToolExecutionStartEvent(string ToolCallId, string ToolName, JsonElement Args) : AgentEvent;
public sealed record ToolExecutionUpdateEvent(string ToolCallId, object PartialResult) : AgentEvent;
public sealed record ToolExecutionEndEvent(string ToolCallId, ToolResult Result, bool IsError) : AgentEvent;
public sealed record TurnEndEvent(AgentMessage AssistantMessage, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;
public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> NewMessages) : AgentEvent;
public sealed record AgentErrorEvent(string Message, Exception? Exception) : AgentEvent;
```

### 6.2. Подписка

```csharp
public interface IAgent
{
    AgentState State { get; }
    CancellationTokenSource AbortSource { get; }
    
    IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener);
    
    Task PromptAsync(string text, CancellationToken ct = default);
    Task PromptAsync(AgentMessage message, CancellationToken ct = default);
    
    /// <summary>Прервать текущий turn, но позволить finish'ить pending tool executions.</summary>
    void Steer(AgentMessage message);
    
    /// <summary>Поставить в очередь после текущего turn'а.</summary>
    void FollowUp(AgentMessage message);
    
    Task WaitForIdleAsync(CancellationToken ct = default);
}
```

Реализация — `Channel<AgentEvent>` для listener'ов, `Channel<AgentMessage>` для steering/follow-up queue. См. `01-architecture.md` §7.

### 6.3. JSON serialization для events

Для `harbor serve` mode (JSON-over-stdio или HTTP+SSE) все `AgentEvent` сериализуются через `JsonSerializerContext`:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(AgentStartEvent))]
[JsonSerializable(typeof(TurnStartEvent))]
// ... все типы events
public partial class HarborEventContext : JsonSerializerContext { }
```

## 7. Agent loop — детальная блок-схема

См. `04-tools.md` §3 для полного псевдокода. Здесь — high-level:

```
PromptAsync(userMessage):
  1. Append userMessage to session (with metadata: agent, model, cost=0, tokens=0)
  2. Subscribe internal listeners (for persistence, telemetry)
  3. Call AgentLoop.RunAsync() in background
  4. Return Task that completes when AgentEnd fires (or abort)

AgentLoop.RunAsync():
  loop:
    turnIndex++
    emit TurnStart
    
    ── Overflow check ──
    if (CompactionService.ShouldCompact(session, model)):
      emit CompactionStarted
      await CompactionService.RunAsync(session, model, ct)
      emit CompactionCompleted
    
    ── Build system prompt ──
    systemPrompt = SystemPromptBuilder.Build(
        agent, model, availableTools, contextFiles, skills, mcpInstructions)
    
    ── Resolve tools (filter by agent.permission) ──
    tools = ToolRegistry.Resolve(agent, session)
    
    ── Convert messages (provider-specific) ──
    llmMessages = MessageConverter.ToLlmMessages(session.Messages, model)
    
    ── Stream LLM ──
    emit MessageStart(partial=AssistantMessage.Empty)
    partial = AssistantMessage.Empty
    async foreach (evt in llmClient.StreamAsync(systemPrompt, llmMessages, tools, ct)):
      switch (evt):
        case TextDelta(text):
          partial = partial.AppendText(text)
          emit MessageUpdate(evt, partial)
          TUI: render text token
        
        case ToolCallStart(toolCallId, toolName):
          partial = partial.AppendToolCall(toolCallId, toolName)
          emit MessageUpdate
        
        case ToolCallDelta(toolCallId, argsDelta):
          partial = partial.UpdateToolCallArgs(toolCallId, argsDelta)
          emit MessageUpdate
        
        case ToolCallEnd(toolCallId, args):
          partial = partial.FinalizeToolCall(toolCallId, args)
          emit MessageUpdate
        
        case StepFinish(reason, usage):
          partial = partial.WithFinish(reason, usage)
          // не emit'им MessageEnd ещё — есть же tool calls
        
        case Error(error):
          emit AgentError
          return
    
    emit MessageEnd(partial)
    session.Append(partial)
    
    ── No tool calls? done ──
    if (partial.ToolCalls.Count == 0 or partial.StopReason is "stop" or "length"):
      break
    
    ── Tool calls ──
    toolResults = await ExecuteToolCallsAsync(partial.ToolCalls, ct)
    session.Append(toolResults)
    
    emit TurnEnd(partial, toolResults)
    
    ── Doom loop detection ──
    if (DetectDoomLoop(session, threshold=3)):
      emit DoomLoopDetected
      // ask user via PermissionService
      break
    
    ── Steering check ──
    if (steeringQueue.TryDequeue(out var steerMsg)):
      session.Append(steerMsg)
    
    ── Max steps check ──
    if (turnIndex >= agent.MaxSteps):
      // inject MAX_STEPS reminder
      llmMessages.Add(new AssistantMessage("...max-steps-reached..."))
  
  emit AgentEnd(session.NewMessages)
```

## 8. Session, message, part — data model

```csharp
public sealed record Session(
    string Id,
    string ProjectId,
    string Directory,
    string Title,
    string Agent,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionMetadata Metadata);

public sealed record SessionMetadata(
    decimal Cost,
    int TokensInput,
    int TokensOutput,
    int TokensReasoning,
    int TokensCacheRead,
    int TokensCacheWrite);

public abstract record AgentMessage(string Id, string SessionId, DateTimeOffset CreatedAt);

public sealed record UserMessage(
    string Id, string SessionId, DateTimeOffset CreatedAt,
    string Content,  // text or structured
    string Agent,
    string Model,
    IReadOnlyDictionary<string, string>? Metadata = null) : AgentMessage;

public sealed record AssistantMessage(
    string Id, string SessionId, DateTimeOffset CreatedAt,
    IReadOnlyList<ContentPart> Parts,
    string StopReason,  // "stop" | "length" | "tool_use" | "error" | "aborted"
    Usage Usage,
    string Model) : AgentMessage;

public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart;
public sealed record ThinkingPart(string Text) : ContentPart;
public sealed record ToolCallPart(string ToolCallId, string ToolName, JsonElement Args) : ContentPart;
public sealed record FilePart(string Path, string MimeType, long SizeBytes) : ContentPart;

public sealed record ToolResultMessage(
    string Id, string SessionId, DateTimeOffset CreatedAt,
    IReadOnlyList<ToolResult> Results) : AgentMessage;

public sealed record ToolResult(
    string ToolCallId,
    string ToolName,
    string Output,
    bool IsError,
    object? Metadata);

public sealed record Usage(
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens,
    int CacheReadTokens,
    int CacheWriteTokens);
```

## 9. Persistence flow

```
UserMessage appended ─┐
                      ├─→ SessionStore.AppendMessageAsync (sync, fast)
AssistantMessage appended ─┘
                              │
                              ▼
                    SQLite INSERT (WAL, fire-and-forget for streaming parts)
                              │
                              ▼
                    (commit on MessageEnd, not on each delta)
                              
ToolResultMessage appended ─→ SQLite INSERT (sync, all parts at once)
```

**Streaming optimization**: во время LLM-стриминга мы не пишем каждую `TextDelta` в БД. Буферизуем в памяти, пишем на `MessageEnd` (или каждые N дельт, или каждые T ms — что раньше).

## 10. Logging и observability

### 10.1. Logging

```csharp
// Serilog с async file sink + console sink (для dev)
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("SessionId", sessionId)
    .WriteTo.Async(a => a.File("~/.harbor/logs/harbor-.log", rollingInterval: RollingInterval.Day))
    .WriteTo.Console(condition: builder.Environment.IsDevelopment())
    .CreateLogger());
```

### 10.2. OpenTelemetry (опционально)

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Harbor.Agent")
        .AddSource("Harbor.Llm")
        .AddSource("Harbor.Tool")
        .AddOtlpExporter()  // только если telemetry.enabled=true
    );
```

`System.Diagnostics.ActivitySource` для ручных spans в hot path:
- `Harbor.Agent.RunLoop` — весь цикл
- `Harbor.Llm.Stream` — один LLM-запрос
- `Harbor.Tool.Execute` — одна tool execution
- `Harbor.Compaction.Run` — одна compaction

### 10.3. Metrics

`System.Diagnostics.Metrics` для gauges/counters:
- `harbor.sessions.active` (gauge)
- `harbor.llm.tokens.input` / `harbor.llm.tokens.output` (counter)
- `harbor.llm.latency_ms` (histogram)
- `harbor.tool.executions` (counter, tagged by tool_name, is_error)
- `harbor.compaction.runs` (counter)

## 11. Error handling strategy

| Тип ошибки | Стратегия |
|---|---|
| `LlmProviderError` (429, 500, 503) | Retry с exponential backoff (`Polly`) — 3 попытки, 1s/2s/4s |
| `LlmContextOverflow` | Auto-compaction + retry (один раз, дальше fail) |
| `LlmAuthError` (401, 403) | Fail сразу, попросить пользователя обновить API key |
| `LlmAborted` (CancellationToken) | Тихо, без error event — штатная отмена |
| `ToolExecutionError` | Записать как tool_result с `isError=true`, отдать LLM — он сам разберётся |
| `ToolValidationError` (schema mismatch) | То же — `isError=true` с описанием ошибки |
| `PluginError` | Логировать, deactivate plugin, продолжить работу без него |
| `PersistenceError` | Логировать как critical, предложить пользователю export текущей сессии |

## 12. Startup sequence (порядок инициализации)

```
1. Parse CLI args (ConsoleAppFramework)
2. Load configuration (file → env → args)
3. Configure logging
4. Build DI container
5. Run DB migrations (если впервые)
6. Load plugins (~/.harbor/plugins/*.dll)
   ├─ Resolve dependencies
   ├─ Call IPlugin.Initialize()
   └─ Register tools/agents/providers
7. Connect MCP servers (если настроены)
   └─ Parallel, с timeout 5s каждый
8. Restore last session (если TUI mode и есть флаг --resume)
9. Start TUI render loop
10. Start CLI input loop
11. Wait for user input
```

**Critical**: шаги 1–8 должны занимать <50ms суммарно. Метрики:
- ConsoleAppFramework parse: ~1ms
- Config load: ~5ms
- DI build: ~5ms
- DB migrations (no-op если уже applied): ~2ms
- Plugin load (если 2–3 плагина): ~10ms
- MCP connect (parallel, 5s timeout): 0–5s (но не блокирует TUI)

MCP connect запускается в background — TUI может показать "MCP servers connecting..." и продолжить работу.

## 13. Shutdown sequence

```
1. CancellationTokenSource.Cancel() — отмена всех in-flight операций
2. Wait for AgentLoop to finish (или timeout 5s)
3. Flush pending DB writes
4. Disconnect MCP servers (send proper shutdown)
5. Stop LSP servers (если есть)
6. Unload plugins (если collectible ALC)
7. Restore terminal state (raw mode off, show cursor, alt screen exit)
8. Dispose DI container
9. Exit
```

**Hard timeout** 10s — если что-то зависло, kill процесс.

---

**Next**: `02-plugins.md` — детально про plugin contract, discovery, загрузку, изоляцию.
