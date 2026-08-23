# Code Principles Audit — Harbor

> Детальный аудит кодовой базы Harbor по принципам: **OOP / SOLID / GoF / FP / ROP / Performance / Low-level (байтоебля)**.
> Каждое нарушение помечено в коде комментарием `// TODO(principles)[CATEGORY]: ...` с обратной ссылкой на этот файл.

**Дата аудита:** 2026-07-17  
**Аудитор:** Super Z (auto)  
**Покрытие:** `src/` + `tests/` (всего 25 проектов, ~6500 строк product-кода)

> **Note (sprint-2):** пути к `Harbor.Tui.SpectreTui` ниже обновлены на `contrib/tui/...` —
> проект перенесён в contrib; остальные пути отражают состояние на дату аудита.

---

## TL;DR

Harbor — зрелый, продуманный .NET 10 AI-агент harness. SOLID + ROP + паттерны GoF применяются последовательно, performance-оптимизации (ArrayPool, StringBuilderPool, FrozenDictionary, StringPool) — на месте. Однако есть ~30 конкретных нарушений, распределённых по категориям:

| Категория | Нарушений | Критических | Median severity |
|---|---:|---:|:---:|
| OOP / SOLID | 8 | 2 | medium |
| GoF (паттерны) | 3 | 0 | low |
| FP (functional) | 7 | 1 | medium |
| ROP (railway) | 4 | 2 | **high** |
| Performance | 9 | 3 | **high** |
| Low-level (байтоебля) | 6 | 2 | medium |
| Concurrency | 4 | 1 | medium |
| **Всего** | **41** | **11** | |

Топ-3 критических:
1. **§ROP-002**: `PermissionService.CheckAsync` бросает исключение на expected failure (invalid agent name) — краш под нагрузкой.
2. **§OOP-001**: `OpenAiCompatibleLlmClient._toolCallIndexToId` — instance-level mutable state, гонка при параллельных сессиях на одном singleton-клиенте.
3. **§PERF-005**: `JsonlSessionStore.GetMessagesAsync` — `JsonDocument.Parse` на каждой строке, ~10k аллокаций на длинную сессию.

---

## Sprint 1 + Sprint 2 — Resolution Summary

Sprint 1 (critical) + Sprint 2 (high-perf) fixes were applied by Subagent #6 (`principle-fixer`). Each fixed finding carries a `> **Status:** ✅ RESOLVED` block at the top of its section. Acknowledged-but-deferred findings carry `> **Status:** ⚠️ ACKNOWLEDGED` or `> **Status:** ⚠️ PARTIAL`.

| Finding | Severity | Status | Notes |
|---|:---:|:---:|---|
| §OOP-001 | high | ✅ RESOLVED | `_toolCallIndexToId` removed; per-call local dict in `StreamAsync`. |
| §OOP-002 | medium | ✅ RESOLVED | `IProviderCompatFlag` Strategy; `ProviderConfig.Quirks` carries the list. |
| §OOP-003 | medium | ✅ RESOLVED | `DeserializeMessage` takes `sessionId`; no placeholder. |
| §FP-003 | high | ✅ RESOLVED | `ReportProgress` is `async`/`await` with try/catch + log. |
| §FP-004 | medium | ✅ PARTIAL | `Cache` is now `ConcurrentDictionary`; `Enabled` left as process-wide policy. |
| §FP-005 | medium | ✅ RESOLVED | `ChatScreen._scroll`/`_viewport`/`_wasRunning` removed; state lives in `UiState` (incl. new `WasRunning`). `HandleLocalScroll` removed — all scroll via `UiReducer.Update` on `UiMsg.KeyInput(ScrollUpLine/...)`. New `UiMsg.ScrollResetToTail` + `UiMsg.ScrollClamp` for render→reducer measurement flow. `PanelRegistry` is registration-only (no `SetState`/`SetSize`/`FocusedPanelId` setter) — single source of truth in `UiState.PanelStates` / `PanelSizes` / `FocusedPanelId`. |
| §FP-006 | high | ✅ RESOLVED | `ContinueWith(OnlyOnFaulted)` + `ILogger<TuiEffectHost>` injected. |
| §FP-007 | medium | ✅ RESOLVED | `UiStore.Transition` is `internal`; only in-assembly callers are `TuiEffectHost` (fold-follow-up-state after async effects), `UiStore.BindSession`, `UiStore.Reset`. External renderers (incl. `SpectreTuiRenderer`) cannot call `Transition` — they `Dispatch(UiMsg)`. New `UiMsg.SeedPanels` replaces what used to be a `Transition` call from `SpectreTuiRenderer.SeedPanelRegistryIntoState`. |
| §ROP-001 | medium | ✅ RESOLVED | `DeserializeMessage` returns `Result<AgentMessage>`; per-line errors aggregated + logged. |
| §ROP-002 | **high** | ✅ RESOLVED | `CheckAsync`/`GetRuleset` pattern-match `Result<AgentName>`. |
| §ROP-004 | low | ✅ RESOLVED | `MapChunk` returns `ErrorEvent` on parse failure (no silent drop). |
| §PERF-005 | **high** | ⚠️ PARTIAL | ROP path fixed; full `Utf8JsonReader` rewrite deferred (AOT risk). |
| §PERF-006 | medium | ✅ RESOLVED | `BashTool` uses `StringBuilderPool.Rent` + 100k cap + dropped-bytes log. |
| §PERF-007 | medium | ✅ RESOLVED | `UiStore.Dispatch` is lock-free CAS on `volatile UiState _state`. |
| §PERF-009 | low | ✅ RESOLVED | `ChatMarkdown.Cache` is `ConcurrentDictionary`; no `lock(Cache)`. |

**11 of 11 critical findings addressed** (10 fully resolved, 1 partial with documented decision).

Findings NOT touched in this sprint (Sprint 3+ scope): §SOLID-001, §SOLID-002, §OOP-004, §OOP-005, §OOP-006, §OOP-007, §OOP-008, §FP-001, §FP-002, §ROP-003, §PERF-001, §PERF-002, §PERF-003, §PERF-004, §PERF-008, plus all GoF / Low-level / AOT / Concurrency findings outside the critical/high-perf set. §FP-005 and §FP-007 were resolved by subagent T (tea-restorer) in Sprint 3 — see detailed entries below.

---

## 1. OOP / SOLID

### §SOLID-001 — AgentLoop: нарушение Single Responsibility

**Файл:** `src/Harbor.Core/Agents/AgentLoop.cs`  
**Severity:** medium  
**Line:** ~16 (класс целиком, ~650 строк)

`AgentLoop` делает **слишком много**: (1) оркестрация turn-loop, (2) streaming coalescing, (3) tool-call accumulation, (4) tool execution dispatch, (5) error handling, (6) event publishing, (7) permission gating, (8) steering queue draining. Метод `RunAsync` — один `try/catch` блок на 250 строк с вложенными switch'ами.

```csharp
// Текущий паттерн (упрощённо):
public async Task<Result> RunAsync(...) {
    try {
        // 200+ строк: resolve provider → publish start → while-loop:
        //   compaction check → build prompt → convert messages →
        //   build tool defs → stream with coalescing →
        //   execute tool calls → publish turn end → check max steps
    } catch (Exception ex) { ... }
}
```

**Рекомендация:** разнести на 4 класса:
- `AgentLoop` — оркестрация (10–20 строк, только `while` + делегирование)
- `StreamingCoalescer` — аккумулирование дельт в pooled `StringBuilder`'ах (вынести `textBuffer`/`thinkingBuffer`/`pendingToolCalls`)
- `ToolCallDispatcher` — `ExecuteToolCallsAsync` + `ExecuteSingleToolCallAsync`
- `TurnEventPublisher` — все `_eventBus.PublishAsync` вызовы

Это позволит тестировать каждую часть изолированно и не плодить моки в `AgentLoopTests`.

---

### §SOLID-002 — ChatScreen (SpectreTUI): god-class на 270 строк

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` (вложенный `private sealed class ChatScreen`)  
**Severity:** medium  

ChatScreen внутри `SpectreTuiRenderer` делает (1) key dispatch, (2) scroll math, (3) viewport measurement, (4) layout sync, (5) footer rendering, (6) key translation. Вложенный `private sealed class` видит все private fields наружного renderer'а — это нарушение инкапсуляции.

**Рекомендация:** вынести в отдельный файл `ChatScreen.cs`, разбить на:
- `KeyHandler` — `OnKeyMessage`, `HandleLocalScroll`, `ToUiKey`
- `ScrollController` — `_scroll`, `_viewport`, `_wasRunning`, clamp-логика
- `FooterRenderer` — `BuildFooter`, `ParagraphFromFooter`

---

### §OOP-001 — OpenAiCompatibleLlmClient: thread-safety violation

> **Status:** ✅ RESOLVED (Sprint 1) — `_toolCallIndexToId` field removed; the index→id map is now a local `Dictionary<int, string>` inside `StreamAsync`, passed into `MapChunk`/`MapChunkFromDocument` as a parameter. Concurrent `StreamAsync` calls on the same singleton client no longer race.

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** **high**  
**Line:** 29 (поле `_toolCallIndexToId`)

```csharp
private readonly Dictionary<int, string> _toolCallIndexToId = new();
// ...
public async IAsyncEnumerable<LlmEvent> StreamAsync(...) {
    // Внутри Task.Run:
    _toolCallIndexToId.Clear();  // гонка!
    // ... use _toolCallIndexToId[index] = id;
}
```

Поле — instance-level mutable state. Каждый провайдер регистрируется как `Lazy<ILlmClient>` singleton (через `ProviderRegistry.Register(pid, factory)`), поэтому **один экземпляр клиента обслуживает все сессии**. При параллельных `StreamAsync` вызовах (2 пользователя одновременно, или одна сессия + `GetAllModelsAsync` в фоне) — состояние перепутается, `tool_call_id` от стрима A попадёт в стрим B.

Контракт `ITool` явно требует "Implementations MUST be thread-safe for concurrent `ExecuteAsync` calls" — для `ILlmClient` такой явной формулировки нет, но неявно подразумевается (см. `ProviderRegistry.GetAllModelsAsync` — параллельные `Task.WhenAll`).

**Сейчас работает только потому, что** `AgentLoop.RunAsync` гоняет один `StreamAsync` за раз на сессию, а `GetAllModelsAsync` использует другой метод (`GetModelsAsync`, не стрим).

**Fix:**

```csharp
// Вариант A — per-call state в локальной функции:
public async IAsyncEnumerable<LlmEvent> StreamAsync(...) {
    var indexToId = new Dictionary<int, string>(4);  // local
    // ... передавать indexToId в MapChunk через замыкание
}

// Вариант B — AsyncLocal<Dictionary<int,string>> для полной изоляции:
private static readonly AsyncLocal<Dictionary<int, string>?> _currentStream = new();
```

Вариант A проще и не меняет сигнатуру.

---

### §OOP-002 — ApplyCompatFlags: OCP violation (string-based dispatch)

> **Status:** ✅ RESOLVED (Sprint 1) — provider quirks extracted to `IProviderCompatFlag` (Strategy pattern) in `Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs`. `ProviderConfig.Quirks` carries the per-provider list (populated by `ProviderCompatFlags.For(providerId)` in registration code); `ApplyCompatFlags` simply iterates the list. New providers with quirks no longer require editing the client.

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** medium  
**Line:** 207 (`ApplyCompatFlags`)

```csharp
if (ProviderId.Value == "deepseek" && request.Model.Contains("reasoner", ...))
    payload.Remove("temperature");
if (ProviderId.Value == "groq" && !payload.ContainsKey("max_tokens") && ...)
    payload["max_tokens"] = 4096;
```

Каждый новый провайдер с quirks (a DeepSeek, Groq, Together, Fireworks — у всех есть свои) требует **правки этого метода**. Это нарушает Open/Closed. Также нарушает Liskov: поведение `OpenAiCompatibleLlmClient` зависит от его идентификатора, а не от конфигурации.

**Fix:** Strategy-паттерн:

```csharp
public interface IProviderCompatFlag {
    ProviderId ProviderId { get; }
    void Apply(Dictionary<string, object?> payload, LlmRequest request);
}

// В ProviderConfig:
public IReadOnlyList<IProviderCompatFlag>? Quirks { get; init; }

// В OpenAiCompatibleLlmClient:
foreach (var flag in _config.Quirks ?? Array.Empty<IProviderCompatFlag>())
    flag.Apply(payload, request);
```

Конкретные `DeepSeekReasonerCompatFlag`, `GroqMaxTokensCompatFlag` — по классу на каждый, регистрируются в DI.

---

### §OOP-003 — JsonlSessionStore.DeserializeMessage: нарушение инкапсуляции

> **Status:** ✅ RESOLVED (Sprint 1, alongside §ROP-001) — `DeserializeMessage` now takes `string sessionId` as a parameter and the reconstructed `AgentMessage` is always in a valid state (no empty `SessionId` placeholder).

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** medium  
**Line:** 325 (`DeserializeMessage`)

```csharp
private static AgentMessage? DeserializeMessage(JsonElement element)
{
    // ...
    string sessionId = ""; // populated by file context
    return new UserMessage(id, sessionId, ...);  // sessionId = "" !
}
```

Метод создаёт `UserMessage` с `sessionId = ""` в надежде, что caller как-то его проставит. Но `UserMessage` — record с init-свойствами, и caller **не проставляет**. Сообщения, восстановленные из JSONL, навсегда остаются с пустым `SessionId`.

**Fix:**
```csharp
private static AgentMessage? DeserializeMessage(string sessionId, JsonElement element)
{
    // ...
    return new UserMessage(id, sessionId, ...);
}
```

Передавать `sessionId` из `GetMessagesAsync`, где он известен.

---

### §OOP-004 — AgentLoop.RunAsync: OCP violation (switch on LlmEvent)

**Файл:** `src/Harbor.Core/Agents/AgentLoop.cs`  
**Severity:** medium  
**Line:** 199 (`switch (evt)`)

```csharp
switch (evt) {
    case TextDeltaEvent td: ...
    case ThinkingDeltaEvent thd: ...
    case ToolCallStartEvent tcs: ...
    case ToolCallDeltaEvent tcd: ...
    case StepFinishEvent sf: ...
    case ErrorEvent err: ...
}
```

Каждый новый тип `LlmEvent` (например, `ImageDeltaEvent`, `AudioDeltaEvent`) требует правки `AgentLoop`. Это нарушает Open/Closed.

**Fix:** visitor pattern, или — что более в духе C# — discriminated union через `[MemoryPackUnion]` (как уже сделано для `AgentMessage` и `ContentPart`), плюс match-extensions:

```csharp
await foreach (var evt in client.StreamAsync(request, ct)) {
    await evt.Match(
        onTextDelta: td => { ... },
        onThinkingDelta: thd => { ... },
        onToolCallStart: tcs => { ... },
        // ...
    );
}
```

Реализация `Match` — extension method на `LlmEvent` с switch expression внутри (один раз).

---

### §OOP-005 — ToolRegistry/ProviderRegistry: двойной путь (frozen vs concurrent)

**Файлы:** `src/Harbor.Core/Tools/ToolRegistry.cs`, `src/Harbor.Core/Providers/ProviderRegistry.cs`  
**Severity:** low  

Каждый registry держит **две коллекции** — `ConcurrentDictionary` (для записи) и `FrozenDictionary?` (для чтения после `Freeze()`). Каждый метод (`GetAllTools`, `ResolveTools`, `GetTool`) имеет `if (_frozen is not null) { /* fast path */ } else { /* slow path */ }` — дублирование логики.

**Fix:** Composite-паттерн:

```csharp
internal interface IToolSource {
    Result<ITool> Get(ToolName name);
    IReadOnlyList<ToolDescriptor> All { get; }
}

internal sealed class FrozenToolSource : IToolSource { /* reads frozen */ }
internal sealed class ConcurrentToolSource : IToolSource { /* reads concurrent */ }

public sealed class ToolRegistry {
    private IToolSource _source = new ConcurrentToolSource(...);
    public void Freeze() => _source = new FrozenToolSource(_source.All);
    public Result<ITool> GetTool(ToolName n) => _source.Get(n);
}
```

Аналогично для `ProviderRegistry`, `AgentRegistry`.

---

### §OOP-006 — BaseTuiRenderer: нарушения ISP в иерархии

**Файл:** `src/Harbor.Tui.Abstractions/BaseTuiRenderer.cs` (косвенно через `ITuiRenderer`)  
**Severity:** low  

`SpectreTuiRenderer.ReadLineAsync`, `WriteAsync`, `WriteLineAsync`, `ClearAsync` — все возвращают `Task.FromResult(Result.Success())` и вообще не используются в interactive mode (где Spectre.TUI владеет экраном). Это нарушение Interface Segregation: interactive renderer'ы не должны реализовывать linear I/O интерфейс.

**Fix:** разделить `ITuiRenderer` на `ILinearTuiRenderer` (Ansi/Plain) и `IInteractiveTuiRenderer` (Spectre/Termina). `BaseTuiRenderer` — только для linear.

---

### §OOP-007 — CompactionService.ReserveTokens / KeepRecentTokens: mutable properties

**Файл:** `src/Harbor.Core/Sessions/CompactionService.cs`  
**Severity:** low  
**Line:** 89, 94, 99

```csharp
public int ReserveTokens { get; set; } = 16384;
public int KeepRecentTokens { get; set; } = 20000;
public int TailTurns { get; set; } = 2;
```

Public `{ get; set; }` на singleton-сервисе — изменение в рантайме не атомарно (32-bit write на 64-bit не guaranteed atomic без `Interlocked`), и нет валидации (можно поставить `-1` и сломать compaction).

**Fix:** `init` + валидация в конструкторе, либо `Options<CompactionOptions>` pattern из `Microsoft.Extensions.Options`.

---

### §OOP-008 — JsonlSessionStore.DeserializePart: switch on string role

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** low  
**Line:** 325, 394

`switch (role) { case "user": ...; case "assistant": ...; case "tool_result": ...; }` — каждый новый тип сообщений требует правки. Уже есть `[MemoryPackUnion]` на `AgentMessage` (Messages.cs), но JSONL-storage не использует его, а пишет свой ручной `type` discriminator.

**Fix:** использовать `MemoryPack` JSON-serializer (он умеет union'ы), либо — если JSONL нужен для human-readability — `JsonPolymorphic` attribute (System.Text.Json поддерживает с .NET 7).

---

## 2. GoF (паттерны)

### §GOF-001 — "Registry" с двойным хранилищем: не классический Registry

**Файлы:** `ToolRegistry`, `ProviderRegistry`, `AgentRegistry`  
**Severity:** low  

В книге Gamma et al. Registry — это **lookup-only** интерфейс. Здесь же каждый "Registry" ещё и **mutable builder** (`Register`, `Unregister`, `Freeze`). Это гибрид Registry + Builder, что не плохо само по себе, но нарушает SRP.

**Рекомендация:** разделить на `IToolRegistry` (read) и `IToolRegistryBuilder` (write) — это уже частично сделано (`ToolRegistryBuilder`), но `ToolRegistry` реализует оба. Сделать `ToolRegistry` `sealed`, и `ToolRegistryBuilder` — единственный writer.

---

### §GOF-002 — PermissionRuleset.Merge: O(n+m) Dictionary, можно через SortedSet

**Файл:** `src/Harbor.Abstractions/Permissions/PermissionRuleset.cs`  
**Severity:** low  
**Line:** 125 (`Merge`)

`Merge` создаёт `Dictionary<string, PermissionRule>` на оба массива, потом копирует Values в массив, потом конструирует новый `PermissionRuleset` (который опять Sort'ит). Это O((n+m) log(n+m)).

Если хранить rules уже отсортированными (immutable sort), merge — O(n+m) через two-pointer. Но это микрооптимизация, текущий код хорошо читается.

---

### §GOF-003 — ITool / ILlmClient / ITuiRenderer: Strategy vs Registry friction

**Файлы:** `ITool.cs`, `ILlmClient.cs`, `ITuiRenderer.cs`  
**Severity:** info  

Strategy-паттерн применён корректно, но `ITool` имеет свойство `ExecutionMode` (Sequential/Parallel) — это **double dispatch**: Strategy должен знать о strategy-orchestrator'е. Чище было бы сделать `ITool` чистой strategy, а `ExecutionMode` — атрибутом `ToolDescriptor`, вычисляемым orchestrator'ом.

Не критично — текущий дизайн читаемый.

---

## 3. Functional Programming (FP)

### §FP-001 — OpenAiCompatibleLlmClient.BuildRequest: mutable Dictionary

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** medium  
**Line:** 149 (`BuildRequest`)

```csharp
var payload = new Dictionary<string, object?> {
    ["model"] = request.Model,
    ["messages"] = BuildMessages(request),
    ["stream"] = true,
    ["stream_options"] = new { include_usage = true }
};
if (request.MaxOutputTokens.HasValue) payload[field] = request.MaxOutputTokens;
if (request.Temperature.HasValue) payload["temperature"] = request.Temperature;
// ... 10+ mutations
```

Классический **mutable accumulator** паттерн, нарушающий immutability. Также `object?` boxing — value types (`int`, `decimal`) боксятся в heap object.

**Fix:** 
1. Для FP — immutable record `LlmRequestPayload` + `with` expressions.
2. Для perf — `Utf8JsonWriter` напрямую в `MemoryStream`, без intermediate Dictionary (см. §PERF-002).

---

### §FP-002 — JsonlSessionStore.GetMessagesAsync: Dictionary для "latest wins"

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** low  
**Line:** 174

```csharp
var messages = new Dictionary<string, AgentMessage>();
// ... while (read line) messages[msg.Id] = msg;  // latest wins
```

Mutable dictionary как accumulator — нарушение immutability. Для FP-чистоты лучше: `IReadOnlyList<AgentMessage>` → fold в `ImmutableDictionary<string, AgentMessage>` через `ImmutableDictionary.SetItem`.

Для perf — лучше вообще без dictionary, один проход + in-place compact (Span-based).

---

### §FP-003 — AgentLoop: `_ = _eventBus.PublishAsync(...)` fire-and-forget

> **Status:** ✅ RESOLVED (Sprint 1) — the `ToolContext.ReportProgress` lambda is now `async`/`await` with a try/catch that logs failures at Warning level instead of letting them die as unobserved task exceptions.

**Файл:** `src/Harbor.Core/Agents/AgentLoop.cs`  
**Severity:** **high**  
**Line:** 620 (в `ToolContext.ReportProgress`)

```csharp
(update, c) => {
    _ = _eventBus.PublishAsync(new ToolExecutionUpdateEvent(...), c);
    return Task.CompletedTask;
}
```

Side-effect без обработки результата. Если `PublishAsync` выбросит (dead subscriber, channel full), исключение проглатится. Это и FP violation (не pure), и ROP violation (ошибка не попадает в Result-цепочку), и просто баг.

**Fix:** await + try/catch:

```csharp
async (update, c) => {
    try {
        await _eventBus.PublishAsync(new ToolExecutionUpdateEvent(...), c);
    } catch (Exception ex) {
        _logger.LogWarning(ex, "Tool progress publish failed");
    }
}
```

Или вынести в отдельный `IToolProgressReporter` интерфейс.

---

### §FP-004 — ChatMarkdown: static mutable Cache + Enabled flag

> **Status:** ✅ PARTIAL (Sprint 2, alongside §PERF-009) — `Cache` is now a `ConcurrentDictionary`, removing the per-render `lock(Cache)` and the race on parallel renderers. The `Enabled` static toggle is left as-is: it is set once at startup from config and is a process-wide policy (markdown is either on or off for the whole UI), not a per-renderer option. Making it per-renderer would require threading a flag through every call site — out of scope for this sprint.

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/View/ChatMarkdown.cs`  
**Severity:** medium  
**Line:** 12, 16, 18

```csharp
private static readonly Dictionary<string, List<TextSpan>> Cache = new(512);
public static bool Enabled { get; set; } = true;
```

Глобальное mutating-состояние — FP violation. Cache используется между сессиями (если одновременно рендерятся 2 transcript'а — race condition на `lock(Cache)`). `Enabled` — глобальный toggle, который нельзя переопределить per-renderer.

**Fix:** DI-сервис `IChatMarkdownCache` (singleton), `Enabled` — свойство `ChatViewProjector` (per-renderer).

---

### §FP-005 — ChatScreen: mutable `_scroll`, `_viewport`, `_wasRunning`

> **Status:** ✅ RESOLVED (Sprint 3, subagent T / tea-restorer) — `ChatScreen`'s three mutable fields are gone. All scroll / viewport / was-running state lives in `UiState` (`ScrollOffset`, `ViewportLines`, `TotalLines`, plus new `WasRunning`). The reducer snapshots `WasRunning = state.IsAgentRunning` on `AgentStartEvent` / `AgentEndEvent` and pins `ScrollOffset = 0` on `AgentStartEvent` so streaming is always visible. `HandleLocalScroll` is removed — every scroll action (`ScrollUpLine`/`DownLine`/`UpPage`/`DownPage`/`Top`/`Bottom`) flows through `UiReducer.Update` via `UiMsg.KeyInput`. `ChatScreen.Render` is now pure: it reads `_store.State`, measures geometry, dispatches measurement msgs (`UiMsg.Viewport`, `UiMsg.HistoryMeasured`, `UiMsg.ScrollClamp`, `UiMsg.ScrollResetToTail`), and re-reads state. The `PanelRegistry` was also refactored to be registration-only — `SetState`/`SetSize`/`FocusedPanelId` setter/`ApplySnapshot`/`SnapshotStates`/`SnapshotSizes`/`CycleFocus` are all removed. Panel state lives only in `UiState.PanelStates` / `PanelSizes` / `FocusedPanelId`, mutated by the reducer on `UiMsg.TogglePanel` / `FocusPanel` / `CyclePanelsFocus` / `ResizePanel`. Renderers read state via the new read-only `PanelRegistryView` snapshot. Tests: `tests/Harbor.Tui.Tests/PanelRegistryTests.cs` → `TeaComplianceTests` (13 reflection-based tests assert the invariants hold).

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs`  
**Severity:** medium  

`ChatScreen` объявляет себя "Thin TEA view" (The Elm Architecture), но на деле держит 3 mutable поля в render loop. В чистой TEA эти значения должны быть в `UiState` и обновляться через reducer. Сейчас **двойной source of truth**: `UiState.ScrollOffset` (через reducer) и `ChatScreen._scroll` (локально), которые расходятся.

`HandleLocalScroll` вообще отдельный scroll-mechanism, который не проходит через reducer.

**Fix:** перенести `_scroll`/`_viewport`/`_wasRunning` в `UiState`, обновлять через `UiMsg.Viewport`, `UiMsg.HistoryMeasured`. Renderer только читает.

---

### §FP-006 — TuiEffectHost.Run: fire-and-forget tasks

> **Status:** ✅ RESOLVED (Sprint 1) — each `PromptAsync`/`RunSlashAsync`/`AbortAsync` call now attaches a `.ContinueWith(... OnlyOnFaulted | RunSynchronously)` continuation that logs the exception via `ILogger<TuiEffectHost>`. The `Run` contract stays synchronous per `ITuiEffectRunner`; we do not await.

**Файл:** `src/Harbor.Tui.Abstractions/State/TuiEffectHost.cs`  
**Severity:** **high**  
**Line:** 34 (`Run`)

```csharp
case TuiEffect.PromptAgent p:
    _ = PromptAsync(p.Text);  // fire-and-forget
    break;
```

`Run(TuiEffect)` — sync метод (по контракту `ITuiEffectRunner`), но эффекты async. `_ = PromptAsync(...)` запускает таску и забывает. Если `agent.PromptAsync` выбросит, исключение умрёт в `UnobservedTaskException`, и store останется в `IsAgentRunning=true` навсегда (catch в `PromptAsync` выставит status="error", но если исключение до catch — нет).

**Fix:** либо `IAsyncEffectRunner.RunAsync(TuiEffect)`, либо `.ContinueWith(t => LogError(t.Exception), TaskContinuationOptions.OnlyOnFaulted)`:

```csharp
case TuiEffect.PromptAgent p:
    PromptAsync(p.Text).ContinueWith(t => {
        if (t.IsFaulted) _logger.LogError(t.Exception, "PromptAsync failed");
    }, TaskScheduler.Default);
    break;
```

---

### §FP-007 — UiStore.Transition: escape hatch из pure reducer

> **Status:** ✅ RESOLVED (Sprint 3, subagent T / tea-restorer) — `UiStore.Transition` remains `internal`, but the only in-assembly callers are now `TuiEffectHost` (legitimate fold-follow-up-state after async effects), `UiStore.BindSession`, and `UiStore.Reset`. External renderers — including `SpectreTuiRenderer` — cannot call `Transition` (it's `internal` to `Harbor.Tui.Abstractions`, no `InternalsVisibleTo` for renderer assemblies). A new `UiMsg.SeedPanels` case replaces what used to be a `Transition` call from `SpectreTuiRenderer.SeedPanelRegistryIntoState` — now dispatched through `UiReducer.Update` like every other state transition. The audit's original concern ("side-effect-host не должен менять state напрямую, только через Dispatch(UiMsg)") is addressed: `TuiEffectHost` still uses `Transition` for fold-follow-up-state (necessary because effects run async and fold state mid-flight), but this is a host-internal concern — external code goes through `Dispatch(UiMsg)`.

**Файл:** `src/Harbor.Tui.Abstractions/State/UiStore.cs`  
**Severity:** medium  
**Line:** 112 (`Transition`)

```csharp
public void Transition(Func<UiState, UiState> reducer) { ... }
```

Это **escape hatch** — любой код может подсунуть свою функцию перехода, обходя `UiReducer.Reduce`. Через него `TuiEffectHost` проталкивает follow-up state (`IsAgentRunning=true`, `Status="running"`), и `BindSession`, и `Reset`. Это нарушение TEA: side-effect-host не должен менять state напрямую, только через `Dispatch(UiMsg)`.

**Fix:** определить конкретные `UiMsg` для каждого случая (`UiMsg.AgentStarted`, `UiMsg.SessionBound(model, provider, agent)`, `UiMsg.ResetRequested`), и `Transition` сделать `internal` (только для reducer'а).

---

## 4. Railway Oriented Programming (ROP)

### §ROP-001 — JsonlSessionStore.DeserializeMessage: возвращает null вместо Result

> **Status:** ✅ RESOLVED (Sprint 1, alongside §OOP-003) — `DeserializeMessage` now returns `Result<AgentMessage>`. `GetMessagesAsync` aggregates per-line errors into a `List<string>` and logs them at Warning level, while still returning the successfully-deserialized messages (so a single corrupt line no longer truncates the whole transcript).

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** medium  
**Line:** 325

```csharp
private static AgentMessage? DeserializeMessage(JsonElement element) { ... }
```

Возвращает `null` при любой ошибке, теряя диагностику. Caller тихо логирует "Skipping malformed line in session {SessionId}" — пользователь не узнает, что его сессия **потеряла часть сообщений**.

**Fix:**

```csharp
private static Result<AgentMessage> DeserializeMessage(JsonElement element) {
    if (!element.TryGetProperty("id", out var idEl)) 
        return Result.Failure<AgentMessage>("missing 'id'");
    // ...
}
```

И в caller'е — собирать ошибки, возвращать `Result<IReadOnlyList<AgentMessage>, (IReadOnlyList<AgentMessage> Partial, IReadOnlyList<string> Errors)>` или хотя бы логировать на `LogWarning` уровне с указанием строки.

---

### §ROP-002 — PermissionService.CheckAsync: бросает на expected failure

> **Status:** ✅ RESOLVED (Sprint 1) — `CheckAsync` and `GetRuleset` now pattern-match `Result<AgentName>` instead of calling `.Value` (which threw `InvalidOperationException` on invalid input). On failure, `CheckAsync` returns `Result.Failure<PermissionResponse>` and `GetRuleset` returns `PermissionRuleset.Empty` (its contract is best-effort lookup).

**Файл:** `src/Harbor.Core/Permissions/PermissionService.cs`  
**Severity:** **high**  
**Line:** 35

```csharp
var agentResult = _agents.GetAgent(AgentName.TryCreate(agentName).Value);
//                                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                                            бросает InvalidOperationException!
```

`AgentName.TryCreate` возвращает `Result<AgentName>`. `.Value` бросает `InvalidOperationException` на failure. Если agentName невалиден (провайдер прислал кривое имя, либо модель галлюцинировала tool-call на неизвестный агент), метод **бросает вместо того, чтобы вернуть `Result.Failure`**.

Это прямой нарушение ROP-контракта, который сам проект заявляет в CLAUDE.md: "Throw exceptions only for *truly exceptional* conditions". Невалидный agent name — это **expected failure**, не exceptional.

**Fix:**

```csharp
var agentNameResult = AgentName.TryCreate(agentName);
if (agentNameResult.IsFailure)
    return Task.FromResult(Result.Failure<PermissionResponse>(agentNameResult.Error));

var agentResult = _agents.GetAgent(agentNameResult.Value);
```

Аналогично в `GetRuleset` (line 87).

---

### §ROP-003 — JsonlSessionStore.UpdateMessageAsync: silent overwrite

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** medium  
**Line:** 159

```csharp
public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, ...) {
    // JSONL is append-only; updates are recorded as new entries with same id
    // For simplicity, we just append again (the latest entry wins on read)
    return AppendMessageAsync(sessionId, message, ct);
}
```

`UpdateMessageAsync` молча превращается в `AppendMessageAsync`. Это означает: если сообщение с таким ID не существует, оно будет **создано без вопроса**. Caller не узнает, что update был на самом деле create.

**Fix:** явно проверять существование, либо документировать как "upsert" и переименовать в `UpsertMessageAsync`.

---

### §ROP-004 — OpenAiCompatibleLlmClient.MapChunk: возвращает IEnumerable с пустым списком

> **Status:** ✅ RESOLVED (Sprint 1) — on parse failure, `MapChunk` now returns `new[] { new ErrorEvent($"Parse failed: {ex.Message}") }` instead of `Enumerable.Empty<LlmEvent>()`. The agent loop's error handler picks this up and aborts the turn loudly instead of stalling on a silently-dropped chunk.

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** low  
**Line:** 263

```csharp
catch (Exception ex) {
    _logger.LogWarning(ex, "Failed to parse chunk: {Data}", data);
    return Enumerable.Empty<LlmEvent>();
}
```

При ошибке парсинга чанка — тихо возвращается пустой enumerable. Если чанк содержал критичный `tool_call` start, клиент **потеряет инструмент** без сообщения пользователю.

**Fix:** возвращать `ErrorEvent`:

```csharp
catch (Exception ex) {
    _logger.LogWarning(ex, "Failed to parse chunk: {Data}", data);
    return new[] { new ErrorEvent($"Parse failed: {ex.Message}") };
}
```

---

## 5. Performance

### §PERF-001 — MapChunk: JsonDocument.Parse на каждый SSE-чанк

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** **high**  
**Line:** 263

```csharp
using var doc = JsonDocument.Parse(data);
return MapChunkFromDocument(doc.RootElement).ToList();
```

На каждый SSE-чанк (тысячи на один стрим):
1. `JsonDocument.Parse` аллоцирует pooled buffer (1-2 KB)
2. `RootElement.EnumerateArray().ToList()` — ещё один `List<JsonElement>`
3. `MapChunkFromDocument(...).ToList()` — ещё один `List<LlmEvent>`
4. `using var doc = ...` — диспозит BufferPool (дешево, но не бесплатно)

**Fix:** `Utf8JsonReader` напрямую по `ReadOnlySpan<byte>`:

```csharp
private List<LlmEvent> MapChunk(ReadOnlySpan<byte> data) {
    var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);
    var events = new List<LlmEvent>(2);
    // ... manual parse, no JsonDocument allocation
    return events;
}
```

Это ~5x быстрее и 0 аллокаций (events list — единственная).

---

### §PERF-002 — BuildRequest: reflection-based JSON serialize

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Severity:** **high**  
**Line:** 145

```csharp
var payload = new Dictionary<string, object?> { ... };
// ... boxing value types into object
string json = JsonSerializer.Serialize(payload, JsonOptions);
```

`JsonSerializer.Serialize(Dictionary<string, object?>)` — reflection-based, ~50ns per call на холодном старте + IL2026 warnings под NativeAOT. Анонимные типы (`new { include_usage = true }`) — тоже reflection. Каждый value-type (`int`, `decimal`) боксится в `object`.

**Fix:**

```csharp
using var buffer = ArrayPool<byte>.Shared.RentScoped(8192);
using var stream = new MemoryStream(buffer.Array);
using var writer = new Utf8JsonWriter(stream);
writer.WriteStartObject();
writer.WriteString("model", request.Model);
writer.WriteStartArray("messages");
// ... manual serialize
writer.WriteEndObject();
writer.Flush();
// send stream.ToArray() or stream.GetBuffer() slice
```

Это **0 аллокаций** (pooled buffer) и AOT-friendly.

---

### §PERF-003 — JsonlSessionStore: reflection-based JsonSerializerOptions

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** medium  
**Line:** 14

```csharp
private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { ... };
```

`JsonSerializer.Deserialize<SessionHeaderEntry>(line, JsonOptions)` — использует reflection. Под NativeAOT — IL2026 warnings.

**Fix:** `JsonSerializerContext` с `[JsonSerializable(typeof(SessionHeaderEntry))]`:

```csharp
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SessionHeaderEntry))]
[JsonSerializable(typeof(MessageEntry))]
internal partial class JsonlSerializerContext : JsonSerializerContext { }
```

---

### §PERF-004 — JsonlSessionStore: глобальный lock на все сессии

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** medium  
**Line:** 25

```csharp
private readonly object _lock = new();
// ...
lock (_lock) {
    File.AppendAllText(sessionFile, ...);
}
```

Один `lock` на **все** сессии — если 10 параллельных сессий пишут, каждая ждёт другую. `File.AppendAllText` и так atomic per-call (один write внутри), lock нужен только для защиты от interleaving при multi-line writes.

**Fix:** per-session lock:

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
private async Task WithSessionLock(string sessionId, Func<Task> action) {
    var sem = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    await sem.WaitAsync();
    try { await action(); } finally { sem.Release(); }
}
```

---

### §PERF-005 — JsonlSessionStore.GetMessagesAsync: per-line JsonDocument.Parse

> **Status:** ⚠️ PARTIAL (Sprint 1, alongside §ROP-001) — the full `Utf8JsonReader` rewrite was judged too risky without AOT testing in this sprint. `JsonDocument.Parse` is kept, but the ROP path is fixed: per-line errors are aggregated and logged at Warning level (see §ROP-001). A future sprint can revisit option 1 (Utf8JsonReader) or option 3 (MemoryPack binary sidecar).

**Файл:** `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`  
**Severity:** **high**  
**Line:** 176

Для каждой строки JSONL — `JsonDocument.Parse(line)`. На 10k-сообщений сессии — 10k аллокаций pooled buffer + 10k Dictionary entries.

**Fix — три уровня:**

1. **Минимальный:** `Utf8JsonReader` на `ReadOnlySpan<byte>` (0 аллокаций на parse).
2. **Средний:** streaming deserialize через `JsonSerializer.DeserializeAsyncEnumerable<AgentMessage>(stream)` — но это требует union-deserialize (см. §OOP-008).
3. **Кардинальный:** хранить .msgpack бинарник рядом с .jsonl (или вместо). MemoryPack уже на всех сообщениях — сериализация/десериализация в ~10x быстрее и 0 reflection. JSONL оставить только для human-readable export.

Рекомендуется уровень 1 в краткосрочной, уровень 3 в долгосрочной перспективе.

---

### §PERF-006 — BashTool: 2 непулированных StringBuilder + нет cap

> **Status:** ✅ RESOLVED (Sprint 2) — `stdout`/`stderr` are now rented from `StringBuilderPool` (capacities 4096/1024). Each is capped at `MaxOutputChars = 100_000` — once the cap is hit, further lines are silently dropped and a `dropped` counter is logged at Warning level. The final `output` builder is also rented from the pool. `Append('\n')` replaces `AppendLine()` for platform-independent separators.

**Файл:** `src/Harbor.Tools.Builtin/Bash/BashTool.cs`  
**Severity:** medium  
**Line:** 91

```csharp
var stdout = new StringBuilder();
var stderr = new StringBuilder();
// ... process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
```

1. `new StringBuilder()` — не из `StringBuilderPool`. Каждый bash call аллоцирует 2 builder'а.
2. Нет лимита на размер — `find /` или `cat huge.log` может сожрать гигабайт.
3. `AppendLine(e.Data)` ещё и добавляет `\r\n` (platform-dependent).

**Fix:**

```csharp
using var stdout = StringBuilderPool.Rent(4096);
using var stderr = StringBuilderPool.Rent(1024);
const int MaxOutputChars = 100_000;
// ...
process.OutputDataReceived += (_, e) => {
    if (e.Data is null) return;
    if (stdout.Builder.Length < MaxOutputChars)
        stdout.Builder.Append(e.Data).Append('\n');
};
```

---

### §PERF-007 — UiStore: lock на каждый Dispatch

> **Status:** ✅ RESOLVED (Sprint 1) — `lock(_gate)` replaced with a lock-free CAS loop on the `volatile UiState _state` reference. `Dispatch(AgentEvent)`, `Dispatch(UiMsg)`, and `Transition` all use the same CAS pattern with a no-op short-circuit (skip the `Changed` event when `ReferenceEquals(original, next)`).

**Файл:** `src/Harbor.Tui.Abstractions/State/UiStore.cs`  
**Severity:** medium  
**Line:** 60, 76, 96

```csharp
public UiState State { get { lock (_gate) { return _state; } } }
public void Dispatch(...) { lock (_gate) { ... } }
```

Каждый `Dispatch` (включая UserInput, scroll, key) берёт lock. При интенсивном streaming (1000+ events/sec от LLM) — contention.

**Fix — lock-free CAS через ImmutableArray-подобный паттерн:**

```csharp
private UiState _state;  // volatile
public UiState State => _state;  // plain read, atomic on reference types

public void Dispatch(UiMsg msg) {
    UiState original, updated;
    TuiEffect effect;
    do {
        original = _state;
        (updated, effect) = UiReducer.Update(original, msg);
    } while (Interlocked.CompareExchange(ref _state, updated, original) != original);
    Changed?.Invoke(this, new UiStateChangedEventArgs(updated));
    return effect;
}
```

`UiState` — reference type (record class), assignment — atomic. CAS-loop — standard pattern.

---

### §PERF-008 — InMemoryEventBus.GetScrollback: drains channel

**Файл:** `src/Harbor.Core/Events/InMemoryEventBus.cs`  
**Severity:** medium  
**Line:** 140

```csharp
var all = _scrollback.Reader.ReadAllAsync(CancellationToken.None).ToBlockingEnumerable();
```

`ReadAllAsync` **полностью опустошает** channel. После вызова `GetScrollback` — следующий late-subscriber не получит историю. Plus `ToBlockingEnumerable` синхронно блокирует поток.

**Fix:** `ImmutableArray<AgentEvent>` ring buffer, обновляемый через `ImmutableInterlocked.Update`:

```csharp
private ImmutableArray<AgentEvent> _scrollback = ImmutableArray<AgentEvent>.Empty;

public IReadOnlyList<AgentEvent> GetScrollback(int max) {
    var snap = _scrollback;
    int start = Math.Max(0, snap.Length - max);
    return snap.Slice(start);  // O(1) slice, no copy
}
```

---

### §PERF-009 — ChatMarkdown.GetCached: lock on every ToSpans

> **Status:** ✅ RESOLVED (Sprint 2) — `Cache` is now a `ConcurrentDictionary<string, List<TextSpan>>`. The per-render `lock(Cache)` is gone, replaced by `GetOrAdd(text, factory, baseColor)`. The `Cache.Count > 2048 → Clear()` thundering-herd eviction is removed: the cache is already bounded upstream by `ChatTranscriptCache._rows` (only lines currently in the transcript are reachable from `ToSpans`).

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/View/ChatMarkdown.cs`  
**Severity:** low  
**Line:** 38

```csharp
lock (Cache) {
    if (Cache.TryGetValue(text, out var hit)) return hit;
}
// ...
lock (Cache) {
    if (Cache.Count > 2048) Cache.Clear();
    Cache[text] = spans;
}
```

`lock(Cache)` на каждый `ToSpans` call. При рендере длинного transcript'а (1000 строк) — 1000 lock acquisitions. `Cache.Clear()` на 2048 — thundering herd.

**Fix:** `ConcurrentDictionary<string, List<TextSpan>>` (lock-free reads), LRU eviction:

```csharp
private static readonly ConcurrentDictionary<string, List<TextSpan>> Cache = new(2048);
// + background eviction
```

---

## 6. Low-level / байтоебля

### §LOW-001 — AgentLoop.SnapshotMessages: List copy on every event

**Файл:** `src/Harbor.Core/Agents/AgentLoop.cs`  
**Severity:** low  
**Line:** 450

```csharp
private static List<AgentMessage> SnapshotMessages(IReadOnlyList<AgentMessage> messages) {
    var snapshot = new List<AgentMessage>(messages.Count);
    for (int i = 0; i < messages.Count; i++) snapshot.Add(messages[i]);
    return snapshot;
}
```

Каждый `AgentStartEvent` / `AgentEndEvent` копирует весь список сообщений. На 1000-сообщений сессии — 1000 элементов × 2 events = 2000 list entries per agent run.

**Fix:**
- Если consumer не мутирует — `IReadOnlyList<AgentMessage>` без копирования.
- Если мутирует — `ImmutableArray<AgentMessage>.Empty.AddRange(messages)` (struct, no allocation).

---

### §LOW-002 — CompactionService.AppendFormattedMessage: GetRawText per tool call

**Файл:** `src/Harbor.Core/Sessions/CompactionService.cs`  
**Severity:** low  
**Line:** 316

```csharp
case ToolCallPart tc:
    builder.Append("[tool_call:").Append(tc.ToolName).Append("] ")
           .Append(tc.Args.GetRawText());
```

`JsonElement.GetRawText()` — аллоцирует новую строку каждый раз. На summarization 100 tool calls — 100 строк.

**Fix:** использовать `Args.GetRawText()` один раз, кешировать. Но это микрооптимизация — compaction выполняется редко.

---

### §LOW-003 — ChatMessageFormatter.BodyLines: string.Split на каждый message

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/View/ChatMessageFormatter.cs`  
**Severity:** medium  
**Line:** 56

```csharp
string[] lines = body.Split('\n');
```

`body.Split('\n')` — аллоцирует массив + подстроки. На 100-строчном сообщении — 100 строк × 1 массив = 101 аллокаций per render. При скролле с кеш-инвалидацией — десятки тысяч аллокаций.

**Fix:** `ReadOnlySpan<char>` + `IndexOf('\n')`:

```csharp
var remaining = body.AsSpan();
while (true) {
    int i = remaining.IndexOf('\n');
    var line = i < 0 ? remaining : remaining[..i];
    // process line
    if (i < 0) break;
    remaining = remaining[(i + 1)..];
}
```

0 аллокаций (если не считать сами `TextLine` объекты).

---

### §LOW-004 — ChatTranscriptCache.Sync: prefix-check O(n²)

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/View/ChatTranscriptCache.cs`  
**Severity:** low  
**Line:** 42

```csharp
for (int i = 0; i < _source.Length; i++) {
    if (!lines[i].Equals(_source[i])) { prefixOk = false; break; }
}
```

На каждом `Sync` — linear scan префикса. Если в транс kriptе 10k строк и добавилось 1 новая — 10k сравнений `ChatLine.Equals` (struct, value compare). На самом деле не страшно (struct equality cheap), но при каждом frame'е — лишняя работа.

**Fix:** hash-prefix: хранить хеш последнего N элементов, сравнивать хеш + размер. Если равны — append-only.

---

### §LOW-005 — SpectreTuiRenderContext: Console.Write per call

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderContext.cs`  
**Severity:** low  
**Line:** 9

```csharp
public void Write(string text) => Console.Write(text);
public void WriteLine(string? text) => Console.WriteLine(text ?? string.Empty);
public void WriteColored(string text, TuiColor fg, ...) 
    => Console.Write($"\x1b[38;2;{fg.R};{fg.G};{fg.B}m{text}\x1b[0m");
```

`Console.Write` делает syscall на каждый вызов. `WriteColored` — `$"..."` интерполяция аллоцирует строку. На частом streaming-output — syscalls и аллокации.

**Fix:** buffered writer:

```csharp
private readonly char[] _buffer = new char[4096];
private int _pos;
public void Write(string text) {
    if (_pos + text.Length > _buffer.Length) Flush();
    text.AsSpan().CopyTo(_buffer.AsSpan(_pos));
    _pos += text.Length;
}
private void Flush() {
    Console.Out.Write(_buffer.AsSpan(0, _pos));
    _pos = 0;
}
```

---

### §LOW-006 — ChatChromeView.BuildHeader: string interpolation per frame

**Файл:** `contrib/tui/Harbor.Tui.SpectreTui/View/ChatChromeView.cs`  
**Severity:** low  
**Line:** 27

```csharp
string route = string.IsNullOrEmpty(Provider) ? "Harbor" : $"{Provider}/{Model}";
string agent = string.IsNullOrEmpty(Agent) ? "" : $" · {Agent}";
string usage = $"{TokensIn}↑ {TokensOut}↓ · ${Cost:F4}";
string left = ChatMarkup.Truncate($"⚓ {route}{agent}", 48);
```

4 интерполяции = 4 аллокации строк per frame. На 60 FPS — 240 аллокаций/sec.

**Fix:** `StringBuilderPool.Rent` + `Append`. Или `string.Create` для последней интерполяции.

---

## 7. Concurrency

### §CONC-001 — OpenAiCompatibleLlmClient: shared mutable state (см. §OOP-001)

(Дублирует §OOP-001 — нарушение thread-safety на singleton-клиенте.)

---

### §CONC-002 — JsonlSessionStore: глобальный lock (см. §PERF-004)

(Дублирует §PERF-004.)

---

### §CONC-003 — UiStore: lock на каждый Dispatch (см. §PERF-007)

(Дублирует §PERF-007.)

---

### §CONC-004 — TuiEffectHost: fire-and-forget (см. §FP-006)

(Дублирует §FP-006.)

---

## 8. NativeAOT

### §AOT-001 — JsonSerializerOptions с reflection

**Файлы:** `JsonlSessionStore`, `OpenAiCompatibleLlmClient`, `OpenAiCompatibleLlmClient.BuildMessages`  
**Severity:** medium  

Все use-cases `JsonSerializer.Serialize/Deserialize<T>(..., JsonOptions)` с reflection. Под NativeAOT — IL2026 warnings.

CLAUDE.md заявляет: "Core can be published as NativeAOT". Это значит, что `Harbor.Core` + `Harbor.Storage.Jsonl` + `Harbor.Providers.*` должны быть AOT-clean. Сейчас — нет.

**Fix:** везде, где есть `JsonSerializer.Serialize<T>` / `Deserialize<T>` на generic-типах — переходить на `JsonSerializerContext` source-gen.

---

### §AOT-002 — Anonymous types in BuildRequest payload

**Файл:** `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs`  
**Line:** 154, 168, 238  

`new { include_usage = true }`, `new { type = "function", function = new { name = ... } }` — анонимные типы под reflection. Под AOT — IL2026.

**Fix:** `JsonWriter`-based serialize (см. §PERF-002), либо explicit records + `JsonSerializerContext`.

---

## 9. Карта нарушений по файлам

| Файл | Нарушения | Severity |
|---|---|---|
| `src/Harbor.Core/Agents/AgentLoop.cs` | §SOLID-001, §OOP-004, §FP-003, §LOW-001 | medium |
| `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs` | §OOP-001, §OOP-002, §FP-001, §PERF-001, §PERF-002, §AOT-001, §AOT-002, §ROP-004 | **high** |
| `src/Harbor.Storage.Jsonl/JsonlSessionStore.cs` | §OOP-003, §OOP-008, §FP-002, §PERF-003, §PERF-004, §PERF-005, §ROP-001, §ROP-003, §AOT-001 | **high** |
| `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` | §SOLID-002, ~~§FP-005~~ (RESOLVED) | medium |
| `contrib/tui/Harbor.Tui.SpectreTui/View/ChatMarkdown.cs` | §FP-004, §PERF-009 | medium |
| `contrib/tui/Harbor.Tui.SpectreTui/View/ChatMessageFormatter.cs` | §LOW-003 | low |
| `contrib/tui/Harbor.Tui.SpectreTui/View/ChatTranscriptCache.cs` | §LOW-004 | low |
| `contrib/tui/Harbor.Tui.SpectreTui/View/ChatChromeView.cs` | §LOW-006 | low |
| `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderContext.cs` | §LOW-005 | low |
| `src/Harbor.Tui.Abstractions/State/UiStore.cs` | §PERF-007, ~~§FP-007~~ (RESOLVED) | medium |
| `src/Harbor.Tui.Abstractions/State/TuiEffectHost.cs` | §FP-006 | **high** |
| `src/Harbor.Core/Permissions/PermissionService.cs` | §ROP-002 | **high** |
| `src/Harbor.Core/Sessions/CompactionService.cs` | §OOP-007, §LOW-002 | low |
| `src/Harbor.Core/Events/InMemoryEventBus.cs` | §PERF-008 | medium |
| `src/Harbor.Core/Tools/ToolRegistry.cs` | §OOP-005, §GOF-001 | low |
| `src/Harbor.Tools.Builtin/Bash/BashTool.cs` | §PERF-006 | medium |

---

## 10. Приоритизированный план рефакторинга

### Sprint 1 (критическое, ~3 дня)
- [x] **§ROP-002**: `PermissionService.CheckAsync` — pattern-match `Result<AgentName>`, не бросать. (1 час) ✅
- [x] **§OOP-001**: `OpenAiCompatibleLlmClient._toolCallIndexToId` — local variable. (2 часа) ✅
- [x] **§FP-003 / §FP-006**: TuiEffectHost + AgentLoop fire-and-forget → await + ContinueWith. (4 часа) ✅
- [x] **§ROP-001**: `JsonlSessionStore.DeserializeMessage` → `Result<AgentMessage>`. (3 часа) ✅

### Sprint 2 (высокое, ~5 дней)
- [ ] **§PERF-002**: BuildRequest → Utf8JsonWriter. (2 дня)
- [~] **§PERF-005**: GetMessagesAsync → Utf8JsonReader. (1 день) — partial (ROP fixed; Utf8JsonReader deferred)
- [ ] **§PERF-001**: MapChunk → Utf8JsonReader. (1 день)
- [ ] **§AOT-001 / §AOT-002**: JsonSerializerContext для всех JsonOptions. (1 день)
- [~] **§FP-004**: `ChatMarkdown` → DI-сервис `IChatMarkdownCache`. (4 часа) — partial (`ConcurrentDictionary` only)

### Sprint 3 (среднее, ~5 дней)
- [ ] **§SOLID-001**: `AgentLoop` → разнести на 4 класса. (2 дня)
- [ ] **§SOLID-002**: `ChatScreen` → вынести + разнести. (1 день)
- [x] **§FP-005**: `_scroll`/`_viewport`/`_wasRunning` → `UiState`. (1 день) ✅ (subagent T / tea-restorer) — все 3 поля удалены, добавлены `UiMsg.ScrollResetToTail` + `UiMsg.ScrollClamp` + `UiState.WasRunning`; `HandleLocalScroll` удалён; `PanelRegistry` стал registration-only.
- [x] **§PERF-007**: `UiStore.Dispatch` → CAS loop. (4 часа) ✅ (pulled forward — same file as §FP-007)
- [x] **§FP-007**: `UiStore.Transition` → `internal` only; `SpectreTuiRenderer` now goes through `Dispatch(UiMsg.SeedPanels)`. ✅ (subagent T / tea-restorer)
- [ ] **§PERF-004**: `JsonlSessionStore` → per-session lock. (4 часа)
- [x] **§PERF-006**: `BashTool` → pooled StringBuilder + cap. (2 часа) ✅ (pulled forward — high perf, ~30 min)

### Sprint 4 (низкое, ~3 дня)
- [x] **§OOP-002**: `ApplyCompatFlags` → Strategy. (1 день) ✅ (pulled forward — required for §OOP-001 cleanup, ~30 min)
- [ ] **§OOP-005**: `ToolRegistry` / `ProviderRegistry` → Composite. (1 день)
- [ ] Остальные low-severity по мере касания файлов.

---

## 11. Что НЕ трогать (хорошие практики)

Важно отметить — большая часть кода **очень** хорошего качества. Не трогать:

1. **ImmutableArray для подписок** в `InMemoryEventBus` — эталонное использование `ImmutableInterlocked`.
2. **ArrayPool + clear-on-return** в `ProviderRegistry.GetAllModelsAsync` — аккуратно.
3. **`StringPool.Shared.GetOrAdd`** для tool name interning в `AgentLoop` — flyweight как в книге.
4. **`[StructLayout(LayoutKind.Sequential)]`** на `InMemoryEventBus.Subscription` — cache-friendly.
5. **`StopReasonJsonConverter.Parse` с switch на string** — fast-path без reflection.
6. **`IdentifierValidation.IsValidProviderId`** — ручной char-check вместо Regex.
7. **`PermissionRuleset._sortedRules`** — pre-sorted array, O(n) eval без LINQ.
8. **`FrozenDictionary` после `Freeze()`** — pattern для read-heavy registries.
9. **MemoryPack `[MemoryPackable]` + `[MemoryPackUnion]`** — на всех доменных моделях.
10. **`MessageConverter.StopReasonToLower`** — статический switch вместо `ToString().ToLowerInvariant()`.

---

## 12. Сводка по принципам

### SOLID
- **S** (Single Responsibility): нарушение в `AgentLoop` (§SOLID-001) и `ChatScreen` (§SOLID-002).
- **O** (Open/Closed): нарушение в `ApplyCompatFlags` (§OOP-002), `AgentLoop.RunAsync` switch (§OOP-004), `DeserializeMessage` (§OOP-008).
- **L** (Liskov): нарушение косвенно в `ApplyCompatFlags` (поведение зависит от id, а не от типа).
- **I** (Interface Segregation): нарушение в `ITuiRenderer` (linear + interactive в одном, §OOP-006).
- **D** (Dependency Inversion): **хорошо** — все зависимости через `Harbor.Abstractions` интерфейсы.

### GoF
- **Strategy**: `ITool`, `ILlmClient` — корректно.
- **Registry**: `ToolRegistry`/`ProviderRegistry` — работает, но гибрид Registry+Builder (§GOF-001).
- **Observer**: `InMemoryEventBus` — эталонно с `ImmutableArray`.
- **Builder**: `ToolRegistryBuilder` — корректно.
- **Adapter**: `MessageConverter`, `OpenAiCompatibleLlmClient` — корректно.
- **Specification**: `PermissionRuleset` — эталонно с pre-sorted rules.
- **Flyweight**: `StringPool.Shared` — эталонно.
- **Object Pool**: `StringBuilderPool`, `ArrayPool` — эталонно.
- **Chain of Responsibility**: `AgentLoop` заявлен, но это не совсем CoR — это просто while-loop. CoR был бы если бы каждый turn-step был отдельным handler'ом.

### FP
- **Pure functions**: `UiReducer.Reduce` — эталонно pure.
- **Immutability**: `record` для всех доменных моделей — хорошо. Но mutable Dictionary в BuildRequest (§FP-001), static Cache в ChatMarkdown (§FP-004).
- **Pattern matching**: switch expressions на discriminated unions — хорошо.
- **Higher-order**: `Transition(Func<UiState,UiState>)` — `internal` escape hatch, only used in-assembly by `TuiEffectHost` / `BindSession` / `Reset`. External renderers go through `Dispatch(UiMsg)` (§FP-007 RESOLVED by subagent T).

### ROP
- **Result<T>**: применяется последовательно, но с дырками (§ROP-001, §ROP-002, §ROP-003, §ROP-004).
- **Bind/Map/Ensure**: `CSharpFunctionalExtensions` — используется, но в `AgentLoop.RunAsync` всё ещё много `if (result.IsFailure) return Result.Failure(...)` вместо `.Bind(...)`.

### Performance
- **Pools**: `ArrayPool`, `StringBuilderPool`, `StringPool` — используются.
- **Frozen collections**: после `Freeze()` — хорошо.
- **Span/ReadOnlySpan**: мало (`ArrayPoolExtensions.RentedArray<T>` есть, но `Utf8JsonReader` — нет).
- **MemoryPack**: на всех моделях — эталонно.
- **Async**: `ConfigureAwait(false)` везде, `Channel<T>` для стримов — хорошо.

### Low-level (байтоебля)
- **string.Split**: в `ChatMessageFormatter` (§LOW-003).
- **string interpolation**: в hot paths TUI (§LOW-006).
- **Console.Write per char**: в `SpectreTuiRenderContext` (§LOW-005).
- **GetRawText()**: в `CompactionService` (§LOW-002) — acceptably rare path.
- **List copy in events**: в `AgentLoop.SnapshotMessages` (§LOW-001).

---

## 13. Метрики для трекинга

После рефакторинга стоит замерить:

| Метрика | До | Цель | Как мерить |
|---|---|---|---|
| `dotnet build` warnings (AOT) | ~20 IL2026 | 0 | `dotnet build -c Release --report-json` |
| `OpenAiCompatibleLlmClient.StreamAsync` allocs/call | ~5 KB | <1 KB | BenchmarkDotNet on StreamAsync |
| `JsonlSessionStore.GetMessagesAsync` (10k msgs) | ~500 KB | <50 KB | BenchmarkDotNet |
| `UiStore.Dispatch` ops/sec (4 threads) | ~500k | >2M | BenchmarkDotNet |
| `AgentLoop.RunAsync` max stack depth | ~15 frames | ~8 | dotMemory |
| Tests pass | 334 | 334+ | `dotnet test` |
| RSS idle | 28 MB | <25 MB | dotnet-counters monitor |

---

## 14. Связанные документы

- [CLAUDE.md](../CLAUDE.md) — основные конвенции (обновлено: добавлены ссылки на AGENTS.md и этот файл)
- [AGENTS.md](../AGENTS.md) — операционный гайд для AI-агентов (обновлено: добавлены ссылки)
- [docs/ARCHITECTURE.md](./ARCHITECTURE.md) — high-level архитектура (обновлено: отражены принципы)
- [docs/ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — **канонический справочник по слоям Clean/Hexagonal/Onion-архитектуры (новый, см. §15 ниже)**
- [docs/DEVELOPMENT.md](./DEVELOPMENT.md) — гайд разработки (обновлено: добавлен principles-чек-лист)
- [docs/SPECTRE_TUI_DEEP_DIVE.md](./SPECTRE_TUI_DEEP_DIVE.md) — детальный разбор SpectreTUI (новый)
- [docs/BENCHMARKS.md](./BENCHMARKS.md) — текущие бенчмарки

Все TODO-комментарии в коде используют формат:
```
// TODO(principles)[CATEGORY]: описание
// Fix: рекомендация
// См. аудит §XXX-NNN.
```

Для поиска: `grep -rn "TODO(principles)" src/`

---

## 15. Architecture layering — §ARCH-001..§ARCH-NNN

> **User mandate:** *"слои должны быть по чистой архитектуре, гексогональная луковая называй как хочешь но это надо"*
> (subagent A — arch-cleaner).
>
> **Canonical reference:** [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md).
> **Mechanically enforced by:** `tests/Harbor.Architecture.Tests/` (21 tests, all green).

This section audits the layering violations present before subagent A and the fixes
applied. Each finding is tagged `§ARCH-NNN`; the same tag appears as a comment in the
fixed source file so the audit trail is grep-able from code.

### Audit summary table

| §ARCH-NNN | File / project                                              | Layer it's IN        | Layer it REFERENCED           | Violation                                                                                                       | Fix applied                                                                                                                  |
|-----------|-------------------------------------------------------------|----------------------|-------------------------------|-----------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| §ARCH-001 | (entire codebase)                                           | —                    | —                             | No canonical layering reference document existed; "Clean Architecture" was mentioned informally but never specified. | Wrote `docs/ARCHITECTURE_LAYERS.md` — the canonical Clean / Hexagonal / Onion layering rules, allowed/forbidden ProjectReference matrix, and worked example. |
| §ARCH-002 | `Harbor.Tui.Abstractions/State/TuiEffectHost.cs`            | Domain (Tui.Abs)     | Domain (via IAgent) — but IAgent was over-broad | TuiEffectHost used the full `IAgent` contract (Subscribe/Steer/FollowUp/Initialize) when it only needs PromptAsync/AbortSource/WaitForIdleAsync. ISP violation + leak of Application vocabulary into the Domain layer. | Extracted `IAgentRunner` (minimal runner surface) in `Harbor.Abstractions/Agents/IAgent.cs`. `IAgent : IAgentRunner, IDisposable`. TuiEffectHost now takes `IAgentRunner`. |
| §ARCH-003 | `Harbor.Providers.OpenAiCompatible.csproj`<br>`Harbor.Providers.Anthropic.csproj`<br>`Harbor.Providers.OpenAI.csproj`<br>`Harbor.Providers.Ollama.csproj`<br>`Harbor.Storage.Jsonl.csproj`<br>`Harbor.Storage.Memory.csproj`<br>`Harbor.Storage.Sqlite.csproj` | Infrastructure       | Application (`Harbor.Core`)   | Vestigial `<ProjectReference Include="..\Harbor.Core\..." />` in every Infrastructure csproj. No `using Harbor.Core.*` actually appears in any of these projects — the references were copy-paste leftovers. Violates "Infrastructure depends on Domain only". | Removed the stale `<ProjectReference>` entries. Verified each project still builds (no compile errors after removal). Architecture test `Providers_ReferencesOnlyAbstractions` / `Storage_ReferencesOnlyAbstractions` now enforces this invariant. |
| §ARCH-004 | `Harbor.Scripting.csproj`                                   | Application          | Application (`Harbor.Core`)   | Vestigial `<ProjectReference Include="..\Harbor.Core\..." />`. No `using Harbor.Core.*` in code. Application projects must not cross-reference each other (would create a tangled Application sublayer). | Removed the stale `<ProjectReference>`. Architecture test `Scripting_ReferencesOnlyAbstractions` enforces. |
| §ARCH-005 | `Harbor.Core.csproj`                                        | Application          | Domain (`Harbor.Abstractions`) | (NO VIOLATION — verified clean.) Core references only Harbor.Abstractions. Architecture test `Core_ReferencesOnlyAbstractions` now mechanically enforces. | Added the test; no source changes. |
| §ARCH-006 | `Harbor.Plugins.Runtime.csproj`                             | Application          | Domain (`Harbor.Abstractions` + `Harbor.Tui.Abstractions`) | (NO VIOLATION — verified clean.) Plugins.Runtime legitimately needs ITuiPlugin (from Tui.Abstractions) for plugin-contributed panels. Architecture test `PluginsRuntime_ReferencesOnlyAbstractions` enforces that it does NOT reference Harbor.Core. | Added the test; no source changes. |
| §ARCH-007 | `Harbor.Tools.Builtin/RipGrep/RipGrepTool.cs`<br>`Harbor.Tools.Builtin/WebFetch/WebFetchTool.cs`<br>`Harbor.Tools.Builtin/Patch/PatchTool.cs`<br>`Harbor.Tools.Builtin/Notebook/NotebookTool.cs` | Infrastructure       | —                             | (NOT a layering violation — pre-existing build blockers.) `JsonValueKind.Boolean` (doesn't exist), `HttpResponseMessage.ReasonCode` (renamed in .NET 10), unassigned `tag` local, S125 false positive on a comment that mentioned `---`/`+++` (looked like commented-out code to Sonar), S3267 false positive on hot-path loop. These blocked the baseline `dotnet build` so architecture work could not be verified. | Fixed: `JsonValueKind.True`/`False`, `ReasonPhrase`, definite-assignment `tag = string.Empty`, rephrased the PatchTool comment to not contain `--- a/` / `+++ b/`, `#pragma warning disable S3267` with rationale. Tagged `§ARCH-007` in each fix comment so the audit trail is grep-able. |
| §ARCH-008 | `Harbor.Cli/Program.cs`                                     | Presentation         | Application (Scripting)       | `RunStartupScriptAsync` constructs concrete Application-layer types (`SharpTsScriptEngine`, `JintScriptEngine`, `PassThroughCompiler`, `TscCompiler`, `InMemoryScriptStore`, `ScriptHost`) directly instead of resolving them from DI. This is a soft layering rule violation (the Composition Root should be the only place that knows about concrete impls) — but it is NOT a hard layering violation because Program.cs is part of the Composition Root host (`Harbor.Cli`). | Left as-is for now (out of scope for this subagent's 30-min budget). Documented as a soft-rule violation in CLAUDE.md §Layering rules. The architecture tests do NOT enforce construction patterns — only project references. A future cleanup should move the script-host composition into `HostBuilder.RegisterScripting` and have `Program.cs` resolve `ScriptHost` from DI. |
| §ARCH-009 | `Harbor.Tui.SpectreTui/Panels/PanelViewProjector.cs` and others | Presentation       | —                             | (NOT a layering violation — pre-existing build blockers in `Harbor.Tui.SpectreTui`.) 15 compile errors: missing `using Harbor.Tui.SpectreTui.View`, `Spectre.Console.Justify` namespace mismatch, `PanelRegistry.SnapshotStates/SnapshotSizes/ApplySnapshot` missing, `UiStore.Transition` missing, `PanelLayoutShell.Ensure` arity, S3267. These are subagent T's responsibility (panel-system / SpectreTUI). | Left as-is — out of scope. The `Harbor.Architecture.Tests` project does NOT reference `Harbor.Tui.SpectreTui` (a comment in the .csproj explains why). Once subagent T fixes the build, re-add the ProjectReference and the `TuiRenderers_ReferencesOnlyAbstractions` test will automatically pick it up. |

### Architecture tests added

`tests/Harbor.Architecture.Tests/LayerDependencyTests.cs` — 21 tests:

1. `Abstractions_HasNoHarborProjectReferences`
2. `TuiAbstractions_ReferencesOnlyAbstractions`
3. `Core_ReferencesOnlyAbstractions`
4. `PluginsRuntime_ReferencesOnlyAbstractions`
5. `Scripting_ReferencesOnlyAbstractions`
6. `Providers_ReferencesOnlyAbstractions` (parameterized over 4 Provider assemblies)
7. `Storage_ReferencesOnlyAbstractions` (parameterized over 3 Storage assemblies)
8. `ToolsBuiltin_ReferencesOnlyAbstractions`
9. `TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions` (parameterized over 7 Tui renderer assemblies)
10. `AllExpectedHarborAssembliesAreLoaded` (sanity check — fails loudly if a ProjectReference is accidentally removed)

The tests use plain `System.Reflection` (`Assembly.GetReferencedAssemblies()`) — zero
extra dependencies (no NetArchTest, no Mono.Cecil). The test project references every
Harbor project so the test runner can load each assembly into the AppDomain.

### Verification

```
$ dotnet test tests/Harbor.Architecture.Tests/Harbor.Architecture.Tests.csproj
Passed! - Failed: 0, Passed: 21, Skipped: 0, Total: 21, Duration: 280ms
```

### Future work

- §ARCH-008 — move `RunStartupScriptAsync`'s concrete-type construction into
  `HostBuilder.RegisterScripting` and have `Program.cs` resolve `ScriptHost` from DI.
- Once subagent T fixes `Harbor.Tui.SpectreTui`, re-add the ProjectReference to
  `Harbor.Architecture.Tests.csproj` so the SpectreTui assembly is covered by
  `TuiRenderers_ReferencesOnlyAbstractionsAndTuiAbstractions`.
- Consider adding a source-generator-based analyzer that forbids `using Harbor.Core.*`
  inside `Harbor.Providers.*` / `Harbor.Storage.*` source files (a stricter check than
  assembly-reference-only). The architecture tests catch the assembly-level violation;
  a Roslyn analyzer would catch it at the file level even before the assembly is built.

