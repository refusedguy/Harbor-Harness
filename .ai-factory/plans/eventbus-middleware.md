# Plan — EventBus Middleware

Создано: 2026-07-22 19:51
Ветка: desktop-apps

## Original Request

eventbus-middleware

## Settings

- **Тесты:** да
- **Логирование:** verbose (DEBUG)
- **Документация:** да, обязательный docs-checkpoint
- **Roadmap:** нет `.ai-factory/ROADMAP.md`, привязка пропущена

## Цель

Добавить в `IEventBus` / `InMemoryEventBus` extensible middleware-конвейер с **нулевыми аллокациями** на hot path, без блокировок, без рефлексии. Middleware может фильтровать, оборачивать или преобразовывать события ДО fan-out现有 subscriber'ов.

## Ограничения

- Нет `ref struct` / `ref AgentEvent` на интерфейсе (не AOT-friendly для泛型)
- Нет аллокаций в `ProcessAsync` для синхронных middleware (`ValueTask.FromResult`)
- Нет `lock` в `PublishAsync` на happy path (`ImmutableInterlocked` уже обеспечивает lock-free)
- Pattern matching вместо `GetType()` + reflection
- Middleware **не может** отменить `PublishAsync` полностью — только drop отдельное событие (возврат `false`)

## Задачи

### Task 1: Интерфейс `IEventBusMiddleware`

**Файл:** `src/Harbor.Abstractions/Events/IEventBusMiddleware.cs` (новый)

Создать интерфейс в `Harbor.Abstractions/Events/`:

```csharp
namespace Harbor.Abstractions.Events;

public interface IEventBusMiddleware
{
    string Name { get; }

    ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default);
}
```

- `Name` — для логирования (какой middleware отфильтровал событие)
- `ProcessAsync` возвращает `bool`: `true` = передать дальше, `false` = drop
- `ref AgentEvent` — middleware может заменить событие in-place (для record `with` будет аллокация, но сама передача по ref лишний copy не делает)
- `ValueTask<bool>` — для sync middlewares: `ValueTask.FromResult(true/false)` (zero alloc)

**Логирование:** нет собственного логирования на этом уровне (логирует `InMemoryEventBus`).

**Блокирующие зависимости:** нет.

---

### Task 2: Middleware pipeline в `InMemoryEventBus`

**Файл:** `src/Harbor.Registries/Events/InMemoryEventBus.cs`

Изменения:

1. Добавить поле `_middlewares: IReadOnlyList<IEventBusMiddleware>`
2. Добавить конструктор с `IEnumerable<IEventBusMiddleware>`
3. Изменить `PublishAsync`:

```csharp
public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
{
    // ── Middleware pipeline (BEFORE scrollback + fan-out) ──
    foreach (var mw in _middlewares)
    {
        try
        {
            bool continuePipeline = await mw.ProcessAsync(ref @event, ct).ConfigureAwait(false);
            if (!continuePipeline)
            {
                _logger.LogTrace("Event {EventType} dropped by middleware {Middleware}",
                    @event.GetType().Name, mw.Name);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Middleware {Middleware} threw — event dropped", mw.Name);
            return;
        }
    }

    // ── 1. AppendScrollback (unchanged) ──
    AppendScrollback(@event);

    // ── 2. Fan-out (unchanged) ──
    var snapshot = _subscriptions;
    ...
}
```

**Ключевые моменты:**
- Pipeline выполняется **ДО** `AppendScrollback` — dropped events не попадают в scrollback
- `ConfigureAwait(false)` — обязателен для library-кода
- Exception в middleware → drop event + WARN лог (не ломает весь bus)
- `foreach` по `IReadOnlyList<IEventBusMiddleware>` — JIT инлайнит для малых размеров

**Логирование:** `LogTrace` при dropped event, `LogWarning` при middleware exception.

**Блокирующие зависимости:** Task 1.

---

### Task 3: Регистрация в DI

**Файл:** `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` — метод `RegisterCore`

Изменение одной строки:

```csharp
// ДО:
builder.Services.AddSingleton<IEventBus>(sp => 
    new InMemoryEventBus(sp.GetRequiredService<ILogger<InMemoryEventBus>>()));

// ПОСЛЕ:
builder.Services.AddSingleton<IEventBus>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<InMemoryEventBus>>();
    var middlewares = sp.GetServices<IEventBusMiddleware>().ToArray();
    return new InMemoryEventBus(logger, maxScrollback: 1000, middlewares);
});
```

- `sp.GetServices<IEventBusMiddleware>()` — резолвит все зарегистрированные middleware в DI
- `.ToArray()` —ONE аллокация при старте, на runtime массив читается без allocations
- `InMemoryEventBus` принимает `IReadOnlyList<IEventBusMiddleware>` в конструкторе

**Логирование:** нет изменений (логирует сам `InMemoryEventBus`).

**Блокирующие зависимости:** Task 2.

---

### Task 4: Built-in middlewares

**Файлы:**
- `src/Harbor.Core/Events/SamplingMiddleware.cs` (новый)
- `src/Harbor.Core/Events/TypeFilterMiddleware.cs` (новый)

#### 4a: SamplingMiddleware

```csharp
namespace Harbor.Core.Events;

public sealed class SamplingMiddleware : IEventBusMiddleware
{
    public string Name => "sampling";
    
    private readonly ILogger _logger;
    private readonly double _rate;
    private readonly Random _rnd;

    public SamplingMiddleware(ILogger<SamplingMiddleware> logger, double rate = 0.1)
    {
        _logger = logger;
        _rate = rate;
        _rnd = new Random();
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is MessageUpdateEvent)
        {
            if (_rnd.NextDouble() > _rate)
            {
                _logger.LogTrace("Sampled out MessageUpdateEvent");
                return ValueTask.FromResult(false);
            }
        }
        return ValueTask.FromResult(true);
    }
}
```

- Пропускает 90% `MessageUpdateEvent` (настраиваемый `rate`)
- `is MessageUpdateEvent` — sealed type check, zero reflection
- `Random` создаётся один раз в конструкторе

#### 4b: TypeFilterMiddleware

```csharp
namespace Harbor.Core.Events;

public sealed class TypeFilterMiddleware : IEventBusMiddleware
{
    public string Name => "type-filter";
    
    private readonly ILogger _logger;
    private readonly Type[] _allowedTypes;
    private readonly bool _allowAll;

    public TypeFilterMiddleware(ILogger<TypeFilterMiddleware> logger, params Type[] allowedTypes)
    {
        _logger = logger;
        if (allowedTypes.Length == 0)
        {
            _allowAll = true;
            _allowedTypes = Array.Empty<Type>();
        }
        else
        {
            _allowAll = false;
            _allowedTypes = allowedTypes;
        }
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        if (_allowAll)
            return ValueTask.FromResult(true);

        int count = _allowedTypes.Length;
        for (int i = 0; i < count; i++)
        {
            if (_allowedTypes[i] == @event.GetType())
                return ValueTask.FromResult(true);
        }

        _logger.LogTrace("Filtered out event type {Type}", @event.GetType().Name);
        return ValueTask.FromResult(false);
    }
}
```

- Жёстко заданные типы в конструкторе, на runtime — `for` loop с type equality
- `GetType()` на record — встроенный CLR метод, не reflection

**Логирование:** оба middleware используют `LogTrace` для dropped events.

**Блокирующие зависимости:** Task 2.

---

### Task 5: Модульные тесты

**Файл:** `tests/Harbor.Core.Tests/EventBusMiddlewareTests.cs` (новый)

Тесты:

1. **Pipeline pass-through** — событие проходит через middleware без изменений
2. **Pipeline drop** — middleware возвращает `false`, событие не достигает subscriber'ов
3. **Pipeline transformation** — middleware изменяет событие, subscriber видит изменённое
4. **Middleware exception** — exception в middleware → event dropped, bus не ломается
5. **Multiple middlewares** — порядок выполнения, short-circuit при drop
6. **SamplingMiddleware** — statistical test (1000 events, verify ~10% pass)
7. **TypeFilterMiddleware** — allowlist работает, unknown type → dropped
8. **Zero allocation на hot path** — `SamplingMiddleware.ProcessAsync` не аллоцирует для `MessageUpdateEvent` (можно проверить через `GC.GetAllocatedBytesForCurrentThread` или `BenchmarkDotNet`)

```csharp
// Пример allocation check:
[Test]
public async Task SamplingMiddleware_ProcessAsync_ZeroAlloc()
{
    var logger = new Mock<ILogger<SamplingMiddleware>>();
    var mw = new SamplingMiddleware(logger.Object, rate: 1.0); // pass all
    
    AgentEvent evt = new MessageStartEvent(new AssistantMessage(...));
    
    long before = GC.GetAllocatedBytesForCurrentThread();
    bool result = await mw.ProcessAsync(ref evt, CancellationToken.None);
    long after = GC.GetAllocatedBytesForCurrentThread();
    
    await Assert.That(result).IsTrue();
    await Assert.That(after - before).IsEqualTo(0);
}
```

**Логирование:** тесты не проверяют логи напрямую (это integration tests), но проверяют что subscriber'ы получают/не получают события.

**Блокирующие зависимости:** Task 1, Task 2, Task 4.

---

### Task 6: Документация

**Файлы:**
- `docs/EVENT_BUS_MIDDLEWARE.md` (новый)
- `AGENTS.md` — секция про middleware (обновление)

Содержание `EVENT_BUS_MIDDLEWARE.md`:
- Обзор middleware-конвейера
- Интерфейс `IEventBusMiddleware` с примером
- Примеры built-in middlewares (`SamplingMiddleware`, `TypeFilterMiddleware`)
- Паттерны: filtering, sampling, enrichment, metrics
- Performance notes: zero-alloc, lock-free, AOT compatibility
- Регистрация в DI

Обновление `AGENTS.md`:
- Добавить в раздел "Event bus" упоминание о middleware
- Ссылка на `docs/EVENT_BUS_MIDDLEWARE.md`

**Логирование:** нет.

**Блокирующие зависимости:** Task 1, Task 2, Task 4.

## Commit Plan

```
commit 1: feat(eventbus): add IEventBusMiddleware interface and pipeline in InMemoryEventBus
  - Task 1: IEventBusMiddleware.cs
  - Task 2: InMemoryEventBus middleware pipeline

commit 2: feat(eventbus): register middleware pipeline in DI, add built-in middlewares
  - Task 3: HostBuilder.RegisterCore DI registration
  - Task 4: SamplingMiddleware + TypeFilterMiddleware

commit 3: test(eventbus): add middleware unit tests and documentation
  - Task 5: EventBusMiddlewareTests.cs
  - Task 6: docs/EVENT_BUS_MIDDLEWARE.md + AGENTS.md update
```

## Следующие шаги

```
/aif:implement

CONTEXT FROM /aif:plan:
- Plan file: .ai-factory/plans/eventbus-middleware.md
- Testing: yes
- Logging: verbose
- Docs: yes
```
