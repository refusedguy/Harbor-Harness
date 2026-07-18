# ANTIPATTERNS.md — What we explicitly avoid (and why)

> 30+ antipatterns Harbor forbids. For each: name, description, what goes wrong,
> right way, code example. Categories: OOP, FP, ROP, Perf, Concurrency, AOT, Testing.

Связанные документы:
- [PATTERNS.md](./PATTERNS.md) — что мы ДЕЛАЕМ (каталог паттернов).
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 41 known violation.
- [CLAUDE.md](../CLAUDE.md) §Code review checklist — что проверять в PR.

---

## Categories

1. [OOP / SOLID](#oop--solid)
2. [FP (Functional Programming)](#fp-functional-programming)
3. [ROP (Railway Oriented Programming)](#rop-railway-oriented-programming)
4. [Perf](#perf)
5. [Concurrency](#concurrency)
6. [AOT (NativeAOT)](#aot-nativeaot)
7. [Testing](#testing)

---

## OOP / SOLID

### 1. God class

**What.** A class that does many unrelated things (orchestration + parsing +
state + I/O + ...). Usually >500 lines and >10 public methods.

**What goes wrong.** Hard to test (mock 5 dependencies). Hard to reason about
(changes ripple). Hard to subclass (which method to override?).

**Right way.** Single Responsibility: one class, one reason to change.

```csharp
// ❌ WRONG — god class
public sealed class AgentLoop
{
    public async Task RunAsync(...) { /* orchestration */ }
    public void CoalesceStreaming(...) { /* parsing */ }
    public void DispatchToolCalls(...) { /* tool dispatch */ }
    public void PublishEvents(...) { /* event publishing */ }
    public void CheckPermissions(...) { /* permission gating */ }
    public void DrainSteeringQueue(...) { /* steering */ }
    public void UpdateScrollback(...) { /* TUI */ }
    // ... 650 lines total
}

// ✅ RIGHT — split into focused collaborators
public sealed class AgentLoop { /* orchestration only, ~50 lines */ }
public sealed class StreamingCoalescer { /* text/thinking/tool-call accumulation */ }
public sealed class ToolCallDispatcher { /* tool execution */ }
public sealed class TurnEventPublisher { /* event publishing */ }
```

> **Harbor status:** `AgentLoop` is currently a god class (650 lines, 8
> responsibilities). See §SOLID-001 in [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md).
> Planned split: v0.3.

### 2. Switch on type (OCP violation)

**What.** `switch (obj.GetType())` or `switch (providerId.Value) { case "deepseek": ... }`.

**What goes wrong.** Adding a new type requires editing every switch — violates
Open/Closed. Switches spread like cancer across the codebase.

**Right way.** Strategy pattern: each type implements an interface, dispatch via vtable.

```csharp
// ❌ WRONG — switch on provider id
switch (providerId.Value)
{
    case "deepseek":  BuildDeepSeekRequest(req);  break;
    case "groq":      BuildGroqRequest(req);      break;
    case "mistral":   BuildMistralRequest(req);   break;
    default:          BuildGenericRequest(req);   break;
}

// ✅ RIGHT — strategy per provider
public interface IProviderCompatFlags
{
    bool SupportsStreamingObject { get; }
    bool NeedsEmptyToolArgsObject { get; }
    void CustomizeRequest(LlmRequest req);
}

public sealed class DeepSeekCompat : IProviderCompatFlags { /* ... */ }
public sealed class GroqCompat : IProviderCompatFlags { /* ... */ }

// Dispatch via DI:
var compat = _compatRegistry.Get(providerId);
compat.CustomizeRequest(req);
```

> **Harbor status:** `OpenAiCompatibleLlmClient.BuildRequest` has a
> `switch (ProviderId.Value)`. See §OOP-002.

### 3. Mutable singleton state

**What.** Singleton service (`AddSingleton<T>()`) with mutable instance fields
that aren't protected by `lock` / `Interlocked`.

**What goes wrong.** Race conditions when two requests hit the singleton
concurrently. Corruption, heisenbugs, lost updates.

**Right way.** Either (a) make state immutable, (b) protect with `lock` /
`Interlocked`, or (c) move state to per-call locals.

```csharp
// ❌ WRONG — instance-level mutable state in singleton
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly Dictionary<string, string> _toolCallIndexToId = new();
    // ^ race condition: two concurrent streams will corrupt this

    public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest req, ...)
    {
        _toolCallIndexToId.Clear();   // ← BAD
        // ...
    }
}

// ✅ RIGHT — per-call local state
public async IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest req, ...)
{
    var toolCallIndexToId = new Dictionary<string, string>(4);   // local
    // ...
}

// ✅ ALSO RIGHT — ConcurrentDictionary for shared state
private readonly ConcurrentDictionary<string, string> _shared = new();
```

> **Harbor status:** `OpenAiCompatibleLlmClient._toolCallIndexToId` is a known
> instance-level mutable. See §OOP-001.

### 4. Throw exceptions for control flow

**What.** Using `throw new FooException()` to signal expected failures (file not
found, invalid args, missing API key).

**What goes wrong.** Exceptions are 1000x slower than `Result<T>` for the
happy-path-on-failure case. Stack unwinding, catch blocks everywhere.

**Right way.** `Result<T>` for expected failures. Throw only for truly exceptional
conditions (corrupted state, NRE).

```csharp
// ❌ WRONG — exceptions for expected failure
public Session Load(string id)
{
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("id");
    return _store.Load(id) ?? throw new NotFoundException(id);
}

// ✅ RIGHT — Result<T> for expected failure
public Result<Session> Load(string? id) =>
    SessionId.TryCreate(id)
        .Bind(sid => _store.GetAsync(sid))
        .Ensure(s => s is not null, "session not found");
```

### 5. `.Value` on `Result` without checking `IsSuccess`

**What.** `result.Value` accessed without `if (result.IsSuccess)` check.

**What goes wrong.** `Result<T>.Value` throws `InvalidOperationException` on a
failure Result. Crash, no useful error message.

**Right way.** Always check `IsSuccess` first, or use `Match`/`Bind`/`Map`.

```csharp
// ❌ WRONG — crash on failure
var agent = _agents.GetAgent(AgentName.TryCreate(agentName).Value);
//                                       ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                                       throws if agentName invalid

// ✅ RIGHT — pattern-match
var nameResult = AgentName.TryCreate(agentName);
if (nameResult.IsFailure)
    return Result.Failure<PermissionResponse>(nameResult.Error);
var agentResult = _agents.GetAgent(nameResult.Value);
if (agentResult.IsFailure)
    return Result.Failure<PermissionResponse>(agentResult.Error);
// ... use agentResult.Value

// ✅ BETTER — Railway
return AgentName.TryCreate(agentName)
    .Bind(name => _agents.GetAgent(name))
    .Map(agent => agent.Permission.Evaluate(toolName, argPath));
```

> **Harbor status:** `PermissionService.CheckAsync` and `GetRuleset` both have
> this. See §ROP-002 (critical crash bug).

### 6. Returning `null` instead of `Result<T>`

**What.** Method returns `T?` and callers must remember to null-check.

**What goes wrong.** Easy to forget null-check → NRE. Loses error context
(why was it null?).

**Right way.** Return `Result<T>` with explicit error.

```csharp
// ❌ WRONG — null on failure
public AgentMessage? DeserializeMessage(string json)
{
    try { return JsonSerializer.Deserialize<AgentMessage>(json); }
    catch { return null; }   // ← loses JsonException details
}

// ✅ RIGHT — Result<T> with error
public Result<AgentMessage> DeserializeMessage(string json)
{
    try { return Result.Success(JsonSerializer.Deserialize<AgentMessage>(json)!); }
    catch (JsonException ex) { return Result.Failure<AgentMessage>(ex.Message); }
}
```

> **Harbor status:** `MessageConverter.DeserializeMessage` returns null. See §ROP-001.

### 7. `IServiceProvider` injected as a constructor parameter

**What.** Service depends on `IServiceProvider` and resolves dependencies at
runtime via `GetService<T>()`.

**What goes wrong.** Hides dependencies, breaks testability, makes DI graph
opaque. Can cause captive dependencies (singleton capturing a scoped service).

**Right way.** Inject only what you need; for occasional resolution, inject
`IServiceScopeFactory`.

```csharp
// ❌ WRONG — service locator
public sealed class MyService
{
    private readonly IServiceProvider _sp;
    public MyService(IServiceProvider sp) { _sp = sp; }

    public void DoWork()
    {
        var repo = _sp.GetRequiredService<IRepo>();   // hidden dep
        repo.Save(...);
    }
}

// ✅ RIGHT — explicit dependency
public sealed class MyService
{
    private readonly IRepo _repo;
    public MyService(IRepo repo) { _repo = repo; }

    public void DoWork() => _repo.Save(...);
}
```

> **Exception:** `ToolContext.Services` deliberately exposes `IServiceProvider`
> to plugins — they need to resolve plugin-specific services without modifying Core.

### 8. Open class for inheritance when not designed for it

**What.** Non-`sealed` class that isn't designed for subclassing (no virtual
methods, no protected hooks).

**What goes wrong.** Subclasses break invariants, override behavior in unexpected
ways, break LSP.

**Right way.** Seal by default. Open for inheritance only with explicit design
(virtual methods, protected ctors, well-documented invariants).

```csharp
// ❌ WRONG — implicitly open
public class Foo { /* no virtual methods, no protected */ }

// ✅ RIGHT — sealed by default
public sealed class Foo { /* ... */ }

// ✅ RIGHT — explicitly designed for inheritance
public abstract class Bar
{
    public abstract void Step1();
    public virtual void Step2() { /* default */ }
    protected Bar() { /* invariant setup */ }
}
```

---

## FP (Functional Programming)

### 9. Fire-and-forget async

**What.** `_ = SomeAsync()` — discarding the Task without awaiting or attaching
a fault continuation.

**What goes wrong.** Exceptions are silently swallowed (or, worse, surface as
unobserved task exceptions later). State isn't updated. Hangs.

**Right way.** `await` it. If you really must fire-and-forget, use
`.ContinueWith(t => log(t.Exception), TaskContinuationOptions.OnlyOnFaulted)`.

```csharp
// ❌ WRONG — fire-and-forget, errors swallowed
_ = _eventBus.PublishAsync(evt, ct);

// ✅ RIGHT — await
await _eventBus.PublishAsync(evt, ct).ConfigureAwait(false);

// ✅ OK — fire-and-forget with fault handler
_ = Task.Run(async () =>
{
    try { await SomeAsync(ct); }
    catch (Exception ex) { _logger.LogError(ex, "Background task failed"); }
});
```

> **Harbor status:** `AgentLoop.ReportProgress` and `TuiEffectHost.Run` both
> have fire-and-forget. See §FP-003, §FP-006.

### 10. Mutating state in render path

**What.** `View.RenderAsync` mutates `UiState` or other view models.

**What goes wrong.** Side-effect in render → renders aren't idempotent, hard to
debug. Breaks TEA.

**Right way.** Render only *reads* state. Mutations go through `UiReducer.Reduce`.

```csharp
// ❌ WRONG — mutating state in render
public override Task RenderAsync(ITuiRenderContext ctx, CancellationToken ct)
{
    _state.ScrollOffset += 1;   // ← mutation in render
    ctx.WriteLine(_state.Lines[_state.ScrollOffset]);
    return Task.CompletedTask;
}

// ✅ RIGHT — render only reads
public override Task RenderAsync(ITuiRenderContext ctx, CancellationToken ct)
{
    for (int i = 0; i < _state.Lines.Length; i++)
        ctx.WriteLine(_state.Lines[i]);
    return Task.CompletedTask;
}
```

> **Harbor status:** `ChatScreen.Render` mutates `_scroll` / `_viewport`. See §FP-005.

### 11. `yield` inside `try` / `catch`

**What.** `yield return` inside a `try` block that has a `catch` clause.

**What goes wrong.** Compile error CS1626. C# forbids it because iterators can't
resume from a catch.

**Right way.** Extract the body to a separate method, or use `Channel<T>`.

```csharp
// ❌ WRONG — CS1626 compile error
public async IAsyncEnumerable<LlmEvent> StreamAsync(...)
{
    try
    {
        var response = await _http.SendAsync(req);
        yield return new TextDeltaEvent("1", "...");   // ← ERROR
    }
    catch (HttpRequestException ex)
    {
        yield return new ErrorEvent(ex.Message);       // ← ERROR
    }
}

// ✅ RIGHT — extract body
public async IAsyncEnumerable<LlmEvent> StreamAsync(...)
{
    IAsyncEnumerable<LlmEvent> stream;
    try { stream = InnerStream(...); }
    catch (HttpRequestException ex) { stream = ErrorEvent(ex); }
    await foreach (var e in stream) yield return e;
}

// ✅ BETTER — Channel<T>
public async IAsyncEnumerable<LlmEvent> StreamAsync(...)
{
    var channel = Channel.CreateUnbounded<LlmEvent>();
    _ = Task.Run(async () =>
    {
        try { await PumpStreamIntoChannel(channel.Writer, ...); }
        catch (Exception ex) { await channel.Writer.WriteAsync(new ErrorEvent(ex.Message)); }
        finally { channel.Writer.Complete(); }
    });
    await foreach (var e in channel.Reader.ReadAllAsync()) yield return e;
}
```

### 12. Impure reducer

**What.** Reducer function (e.g. `UiReducer.Reduce`) performs I/O, calls
non-deterministic methods (`DateTime.Now`, `Guid.NewGuid`), or mutates shared state.

**What goes wrong.** Reducer isn't reproducible. Time-travel debugging breaks.
Tests need mocks.

**Right way.** Reducer must be a pure function: `(state, event) → state`.
Side-effects go to a separate Effect runner.

```csharp
// ❌ WRONG — impure reducer
public static UiState Reduce(UiState state, AgentEvent e)
{
    if (e is AgentStartEvent)
    {
        File.AppendAllText("log.txt", "agent started");  // ← I/O
        return state with { StartedAt = DateTime.Now };   // ← non-deterministic
    }
    return state;
}

// ✅ RIGHT — pure reducer
public static UiState Reduce(UiState state, AgentEvent e) => e switch
{
    AgentStartEvent ase => state with
    {
        Status = "running",
        StartedAt = ase.Timestamp   // ← from event, not DateTime.Now
    },
    _ => state
};
// I/O goes in TuiEffectHost.Run, not the reducer.
```

### 13. Async void

**What.** `async void` method (instead of `async Task`).

**What goes wrong.** Caller can't await. Exceptions crash the process (no Task
to surface them on).

**Right way.** `async Task` everywhere. The only legit `async void` is event handlers.

```csharp
// ❌ WRONG — async void
public async void DoWork()
{
    await _service.FooAsync();   // ← caller can't await, errors crash
}

// ✅ RIGHT — async Task
public async Task DoWork()
{
    await _service.FooAsync().ConfigureAwait(false);
}
```

---

## ROP (Railway Oriented Programming)

### 14. `try` / `catch` for expected failure

**What.** Wrapping every method call in `try { ... } catch (Exception ex) { ... }`
to handle expected failures.

**What goes wrong.** Performance (try/catch is slow on failure path), boilerplate,
loses type info on the error.

**Right way.** `Result<T>` for expected failures. `try`/`catch` only at boundaries
(HTTP, file I/O, process spawn).

```csharp
// ❌ WRONG — try/catch for expected failure
public string LoadConfig(string path)
{
    try { return File.ReadAllText(path); }
    catch (FileNotFoundException) { return "{}"; }
    catch (IOException ex) { throw new InvalidOperationException("bad", ex); }
}

// ✅ RIGHT — Result<T> at boundary, ROP after
public Result<string> LoadConfig(string path)
{
    try { return Result.Success(File.ReadAllText(path)); }
    catch (FileNotFoundException) { return Result.Failure<string>($"not found: {path}"); }
    catch (IOException ex) { return Result.Failure<string>(ex.Message); }
}
```

### 15. `.Result` or `.Wait()` on async

**What.** Blocking on async via `.Result` or `.Wait()`.

**What goes wrong.** Deadlock in any context with a `SynchronizationContext` (UI,
ASP.NET classic). Wastes a thread.

**Right way.** `await` it. All the way up.

```csharp
// ❌ WRONG — sync-over-async
var session = _store.LoadAsync(id).Result;
//                        ^^^^^^^ deadlock risk

// ✅ RIGHT — async all the way
public async Task DoWorkAsync(...)
{
    var session = await _store.LoadAsync(id).ConfigureAwait(false);
    // ...
}
```

> **Exception:** `HostBuilder.Build` uses `.GetAwaiter().GetResult()` once
> (synchronous DI setup, no SynchronizationContext). Marked with `#pragma warning disable RS0030`.

### 16. Catching and re-wrapping exceptions inside library code

**What.** `catch (Exception ex) { throw new MyException("foo", ex); }` inside a
library method.

**What goes wrong.** Loses the original stack trace (sometimes). Adds a useless
wrapper. Forces callers to catch the wrapper.

**Right way.** Let it propagate, or convert to `Result.Failure(ex.Message)` at
the boundary.

```csharp
// ❌ WRONG — wrap inside library
public Result<Session> Load(string id)
{
    try { return _store.Load(id); }
    catch (Exception ex) { throw new MyLibraryException("load failed", ex); }
}

// ✅ RIGHT — propagate, or convert to Result at boundary
public Result<Session> Load(string id)
{
    try { return Result.Success(_store.Load(id)); }
    catch (Exception ex) { return Result.Failure<Session>(ex.Message); }
}
```

---

## Perf

### 17. LINQ on hot path

**What.** `System.Linq` in a method called >1,000 times/sec (`AgentLoop.RunAsync`,
`StreamAsync`, `Render`, `Dispatch`).

**What goes wrong.** Each LINQ call allocates an iterator + delegate. GC pressure
dominates.

**Right way.** Manual `for` loop, or `ZLinq` (`AsValueEnumerable()`).

```csharp
// ❌ WRONG — LINQ on hot path
foreach (var tool in tools.Where(t => t.ExecutionMode == ExecutionMode.Parallel)
                          .Select(t => ToDescriptor(t)))
{
    // ...
}

// ✅ RIGHT — manual for loop
for (int i = 0; i < tools.Count; i++)
{
    var t = tools[i];
    if (t.ExecutionMode == ExecutionMode.Parallel)
    {
        var d = ToDescriptor(t);
        // ...
    }
}

// ✅ ALSO RIGHT — ZLinq (zero alloc)
foreach (var d in tools.AsValueEnumerable()
                       .Where(t => t.ExecutionMode == ExecutionMode.Parallel)
                       .Select(ToDescriptor))
{
    // ...
}
```

### 18. `string.Split` on hot path

**What.** `line.Split(',')` in a tight loop (e.g. parsing JSONL or CSV).

**What goes wrong.** Allocates `string[]` + N substrings per call.

**Right way.** `Span<T>` + `IndexOf`.

```csharp
// ❌ WRONG — string.Split allocates
foreach (var part in line.Split(','))
{
    Process(part);
}

// ✅ RIGHT — span-based, zero alloc
ReadOnlySpan<char> span = line.AsSpan();
int start = 0;
while (start < span.Length)
{
    int comma = span.Slice(start).IndexOf(',');
    if (comma < 0) { Process(span.Slice(start)); break; }
    Process(span.Slice(start, comma));
    start += comma + 1;
}

void Process(ReadOnlySpan<char> s) { /* ... */ }
```

### 19. `JsonDocument.Parse` per line

**What.** Parsing each JSONL line via `JsonDocument.Parse(line)`.

**What goes wrong.** 10k lines = 10k `JsonDocument` allocations + pool pressure.

**Right way.** `Utf8JsonReader` (struct, zero-alloc).

```csharp
// ❌ WRONG — JsonDocument per line
foreach (var line in File.ReadLines(path))
{
    using var doc = JsonDocument.Parse(line);   // alloc per line
    var type = doc.RootElement.GetProperty("type").GetString();
    // ...
}

// ✅ RIGHT — Utf8JsonReader
foreach (var line in File.ReadLines(path))
{
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
    while (reader.Read())
    {
        if (reader.TokenType == JsonTokenType.PropertyName
            && reader.ValueTextEquals("type"))
        {
            reader.Read();
            var type = reader.GetString();
            // dispatch on type
        }
    }
}
```

> **Harbor status:** `JsonlSessionStore.GetMessagesAsync` uses `JsonDocument.Parse`
> per line. See §PERF-005.

### 20. `JsonSerializer.Serialize<T>` with reflection

**What.** `JsonSerializer.Serialize(obj)` without a `[JsonSerializable]` context.

**What goes wrong.** Reflection-based serialization → IL2026 warnings under
NativeAOT. Slow first call (cache build-up).

**Right way.** `JsonSerializerContext` source-gen.

```csharp
// ❌ WRONG — reflection
var json = JsonSerializer.Serialize(myObj);
var obj  = JsonSerializer.Deserialize<MyType>(json);

// ✅ RIGHT — source-gen
[JsonSerializable(typeof(MyType))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class MyJsonContext : JsonSerializerContext { }

var json = JsonSerializer.Serialize(myObj, MyJsonContext.Default.MyType);
var obj  = JsonSerializer.Deserialize(json, MyJsonContext.Default.MyType);
```

> **Harbor status:** `OpenAiCompatibleLlmClient.BuildRequest` uses reflection-based
> `JsonSerializer.Serialize<Dictionary<string, object?>>`. See §PERF-002.

### 21. `new StringBuilder()` on hot path

**What.** `var sb = new StringBuilder();` in a method called >1,000 times/sec.

**What goes wrong.** Allocates a new `StringBuilder` (and its internal char
buffer) per call. GC pressure.

**Right way.** `StringBuilderPool.Rent()`.

```csharp
// ❌ WRONG — alloc per call
public string FormatMany(List<string> items)
{
    var sb = new StringBuilder();
    foreach (var s in items) sb.AppendLine(s);
    return sb.ToString();
}

// ✅ RIGHT — pooled
public string FormatMany(List<string> items)
{
    using var sb = StringBuilderPool.Rent();
    foreach (var s in items) sb.Builder.AppendLine(s);
    return sb.ToString();
}
```

### 22. `foreach` on `IEnumerable<T>` for hot iteration

**What.** `foreach (var x in list)` where `list` is `IEnumerable<T>` (not `List<T>`).

**What goes wrong.** Allocates an enumerator struct (boxing if via interface).

**Right way.** `for (int i = 0; i < list.Count; i++)` on `IReadOnlyList<T>` or
`T[]`.

```csharp
// ❌ WRONG — foreach on interface (may allocate enumerator)
IEnumerable<Subscription> subs = _subscriptions;
foreach (var s in subs) { /* ... */ }

// ✅ RIGHT — for on indexed collection
var subs = _subscriptions;   // ImmutableArray<Subscription>
int n = subs.Length;
for (int i = 0; i < n; i++)
{
    var s = subs[i];
    // ...
}
```

### 23. `string.Format` / interpolation on hot path

**What.** `$"Hello {name} from {place}"` in a tight loop.

**What goes wrong.** Allocates `object[]` for args + the formatted string. Box
if any arg is value type.

**Right way.** `StringBuilder.Append` (pooled), or `string.Create` for very hot paths.

```csharp
// ❌ WRONG — interpolation allocates
public string Format(int n, string name)
{
    return $"[{n}] {name}";   // allocates object[] + string
}

// ✅ RIGHT — pooled StringBuilder
public string Format(int n, string name)
{
    using var sb = StringBuilderPool.Rent(32);
    sb.Builder.Append('[').Append(n).Append("] ").Append(name);
    return sb.ToString();
}

// ✅ BEST — string.Create (zero intermediate alloc)
public string Format(int n, string name) =>
    string.Create(4 + name.Length, (n, name), (span, state) =>
    {
        span[0] = '[';
        ((Span<char>)span)[1] = (char)('0' + (state.n % 10));
        span[2] = ']';
        span[3] = ' ';
        state.name.AsSpan().CopyTo(span[4..]);
    });
```

### 24. Unpooled transient buffers

**What.** `byte[] buf = new byte[8192];` in a method called frequently.

**What goes wrong.** Allocates 8KB per call. Gen0 GC thrash.

**Right way.** `ArrayPool<byte>.Shared.Rent(8192)`.

```csharp
// ❌ WRONG — alloc per call
public byte[] ReadAll(string path)
{
    var buf = new byte[8192];
    using var fs = File.OpenRead(path);
    int n = fs.Read(buf, 0, 8192);
    return buf[..n];
}

// ✅ RIGHT — pooled
public byte[] ReadAll(string path)
{
    byte[] buf = ArrayPool<byte>.Shared.Rent(8192);
    try
    {
        using var fs = File.OpenRead(path);
        int n = fs.Read(buf, 0, 8192);
        return buf[..n].ToArray();   // copy out before returning
    }
    finally
    {
        Array.Clear(buf, 0, 8192);   // don't keep refs alive
        ArrayPool<byte>.Shared.Return(buf);
    }
}
```

### 25. `JsonSerializer.Deserialize<dynamic>` or `object`

**What.** Deserializing to `dynamic` or `object` to "avoid typing".

**What goes wrong.** Reflection, no compile-time safety, slow.

**Right way.** Strongly-typed records + `JsonSerializerContext`.

```csharp
// ❌ WRONG — dynamic
var dyn = JsonSerializer.Deserialize<dynamic>(json);
var name = dyn.name;   // reflection

// ✅ RIGHT — strongly typed
var obj = JsonSerializer.Deserialize(json, MyJsonContext.Default.Foo);
var name = obj.Name;
```

---

## Concurrency

### 26. `lock` on hot path

**What.** `lock (_lock) { ... }` in a method called >1,000 times/sec.

**What goes wrong.** Contention kills throughput. Thundering herd on wakeup.

**Right way.** `Interlocked`, `ImmutableArray<T>` + `ImmutableInterlocked.Update`,
or `NonBlocking.ConcurrentDictionary`.

```csharp
// ❌ WRONG — lock per dispatch
private readonly object _lock = new();
private List<Subscription> _subs = new();

public void Subscribe(Subscription s)
{
    lock (_lock) { _subs.Add(s); }   // contention
}

// ✅ RIGHT — ImmutableInterlocked
private ImmutableArray<Subscription> _subs = ImmutableArray<Subscription>.Empty;

public void Subscribe(Subscription s)
{
    ImmutableInterlocked.Update(ref _subs, arr => arr.Add(s));
}
```

> **Harbor status:** `UiStore.Dispatch` uses `lock` per dispatch. See §PERF-007.

### 27. `async`/`await` with `ContinueWith`

**What.** Mixing `ContinueWith` with `async`/`await` for non-fire-and-forget code.

**What goes wrong.** Confusing control flow, harder to debug, edge cases around
`TaskScheduler`.

**Right way.** `async`/`await` everywhere. `ContinueWith` only for fire-and-forget
with `OnlyOnFaulted`.

```csharp
// ❌ WRONG — ContinueWith for control flow
var result = SomeAsync().ContinueWith(t => t.Result * 2)
                        .ContinueWith(t => t.Result + 1);

// ✅ RIGHT — async/await
async Task<int> DoAsync()
{
    var x = await SomeAsync().ConfigureAwait(false);
    return x * 2 + 1;
}
```

### 28. `CancellationToken` not threaded through

**What.** Async method ignores the `CancellationToken` parameter.

**What goes wrong.** User presses Ctrl+C → request hangs forever.

**Right way.** Pass `CancellationToken` to every async call. Check
`ct.IsCancellationRequested` in tight loops.

```csharp
// ❌ WRONG — ct ignored
public async Task<long> CountLinesAsync(string path, CancellationToken ct = default)
{
    long count = 0;
    foreach (var line in File.ReadLines(path))   // ← ct not passed
        count++;
    return count;
}

// ✅ RIGHT — ct threaded through + checked
public async Task<long> CountLinesAsync(string path, CancellationToken ct = default)
{
    long count = 0;
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var sr = new StreamReader(fs);
    string? line;
    while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
    {
        ct.ThrowIfCancellationRequested();
        count++;
    }
    return count;
}
```

### 29. Non-thread-safe singleton

**What.** `AddSingleton<T>()` where `T` has mutable instance state without
synchronization.

**What goes wrong.** Race conditions when concurrent requests hit the singleton.

**Right way.** Either (a) make state immutable, (b) `ConcurrentDictionary` /
`lock` / `Interlocked`, or (c) per-call local state.

See antipattern #3 (mutable singleton state) for the code example.

---

## AOT (NativeAOT)

### 30. `AssemblyLoadContext` collectible

**What.** `new AssemblyLoadContext("plugins", isCollectible: true)` for hot-reload
of plugins.

**What goes wrong.** NativeAOT doesn't support collectible ALCs (reflection
emit not available). Crashes on AOT publish.

**Right way.** Use out-of-process plugin host (separate JIT process), or
Roslyn-compiled in-process plugins (no unload).

```csharp
// ❌ WRONG — collectible ALC, fails on AOT
var alc = new AssemblyLoadContext("plugins", isCollectible: true);
var asm = alc.LoadFromAssemblyPath(pluginPath);
// ... use types from asm
alc.Unload();   // ← NativeAOT: not supported

// ✅ RIGHT — Roslyn-compiled in-process (no unload, but works on AOT)
// TODO: subagent #1's CsPluginLoader — compiles .cs file at startup, no unload.

// ✅ ALSO RIGHT — out-of-process plugin host (JIT process, separate)
// v0.7+ architecture: Core (AOT) + TUI (JIT) via UDS.
```

### 31. Reflection emit

**What.** `DynamicMethod`, `Expression.CompileToDynamicMethod`, `Reflection.Emit`.

**What goes wrong.** NativeAOT doesn't support runtime codegen.

**Right way.** Source generators (compile-time), or pre-compiled assemblies.

```csharp
// ❌ WRONG — reflection emit, fails on AOT
var dm = new DynamicMethod("Foo", typeof(int), new[] { typeof(int) });
var il = dm.GetILGenerator();
il.Emit(OpCodes.Ldarg_0);
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Add);
il.Emit(OpCodes.Ret);
var func = (Func<int, int>)dm.CreateDelegate(typeof(Func<int, int>));

// ✅ RIGHT — source generator (compile-time)
[Mapper]
public partial class MyMapper
{
    public partial Dto Map(Entity e);
}
```

### 32. `Type.GetProperties()` reflection

**What.** Iterating type properties at runtime via reflection.

**What goes wrong.** Slow, fails under AOT (trimming removes metadata).

**Right way.** Source-generated, or known schema (records with explicit fields).

```csharp
// ❌ WRONG — reflection
var props = typeof(Foo).GetProperties();
foreach (var p in props)
{
    var val = p.GetValue(obj);
    // ...
}

// ✅ RIGHT — pattern-match on known type
switch (obj)
{
    case Foo f: ProcessFoo(f); break;
    case Bar b: ProcessBar(b); break;
    // ...
}
```

### 33. Newtonsoft.Json

**What.** Using `Newtonsoft.Json` for serialization.

**What goes wrong.** Reflection-based, slow, AOT-incompatible (IL2026 warnings).

**Right way.** `System.Text.Json` + `JsonSerializerContext` source-gen.

```csharp
// ❌ WRONG — Newtonsoft
using Newtonsoft.Json;
var json = JsonConvert.SerializeObject(obj);
var obj  = JsonConvert.DeserializeObject<Foo>(json);

// ✅ RIGHT — System.Text.Json + source-gen
using System.Text.Json;
var json = JsonSerializer.Serialize(obj, MyJsonContext.Default.Foo);
var obj  = JsonSerializer.Deserialize(json, MyJsonContext.Default.Foo);
```

### 34. EF Core

**What.** Entity Framework Core for data access.

**What goes wrong.** Heavy, reflection-based, AOT-incompatible, slow startup.

**Right way.** Dapper, raw ADO.NET, or custom repositories. Harbor uses
`Microsoft.Data.Sqlite` directly via `SqliteSessionStore`.

```csharp
// ❌ WRONG — EF Core, AOT-incompatible
public class MyDbContext : DbContext
{
    public DbSet<Foo> Foos => Set<Foo>();
}

// ✅ RIGHT — raw ADO.NET
using var conn = new SqliteConnection($"Data Source={path}");
await conn.OpenAsync(ct);
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT id, name FROM foos WHERE id = @id";
cmd.Parameters.AddWithValue("@id", id);
using var reader = await cmd.ExecuteReaderAsync(ct);
while (await reader.ReadAsync(ct))
{
    var foo = new Foo(reader.GetString(0), reader.GetString(1));
}
```

---

## Testing

### 35. `Assert.That(() => method()).Throws<T>()`

**What.** TUnit 0.50 `.Throws<T>()` doesn't compile for non-`Action` overloads.

**What goes wrong.** Compile error, or false positive when it does compile.

**Right way.** `try` / `catch` (explicit), or design the API to return `Result<T>`
(no exception to catch).

```csharp
// ❌ WRONG — TUnit 0.50 limitation
await Assert.That(() => MethodThatThrows()).Throws<ArgumentException>();

// ✅ RIGHT — try/catch
try
{
    MethodThatThrows();
    await Assert.That(false).IsTrue();   // fail if we got here
}
catch (ArgumentException) { /* expected */ }

// ✅ BEST — Result<T> (no exception to catch)
var result = MethodThatMightFail();
await Assert.That(result.IsFailure).IsTrue();
await Assert.That(result.Error).Contains("expected error");
```

### 36. `DateTime.Now` in tests

**What.** Tests that use `DateTime.Now` or `DateTime.UtcNow` directly.

**What goes wrong.** Non-deterministic, time-sensitive tests flake.

**Right way.** Inject `TimeProvider` (or `Func<DateTimeOffset>`), use a fixed
clock in tests.

```csharp
// ❌ WRONG — non-deterministic
[Test]
public async Task CreatedAt_IsNow()
{
    var session = Session.Create(...);
    await Assert.That(session.CreatedAt).IsEqualTo(DateTimeOffset.UtcNow);
    // ← flaky: might be off by a tick
}

// ✅ RIGHT — inject clock (planned)
public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

[Test]
public async Task CreatedAt_UsesClock()
{
    var clock = new FakeClock();
    var session = Session.Create(..., clock);
    await Assert.That(session.CreatedAt).IsEqualTo(clock.Now);
}
```

### 37. Network calls in unit tests

**What.** Tests that call real HTTP endpoints (e.g. real LLM provider).

**What goes wrong.** Slow, flaky (network down), costly (real API calls),
non-deterministic (rate limits, model output).

**Right way.** Mock `ILlmClient` or use `HttpMessageHandler` for HTTP. Real
calls only in E2E tests guarded by `HARBOR_E2E` env var.

```csharp
// ❌ WRONG — real network call in unit test
[Test]
public async Task Ask_ReturnsResponse()
{
    var client = new OpenAiCompatibleLlmClient(...);
    var result = await client.StreamAsync(req, ct);
    // ← slow, costs money, flaky
}

// ✅ RIGHT — stub ILlmClient
public sealed class StubLlmClient : ILlmClient
{
    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest req, CancellationToken ct = default)
    {
        return Stream();
        async IAsyncEnumerable<LlmEvent> Stream()
        {
            yield return new TextDeltaEvent("1", "Hello");
            yield return new StepFinishEvent(1, "stop", new Usage(10, 5));
        }
    }
}
```

### 38. `void` test method

**What.** `[Test] public void Method_State_Expected()` — synchronous void test.

**What goes wrong.** TUnit source-generated discovery requires `public async Task`.

**Right way.** `public async Task`, even if the body doesn't await anything.

```csharp
// ❌ WRONG — void test, not discovered
[Test]
public void Add_TwoPlusTwo_EqualsFour()
{
    Assert.That(2 + 2).IsEqualTo(4);
}

// ✅ RIGHT — async Task
[Test]
public async Task Add_TwoPlusTwo_EqualsFour()
{
    await Assert.That(2 + 2).IsEqualTo(4);
}
```

---

## See also

- [PATTERNS.md](./PATTERNS.md) — что мы ДЕЛАЕМ (18 паттернов).
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 41 known violation в Harbor.
- [CLAUDE.md](../CLAUDE.md) §Code review checklist — что проверять в PR.
- [DEVELOPMENT.md](./DEVELOPMENT.md) §Principles checklist — расширенный чек-лист.
