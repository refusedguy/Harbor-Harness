# PATTERNS.md — Pattern Catalog

> "I want to understand the codebase" — для каждого паттерна: определение,
> где используется в Harbor (с file:line), код, почему именно этот паттерн, и
> когда его НЕ применять.

Связанные документы:
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — что мы НЕ делаем (30+ анти-паттернов).
- [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level дизайн.
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — известные нарушения.

---

## TL;DR table

| # | Pattern | Where in Harbor | Key file |
|---|---|---|---|
| 1 | Strategy | `ILlmClient`, `ITool`, `ITuiRenderer`, `ISessionStore` | `src/Harbor.Abstractions/Tools/ITool.cs` |
| 2 | Registry | `ToolRegistry`, `ProviderRegistry`, `AgentRegistry` | `src/Harbor.Registries/Tools/ToolRegistry.cs` |
| 3 | Observer | `IEventBus` / `InMemoryEventBus` | `src/Harbor.Registries/Events/InMemoryEventBus.cs` |
| 4 | Builder | `IToolRegistryBuilder`, `ISystemPromptBuilder` | `src/Harbor.Registries/Tools/ToolRegistry.cs:194` |
| 5 | Adapter | `MessageConverter`, `OpenAiCompatibleLlmClient` | `src/Harbor.Application/Sessions/MessageConverter.cs` |
| 6 | Command | `IAgent`, `DefaultAgent`, `TaskTool` | `src/Harbor.Application/Agents/DefaultAgent.cs` |
| 7 | Specification | `PermissionRuleset`, `PermissionService` | `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs` |
| 8 | Value Object | `SessionId`, `ProviderId`, `ToolName` (7 types) | `src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs` |
| 9 | Factory Method | `Session.Create`, `ToolResult.Success/Error` | `src/Harbor.Abstractions.Contracts/Models/Session.cs:42` |
| 10 | Plugin | `IPlugin`, `IToolPlugin`, `IProviderPlugin`, `IAgentPlugin`, `ITuiPlugin` | `src/Harbor.Abstractions/Plugins/IPlugin.cs` |
| 11 | Repository | `ISessionStore` | `src/Harbor.Abstractions/Sessions/ISessionStore.cs` |
| 12 | Chain of Responsibility | `AgentLoop` (prompt → LLM → tool → next turn → compaction) | `src/Harbor.Application/Agents/AgentLoop.cs` |
| 13 | Flyweight | interned tool names in frozen dictionaries (`ToolRegistry`) | `src/Harbor.Registries/Tools/ToolRegistry.cs` |
| 14 | Object Pool | `StringBuilderPool`, `ArrayPool<T>.Shared` | `src/Harbor.Extensions/ArrayPoolExtensions.cs` |
| 15 | MVVM | `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` | `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs` |
| 16 | Decorator | `BaseTuiRenderer` decorates concrete renderers | `src/Harbor.Terminal.Abstractions/BaseTuiRenderer.cs` |
| 17 | TEA (The Elm Architecture) | `UiReducer` / `UiState` / `UiMsg` | `src/Harbor.Ui.Framework.State/State/UiReducer.cs` |
| 18 | Discriminated Union | `AgentEvent`, `LlmEvent` (16 + 12 variants) | `src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs` |

---

## 1. Strategy

**Definition.** Define a family of algorithms, encapsulate each one, and make them
interchangeable at runtime via an interface.

**Where in Harbor.** Every pluggable concern is an interface — implementations are
swapped via DI:

| Interface | Implementations |
|---|---|
| `ILlmClient` | `AnthropicLlmClient`, `OpenAILlmClient`, `OllamaLlmClient`, `OpenAiCompatibleLlmClient` |
| `ITool` | `ReadTool`, `WriteTool`, `EditTool`, `BashTool`, `GlobTool`, `GrepTool`, `LsTool`, `TaskTool` |
| `ITuiRenderer` | `AnsiTuiRenderer`, `PlainTuiRenderer`, `SpectreTuiRenderer`, `FullscreenTuiRenderer`, `TerminalGuiRenderer`, `TerminaRenderer`, `RazorConsoleRenderer` |
| `ISessionStore` | `JsonlSessionStore`, `MemorySessionStore`, `SqliteSessionStore` |

**Code snippet** (`src/Harbor.Abstractions/Tools/ITool.cs:20`):

```csharp
public interface ITool
{
    public ToolName Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public JsonDocument ParameterSchema { get; }
    public ExecutionMode ExecutionMode { get; }
    public string? PromptSnippet { get; }
    public IReadOnlyList<string> PromptGuidelines { get; }
    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default);
    public Result ValidateArguments(JsonElement args) => Result.Success();
}
```

**Why this pattern.**
- OCP: new tool = new class, not modification of `switch`.
- LSP: any `ITool` can replace any other (uniform contract).
- Testing: trivially mockable.

**Alternative considered.** `switch (toolName)` dispatcher. Rejected — every new
tool requires editing the dispatcher, violates OCP. (See §OOP-002 — this is an
antipattern that snuck into `OpenAiCompatibleLlmClient` for provider quirks.)

**Common mistakes.**
- Strategy with mutable instance state in a singleton — race condition. (See §OOP-001.)
- Strategy that holds `IDisposable` resources — leak when swapped.
- "Strategy of Strategies" — over-engineered, just use a builder.

---

## 2. Registry

**Definition.** Central lookup of objects by key, with O(1) access and thread-safety.

**Where in Harbor.** Three core registries + two TUI registries:

| Registry | Backing store | Lookup key |
|---|---|---|
| `ToolRegistry` | `NonBlocking.ConcurrentDictionary` + `FrozenDictionary` snapshot | `ToolName` |
| `ProviderRegistry` | `NonBlocking.ConcurrentDictionary` + `FrozenDictionary` snapshot | `ProviderId` |
| `AgentRegistry` | `NonBlocking.ConcurrentDictionary` | `AgentName` |
| `ViewRegistry` (TUI) | `Dictionary` (single-threaded) | `string Id` |
| `ViewModelRegistry` (TUI) | `Dictionary` (single-threaded) | `string Id` |

**Code snippet** (`src/Harbor.Registries/Tools/ToolRegistry.cs:11`):

```csharp
public sealed class ToolRegistry : IToolRegistry
{
    private readonly object _frozenLock = new();
    private readonly ConcurrentDictionary<ToolName, ITool> _tools = new();
    private FrozenDictionary<ToolName, ITool>? _frozenTools;

    public Result<ITool> GetTool(ToolName name)
    {
        // Fast path — frozen snapshot (lock-free)
        var frozen = _frozenTools;
        if (frozen is not null && frozen.TryGetValue(name, out var tool))
            return Result.Success(tool);
        // Slow path — concurrent dictionary
        if (_tools.TryGetValue(name, out var t))
            return Result.Success(t);
        return Result.Failure<ITool>($"Tool '{name}' is not registered.");
    }

    public void Freeze()
    {
        lock (_frozenLock) { _frozenTools = _tools.ToFrozenDictionary(); }
    }
}
```

**Why this pattern.**
- Central lookup avoids passing all tools via DI ctor (which would explode).
- `Freeze()` is called once at startup → `FrozenDictionary` gives ~0.18 µs lookups.
- `ConcurrentDictionary` lets plugins register tools at runtime (rare, but supported).

**Alternative considered.** `IServiceCollection` + `IEnumerable<ITool>`. Rejected —
need key lookup by `ToolName`, and `IServiceProvider` is slower than `FrozenDictionary`.

**Common mistakes.**
- Mutating the frozen snapshot post-`Freeze()` — silently breaks lookups. We
  invalidate via `InvalidateFrozenSnapshot()` (which itself is a `lock`, see §CONCURRENCY).
- Using `FrozenDictionary` when writes are frequent — that's not what it's for.

---

## 3. Observer

**Definition.** A subject maintains a list of dependents (subscribers) and notifies
them of state changes by publishing typed events.

**Where in Harbor.** `IEventBus` + `InMemoryEventBus`. The agent loop is the
publisher; TUI, loggers, plugins are subscribers.

**Code snippet** (`src/Harbor.Registries/Events/InMemoryEventBus.cs:54`):

```csharp
public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
{
    _scrollback.Writer.TryWrite(@event);            // bounded scrollback
    var snapshot = _subscriptions;                  // lock-free snapshot
    if (snapshot.IsEmpty) return;
    for (int i = 0; i < snapshot.Length; i++)
    {
        try { await snapshot[i].Handler(@event, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subscriber threw — removing");
            dead[deadCount++] = snapshot[i];        // collect dead subscribers
        }
    }
    if (deadCount > 0) RemoveDeadSubscriptions(dead, deadCount);
}
```

**Subscribers** subscribe via `IEventBus.Subscribe(handler)`:

```csharp
bus.Subscribe(async (AgentEvent e, CancellationToken ct) =>
{
    if (e is MessageEndEvent me) Console.WriteLine(me.Message.GetText());
    return Task.CompletedTask;
});
```

**Why this pattern.**
- Decouples `AgentLoop` from TUI — TUI can be swapped, run in another process, or skipped.
- Multiple subscribers (TUI + logger + plugin) get the same event.
- `ImmutableArray<Subscription>` gives O(1) lock-free snapshot reads.

**Alternative considered.** Direct calls (`tui.RenderAsync(event)`). Rejected —
tight coupling, can't swap TUI, can't add subscribers without modifying `AgentLoop`.

**Common mistakes.**
- Subscribers that throw — kills the publish loop (we mitigate with try/catch + dead removal).
- Long-running subscribers blocking the publisher — should use a `Channel<T>` to offload.
- Fire-and-forget inside subscriber — §FP-003 antipattern.

---

## 4. Builder

**Definition.** Separate construction of a complex object from its representation.
Lets the same construction process create different representations.

**Where in Harbor.**

| Builder | What it builds |
|---|---|
| `IToolRegistryBuilder` (`ToolRegistryBuilder`) | Populates `ToolRegistry` |
| `IProviderRegistryBuilder` | Populates `ProviderRegistry` |
| `IAgentRegistryBuilder` | Populates `AgentRegistry` |
| `ISystemPromptBuilder` | Assembles system prompt string |
| `LlmRequest` (record, fluent) | LLM request payload |

**Code snippet** (`src/Harbor.Registries/Tools/ToolRegistry.cs:194`):

```csharp
public sealed class ToolRegistryBuilder : IToolRegistryBuilder
{
    private readonly IToolRegistry _registry;
    public ToolRegistryBuilder(IToolRegistry registry) => _registry = registry;

    public void AddTool(ITool tool)
    {
        var result = _registry.Register(tool);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
    }

    public void AddTool<T>() where T : ITool, new() => AddTool(new T());
    public void AddTool(Func<ITool> factory) => AddTool(factory());
}
```

Usage in `HostBuilder.CreateToolRegistry`:

```csharp
var tb = new ToolRegistryBuilder(registry);
tb.AddTool(() => new ReadTool(loggerFactory.CreateLogger<ReadTool>()));
tb.AddTool(() => new WriteTool(loggerFactory.CreateLogger<WriteTool>()));
// ...
registry.Freeze();
```

**Why this pattern.**
- Plugins register their tools via the same builder API → no special plugin path.
- Hides `Result` error handling (the builder throws on failure, but only at startup).

**Alternative considered.** Direct `registry.Register(tool)` calls. Rejected —
plugins would have to handle `Result`, easy to forget error path.

**Common mistakes.**
- Builder that mutates shared state after `Build()` — should be a one-shot.
- Builder with too many methods — split into separate builders per concern.

---

## 5. Adapter

**Definition.** Convert the interface of a class into another interface clients
expect. Lets classes work together that couldn't otherwise because of incompatible
interfaces.

**Where in Harbor.**

| Adapter | Adapts from → to |
|---|---|
| `MessageConverter` | Domain `AgentMessage[]` → LLM-specific `LlmMessage[]` |
| `OpenAiCompatibleLlmClient` | Generic `LlmRequest` → OpenAI Chat Completions JSON |
| `AnthropicLlmClient` | Generic `LlmRequest` → Anthropic Messages API JSON |
| `ConfigAuthResolver` | `AuthStore` → `string` (API key resolution) |

**Code snippet** (conceptual — `src/Harbor.Application/Sessions/MessageConverter.cs`):

```csharp
// Domain model:
public sealed record UserMessage(string Content) : AgentMessage;
public sealed record AssistantMessage(string Text, ToolCallPart[] ToolCalls) : AgentMessage;

// LLM-specific (OpenAI Chat Completions):
// { "role": "user", "content": "..." }
// { "role": "assistant", "content": "...", "tool_calls": [{ "id": "...", "function": { ... } }] }

public sealed class MessageConverter
{
    public IReadOnlyList<LlmMessage> ToLlmMessages(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<LlmMessage>(messages.Count);
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage u:
                    result.Add(new LlmMessage("user", u.Content));
                    break;
                case AssistantMessage a:
                    result.Add(new LlmMessage("assistant", a.Text, ToToolCalls(a.ToolCalls)));
                    break;
                // ...
            }
        }
        return result;
    }
}
```

**Why this pattern.**
- Domain model stays provider-agnostic.
- Each provider has its own quirks (Anthropic's `content` array vs OpenAI's `content` string).
- One converter per provider, no god-class.

**Alternative considered.** Single `IMessage` interface implemented by all. Rejected
— would leak provider-specifics into the domain model.

**Common mistakes.**
- Adapter that adds behavior (becomes a Strategy) — keep it pure translation.
- Adapter with state — should be a singleton or per-call.

---

## 6. Command

**Definition.** Encapsulate a request as an object, letting you parameterize
clients with different requests, queue or log requests, and support undoable operations.

**Where in Harbor.**

| Command | What it does |
|---|---|
| `IAgent.PromptAsync(session, prompt, ct)` | Submits a prompt to the agent loop |
| `DefaultAgent` | Default `IAgent` implementation |
| `TaskTool` | Delegates to a sub-agent (`explore`, `plan`) |
| `ISlashCommand` implementations | REPL slash commands (`/help`, `/sessions`, ...) |

**Code snippet** (`src/Harbor.Application/Agents/DefaultAgent.cs`, simplified):

```csharp
public sealed class DefaultAgent : IAgent
{
    public async Task<Result> PromptAsync(ISessionContext session, string prompt, CancellationToken ct = default)
    {
        // 1. Append user message
        await session.AppendMessageAsync(UserMessage.Create(prompt), ct).ConfigureAwait(false);
        // 2. Run the agent loop
        var agent = _agents.GetAgent(session.Session.Agent).Value;
        return await _loop.RunAsync(session, agent, ct).ConfigureAwait(false);
    }
}
```

**Why this pattern.**
- Lets `ReplRunner` treat "ask" and "interactive" uniformly — both call `PromptAsync`.
- `TaskTool` reuses the same `IAgent` to spawn sub-agents.
- Future: queue, replay, undo — all are extensions to the same command object.

**Alternative considered.** Direct `AgentLoop.RunAsync(session, agent, ct)` from REPL.
Rejected — that skips the user-message-append step, easy to forget.

**Common mistakes.**
- Command with side-effects in constructor — should be lazy in `Execute`.
- Command that doesn't accept `CancellationToken` — non-cancellable = bad UX.

---

## 7. Specification

**Definition.** Encapsulate a piece of business rule (a predicate) into a standalone
object, composable with `And` / `Or` / `Not`.

**Where in Harbor.** `PermissionRuleset` + `PermissionRule`. Each rule is
`(toolName, glob) → action`. The ruleset evaluates `(toolName, argPath)` and
returns `Allow | Ask | Deny`.

**Code snippet** (`src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`):

```csharp
public sealed record PermissionRule(string ToolName, string ArgPattern, PermissionAction Action);

public sealed class PermissionRuleset
{
    private readonly PermissionRule[] _rules;
    public PermissionRuleset(IEnumerable<PermissionRule> rules) => _rules = rules.ToArray();

    public PermissionAction Evaluate(string toolName, string argPath)
    {
        foreach (var r in _rules)
        {
            if (Matches(r.ToolName, toolName) && GlobMatches(r.ArgPattern, argPath))
                return r.Action;
        }
        return PermissionAction.Deny;   // safe default
    }
}
```

Usage in `PermissionService.CheckAsync`:

```csharp
var action = agent.Permission.Evaluate(toolName, argPath);
// Allow → proceed; Deny → reject; Ask → call _userAsker
```

**Why this pattern.**
- Rules are data, not code — user can override via `~/.harbor/config.json`.
- Composable: agent default + user override = merged ruleset.
- Testable: pure function, no I/O.

**Alternative considered.** `Func<ToolCall, PermissionAction>` callbacks. Rejected
— not serializable, not user-configurable.

**Common mistakes.**
- Rules in unspecified order — must be first-wins.
- Default `Allow` — always default `Deny` for safety.

---

## 8. Value Object

**Definition.** A small immutable object identified by its value (not by identity).
Two VOs with the same value are equal.

**Where in Harbor.** 7 strongly-typed IDs (from CSharpFunctionalExtensions):

| Value Object | Underlying | Validation |
|---|---|---|
| `SessionId` | `string` | non-empty GUID N-format |
| `MessageId` | `string` | non-empty |
| `ToolCallId` | `string` | non-empty |
| `ProviderId` | `string` | lowercase alphanumeric + dash |
| `ModelRef` | `string` | `provider/model` format |
| `ToolName` | `string` | lowercase alphanumeric + underscore |
| `AgentName` | `string` | lowercase alphanumeric + underscore |

**Code snippet** (`src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs`):

```csharp
public sealed class ProviderId : ValueObject
{
    public string Value { get; }
    private ProviderId(string value) { Value = value; }

    public static ProviderId Create(string value) =>
        TryCreate(value).Value;   // throws on invalid

    public static Result<ProviderId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ProviderId>("ProviderId is empty.");
        if (!IsValid(value))
            return Result.Failure<ProviderId>($"Invalid ProviderId: '{value}'.");
        return Result.Success(new ProviderId(value));
    }

    protected override IEnumerable<IComparable> GetEqualityComponents()
    { yield return Value; }
}
```

**Why this pattern.**
- Compile-time safety: can't pass `string agentId` where `SessionId` expected.
- Validation lives in one place (`TryCreate`).
- No nullability issues — VOs are never null.

**Alternative considered.** Plain `string` IDs. Rejected — stringly-typed, easy
to swap, no validation.

**Common mistakes.**
- VO that's actually mutable (loses equality semantics).
- VO with side-effects in `TryCreate` (should be pure).
- VO that's not `sealed` (subclass breaks equality).

---

## 9. Factory Method

**Definition.** Define an interface for creating an object, but let subclasses or
static methods decide which class to instantiate.

**Where in Harbor.**

| Factory | What it creates |
|---|---|
| `Session.Create(...)` | A new `Session` with generated id + project id |
| `ToolResult.Success(output)` / `ToolResult.Error(msg)` | A `ToolResult` |
| `ProviderId.TryCreate(string?)` | A `Result<ProviderId>` |
| `AssistantMessage.Empty(...)` | An empty streaming-accumulation message |

**Code snippet** (`src/Harbor.Abstractions.Contracts/Models/Session.cs:42`):

```csharp
public static Session Create(string directory, string agentName, string providerId, string modelId, string? title = null)
{
    string id = Guid.NewGuid().ToString("N");
    string projectId = directory.GetHashCode(StringComparison.Ordinal).ToString("x");
    var now = DateTimeOffset.UtcNow;
    return new Session(
        id, projectId, directory,
        title ?? $"Session {now:yyyy-MM-dd HH:mm}",
        agentName, modelId, providerId,
        now, now, SessionMetadata.Empty);
}
```

**Why this pattern.**
- Centralizes id generation, project-id derivation, default title — no caller-code duplication.
- Static method on the type is more discoverable than a separate `SessionFactory` class.
- Validates args (e.g. `ProviderId.Create` throws on invalid format).

**Alternative considered.** Public constructor. Rejected — callers would have to
generate the id themselves, easy to forget.

**Common mistakes.**
- Factory that does I/O (becomes a Repository).
- Factory returning `null` on bad input — should return `Result<T>` or throw.

---

## 10. Plugin

**Definition.** Define a stable extension point (interface) that third-party code
can implement to add features without modifying the core.

**Where in Harbor.** 5 plugin contracts (all in `Harbor.Abstractions`):

| Interface | What plugins contribute |
|---|---|
| `IPlugin` | Base contract (Name, Version, Initialize, ShutdownAsync) |
| `IToolPlugin` | One or more `ITool` |
| `IProviderPlugin` | One or more `ILlmClient` |
| `IAgentPlugin` | One or more `AgentDefinition` |
| `ITuiPlugin` | TUI views + view models |

**Code snippet** (`src/Harbor.Abstractions/Plugins/IPlugin.cs:29`):

```csharp
public interface IPlugin
{
    public string Name { get; }
    public Version Version { get; }
    public Version RequiredHarborVersion { get; }
    public string Description { get; }
    public void Initialize(PluginContext context);
    public Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface IToolPlugin : IPlugin
{
    public void RegisterTools(IToolRegistryBuilder builder);
}
```

**Why this pattern.**
- OCP: add tools / providers / agents / UI without touching Core.
- Plugins share `PluginContext` (DI, config, logger, event bus) — uniform access.
- Future: NuGet-distributed plugins (`harbor plugin install foo`).

**Alternative considered.** Scripting (Lua, F#). Rejected for core — but see
[SCRIPTING.md](./SCRIPTING.md) for the scripting alternative (shipped under
`contrib/scripting/`).

**Common mistakes.**
- Plugin doing heavy work in `Initialize` — should be lazy.
- Plugin not thread-safe for `ShutdownAsync` racing with active use.

---

## 11. Repository

**Definition.** Mediates between the domain and data mapping layers using a
collection-like interface for accessing domain objects.

**Where in Harbor.** `ISessionStore` abstracts persistence — implementations:
`JsonlSessionStore`, `MemorySessionStore`, `SqliteSessionStore`.

**Code snippet** (`src/Harbor.Abstractions/Sessions/ISessionStore.cs`):

```csharp
public interface ISessionStore
{
    Task<Result<Session>> LoadAsync(string sessionId, CancellationToken ct = default);
    Task<Result> SaveAsync(Session session, CancellationToken ct = default);
    Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default);
    Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, string? title = null, CancellationToken ct = default);
    Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default);
    Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default);
}
```

**Why this pattern.**
- Switch storage backend via env var (`HARBOR_STORAGE=jsonl|sqlite|memory`).
- Tests use `MemorySessionStore` (no disk I/O, deterministic).
- LSP of substitutability: any impl can replace any other.

**Alternative considered.** EF Core. Rejected — not AOT-compatible, pulls in
heavy dependency.

**Common mistakes.**
- Repository that returns domain objects with lazy navigation properties — leaks
  DB concerns. Harbor's `ISessionStore` returns fully-materialized `Session` + `AgentMessage[]`.
- Repository with `IQueryable<T>` return — leaks query provider.

---

## 12. Chain of Responsibility

**Definition.** Pass a request along a chain of handlers; each decides to handle
it or pass it on.

**Where in Harbor.** `AgentLoop.RunAsync` is a linear chain:
`prompt → LLM stream → tool execution → next turn → compaction → repeat`.

**Code snippet** (`src/Harbor.Application/Agents/AgentLoop.cs:89`, simplified):

```csharp
public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
{
    // 1. Resolve provider + model
    var client = _providers.GetClient(ProviderId.TryCreate(agent.ProviderId).Value).Value;
    var model = FindModel(await client.GetModelsAsync(ct), agent.Model);

    // 2. Publish agent_start
    await _eventBus.PublishAsync(new AgentStartEvent(session.Session.Id, ..., model), ct);

    int turn = 0;
    while (!ct.IsCancellationRequested)
    {
        turn++;
        // 3. Compaction check (if needed, fold old messages into summary)
        if (_compaction.ShouldCompact(session.Messages, model))
            await _compaction.CompactAsync(session.Session.Id, session.Messages, model, ct);

        // 4. Build system prompt + tool definitions
        var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
        var systemPrompt = await _promptBuilder.BuildAsync(new SystemPromptContext(...), ct);

        // 5. Stream LLM
        var partial = AssistantMessage.Empty(session.Session.Id, model.Id);
        await foreach (var evt in client.StreamAsync(request, ct))
        {
            // 6. Accumulate text/thinking deltas in pooled StringBuilders
            // 7. Materialize tool calls on StepFinishEvent
        }

        // 8. Execute tool calls (parallel or sequential)
        foreach (var tc in partial.ToolCalls)
        {
            var toolResult = await ExecuteToolCall(tc, session, agent, ct);
            await session.AppendMessageAsync(new ToolResultMessage(...), ct);
        }

        // 9. No more tool calls? break.
        if (partial.ToolCalls.Length == 0) break;
        if (turn >= agent.MaxSteps) break;
    }

    await _eventBus.PublishAsync(new AgentEndEvent(...), ct);
    return Result.Success();
}
```

**Why this pattern.**
- Each stage is a clear step — easy to debug, easy to add telemetry.
- Compaction runs *only* at the start of a turn, not mid-stream.
- Cancellation checked at each stage.

**Alternative considered.** Pipeline library (MediatR). Rejected — too heavy for
a linear flow, hides the control flow.

**Common mistakes.**
- Stage that swallows exceptions silently — breaks the chain.
- Stage that doesn't respect `CancellationToken` — unkillable.

---

## 13. Flyweight

**Definition.** Share data among many objects to reduce memory usage. Useful when
many objects have identical immutable parts.

**Where in Harbor.** Repeated strings (tool names, provider ids, role names) are
interned at parse/registration time instead of per-event allocation — `ToolRegistry`
keeps tool names in frozen dictionaries keyed by interned `ToolName` values.

**Code snippet** (`src/Harbor.Application/Agents/AgentLoop.cs`, simplified):

```csharp
// Tool names are highly repeated (a handful of names across thousands of events).
// Resolve the shared tool instance once instead of allocating per event.
var tools = _tools.ResolveTools(agent.Name.Value, agent.Permission);
```

---

## 14. Object Pool

**Definition.** Reuse objects from a pool instead of allocating and garbage-collecting
them. Avoids GC pressure on hot paths.

**Where in Harbor.**

| Pool | What it pools | Used in |
|---|---|---|
| `ArrayPool<T>.Shared` | `T[]` buffers | `InMemoryEventBus` (dead subscribers), `ProviderRegistry` (task array), `ReadTool` (binary probe) |
| `StringBuilderPool` (`Harbor.Extensions`) | `StringBuilder` instances | `StreamingCoalescer` / agent streaming, `BashTool`, `TreeTool`, `PatchTool`, `NotebookTool` |

**Code snippet** (`src/Harbor.Application/Agents/AgentLoop.cs`, simplified):

```csharp
// Pre-allocate pooled StringBuilders for streaming text + thinking deltas.
// Without pooling, each streaming delta would allocate a new string buffer.
using var textBuffer = StringBuilderPool.Rent(4096);
using var thinkingBuffer = StringBuilderPool.Rent(1024);

await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
{
    if (evt is TextDeltaEvent td)
        textBuffer.Builder.Append(td.Delta);
    else if (evt is ThinkingDeltaEvent thd)
        thinkingBuffer.Builder.Append(thd.Delta);
}

// Flush once per text run, not per delta — O(n) allocations instead of O(n²).
if (textBuffer.Builder.Length > 0)
    partial = partial.AppendText(textBuffer.ToString());
```

**Why this pattern.**
- Streaming SSE can emit thousands of small deltas per turn. Without pooling,
  GC pressure dominates.
- `using var` returns the buffer to the pool automatically on scope exit.

**Alternative considered.** Just `new StringBuilder()`. Rejected — 10k+ allocations
per turn = GC death.

**Common mistakes.**
- Pooling objects that hold references — clear before return (`Array.Clear`).
- Pooling tiny short-lived objects — overhead exceeds benefit.
- Returning to pool after the `using` scope — `using` already does it.

---

## 15. MVVM (Model-View-ViewModel)

**Definition.** Separate UI (View) from state (ViewModel) from data (Model).
ViewModels expose observable properties; Views bind to them.

**Where in Harbor.** `Harbor.Tui.Abstractions` is strict MVVM:

- **Models**: `AgentEvent`, `UiState`, `ChatRole`.
- **ViewModels**: `StatusBarViewModel`, `ChatHistoryViewModel`, `InputViewModel`,
  `DiffPreviewViewModel` (4 builtin, extensible via `ITuiPlugin`).
- **Views**: `StatusBarView`, `ChatHistoryView`, `InputView`, `DiffPreviewView`.

**Code snippet** (`src/Harbor.Tui.Abstractions/ViewModels/TuiViewModels.cs`):

```csharp
public sealed partial class StatusBarViewModel : ObservableObject, ITuiViewModel
{
    public string Id => "status-bar";

    [ObservableProperty] private string _provider = "";
    [ObservableProperty] private string _model   = "";
    [ObservableProperty] private string _agent   = "code";
    [ObservableProperty] private string _status  = "idle";
    [ObservableProperty] private decimal _costUsd;
    [ObservableProperty] private long _tokensIn;
    [ObservableProperty] private long _tokensOut;

    public Task UpdateFromEventAsync(AgentEvent e, CancellationToken ct = default)
    {
        switch (e)
        {
            case AgentStartEvent ase when ase.Model is not null:
                Model = ase.Model.DisplayName;
                break;
            case StepFinishEvent sf when sf.Usage is not null:
                TokensIn  += sf.Usage.InputTokens;
                TokensOut += sf.Usage.OutputTokens;
                CostUsd   += EstimateCost(sf.Usage);
                break;
            case AgentEndEvent:
                Status = "idle";
                break;
        }
        return Task.CompletedTask;
    }
}
```

**Why this pattern.**
- Source-generated INPC (no manual `RaisePropertyChanged`).
- ViewModel is renderer-agnostic — same VM works for ANSI, Spectre, Terminal.Gui.
- Easy to test (no Console / terminal dependencies).

**Alternative considered.** Code-behind (one class does both state + rendering).
Rejected — entangles state with terminal specifics, hard to test.

**Common mistakes.**
- ViewModel referencing `ITuiRenderContext` — breaks renderer-agnosticism.
- ViewModel mutating state in `RenderAsync` — that's the View's job.

---

## 16. Decorator

**Definition.** Attach additional behavior to an object without subclassing. Wraps
the decorated object and delegates.

**Where in Harbor.** `BaseTuiRenderer` decorates concrete renderers
(`AnsiTuiRenderer`, `PlainTuiRenderer`, etc.) with view-dispatch logic.

**Code snippet** (conceptual — `src/Harbor.Tui.Abstractions/BaseTuiRenderer.cs`):

```csharp
public abstract class BaseTuiRenderer : ITuiRenderer
{
    private readonly ITuiRenderer _inner;   // decorated renderer

    protected BaseTuiRenderer(ITuiRenderer inner) { _inner = inner; }

    public virtual async Task PublishAsync(AgentEvent e, CancellationToken ct = default)
    {
        // 1. Fan-out to all view models
        foreach (var vm in _viewModels)
            await vm.UpdateFromEventAsync(e, ct);

        // 2. Decide which placements need a repaint
        if (ShouldRenderPlacement(e, TuiViewPlacement.StatusBar))
            await RenderPlacement(TuiViewPlacement.StatusBar, ct);

        // 3. Delegate streaming token emission to the inner renderer
        await _inner.PublishAsync(e, ct);
    }
}
```

**Why this pattern.**
- Add view-dispatch + placement logic to *any* renderer without subclassing each one.
- Streaming renderers (`AnsiTuiRenderer`) emit token deltas directly; full-screen
  renderers (`FullscreenTuiRenderer`) repaint the whole screen — same view-dispatch
  logic via the decorator.

**Alternative considered.** Inheritance (`AnsiTuiRendererWithViews : AnsiTuiRenderer`).
Rejected — combinatorial explosion (7 renderers × N features).

**Common mistakes.**
- Decorator that mutates the inner renderer's state — should be transparent.
- Decorator stack > 3 levels deep — debugging nightmare.

---

## 17. TEA (The Elm Architecture)

**Definition.** Three pure components: Model (immutable state), Update (reducer:
`(state, msg) → state`), View (renders state). Side-effects isolated in a
separate Effect runner.

**Where in Harbor.** `Harbor.Tui.Abstractions/State/`:

- **Model**: `UiState` (immutable record).
- **Update**: `UiReducer.Reduce(UiState, AgentEvent) → UiState`.
- **View**: `BaseTuiRenderer` reads `UiState`, calls `RenderAsync`.
- **Effect**: `TuiEffectHost` runs side-effects asynchronously (scroll, file
  reads for diff preview).

**Code snippet** (`src/Harbor.Tui.Abstractions/State/UiReducer.cs:31`):

```csharp
public static UiState Reduce(UiState state, AgentEvent @event) => @event switch
{
    AgentStartEvent ase         => OnAgentStart(state, ase),
    MessageStartEvent           => OnMessageStart(state),
    MessageUpdateEvent mu       => OnMessageUpdate(state, mu),
    MessageEndEvent             => OnMessageEnd(state),
    ToolExecutionStartEvent tes => state.AddLine(ChatRole.Tool, FormatToolStart(tes)),
    ToolExecutionEndEvent tee   => state.AddLine(ChatRole.ToolResult, FormatToolEnd(tee)),
    CompactionStartedEvent      => state with { Status = "compacting" },
    CompactionCompletedEvent cc => OnCompactionCompleted(state, cc),
    AgentErrorEvent err         => state.AddLine(ChatRole.Error, err.Message).WithStatus("error"),
    AgentEndEvent               => state with { Status = "idle", IsAgentRunning = false, IsStreaming = false },
    _ => state
};

private static UiState OnMessageUpdate(UiState state, MessageUpdateEvent mu) => mu.LlmEvent switch
{
    TextDeltaEvent td =>
        state with { Active = state.Active with { TextBuffer = state.Active.TextBuffer + td.Delta } },
    ThinkingDeltaEvent thd =>
        state with { Active = state.Active with { ThinkBuffer = state.Active.ThinkBuffer + thd.Delta } },
    ToolCallStartEvent tcs =>
        state.AddLine(ChatRole.Tool, $"→ {tcs.ToolName}"),
    StepFinishEvent sf when sf.Usage is not null =>
        OnStepFinish(state, sf.Usage),
    _ => state
};
```

**Why this pattern.**
- Pure reducer = trivially testable, no mocks needed.
- Single source of truth: every interactive renderer funnels events through
  `UiReducer` — no per-renderer `switch (AgentEvent)`.
- Time-travel debugging: log every `(state, event)` pair, replay.

**Alternative considered.** Imperative UI updates (`if event.X: statusLabel.Text = ...`).
Rejected — state spread across N views, hard to reason about.

**Common mistakes.**
- Reducer that does I/O — must be pure, side-effects go to `TuiEffectHost`.
- Mutating `UiState` in-place — must use `with` (record copy expression).
- Renderer holding a stale `UiState` reference — should always read latest from
  the `UiStore`.

---

## 18. Discriminated Union (via `abstract record` hierarchy)

**Definition.** A type that can be exactly one of N variants. C# doesn't have
first-class DUs, but `abstract record` + `sealed record` subclasses + `[JsonDerivedType]`
get us 90% of the way (pattern-matched exhaustively in `switch`).

**Where in Harbor.**

| DU | Variants | Where |
|---|---|---|
| `AgentEvent` | 13 variants (`AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`, `ToolExecutionStartEvent`, `ToolExecutionUpdateEvent`, `ToolExecutionEndEvent`, `TurnEndEvent`, `AgentEndEvent`, `AgentErrorEvent`, `CompactionStartedEvent`, `CompactionCompletedEvent`, `SessionStatsEvent`) | `Harbor.Abstractions/Events/AgentEvent.cs` |
| `LlmEvent` | 12 variants (`TextStartEvent`, `TextDeltaEvent`, `TextEndEvent`, `ThinkingStartEvent`, `ThinkingDeltaEvent`, `ThinkingEndEvent`, `ToolCallStartEvent`, `ToolCallDeltaEvent`, `ToolCallEndEvent`, `StepStartEvent`, `StepFinishEvent`, `FinishEvent`, `ErrorEvent`) | `Harbor.Abstractions/Events/AgentEvent.cs:139` |
| `AgentMessage` | `UserMessage`, `AssistantMessage`, `ToolResultMessage` | `Harbor.Abstractions/Models/Messages.cs` |

**Code snippet** (`src/Harbor.Abstractions/Events/AgentEvent.cs:8`):

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentStartEvent), "agent_start")]
[JsonDerivedType(typeof(TurnStartEvent), "turn_start")]
[JsonDerivedType(typeof(MessageStartEvent), "message_start")]
// ... 10 more variants
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AgentStartEvent(
    string SessionId,
    IReadOnlyList<AgentMessage> Messages,
    ModelInfo? Model = null) : AgentEvent;

public sealed record AgentEndEvent(IReadOnlyList<AgentMessage> NewMessages) : AgentEvent;
```

Pattern-matching consumer:

```csharp
public static UiState Reduce(UiState state, AgentEvent @event) => @event switch
{
    AgentStartEvent ase    => OnAgentStart(state, ase),
    AgentEndEvent          => state with { Status = "idle" },
    AgentErrorEvent err    => state.AddLine(ChatRole.Error, err.Message),
    // ...
    _ => state   // safety net for unknown variants
};
```

**Why this pattern.**
- Exhaustive pattern matching → compiler warns on missing variants (well, would
  if we didn't have `_ => state`; we keep the safety net for forward-compat).
- `[JsonDerivedType]` gives AOT-friendly polymorphic JSON serialization.
- Records give value equality, immutability, `with` expressions for free.

**Alternative considered.** `interface IAgentEvent` with classes. Rejected — no
value equality, no `with`, more boilerplate.

**Common mistakes.**
- Forgetting `[JsonDerivedType]` — serialization breaks for AOT.
- Adding a variant without updating consumers — `switch` falls through to `_`.

---

## See also

- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — 30+ "не делайте так" с примерами.
- [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level дизайн.
- [EXAMPLES.md](./EXAMPLES.md) — cookbook с 40 рецептами.
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 41 known violation с приоритетами.
