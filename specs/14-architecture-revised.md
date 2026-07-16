# 14 — Revised Architecture (Event Bus + Process Split)

> Документ: обновлённая архитектура после feedback loop. **Главное изменение**: TUI и Core в разных процессах, общаются через Unix domain sockets по NDJSON event protocol. TUI под JIT (Terminal.Gui v2), Core под NativeAOT. Это снимает главное ограничение и даёт массу преимуществ.

> **Этот документ supersedes части `01-architecture.md` §1, §3, §7, `07-tui.md` §2.** Остальные разделы остаются в силе.

## 1. Что изменилось vs исходная спека

| Аспект | Было (v1) | Стало (v2) |
|---|---|---|
| Process model | Single-process | **Two-process: Core (AOT) + TUI (JIT)** |
| TUI фреймворк | Custom ANSI wrapper | **Terminal.Gui v2** (markdown, inline mode) |
| Plugin loading под AOT | Out-of-process plugin-host | **SharpTS (TS→IL) + Out-of-process + native libs** |
| Storage | SQLite primary | **JSONL primary, SQLite опционально через `ISessionStore`** |
| Provider config | Hardcoded models catalog | **`modelsUrl` + dynamic fetch + cache** |
| UI plugins | Не было | **TUI plugins через event bus** |
| Streaming markdown | Custom ANSI | **McGugan pattern** (block-splitting + last-block parse + coalescing) |

## 2. High-level архитектура

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          HARBOR PROCESS TOPOLOGY                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   ┌─────────────────────────────┐         ┌───────────────────────────┐    │
│   │     HARBOR CORE (AOT)       │         │      HARBOR TUI (JIT)     │    │
│   │     ~5 MB binary            │         │      ~80 MB process       │    │
│   │     <30 MB RSS              │         │      (Terminal.Gui v2)    │    │
│   │                             │         │                           │    │
│   │  ┌─────────────────────┐    │         │  ┌─────────────────────┐  │    │
│   │  │ AgentLoop           │    │  NDJSON │  │ StreamingRenderer   │  │    │
│   │  │ ToolRegistry        │◄───┼─────────┼──│ (markdown, diff)    │  │    │
│   │  │ ProviderRegistry    │    │  UDS    │  │ SlashCommandRouter  │  │    │
│   │  │ SessionStore (JSONL)│    │         │  │ InputHandler        │  │    │
│   │  │ CompactionService   │    │         │  │ StatusBar           │  │    │
│   │  │ ExtensionHost       │    │         │  │ ChatHistoryView     │  │    │
│   │  │ EventBus            │    │         │  └─────────────────────┘  │    │
│   │  └─────────────────────┘    │         │                           │    │
│   │           │                 │         │  ┌─────────────────────┐  │    │
│   │           ▼                 │         │  │ TUI Plugins (JIT)   │  │    │
│   │  ┌─────────────────────┐    │         │  │ - cost-dashboard    │  │    │
│   │  │ LLM Provider        │    │         │  │ - diff-viewer       │  │    │
│   │  │ (Anthropic, OpenAI) │    │         │  │ - file-tree         │  │    │
│   │  └─────────────────────┘    │         │  └─────────────────────┘  │    │
│   └─────────────────────────────┘         └───────────────────────────┘    │
│                       │                                │                   │
│                       │                                │                   │
│                       ▼                                ▼                   │
│              ┌──────────────────┐         ┌──────────────────────┐         │
│              │  JSONL Sessions  │         │  User Terminal       │         │
│              │  ~/.harbor/sess/ │         │  (iTerm2/WinTerm/    │         │
│              └──────────────────┘         │   Ghostty/etc.)      │         │
│                                             └──────────────────────┘         │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.1. Process boundary

**Harbor Core** (NativeAOT binary, ~5 MB):
- Запускается: `harbor-core serve` (foreground) или `harbor-core --daemon` (background).
- Отвечает за: agent loop, LLM streaming, tool execution, session persistence, compaction.
- Streaming events пушит в EventBus → мультиплексируется на подключённых клиентов.
- Может работать без TUI вообще (headless mode для CI/IDE integration).

**Harbor TUI** (JIT, Terminal.Gui v2):
- Запускается: `harbor` (launcher, который spawn'ит core если не запущен, потом подключается).
- Отвечает за: rendering, user input, slash-commands.
- Подписывается на events от core, отправляет user actions.
- Может быть закрыт без потери сессии — core продолжает работать.

### 2.2. Wire protocol — NDJSON over Unix Domain Socket

```
Core ↔ TUI: NDJSON (one JSON object per line)
Socket: ~/.harbor/runtime/core.sock (Unix) / \\.\pipe\harbor-core (Windows)
```

**Message types** (typed contract, source-gen JSON):

```csharp
// Core → TUI events
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(MessageUpdateEvent), "message_update")]  // token streamed
[JsonDerivedType(typeof(MessageEndEvent), "message_end")]
[JsonDerivedType(typeof(ToolExecutionStartEvent), "tool_execution_start")]
[JsonDerivedType(typeof(ToolExecutionEndEvent), "tool_execution_end")]
[JsonDerivedType(typeof(CompactionStartedEvent), "compaction_started")]
[JsonDerivedType(typeof(CompactionCompletedEvent), "compaction_completed")]
[JsonDerivedType(typeof(SessionStatsEvent), "session_stats")]
[JsonDerivedType(typeof(AgentEndEvent), "agent_end")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record CoreEvent;

// TUI → Core commands
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PromptCommand), "prompt")]
[JsonDerivedType(typeof(SteerCommand), "steer")]
[JsonDerivedType(typeof(CancelCommand), "cancel")]
[JsonDerivedType(typeof(SwitchModelCommand), "switch_model")]
[JsonDerivedType(typeof(SwitchAgentCommand), "switch_agent")]
[JsonDerivedType(typeof(CompactionTriggerCommand), "compact")]
[JsonDerivedType(typeof(SessionCommand), "session")]  // list/resume/fork
[JsonDerivedType(typeof(ToolPermissionResponse), "permission_response")]
public abstract record TuiCommand;

// Auth handshake
public sealed record HelloEvent(string ProtocolVersion, string CoreVersion, IReadOnlyList<string> Capabilities);
public sealed record AttachRequest(string? SessionId, int? ScrollbackLines);
public sealed record AttachResponse(string SessionId, DateTimeOffset AttachedAt, IReadOnlyList<CoreEvent> Scrollback);
```

**Пример стрима** (одно сообщение в строке):

```jsonl
{"$type":"hello","protocolVersion":"1.0","coreVersion":"0.1.0","capabilities":["streaming","scrollback","multi-session"]}
{"$type":"attach_response","sessionId":"abc","attachedAt":"2026-07-16T10:30:00Z","scrollback":[...]}
{"$type":"message_start","messageId":"msg1","role":"assistant"}
{"$type":"message_update","messageId":"msg1","event":{"type":"text_delta","id":"0","delta":"Hello"}}
{"$type":"message_update","messageId":"msg1","event":{"type":"text_delta","id":"0","delta":", "}}
{"$type":"message_update","messageId":"msg1","event":{"type":"text_delta","id":"0","delta":"world!"}}
{"$type":"message_end","messageId":"msg1","usage":{"inputTokens":50,"outputTokens":3}}
```

### 2.3. Late-attach с scrollback replay

Core хранит ring buffer последних N events в памяти (default 1000). При attach нового TUI клиента:

1. TUI шлёт `AttachRequest { sessionId, scrollbackLines: 200 }`.
2. Core отдаёт `AttachResponse` с N последними `MessageEndEvent` + `ToolExecutionEndEvent` (без deltas — они уже в message).
3. TUI рендерит scrollback, потом начинает получать live events.

Это позволяет:
- Закрыть TUI, открыть через час — получить историю.
- Несколько TUI клиентов одновременно (terminal + IDE).
- Crash recovery — TUI крашнулся, core работает, переоткрываем TUI.

### 2.4. Backpressure

LLM может стримить быстрее, чем TUI рендерит. Решение:

- В Core: `Channel<CoreEvent>` с `BoundedChannelOptions { Capacity = 1000, FullMode = BoundedChannelFullMode.DropOldest }` для token deltas (терять промежуточные токены OK — финальный `MessageEnd` всё равно доставит полный текст).
- В TUI: token coalescing (McGugan optimization #4) — буфер 16ms, потом flush.

Если TUI совсем отстал — core шлёт `BackpressureWarning`, TUI может показать "rendering is behind, simplifying..." и переключиться в plain-text mode.

## 3. Process orchestration

### 3.1. `harbor` launcher

`harbor` binary (NativeAOT, маленький) — это launcher, который:

```csharp
// harbor (launcher)
public static async Task<int> Main(string[] args)
{
    // 1. Check if core is running
    var coreRunning = await CheckCoreRunningAsync();
    
    if (!coreRunning)
    {
        // 2. Spawn core as daemon
        var coreProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "harbor-core",
            Arguments = "serve --daemon",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        
        // 3. Wait for socket
        await WaitForCoreSocketAsync(timeout: TimeSpan.FromSeconds(5));
    }
    
    // 4. Spawn TUI (JIT process)
    var tuiProcess = Process.Start(new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"harbor-tui.dll --socket={CoreSocketPath}",
        // OR if TUI is published as self-contained:
        // FileName = "harbor-tui"
    });
    
    // 5. Wait for TUI to exit
    await tuiProcess.WaitForExitAsync();
    
    // 6. Optionally shutdown core if no other clients
    if (await ShouldShutdownCoreAsync())
    {
        await ShutdownCoreAsync();
    }
    
    return tuiProcess.ExitCode;
}
```

### 3.2. Modes

| Command | Что запускается |
|---|---|
| `harbor` | Launcher → spawn core (if not running) → spawn TUI |
| `harbor ask "prompt"` | Launcher → spawn core (if not running) → stream to stdout, no TUI |
| `harbor serve` | Just core, no TUI (for IDE/CI) |
| `harbor tui` | Just TUI, attach to running core (or error if not running) |
| `harbor --headless` | Core in background, expose HTTP API for remote clients |

### 3.3. Core lifecycle

```
harbor-core serve
  ├─ Load config
  ├─ Initialize DI
  ├─ Run DB migrations (if SQLite backend)
  ├─ Load plugins (~/.harbor/plugins/)
  ├─ Start EventBus
  ├─ Start Unix socket server
  ├─ Start HTTP server (optional, for remote clients)
  ├─ Wait for client connections
  │   ├─ Accept connection
  │   ├─ Authenticate (if needed)
  │   ├─ Send HelloEvent
  │   ├─ Receive AttachRequest
  │   ├─ Send AttachResponse with scrollback
  │   ├─ Multiplex events to this client
  │   └─ Receive commands from this client
  └─ On shutdown signal:
      ├─ Cancel all in-flight LLM streams
      ├─ Flush sessions
      ├─ Disconnect clients gracefully
      └─ Exit
```

### 3.4. TUI lifecycle

```
harbor-tui --socket=/path/to/core.sock
  ├─ Connect to core socket
  ├─ Send AttachRequest { sessionId: null, scrollbackLines: 200 }
  ├─ Receive AttachResponse
  ├─ Initialize Terminal.Gui v2
  ├─ Start event listener task (reads from socket, updates state)
  ├─ Start input handler (reads keys, sends commands)
  ├─ Run Terminal.Gui main loop
  └─ On exit:
      ├─ Send DetachRequest
      ├─ Close socket
      └─ Exit (core keeps running if other clients)
```

## 4. Event Bus implementation

### 4.1. In-core EventBus

```csharp
public sealed class CoreEventBus
{
    private readonly List<IEventSubscriber> _subscribers = new();
    private readonly Channel<CoreEvent> _historyBuffer;
    private readonly object _subscribersLock = new();
    
    public CoreEventBus()
    {
        _historyBuffer = Channel.CreateBounded<CoreEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
        
        // Background task: drain history buffer to disk (for crash recovery)
        _ = Task.Run(PersistHistoryAsync);
    }
    
    public async Task PublishAsync(CoreEvent evt, CancellationToken ct)
    {
        // 1. Add to history buffer
        _historyBuffer.Writer.TryWrite(evt);
        
        // 2. Fan-out to all subscribers
        List<IEventSubscriber> snapshot;
        lock (_subscribersLock)
            snapshot = _subscribers.ToList();
        
        foreach (var sub in snapshot)
        {
            try { await sub.SendAsync(evt, ct); }
            catch { /* mark subscriber as dead, will be removed */ }
        }
    }
    
    public IDisposable Subscribe(IEventSubscriber subscriber)
    {
        lock (_subscribersLock)
            _subscribers.Add(subscriber);
        
        return new Subscription(() =>
        {
            lock (_subscribersLock)
                _subscribers.Remove(subscriber);
        });
    }
    
    public IReadOnlyList<CoreEvent> GetScrollback(int maxEvents)
    {
        // Read from in-memory ring buffer
        return _historyBuffer.Reader.ReadAllAsync(CancellationToken.None)
            .ToBlockingEnumerable()
            .TakeLast(maxEvents)
            .ToList();
    }
}

public interface IEventSubscriber
{
    Task SendAsync(CoreEvent evt, CancellationToken ct);
}

public sealed class SocketEventSubscriber : IEventSubscriber
{
    private readonly StreamWriter _writer;
    
    public async Task SendAsync(CoreEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, CoreEventContext.Default.CoreEvent);
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync(ct);
    }
}
```

### 4.2. Crash recovery

Core пишет все events в `~/.harbor/runtime/events.log` (append-only JSONL). При рестарте core после crash:

1. Читает `events.log` с последнего checkpoint.
2. Восстанавливает in-memory state (current sessions, in-flight tool calls, etc.).
3. Маркирует in-flight операции как `InterruptedByCrash` (клиент видит предупреждение).
4. Продолжает работу.

```csharp
public sealed class EventPersistenceService
{
    private readonly string _logPath = "~/.harbor/runtime/events.log";
    private readonly Channel<CoreEvent> _writeQueue;
    
    public EventPersistenceService()
    {
        _writeQueue = Channel.CreateBounded<CoreEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        
        _ = Task.Run(WriteLoopAsync);
    }
    
    public async Task PersistAsync(CoreEvent evt) => 
        await _writeQueue.Writer.WriteAsync(evt);
    
    private async Task WriteLoopAsync()
    {
        using var writer = new StreamWriter(_logPath, append: true);
        await foreach (var evt in _writeQueue.Reader.ReadAllAsync())
        {
            var json = JsonSerializer.Serialize(evt, CoreEventContext.Default.CoreEvent);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
        }
    }
    
    public async Task<IReadOnlyList<CoreEvent>> RecoverAsync(DateTimeOffset since)
    {
        if (!File.Exists(_logPath)) return Array.Empty<CoreEvent>();
        
        var events = new List<CoreEvent>();
        using var reader = new StreamReader(_logPath);
        
        while (await reader.ReadLineAsync() is { } line)
        {
            try
            {
                var evt = JsonSerializer.Deserialize(line, CoreEventContext.Default.CoreEvent);
                if (evt is { } e && e.Timestamp >= since)
                    events.Add(e);
            }
            catch { /* skip malformed */ }
        }
        
        return events;
    }
}
```

## 5. Влияние на NativeAOT

### 5.1. Что можно теперь убрать из AOT-ограничений

| Что | Раньше | Теперь |
|---|---|---|
| TUI rendering | Custom ANSI wrapper (AOT) | Terminal.Gui v2 (JIT, в отдельном процессе) |
| Markdown rendering | Свой ANSI renderer (AOT) | Markdig + Terminal.Gui markdown (JIT) |
| Input handling | Raw `Console.ReadKey` (AOT) | Terminal.Gui input (JIT) |
| Plugin reflection для UI | Не поддерживалось | **Поддерживается** (JIT процесс) |
| Dynamic config for TUI | Не поддерживалось | Поддерживается (JIT процесс) |

### 5.2. Что осталось в AOT-ядре

Core всё ещё под NativeAOT, потому что:
- Lean binary (~5 МБ).
- Fast cold start (<30ms).
- Low RSS (<30 МБ).
- No reflection overhead.

В core НЕТ:
- UI кода.
- Markdown rendering.
- Terminal.Gui.
- Spectre.Console.

В core ЕСТЬ:
- `Microsoft.Extensions.AI` (LLM abstraction).
- `System.Text.Json` source-gen.
- `Microsoft.Extensions.DependencyInjection`.
- `System.Threading.Channels`.
- `System.Net.ServerSentEvents` (.NET 10).
- JSONL session storage.
- Plugin loading (через SharpTS or out-of-process).

### 5.3. SharpTS plugins под AOT

SharpTS компилит TS в IL. Под AOT в core-процессе — НЕ работает (нужен JIT для IL emit). НО:

**Решение**: SharpTS-compiled плагины грузятся в **TUI процессе** (JIT). Core-процесс делегирует UI-related plugin calls в TUI. Tool-related plugin calls (которые не требуют UI) — через out-of-process plugin-host.

```
TS plugin → sharpts --compile → plugin.dll
                                 │
                ┌────────────────┴────────────────┐
                │                                 │
        TUI process (JIT)              Core process (AOT)
        ──────────────────              ─────────────────
        UI extensions                   Tool execution
        Custom views                    (через out-of-process host)
        Slash-commands
```

Это разделяет plugin use cases:
- **UI plugins** (custom views, status bar items, slash-commands) → TUI процесс, SharpTS-loaded.
- **Tool plugins** (read/write/bash/ai-tools) → out-of-process plugin-host (для AOT-core).
- **Provider plugins** (custom LLM clients) → in-core (если pure C#) или out-of-process (если требует reflection).

## 6. Влияние на storage

### 6.1. JSONL-first

См. обновлённый `05-sessions.md`. Кратко:

- **MVP**: JSONL-only. Append-only, atomic, git-friendly, no native deps.
- **v0.3**: SQLite как опциональный backend через `ISessionStore`.
- **v0.4**: Любой storage (LanceDB, PostgreSQL, etc.) через plugin.

Core читает/пишет JSONL. TUI не обращается к storage напрямую — только через core events.

### 6.2. `ISessionStore` interface

```csharp
public interface ISessionStore
{
    Task<Session> CreateAsync(string directory, string agentName, string modelId, CancellationToken ct);
    Task<Session?> GetAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<Session>> ListAsync(string? projectId = null, CancellationToken ct = default);
    Task AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct);
    Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
    Task<SessionStats> GetStatsAsync(string sessionId, CancellationToken ct);
}

// Реализации:
// - JsonlSessionStore (default, MVP)
// - SqliteSessionStore (v0.3, опционально)
// - PostgresSessionStore (future, plugin)
```

Выбор в config:

```jsonc
{
  "sessions": {
    "storage": "jsonl",  // "jsonl" | "sqlite" | "postgres"
    "path": "~/.harbor/sessions"
  }
}
```

## 7. Влияние на providers

См. новый `15-providers-dynamic.md`. Кратко:

- Builtin providers (Anthropic, OpenAI, Google, Ollama) — special cases, в core.
- Generic `openai-compatible` adapter — для десятков провайдеров без кода.
- `modelsUrl` — динамический fetch + cache списка моделей.
- Provider config — JSON файлы в `~/.harbor/providers/` или `.harbor/providers/`.

## 8. Влияние на TUI plugins

См. обновлённый `07-tui.md` §10. Кратко:

- TUI plugins — отдельная категория, грузятся в TUI процессе (JIT).
- Контракт: `ITuiPlugin` + `TuiPluginContext`.
- Доступ к: EventBus, Views, Commands, StatusBar, ChatHistory.
- SharpTS-loaded (TS plugins) или AssemblyLoadContext (C# plugins).

## 9. Performance budget — updated

| Operation | Latency | Где |
|---|---|---|
| LLM API → SSE chunk | ~30 ms | Network |
| Core parse + LLMEvent | <1 ms | Core (AOT) |
| UDS write (NDJSON) | ~50 µs | IPC |
| TUI read | <1 ms | TUI (JIT) |
| Token coalescing (16ms buffer) | 0-16 ms | TUI |
| Markdown render (last block) | <2 ms | TUI |
| Terminal flush | ~1 ms | OS |
| **TOTAL** | **<50 ms** | |

Token-to-screen budget 20ms — выполним с запасом. Без coalescing — <35ms.

## 10. Memory budget — updated

| Component | RSS | Process |
|---|---|---|
| Harbor Core (AOT, idle) | 15-25 MB | core |
| Harbor Core (active, 10K msg) | 30-50 MB | core |
| Harbor TUI (JIT, idle) | 60-80 MB | tui |
| Harbor TUI (active, markdown rendering) | 80-120 MB | tui |
| **TOTAL active** | **110-170 MB** | both |

Сравнение с kilocode (~700-1100 MB) — **в 5-10 раз меньше**.
Сравнение с crush (~100-200 MB) — **сопоставимо**, но с richer UI (markdown, inline mode, plugins).

## 11. Distribution

### 11.1. Single launcher

`harbor` (NativeAOT, ~3 MB) — launcher, который spawn'ит остальные процессы.

### 11.2. Core binary

`harbor-core` (NativeAOT, ~5-7 MB) — основное ядро. Может работать standalone (headless mode).

### 11.3. TUI binary

`harbor-tui` — два варианта:
- **Self-contained JIT**: ~80 MB (включает .NET runtime + Terminal.Gui). Простой distribution.
- **Framework-dependent**: ~5 MB, требует .NET 10 runtime установленного. Лёгкий, но требует runtime.

**Рекомендация**: self-contained для простоты. 80 MB на disk — нормально, главное что в RAM ~80 MB, не 1 GB.

### 11.4. NuGet distribution

```bash
dotnet tool install -g harbor
# Ставит launcher + core (AOT) + TUI (self-contained)
```

Multi-platform RIDs в одном NuGet package:
- `linux-x64`, `linux-arm64`
- `osx-x64`, `osx-arm64`
- `win-x64`, `win-arm64`

## 12. Migration path от v1 спеки

Если уже начал реализацию по v1 спеке (single-process):

1. **Шаг 1**: Вынести `EventBus` в отдельный сервис. Все `AgentEvent` публикуются в bus.
2. **Шаг 2**: Создать `IUserInterface` interface, текущий TUI — одна из реализаций.
3. **Шаг 3**: Добавить `SocketUserInterface` — реализация через UDS.
4. **Шаг 4**: Вынести TUI в отдельный проект, запускаемый отдельно.
5. **Шаг 5**: Core переключить на `SocketUserInterface` вместо direct rendering.

Это можно делать инкрементально, не ломая существующий код.

## 13. Что осталось unchanged из v1

- `00-overview.md` — цели, KPI (но target RSS updated: ~150 MB total vs 30 MB).
- `03-providers.md` — LLM abstraction, SSE parsing (но добавляется generic adapter в `15-providers-dynamic.md`).
- `04-tools.md` — tool contract, tool-calling loop, permission model.
- `05-sessions.md` — compaction, branching, snapshot/revert (но storage swapped: JSONL primary).
- `06-mcp.md` — MCP client (но как plugin, optional).
- `08-native-aot.md` — AOT constraints (но применяются только к core).
- `09-benchmarks.md` — бенчмарки (но добавляется TUI process overhead).
- `10-repo-analysis.md` — анализ реп (но добавляются XenoAtom, termina).
- `11-risks.md` — risks (но некоторые снимаются).
- `12-roadmap.md` — roadmap (но milestones updated).
- `13-questions-and-answers.md` — Q&A (но добавляются новые ответы).

## 14. Decision matrix — почему эта архитектура

| Критерий | Single-process AOT | **Two-process (AOT core + JIT TUI)** | Single-process JIT |
|---|---|---|---|
| Cold start | ✅ <30ms | ✅ <50ms (core <30ms + TUI spawn ~20ms) | ❌ ~200ms |
| RSS idle | ✅ <30 MB | ⚠️ ~80 MB (TUI dominates) | ❌ ~100 MB |
| Markdown UI | ❌ нет | ✅ Terminal.Gui v2 builtin | ✅ Terminal.Gui v2 |
| Inline mode (scroll) | ❌ нет | ✅ Terminal.Gui v2 | ✅ |
| TUI plugins | ❌ ограничено | ✅ full SharpTS + reflection | ✅ |
| AOT core benefits | ✅ full | ✅ full (core only) | ❌ none |
| Crash isolation | ❌ TUI crash = core crash | ✅ independent | ❌ |
| Multi-client | ❌ | ✅ multiple TUI + IDE + web | ❌ |
| Implementation complexity | medium | medium-high (wire protocol) | low |
| Memory on 10K msgs | ✅ <80 MB | ✅ <150 MB | ⚠️ ~200 MB |
| Distribution size | ✅ ~5 MB | ⚠️ ~85 MB (TUI self-contained) | ⚠️ ~80 MB |

**Вердикт**: two-process architecture — лучший компромисс. Чуть сложнее в implementation, но даёт markdown UI, inline mode, TUI plugins, crash isolation, multi-client. Memory overhead TUI процесса (~80 MB) — acceptable, потому что core остаётся lean.

## 15. Risks — updated

| Risk | v1 оценка | v2 оценка | Change |
|---|---|---|---|
| R-T01: AOT incompatibility | Medium | **Low** | TUI больше не под AOT |
| R-T02: Plugin isolation under AOT | High | **Medium** | SharpTS в TUI, out-of-process для tool plugins |
| R-T03: Spectre edge-cases | Low | **N/A** | Не используем Spectre |
| R-T07: TUI perf | Medium | **Low** | Terminal.Gui v2 + streaming markdown |
| R-T08: Cross-platform Windows | High | **Medium** | Terminal.Gui v2 better tested |
| R-T11: Compaction quality | Medium | **Medium** | unchanged |
| R-P01: Scope creep | High | **Medium** | wire protocol добавляет работы, но не фичами |
| **NEW**: Wire protocol complexity | N/A | **Medium** | NDJSON + UDS, ~1-2 недели |
| **NEW**: Process orchestration bugs | N/A | **Medium** | Daemon management, port conflicts |

## 16. Updated MVP scope

| Фича | v1 MVP | **v2 MVP** |
|---|---|---|
| Core (AOT) | ✅ | ✅ |
| TUI (custom ANSI) | ✅ | ❌ |
| TUI (Terminal.Gui v2, JIT) | ❌ | ✅ |
| Wire protocol (NDJSON over UDS) | ❌ | ✅ |
| JSONL sessions | ❌ | ✅ |
| SQLite sessions | ✅ | ❌ (v0.3) |
| Anthropic + OpenAI providers | ✅ | ✅ |
| Generic OpenAI-compatible adapter | ❌ | ✅ |
| `modelsUrl` dynamic fetch | ❌ | ✅ |
| Streaming markdown renderer | ❌ | ✅ (McGugan pattern) |
| Slash-commands | basic | ✅ |
| Permission system | ✅ | ✅ |
| Compaction (basic) | ✅ | ✅ |
| 5 builtin tools | ✅ | ✅ |
| TUI plugins | ❌ | ❌ (v0.4) |
| SharpTS plugin loading | ❌ | ❌ (v0.4) |
| MCP | ❌ | ❌ (v0.5) |
| LSP | ❌ | ❌ (v0.6) |

MVP всё ещё 4-6 недель, но с richer UI.

---

**Next**: `15-providers-dynamic.md` — детально про dynamic provider config + `modelsUrl` + generic adapter.
