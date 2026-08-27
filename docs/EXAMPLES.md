# EXAMPLES.md — Harbor Cookbook

> "How do I...?" — 30+ short recipes, each with a code snippet and explanation.
> Russian explanations where they help, English code throughout.

Связанные документы:
- [GETTING_STARTED.md](./GETTING_STARTED.md) — установка и первый запуск
- [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level дизайн
- [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) — написание плагинов
- [PATTERNS.md](./PATTERNS.md) — каталог паттернов
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — что мы НЕ делаем

---

## Categories

1. [Tools](#tools) — add / call / wrap tools
2. [Providers](#providers) — LLM clients
3. [Storage](#storage) — session persistence
4. [TUI](#tui) — renderers, views, view models
5. [Plugins](#plugins) — CS-plugin system
6. [Sessions](#sessions) — load / branch / compact
7. [Permissions](#permissions) — allow / ask / deny
8. [Performance](#performance) — pools / frozen / span

---

## Tools

### 1. Add a `time` builtin tool

Самый простой tool: возвращает текущее время. 30 строк.

```csharp
// src/Harbor.Tools.Builtin/Tools/Time/TimeTool.cs
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;

namespace Harbor.Tools.Builtin;

public sealed class TimeTool : ITool
{
    public ToolName Name => ToolName.Create("time");
    public string DisplayName => "Time";
    public string Description => "Returns the current UTC time in ISO-8601.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "time: Get current UTC time";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public JsonDocument ParameterSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{}}""");

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext context, CancellationToken ct = default)
        => Task.FromResult(ToolResult.Success(
            DateTimeOffset.UtcNow.ToString("O"),
            new { iso8601 = DateTimeOffset.UtcNow.ToString("O") }));
}
```

Register in `ToolsCatalog.CreateToolRegistry`
(`src/Harbor.Hosting/Modules/ToolsCatalog.cs`):

```csharp
tb.AddTool(lf => new TimeTool(lf.CreateLogger<TimeTool>()));
registry.Freeze();
```

### 2. Validate args with `Result`

Всегда проверяй аргументы до `ExecuteAsync`. Если валидация упала — LLM увидит ошибку и попробует снова.

```csharp
public Result ValidateArguments(JsonElement args)
{
    if (!args.TryGetProperty("path", out var pathEl)
        || pathEl.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(pathEl.GetString()))
        return Result.Failure("Missing or empty 'path'.");
    if (args.TryGetProperty("offset", out var o)
        && o.TryGetInt32(out int offset) && offset < 1)
        return Result.Failure("'offset' must be >= 1.");
    return Result.Success();
}
```

### 3. Read a file with offset/limit (existing `read` tool)

```bash
$ dotnet run --project apps/Harbor.App.Cli -- ask \
    "Read the first 20 lines of src/Harbor.Application/Agents/AgentLoop.cs and summarize it"
```

LLM вызовет `read` с `{"path":"...","offset":1,"limit":20}`. Tool вернёт строки в формате `[0001] using ...`.

### 4. Wrap a shell command safely

Используй `ArgumentList.Add`, а не конкатенацию строк — это защита от shell-инъекций.

```csharp
var psi = new ProcessStartInfo
{
    FileName = "git",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
psi.ArgumentList.Add("log");
psi.ArgumentList.Add("--oneline");
psi.ArgumentList.Add($"-n{n}");           // user input goes as a separate arg
var proc = Process.Start(psi)!;
string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
await proc.WaitForExitAsync(ct);
```

### 5. Stateful tool (per-session)

Используй `ConcurrentDictionary<sessionId, T>` и блокируй на изменения. См.
`samples/plugins/Harbor.Plugin.TodoWrite/TodoWritePlugin.cs` —
полный рабочий пример на 170 строк.

```csharp
internal static readonly ConcurrentDictionary<string, List<TodoItem>> TodosBySession = new();

public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
{
    var todos = TodosBySession.GetOrAdd(ctx.SessionId, _ => new List<TodoItem>());
    lock (todos)
    {
        // mutate safely
    }
    return Task.FromResult(ToolResult.Success("ok"));
}
```

### 6. Report progress from a long-running tool

```csharp
public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
{
    for (int i = 0; i < 100; i++)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(50, ct).ConfigureAwait(false);
        await ctx.ReportProgress(new ToolProgressUpdate(
            Status: $"Step {i}/100",
            PercentComplete: i), ct).ConfigureAwait(false);
    }
    return ToolResult.Success("done");
}
```

> **Gotcha**: `ctx.ReportProgress` fire-and-forget — запрещён (см. `§FP-003`).
> `AgentLoop` уже оборачивает колбэк в `try/catch`, но плагин должен `await`.

### 7. Ask the user for permission mid-tool

```csharp
var response = await ctx.Ask(new PermissionRequest(
    ToolName: "webhook",
    ArgPath: "https://example.com/hook",
    Args: args,
    Choices: new[] { "allow", "deny" }), ct).ConfigureAwait(false);

if (response.Action == PermissionAction.Deny)
    return ToolResult.Error("user denied webhook");
```

### 8. Run tools in parallel

Tools с `ExecutionMode.Parallel` запускаются одновременно в одном turn'е. `AgentLoop`
использует `Task.WhenAll`, чтобы ждать все.

```csharp
// Plan agent: several `read` calls + a `glob` — all run concurrently
public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
```

---

## Providers

### 9. Add a new OpenAI-compatible provider (JSON only)

```jsonc
// providers/myllm.json
{
  "id": "myllm",
  "displayName": "MyLLM",
  "baseUrl": "https://api.myllm.com/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "MYLLM_API_KEY",
  "modelsUrl": "https://api.myllm.com/v1/models",
  "modelsPath": "data",
  "modelMapping": { "id": "id", "displayName": "name", "contextWindow": "context_length" }
}
```

```bash
export MYLLM_API_KEY=...
export HARBOR_MODEL=myllm/llama-4-70b
dotnet run --project apps/Harbor.App.Cli -- providers   # verify it's loaded
```

### 10. Add a native LLM provider (Anthropic-style)

```csharp
// src/Harbor.Providers.MyLLM/MyLlmClient.cs
public sealed class MyLlmClient : ILlmClient
{
    public ProviderId ProviderId { get; } = ProviderId.Create("myllm");

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default)
        => Result.Success<IReadOnlyList<ModelInfo>>(new[]
        {
            new ModelInfo("llama-4-70b", "Llama 4 70B", 128_000, 8_192)
        });

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextStartEvent("1");
        yield return new TextDeltaEvent("1", "Hello");
        yield return new TextEndEvent("1", "Hello");
        yield return new StepFinishEvent(1, "stop", new Usage(10, 5));
    }
}
```

Register in `ProviderFactories.CreateProviderRegistry`
(`src/Harbor.Hosting/Modules/ProviderFactories.cs`):

```csharp
pb.AddProvider("myllm", () => new MyLlmClient(httpFactory.CreateClient("myllm")));
```

### 11. Use Anthropic cache_control for prompt caching

Anthropic-нативный клиент автоматически добавляет `cache_control` к system prompt +
последним двум сообщениям. Это даёт 90% экономию на повторных turn'ах.

```bash
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514
# Cache reads billed at $0.30/MTok (vs $3/MTok for fresh input)
```

### 12. Use Ollama locally (zero network)

```bash
ollama pull llama3.2
ollama serve &
export HARBOR_MODEL=ollama/llama3.2
dotnet run --project apps/Harbor.App.Cli -- ask "Write a haiku about .NET"
```

### 13. Switch provider mid-REPL

```
harbor> /models openrouter
  anthropic/claude-sonnet-4   $3/$15 per MTok
  openai/gpt-4o               $2.50/$10
  deepseek/deepseek-chat      $0.27/$1.10
```

(Planned feature — `HARBOR_MODEL` env var works today.)

---

## Storage

### 14. Default JSONL storage

```bash
$ ls ~/.harbor/sessions/
abc123.jsonl   def456.jsonl
$ head -2 ~/.harbor/sessions/abc123.jsonl
{"type":"session","version":1,"id":"abc123","projectId":"...","directory":"/home/user/project",...}
{"type":"message","id":"m1","role":"user","createdAt":"...","payload":{"content":"Hello",...}}
```

Append-only, git-friendly, zero native deps.

### 15. SQLite for indexed queries

```bash
export HARBOR_STORAGE=sqlite
dotnet run --project apps/Harbor.App.Cli
# Sessions in ~/.harbor/sessions.db
sqlite3 ~/.harbor/sessions.db "SELECT COUNT(*) FROM messages;"
```

### 16. In-memory for tests

```csharp
var store = new MemorySessionStore();
var session = Session.Create("/tmp", "code", "anthropic", "claude-sonnet-4");
await store.SaveAsync(session, ct);
```

### 17. Implement a custom session store

```csharp
public sealed class RedisSessionStore : ISessionStore
{
    public Task<Result<Session>> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        // ... fetch from Redis
    }
    public Task<Result> SaveAsync(Session session, CancellationToken ct = default) { /* ... */ }
    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) { /* ... */ }
    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, string? title = null, CancellationToken ct = default) { /* ... */ }
    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) { /* ... */ }
    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) { /* ... */ }
}
```

Register in `StorageModule` (`src/Harbor.Hosting/Modules/StorageModule.cs`) —
the `HARBOR_STORAGE` env var (`jsonl` / `memory` / `sqlite`) selects the backend.

---

## TUI

### 18. Switch TUI renderer via env var

```bash
export HARBOR_TUI=plain      # no colors, for pipes
export HARBOR_TUI=ansi       # default streaming
export HARBOR_TUI=spectre    # rich interactive shell (contrib renderer, compiled in by default)
export HARBOR_TUI=consoleex  # second in-process interactive shell (raw mode, cell-diff)
```

> `HARBOR_MINIMAL=true` / `-p:HarborWithSpectreTui=false` excludes the contrib
> renderers from the CLI build; unsupported ids then fall back to `plain`.

### 19. Add a TUI view model with MVVM

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Abstractions.Events;
using Harbor.Tui.Abstractions.ViewModels;

public sealed partial class TokenUsageViewModel : ObservableObject, ITuiViewModel
{
    public string Id => "token-usage";

    [ObservableProperty] private int _tokensIn;
    [ObservableProperty] private int _tokensOut;
    [ObservableProperty] private decimal _costUsd;

    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is MessageUpdateEvent mu && mu.LlmEvent is StepFinishEvent sf && sf.Usage is not null)
        {
            TokensIn  += sf.Usage.InputTokens;
            TokensOut += sf.Usage.OutputTokens;
            CostUsd   += sf.Usage.InputTokens * 3m / 1_000_000m
                       + sf.Usage.OutputTokens * 15m / 1_000_000m;
        }
        return Task.CompletedTask;
    }
}
```

### 20. Add a TUI view (renders VM state)

```csharp
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.Views;

public sealed class TokenUsageView : TuiViewBase<TokenUsageViewModel>
{
    public override string Id => "token-usage";
    public override string DisplayName => "Token usage";
    public override TuiViewPlacement Placement => TuiViewPlacement.StatusBar;

    public override Task RenderAsync(ITuiRenderContext ctx, CancellationToken ct = default)
    {
        if (ViewModel is null) return Task.CompletedTask;
        ctx.WriteColored($"${ViewModel.CostUsd:F4} | {ViewModel.TokensIn}↑ {ViewModel.TokensOut}↓",
                         TuiColor.Cyan);
        return Task.CompletedTask;
    }
}
```

### 21. Subscribe to events (logger / metric collector)

```csharp
public sealed class CostLogger
{
    public CostLogger(IEventBus bus)
        => bus.Subscribe(static (AgentEvent e, CancellationToken ct) =>
        {
            if (e is SessionStatsEvent sse)
                Console.WriteLine($"[cost] {sse.SessionId}: ${sse.Metadata.Cost:F4}");
            return Task.CompletedTask;
        });
}
```

### 22. Stream tokens with `IAsyncEnumerable`

LLM clients yield `LlmEvent`s — `AgentLoop` consumes via `await foreach`:

```csharp
await foreach (var evt in client.StreamAsync(request, ct).ConfigureAwait(false))
{
    switch (evt)
    {
        case TextDeltaEvent td: textBuffer.Builder.Append(td.Delta); break;
        case StepFinishEvent sf: finalUsage = sf.Usage; break;
    }
}
```

---

## Plugins

### 23. Drop a .cs plugin file (Roslyn, JIT mode)

> **TODO: confirm with subagent #1** — Roslyn `CsPluginLoader` is being built; this
> API is the expected contract.

```bash
$ cat > ~/.harbor/plugins/HelloTool.cs << 'EOF'
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
EOF

$ dotnet run --project apps/Harbor.App.Cli
harbor> /plugins
  hello  v1.0.0  Says hello
harbor> ask "Say hello to Alice"
```

### 24. Migrate a DLL plugin to .cs (Roslyn)

До (DLL, requires `.csproj`, build, copy):

```bash
cd samples/plugins/Harbor.Plugin.TodoWrite
dotnet build -c Release
cp bin/Release/net10.0/Harbor.Plugin.TodoWrite.dll ~/.harbor/plugins/
```

После (CS, just save the file):

```bash
cp TodoWritePlugin.cs ~/.harbor/plugins/   # .cs file with both Plugin + Tool classes
# Harbor compiles & loads it next startup
```

> **TODO: confirm with subagent #1** — `CsPluginLoader` should accept multi-type .cs files.

### 25. Add a TUI panel plugin

> **TODO: confirm with subagent #2** — `ITuiPanelPlugin` may be added separately.

```csharp
public sealed class LspDiagnosticsPlugin : ITuiPlugin
{
    public string Name => "lsp-diag";
    public Version Version => new(1, 0, 0);
    public string Description => "LSP diagnostics panel";

    public void RegisterTui(ViewRegistry views, ViewModelRegistry vms)
    {
        vms.Register(new LspDiagnosticsViewModel());
        views.Register(new LspDiagnosticsView());
    }
}
```

See [PLUGIN_DEVELOPMENT.md §LspDiagnosticsPanel](./PLUGIN_DEVELOPMENT.md) for the full 50-line example.

---

## Sessions

### 26. List saved sessions

```bash
$ dotnet run --project apps/Harbor.App.Cli -- sessions
abc123  2026-07-16 14:23  Code review of AgentLoop.cs     17 msgs
def456  2026-07-15 09:11  Refactor PermissionService      8 msgs
```

### 27. Trigger compaction manually

Compaction срабатывает автоматически когда сессия близка к context window.
Программно — через `ICompactionService`:

```csharp
var compaction = sp.GetRequiredService<ICompactionService>();
if (compaction.ShouldCompact(messages, model))
{
    var result = await compaction.CompactAsync(sessionId, messages, model, ct);
    await result.Match(
        ok => session.AppendMessageAsync(ok.SummaryMessage, ct),
        err => { logger.LogWarning(err); return Task.CompletedTask; });
}
```

### 28. Branch a session (planned v0.6)

```csharp
// Future API:
var branched = await store.BranchAsync(sessionId, "try-different-approach");
// branched.ParentSessionId == sessionId
```

Today: just create a new session and replay messages.

---

## Permissions

### 29. Deny `bash` for `plan` agent

```csharp
var planRuleset = new PermissionRuleset(new[]
{
    new PermissionRule("bash", "*", PermissionAction.Deny),
    new PermissionRule("write", "*", PermissionAction.Deny),
    new PermissionRule("edit", "*", PermissionAction.Deny),
    new PermissionRule("*", "*", PermissionAction.Allow),   // read-only otherwise
});
```

### 30. Ask before `write` to `*.env`

```csharp
new PermissionRule("write", "*.env", PermissionAction.Ask),
new PermissionRule("write", "*secret*", PermissionAction.Ask),
new PermissionRule("write", "*", PermissionAction.Allow),
```

User sees:

```
[permission] write wants to access .env.production
  [a] allow  [d] deny  [A] always allow
```

### 31. Glob patterns in permission rules

```csharp
new PermissionRule("read",  "src/*",          PermissionAction.Allow),
new PermissionRule("read",  "node_modules/*", PermissionAction.Deny),
new PermissionRule("read",  "*.env",          PermissionAction.Ask),
new PermissionRule("read",  "*",              PermissionAction.Allow),
```

Matching is first-wins (in array order); `*` matches any path segment.

---

## Performance

### 32. Use `FrozenDictionary` after registry freeze

```csharp
private FrozenDictionary<ToolName, ITool>? _frozen;

public void Freeze()
{
    _frozen = _tools.ToFrozenDictionary();
}

public Result<ITool> GetTool(ToolName name)
{
    var frozen = _frozen;
    if (frozen is not null && frozen.TryGetValue(name, out var t))
        return Result.Success(t);
    return Result.Failure<ITool>($"Tool '{name}' not registered.");
}
```

Frozen lookup = ~0.18 µs (vs ~0.4 µs for `ConcurrentDictionary`).

### 33. Pool a `StringBuilder` for streaming coalesce

```csharp
using var buf = StringBuilderPool.Rent(4096);
await foreach (var evt in client.StreamAsync(request, ct))
{
    if (evt is TextDeltaEvent td) buf.Builder.Append(td.Delta);
}
partial = partial.AppendText(buf.ToString());
buf.Builder.Clear();
```

### 34. Guard `LogTrace` with `IsEnabled`

Hot path — `LogTrace` вызывается на каждый streaming event. Без guard'а
выделяется `object?[]` под параметры каждый раз.

```csharp
if (_logger.IsEnabled(LogLevel.Trace))
{
    _logger.LogTrace("Stream event: {EventType}", evt.GetType().Name);
}
```

### 35. Use `ArrayPool<T>` for transient buffers

```csharp
byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
try
{
    int n = await stream.ReadAsync(buffer.AsMemory(0, 8192), ct);
    // ...process buffer[0..n]
}
finally
{
    Array.Clear(buffer, 0, 8192);  // don't keep refs alive
    ArrayPool<byte>.Shared.Return(buffer);
}
```

### 36. Use `StringPool` for highly-repeated strings (tool names)

```csharp
// Tool names are highly repeated — intern via StringPool.
string internedName = StringPool.Shared.GetOrAdd(name);
```

### 37. Use `Utf8JsonReader` instead of `JsonDocument.Parse` per line

```csharp
// ❌ SLOW — allocates per line
using var doc = JsonDocument.Parse(line);

// ✅ FAST — zero alloc, span-based
var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
while (reader.Read())
{
    if (reader.TokenType == JsonTokenType.PropertyName
        && reader.ValueTextEquals("type"))
    {
        reader.Read();
        string type = reader.GetString()!;
        // dispatch on type
    }
}
```

### 38. Manual `for` loop instead of `foreach` on hot path

`foreach` на `IEnumerable<T>` аллоцирует enumerator. На hot path — `for` + индекс:

```csharp
var snapshot = _subscriptions;
int n = snapshot.Length;
for (int i = 0; i < n; i++)
{
    await snapshot[i].Handler(@event, ct).ConfigureAwait(false);
}
```

### 39. ConfigureAwait(false) everywhere in library code

```csharp
await _eventBus.PublishAsync(evt, ct).ConfigureAwait(false);
await client.StreamAsync(request, ct).ConfigureAwait(false);
```

193 occurrences in Harbor — enforced by analyzer. Без него в library коде
`SynchronizationContext` host'а захватывается и убивает throughput.

### 40. Pre-size `List<T>` when capacity is known

```csharp
// ❌ List grows by doubling — log(n) reallocations
var list = new List<ToolDescriptor>();

// ✅ Pre-size if you know the upper bound
var list = new List<ToolDescriptor>(capacity: frozen.Count);
```

---

## See also

- [PATTERNS.md](./PATTERNS.md) — где какой паттерн применяется и почему.
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — 30+ "не делайте так" с примерами.
- [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) — глубокий разбор плагинов.
- [DEVELOPMENT.md](./DEVELOPMENT.md) — workflow add-feature / debug / profile.
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 41 known violation.
