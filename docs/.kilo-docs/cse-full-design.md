# CSharpFunctionalExtensions 3.7.0 — полный каталог API + дизайн максимального внедрения в Harbor

> **СТАТУС (2026-08-27): внедрение закрыто** волнами ROP-B/C/D — см. ADR-002 в [DECISIONS.md](../DECISIONS.md).
> Числа аудита ниже («0 fluent-вызовов», ~94 if-IsFailure) — снимок @ dev/5b01b2d и больше не отражают код;
> самоделка `ResultGuard.Try/TryAsync`, упомянутая здесь, впоследствии удалена (`9e954a5`) в пользу CSE `Result.Try`.
> Каталог API (часть 1) остаётся справедливым справочником по CSE 3.7.0.

> **Библия спринта ROP.** Repo `/mnt/projects/Harbor-Harness`, ветка `dev`, HEAD `5b01b2d`.
> Пакет: `CSharpFunctionalExtensions 3.7.0` (Directory.Packages.props, группа «Performance»).
> Источники каталога: XML-доки из `~/.nuget/packages/csharpfunctionalextensions/3.7.0/lib/net8.0/CSharpFunctionalExtensions.xml`
> (1076 документированных членов) + исходники github.com/vkhorikov/CSharpFunctionalExtensions (master).
> Полная выгрузка сигнатур: `/tmp/harbor-analysis/cse-api-catalog.txt`.
>
> Факт аудита: на весь репо — **1** fluent-вызов `.Match()` (AgentLoop), **0** вызовов Bind/Map/Tap/Ensure,
> ~94 императивных `if (.IsFailure)`, ~49 `if (.IsSuccess)`, 6 ручных try/catch→Result блоков в сторах.
> Самоделка `ResultGuard.Try/TryAsync` (Harbor.Abstractions/Results/ResultGuard.cs) дублирует встроенный
> `Result.Try` из CSE и используется только собственными тестами.

---

## Часть 1. ПОЛНЫЙ КАТАЛОГ API CSharpFunctionalExtensions 3.7.0

### 1.1 Ядро типов

| Тип | Суть | Ключевые члены |
|---|---|---|
| `Result` | struct, операция без значения (`UnitResult<EErrors>`-аналог со string-ошибкой) | `IsFailure`, `IsSuccess`, `Error`, `Deconstruct(out bool, out string)` |
| `Result<T>` | операция со значением | всё выше + `Value` (бросает `ResultFailureException` при чтении на Failure) |
| `Result<T,E>` | значение + **типизированная ошибка** | `Error` типа `E`; фабрики `Success<T,E>` / `Failure<T,E>` |
| `UnitResult<E>` | без значения, типизированная ошибка | `UnitResult.Success<E>()`, `UnitResult.Failure<E>(error)` |
| `Maybe<T>` | опциональность как значение (None/Some) | `HasValue`, `Value`, `None`, неявное приведение из `T` и из `T?` |
| `Maybe` (non-generic) | точка входа: `Maybe.From<T>(T?)`, `Maybe.None<T>()` | `From``1` ×4 перегрузки |
| `ValueObject<TSelf>` | база для DDD Value Objects | переопределить `GetEqualityComponents()` → бесплатное структурное равенство/хеш/сравнение |
| `Entity<TId>` | база сущностей с идентификатором | `Id`, equality by Id |
| `ICombine` | моноид для склейки результатов валидаций | `Append(other)` |

Идентификаторы Harbor (`SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName`)
уже наследуют `ValueObject` — это единственная зона, где CSE освоена глубоко.

### 1.2 Статические фабрики `Result`

```csharp
Result.Success()                          Result.Failure(string error)
Result.Success<T>(T value)                Result.Failure<T>(string error)
Result.Success<T,E>(T value)              Result.Failure<T,E>(E error)
Result.SuccessIf(bool cond, string err)   // ×4 перегрузки (в т.ч. generic)
Result.FailureIf(bool cond, ...)          // противоположность SuccessIf
Result.ConvertFailure<TTarget>(this Result r)   // смена типа T у failure без if
```

`SuccessIf/FailureIf` — замена паттерна `if (!cond) return Result.Failure(...); return Result.Success();`.

### 1.3 `Result.Try*` — канонический try/catch→Result (замена ResultGuard)

Полные сигнатуры из XML 3.7.0:

```csharp
// sync
public static Result Try(Action action, Func<Exception,string>? errorHandler = null);
public static Result<T> Try<T>(Func<T> func, Func<Exception,string>? errorHandler = null);
public static Result<T,E> Try<T,E>(Func<T> func, Func<Exception,E> errorHandler);      // типизир. ошибка
public static UnitResult<E> Try<E>(Action action, Func<Exception,E> errorHandler);

// async (имени TryAsync в CSE НЕТ — async-вариант это перегрузка Try над Func<Task>)
public static Task<Result> Try(Func<Task> action, Func<Exception,string>? errorHandler = null);
public static Task<Result<T>> Try<T>(Func<Task<T>> func, Func<Exception,string>? errorHandler = null);
public static Task<Result<T,E>> Try<T,E>(Func<Task<T>> func, Func<Exception,E> errorHandler);
```

Семантика: ловит ВСЁ (`catch Exception`), сообщение — через error handler
(по умолчанию `Configuration.DefaultTryErrorHandler` = `ex.Message`). ВАЖНО: `OperationCanceledException`
тоже глотается — см. §4.1 про сохранение Esc-семантики Harbor (rethrow через error handler).

Родные inline-аналоги внутри цепочек:
- `MapTry(func [, errorHandler])` — Map с автоловлей исключений;
- `BindTry(func [, errorHandler])` — Bind с автоловлей;
- `TapTry(action)` / `TapIfTry(cond, action)` — side-effect с автоловлей;
- `OnSuccessTry` — legacy-имя TapTry, не использовать.

### 1.4 Комбинаторы-расширения (класс `ResultExtensions`, 290 методов)

| Семейство | Сигнатура (ядро) | Назначение |
|---|---|---|
| `Map` | `Result<U> Map<T,U>(this Result<T>, Func<T,U>)` | преобразовать значение, не трогая railway |
| `Map``2/3/4` | перегрузки под `Task` справа | async-Mapping |
| `Bind` | `Result<U> Bind<T,U>(this Result<T>, Func<T,Result<U>>)` | следующее падение шага |
| `Bind``1/2/3` | перегрузки: Task слева / справа / оба | async-Bind |
| `Tap` | `Result<T> Tap<T>(this Result<T>, Action<T>)` | side-effect только на Success, результат пробрасывается |
| `TapIf` | `+ условие` | условный side-effect |
| `TapError` | `Result<T> TapError<T>(Action<string>)` | side-effect только на Failure (лог!) |
| `TapErrorIf` | `+ условие` | условный лог ошибки |
| `MapError` | `Result<T> MapError<T>(this Result<T>, Func<string,string>)` | переформатировать ошибку |
| `MapError``1/2` | async-перегрузки | — |
| `Ensure` | `Result<T> Ensure<T>(this Result<T>, Func<T,bool> pred, string err)` | инвариант после Map; ×13 перегрузок вкл. async-predicate и `Ensure``2` c Func<T,string> err |
| `EnsureNotNull``1` | ×4 — распаковка nullable в Result | мост T? → Result<T> |
| `Check` | `Result<T> Check<T>(this Result<T>, Func<T,Result>)` | кросс-полевая валидация: failure проверки заменяет результат, иначе исходный T сохраняется |
| `CheckIf` | условный Check ×10 | — |
| `Compensate` | `Result<T> Compensate<T>(this Result<T>, Func<string,Result<T>>)` | fallback/retry ТОЛЬКО на Failure |
| `Compensate``1/2/3` | ×9 перегрузок, вкл. смену типа и async | — |
| `OnFailureCompensate` | legacy-имя Compensate | не использовать |
| `Finally` | `U Finally<T,U>(this Result<T>, Func<T?,string?,U>)` | исчерпывающая развёртка в обычное значение |
| `Match` | `U Match<T,U>(this Result<T>, Func<T,U> ok, Func<string,U> err)` | исчерпывающая развёртка (то же, что Finally, но ok получает T?) |
| `Deconstruct` | `Deconstruct(out bool isSuccess, out T? value, out string? error)` | деструктуризация |
| `Select` / `SelectMany` | LINQ query syntax поверх Result/Maybe | рекомендуется только в query expressions |
| `WithTransactionScope` / `MapWithTransactionScope` / `BindWithTransactionScope` | обёртка в TransactionScope | Harbor не использует (нет БД-транзакций) |
| `BindZip` | zip двух результатов в кортеж | редкая, но полезная |

Условные варианты всех основных комбинаторов: `MapIf`, `BindIf`, `TapIf`, `CheckIf`, `TapErrorIf`,
`Where` (для Maybe) — когда ветвление зависит от внешнего флага, а не от значения.

### 1.5 Async-поверхность

Три статических класса на `Task`:
- `AsyncResultExtensionsLeftOperand` (**107** методов) — receiver это `Task<Result<T>>`;
- `AsyncResultExtensionsRightOperand` (**85**) — аргумент-функция возвращает `Task<...>`;
- `AsyncResultExtensionsBothOperands` (**85**) — оба асинхронны.

Плюс зеркала `CSharpFunctionalExtensions.ValueTasks.*` (LeftOperand 89 / RightOperand 69 / BothOperands 67 /
ResultExtensions 112 / MaybeExtensions 31) — те же имена на `ValueTask`. Для Harbor (горячий путь агента на
`Task`) актуальны Task-версии; ValueTask-версии держать в уме при переходе стор/клиентов на ValueTask.

Имена методов НЕ удваиваются суффиксом Async: `await result.Bind(NextStepAsync)` — это BothOperands-перегрузка.

### 1.6 Батч/агрегация

```csharp
static Result Combine(params Result[] results);                  // ×3
static Result Combine(IEnumerable<Result> results);              // все ошибки через "; "
static Result Combine<T1,...,Tn>(Result<T1>, ..., Result<Tn>);   // generic ×7: Success = кортеж значений
static Result Combine<T1,...,Tn,E>(...);                         // ×4 с типизированной ошибкой
static Result FirstFailureOrSuccess(params Result[] results);    // первый failure ИЛИ success, без конкатенации
CombineInOrder(...)                                              // async-версия: последовательный прогон, стоп на первой ошибке
```

Разница Combine vs FirstFailureOrSuccess: Combine агрегирует ВСЕ ошибки (конфиг-валидация),
FirstFailureOrSuccess — быстрый fail-fast без аллокаций списка ошибок (пакет обязательных предусловий).

### 1.7 Maybe<T>

Члены типа `Maybe<T>`: `GetValueOrThrow()` ×2, `TryGetValue(out T)`; свойства `HasValue`/`Value`;
статика `Maybe.From<T>`. Расширения (`MaybeExtensions`, 48 методов):

- `AsMaybe()` ×4 — из `T?` / из `Result<T>` (Failure → None);
- `Optional()` ×2 — пометить nullable как опциональный (None вместо ошибки);
- `ToResult(err)` / `ToUnitResult` — мост Maybe→Result (None становится Failure);
- `Map` / `Bind` / `Tap` / `Execute` / `ExecuteNoValue` ×4 — тот же railway;
- `Or` ×13 — `maybe.Or(fallbackValue)` / `Or(Func<T>)` / `Or(Maybe<T>)` — элегантная замена `?? `;
- `Flatten` — Maybe<Maybe<T>> → Maybe<T>;
- `AsNullable` — обратно в мир nullables;
- `Where` — фильтрация значения;
- `Select` / `SelectMany` — LINQ syntax;
- `Unwrap`/`GetValueOrDefault` — дефолты;
- async-зеркала в `ValueTasks.MaybeExtensions` (31).

### 1.8 Инфраструктура / вспомогательное

- **JSON**: System.Text.Json конвертеры `ResultJsonConverter`, `ResultJsonOfTConverter`,
  `ResultOfTEJsonConverterFactory`, `UnitResultJsonConverterFactoryOfT`, DTO-формы
  (`ResultDto`, `UnitResultDto`); подключение: `options.AddCSharpFunctionalExtensionsConverters()`.
  Актуально для Harbor.Storage.Jsonl (сессии сериализуются вручную — можно упростить, если Result попадёт в DTO).
- **Конфигурация**: `Configuration.DefaultTryErrorHandler` — глобальный формат ошибки для всех Try*;
  `Configuration.ValueObjectCreationExceptionHandler`.
- `ResultFailureException` / `ResultSuccessException` — бросаются при чтении `.Value` на Failure / `.Error` на Success.
- `ToString()` переопределения для диагностики.

---

## Часть 2. АУДИТ ТЕКУЩЕГО ИСПОЛЬЗОВАНИЯ

### 2.1 Количественно (grep по `src/`, HEAD 5b01b2d)

| Метрика | Значение |
|---|---|
| `using CSharpFunctionalExtensions` / GlobalUsings | 46 файлов, 21 проект |
| Fluent-комбинаторы (`Bind`/`Map`/`Tap`/`Ensure`/`MapError`/`Compensate`/`Check`) | **0 вызовов** |
| `.Match(` | **1** (AgentLoop.cs:185, compactionResult) |
| `if (...IsFailure)` ранние возвраты | ~94 |
| `if (...IsSuccess)` | ~49 |
| Ручные `try/catch → Result.Failure(ex.Message)` | 6+ блоков (SqliteSessionStore ×6 методов, JsonlSessionStore, ProviderRegistry.GetClient ×2, RemoteGateway) |
| `ResultGuard.Try/TryAsync` в проде | **0 вызовов** — только tests/Harbor.Abstractions.Tests/ResultGuardTests.cs |
| Реальное «использование на 6 вызовов» | фабрики `Result.Success/Failure` в идентификаторах + 1 Match |

Вывод: библиотека подключена глобально через GlobalUsings почти везде, но используется
на уровне конструкторов значений. Весь railway-потенциал не задействован.

### 2.2 Качественно — типовые антипаттерны

1. **Лестница `if (x.IsFailure) return Result.Failure(x.Error)` ×N** — AgentLoop.RunAsync:110–142,
   CompactionService:461–470, PermissionService.CheckAsync:58–65, HarborConfig.Normalize:279–331,
   OnboardingWizard:56–78. Каждый шаг дублирует проброс ошибки; тип `Result<T>` известен компилятору,
   но связка делается вручную.
2. **try/catch→ex.Message руками** — SqliteSessionStore (CreateAsync/GetAsync/ListAsync/AppendMessage/
   UpdateMessageAsync/DeleteAsync), JsonlSessionStore.CreateAsync, ProviderRegistry.GetClient.
   Это ровно `Result.Try`, включая потерянный стек и отсутствие единообразия сообщений.
3. **Самоделка поверх самоделки**: ResultGuard.TryAsync повторяет `Result.Try<T>(Func<Task<T>>)`,
   добавляя только rethrow OperationCanceledException — см. §3.1.
4. **Кастомные Result-подобные структуры**: EditTool.EditResult (Fail/Success) рядом с настоящим `Result`.
5. **Логирование ветвлением вместо TapError/TapErrorIf**: RegistriesModule:116–128, ConfigurationModule:45–60,
   McpRegistry.LoadFromFile:90–130, DefaultAgent:295/521.
6. **Fallback-цепочки if'ами**: AuthStore.GetApiKeyAsync (config → preset env → env → aggregated failure)
   и CompactionService.TryResolveSecondaryAsync — канонические `Compensate`.
7. **Батч-валидация руками**: HarborConfig.Validate собирает errors[] и join("; ") — это тело `Result.Combine`.
8. **Мёртвый код ошибки**: DefaultAgent.DefaultSessionContext.UpdateStatsAsync:533 `_ = stats.Error; return;`.

---

## Часть 3. ДИЗАЙН МАКСИМАЛЬНОГО ВНЕДРЕНИЯ ПО ЗОНАМ

Формат пункта: `[ФАЙЛ:СТРОКА]` → verbatim ≤10 строк → CSE-API → sketch → что дешевле.

---

### Зона A. Application/Agents

#### П.1 [Harbor.Application/Agents/AgentLoop.cs:110-118] — лестница TryCreate/GetClient

```csharp
var providerIdResult = ProviderId.TryCreate(agent.ProviderId);
if (providerIdResult.IsFailure)
    return Result.Failure(providerIdResult.Error);

var clientResult = _providers.GetClient(providerIdResult.Value);
if (clientResult.IsFailure)
    return Result.Failure(clientResult.Error);

var client = clientResult.Value;
```
→ CSE-API: `ResultExtensions.Bind``2` — async-перегрузка BothOperands:
`Task<Result<U>> Bind<T,U>(this Task<Result<T>>, Func<T,Task<Result<U>>>)`
→ SKETCH:
```csharp
var client = await ProviderId.TryCreate(agent.ProviderId)
    .Bind(id => _providers.GetClient(id))
    .ConfigureAwait(false);
if (client.IsFailure) return Result.Failure(client.Error);
```
→ ЧТО ДЕШЕВЛЕ: −6 строк на каждый такой стык; ошибка не может быть потеряна при рефакторинге
(компилятор заставляет тянуть railway); исчезает промежуточная переменная providerIdResult.Value.

#### П.2 [AgentLoop.cs:132-138] — GetModelsAsync + кэш-мисс

```csharp
var modelsResult = await client.GetModelsAsync(ct).ConfigureAwait(false);
if (modelsResult.IsFailure)
    return Result.Failure(modelsResult.Error);

models = [.. modelsResult.Value];
_modelCatalogCache[providerId.Value] = (models, DateTimeOffset.UtcNow.Add(ModelCatalogTtl));
```
→ CSE-API: `Map``1` — `Result<U> Map<T,U>(this Result<T>, Func<T,U>)`; кэш-запись — `Tap``1`.
→ SKETCH:
```csharp
models = await client.GetModelsAsync(ct)
    .Map(m => { var arr = m.ToArray(); _modelCatalogCache[providerId.Value] = (arr, DateTimeOffset.UtcNow.Add(ModelCatalogTtl)); return arr; })
    .ConfigureAwait(false);
// либо Map для проекции + отдельный Tap для записи в кэш
```
→ ЧТО ДЕШЕВЛЕ: happy-path читается сверху вниз; side-effect (кэш) помечен комбинатором,
а не спрятан в присваивании.

#### П.3 [AgentLoop.cs:140-142] — FindModel null-check

```csharp
var model = FindModel(models, agent.Model);
if (model is null)
    return Result.Failure($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.");
```
→ CSE-API: `Maybe.From<T>` + `ToResult(error)` (MaybeExtensions) или напрямую
`EnsureNotNull``1` после Map.
→ SKETCH:
```csharp
var modelResult = Maybe.From(FindModel(models, agent.Model))
    .ToResult($"Model '{agent.Model}' not found in provider '{agent.ProviderId}'.");
return await modelResult.Bind(m => RunTurnsAsync(session, agent, m, ct));
```
→ ЧТО ДЕШЕВЛЕ: null-семантика («модель может отсутствовать») выражена типом Maybe,
не соглашением «null значит нет»; ToResult даёт единую ошибку без ручного if.

#### П.4 [ToolDispatcher.cs:156-186] — двойной guard toolName/toolResult

```csharp
var toolNameResult = ToolName.TryCreate(toolCall.ToolName);
if (toolNameResult.IsFailure)
{
    return new ToolResultEntry(toolCall.Id, toolCall.ToolName,
        $"Invalid tool name: {toolNameResult.Error}", true);
}

var toolResult = tools.GetTool(toolNameResult.Value);
if (toolResult.IsFailure)
{ /* ...длинный блок "available tools"... */ }
```
→ CSE-API: `Bind``1` + `MapError``1` (переформатирование ошибки с контекстом).
→ SKETCH:
```csharp
var resolved = ToolName.TryCreate(toolCall.ToolName)
    .MapError(e => $"Invalid tool name: {e}")
    .Bind(name => tools.GetTool(name));
if (resolved.IsFailure)
    return ToolErrorEntry(toolCall, resolved.Error, availableTools: tools.GetAllTools());
```
→ ЧТО ДЕШЕВЛЕ: два guard-блока схлопываются в один обработчик; MapError локализует
форматирование сообщения у места возникновения.

#### П.5 [ToolDispatcher.cs:122-137] HasSequentialTool — вложенные IsSuccess

```csharp
for (int i = 0; i < toolCalls.Count; i++)
{
    var tc = toolCalls[i];
    var toolNameResult = ToolName.TryCreate(tc.ToolName);
    if (toolNameResult.IsSuccess)
    {
        var toolResult = tools.GetTool(toolNameResult.Value);
        if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
            return true;
    }
}
```
→ CSE-API: `Bind``1` + `Map``1` + `GetOrDefault()` (Maybe/Result дефолт) — предикат без if-лесенки.
→ SKETCH:
```csharp
return toolCalls.Select(tc =>
        ToolName.TryCreate(tc.ToolName).Bind(tools.GetTool))
    .Any(r => r.GetValueOrDefault(ToolOrNone()) is { } t && t.ExecutionMode == ExecutionMode.Sequential);
// либо честный Maybe: TryCreate...AsMaybe().Bind(...)
```
→ ЧТО ДЕШЕВЛЕ: предикат становится one-liner над последовательностью результатов;
вложенность 4 уровня → 1.

#### П.6 [DefaultAgent.cs:449-458] LoadSessionContextAsync — два обязательных результата → throw

```csharp
var session = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
if (session.IsFailure)
    throw new InvalidOperationException(session.Error);

var messages = await _sessionStore.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
if (messages.IsFailure)
    throw new InvalidOperationException(messages.Error);

return new DefaultSessionContext(session.Value, messages.Value, ...);
```
→ CSE-API: `Combine``2` — generic `Result.Combine(Result<T1>, Result<T2>)` → Success = кортеж;
+ `FirstFailureOrSuccess` если нужен fail-fast без агрегации.
→ SKETCH:
```csharp
var combined = await Task.WhenAll(_sessionStore.GetAsync(sessionId, ct),
                                  _sessionStore.GetMessagesAsync(sessionId, ct));
return Result.Combine(combined[0], combined[1])
    .Map(() => new DefaultSessionContext(combined[0].Value, combined[1].Value, ...));
```
→ ЧТО ДЕШЕВЛЕ: параллельная загрузка (сейчас последовательная!) + один throw-пункт;
обе ошибки видны сразу, а не первая попавшаяся.

#### П.7 [DefaultAgent.cs:293-301 и 520-527] persist-ошибка → лог → fail run

```csharp
Result persisted = await _sessionStore.AppendMessageAsync(State.SessionId, message, ct)
    .ConfigureAwait(false);
if (persisted.IsFailure)
{
    _logger.LogError(
        "Failed to persist user message {MessageId} for session {SessionId}: {Error}",
        message.Id, State.SessionId, persisted.Error);
    return Result.Failure($"Failed to persist prompt: {persisted.Error}");
}
```
→ CSE-API: `TapError``1` — `Result<T> TapError<T>(this Result<T>, Action<string>)` для лога +
`MapError``1` для префикса. Тот же приём в DefaultSessionContext.AppendMessageAsync:517-528
(там только лог, без fail — чистый TapError на Result).
→ SKETCH:
```csharp
return await _sessionStore.AppendMessageAsync(State.SessionId, message, ct)
    .TapError(e => _logger.LogError(
        "Failed to persist user message {MessageId} for session {SessionId}: {Error}",
        message.Id, State.SessionId, e))
    .MapError(e => $"Failed to persist prompt: {e}");
```
→ ЧТО ДЕШЕВЛЕ: лог и трансформация ошибки разделены; шаблон повторим для второго сайта
(DefaultSessionContext) без копипасты if-блока.

#### П.8 [DefaultAgent.cs:530-539] UpdateStatsAsync — глушение ошибки с мёртвой строкой

```csharp
var stats = await _store.GetStatsAsync(Session.Id, ct).ConfigureAwait(false);
if (stats.IsFailure)
{
    _ = stats.Error;
    return;
}

var updated = stats.Value.AddUsage(usage);
await _store.UpdateStatsAsync(Session.Id, updated, ct).ConfigureAwait(false);
```
→ CSE-API: `Match` (BothOperands) или `Tap``1` + `TapError``1`.
→ SKETCH:
```csharp
await _store.GetStatsAsync(Session.Id, ct)
    .Tap(s => _store.UpdateStatsAsync(Session.Id, s.AddUsage(usage), ct))
    .TapError(e => _logger.LogWarning("stats unavailable for {SessionId}: {Error}", Session.Id, e));
```
→ ЧТО ДЕШЕВЛЕ: удаляется `_ = stats.Error;` (чтение Error на Failure легально, но строка-мусор);
намерение «best-effort обновление» видно из композиции.

### Зона B. Storage/Sessions (Sqlite/Jsonl)

#### П.9 [Harbor.Storage.Sqlite/SqliteSessionStore.cs:75-100] CreateAsync — ручной try/catch→Result

```csharp
            return Task.FromResult(Result.Success(session));
        }
        catch (Exception ex)
        {
            // Nothing to correlate yet — the session id is generated inside the try.
            _logger.LogError(ex, "Failed to create session");
            return Task.FromResult(Result.Failure<Session>(ex.Message));
        }
    }
```
→ CSE-API: `Result.Try<T>(Func<Task<T>>, Func<Exception,string>)` (async-перегрузка Try над
`Func<Task>`, имени TryAsync в CSE нет). Тот же блок ×6 в этом файле.
→ SKETCH:
```csharp
public Task<Result<Session>> CreateAsync(Session session, CancellationToken ct = default) =>
    Result.Try(async () => { /* текущее тело без try/catch */ return session; },
               ResultErrors.Message);   // хелпер из §4.5: OCE → rethrow, остальное → ex.Message
```
→ ЧТО ДЕШЕВЛЕ: −7 строк на каждый из 6+ методов; единообразный текст ошибок; OCE больше не
превращается в «domain failure» (см. П.11); лог уходит в TapError на месте вызова либо в общий
error handler — исчезают 6 копий `_logger.LogError(ex, ...)`.

#### П.10 [Harbor.Storage.Jsonl/JsonlMessageCodec.cs:83-103] DecodeAgentMessage — лесенка per-field try/catch

```csharp
        try
        {
            id = element.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            return Result.Failure<AgentMessage>($"missing 'id': {ex.Message}");
        }
        if (string.IsNullOrEmpty(id))
            return Result.Failure<AgentMessage>("'id' is null or empty");
```
→ CSE-API: `Try<T,E>` + `Ensure<T>(pred, err)`; для повторяющихся полей — локальный хелпер на
`MapTry`/`BindTry` (автоловля внутри цепочки).
→ SKETCH:
```csharp
private static Result<string> Required(JsonElement e, string field) =>
    Result.Try(() => e.GetProperty(field).GetString() ?? string.Empty,
               ex => $"missing '{field}': {ex.Message}")
        .Ensure(v => v.Length > 0, $"'{field}' is null or empty");
// decode: Required(element, "id").Bind(id => Required(element, "createdAt").Map(c => (id, c)))...
```
→ ЧТО ДЕШЕВЛЕ: 3 копии try{GetProperty}catch схлопываются в один `Required`; контекст поля
(«missing 'id'») генерируется, а не пишется руками; новая обязательная секция JSON = одна строка.

#### П.11 [Harbor.Storage.Jsonl/JsonlSessionStore.cs:130-138] CreateAsync — отмена замаскирована под failure

```csharp
        catch (OperationCanceledException)
        {
            return Result.Failure<Session>("Operation was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session");
            return Result.Failure<Session>(ex.Message);
        }
```
→ CSE-API: тот же `Result.Try` + канонический error handler с rethrow (`ex is OperationCanceledException ? throw ex : ...`) — см. §4.5.
→ SKETCH: как П.9; Esc-жест пользователя пробрасывается вверх и не попадает ни в лог LogError,
ни в UI как ошибка сессии.
→ ЧТО ДЕШЕВЛЕ: отмена отличима от сбоя диска на всех уровнях (сейчас Ctrl+C во время создания
сессии рисует «Operation was cancelled.» как ошибку стора); удаляется ветка-дубликат.

### Зона C. Permissions

#### П.12 [Harbor.Application/Permissions/PermissionService.cs:58-65] CheckAsync — лестница TryCreate/GetAgent

```csharp
        var agentNameResult = AgentName.TryCreate(agentName);
        if (agentNameResult.IsFailure)
            return Result.Failure<PermissionResponse>(agentNameResult.Error);

        var agentResult = _agents.GetAgent(agentNameResult.Value);
        if (agentResult.IsFailure)
            return Result.Failure<PermissionResponse>(agentResult.Error);
```
→ CSE-API: `Bind<T,U>(this Result<T>, Func<T,Result<U>>)` — sync-цепочка, оба шага без await.
→ SKETCH:
```csharp
return AgentName.TryCreate(agentName)
    .Bind(_agents.GetAgent)
    .Bind(agent => EvaluateAction(agent, toolName, args));   // остаток метода целиком
```
→ ЧТО ДЕШЕВЛЕ: method-group `_agents.GetAgent` вместо двух if; комментарий «§ROP-002 RESOLVED»
ошибается — `.Value` здесь уже не читается никогда, тип гарантирует; дальнейшие проверки
(workspace confinement) вкладываются теми же Bind/Tap без роста вложенности.

### Зона D. Hosting / Configuration

#### П.13 [Harbor.Application/Configuration/HarborConfig.cs:285-296] Normalize — nullable-лесенка дефолтов

```csharp
        string? modelStr = !string.IsNullOrEmpty(raw.Model)
            ? raw.Model
            : raw.DefaultModel;
        if (!string.IsNullOrEmpty(modelStr))
        {
            var mr = ModelRef.TryParse(modelStr);
            if (mr.IsFailure) return Result.Failure<HarborConfig>(mr.Error);
            config.Identity = config.Identity with { Model = mr.Value };
        }
```
→ CSE-API: `Maybe.From<T>` + `Where(pred)` + `Or(Func<T>)` (ленивый fallback) + `GetOrDefault()` —
это эталон применения **Maybe<T> для nullable-лесенок** (вопрос ТЗ).
→ SKETCH:
```csharp
string? modelStr = Maybe.From(raw.Model)
    .Where(static s => !string.IsNullOrEmpty(s))
    .Or(() => raw.DefaultModel)          // вычисляется только если Model пуст
    .GetOrDefault();
if (modelStr is null) /* секции нет — ок */;
// секция есть — валидация + мутация без чтения .Value (см. §4.2);
// ошибка TryParse пробрасывается вызывающему Normalize через return:
_ = ModelRef.TryParse(modelStr)
    .Tap(mr => config = config.Identity with { Model = mr.Value });
```
→ ЧТО ДЕШЕВЛЕ: тернарник + двойная проверка пустоты → декларативная цепочка «первое непустое»;
та же конструкция закрывает Provider/Agent-секции ниже (тот же паттерн ×3) и `_workspaceRoot ??
Environment.CurrentDirectory` в PermissionService; Maybe честно выражает «значения может не быть»,
а Result остаётся только для настоящей ошибки парсинга.

#### П.14 [Harbor.Application/Configuration/HarborConfig.cs:206-227] Validate — ручная агрегация errors[]

```csharp
        string[] errors = results
            .Where(static r => r.IsFailure)
            .Select(static r => r.Error)
            .ToArray();

        return errors.Length == 0
            ? Result.Success(this)
            : Result.Failure<HarborConfig>(string.Join("; ", errors));
```
→ CSE-API: `Result.Combine(IEnumerable<Result>)` — ровно это тело: все failure через "; ".
Это ответ ТЗ на **Result.Combine для батч-валидаций** (обязательные независимые проверки).
→ SKETCH:
```csharp
var sections = new Result[]
{
    Identity.EffectiveModel(), Tooling.Validate(), Cost.Validate(),
    Compaction.Validate(), Ui.Validate(), Run.Validate(),
}.Concat(Providers.Values.Select(static e => e.Validate()));   // cold path — обычный LINQ допустим

return Result.Combine(sections).Map(() => this);
```
→ ЧТО ДЕШЕВЛЕ: −8 строк ручного Where/Select/join; семантика «все ошибки сразу» выражена
стандартным комбинатором (и совпадает с ним дословно); при добавлении секции правится только
массив — join-формат не может расползтись.

#### П.15 [Harbor.Application/Onboarding/OnboardingWizard.cs:56-78] RunAsync — лестница passthrough-возвратов

```csharp
            var setResult = await _authStore.SetApiKeyAsync(provider.Id, key, ct).ConfigureAwait(false);
            if (setResult.IsFailure) return setResult;
```
(далее тот же паттерн:) `if (saveResult.IsFailure) return saveResult;`
→ CSE-API: passthrough `if (x.IsFailure) return x;` — это буквально `Bind`: следующий шаг
выполняется только на Success, ошибка идёт дальше без изменений.
→ SKETCH:
```csharp
return await PromptProviderAsync(reader, writer, ct)
    .Bind(p => PromptApiKeyIfNeededAsync(p, _authStore, reader, writer, ct))  // ключ + SetApiKey
    .Bind(p => PickModelAsync(reader, writer, p, ct).Map(m => (p, m)))
    .Bind(pm => PickAgentAsync(reader, writer, ct).Map(a => (pm.p, pm.m, a)))
    .Bind(x => _configStore.UpdateAsync(c => { c.Provider = x.p; c.Model = x.m;
                                             c.Agent = x.a; c.Onboarded = true; return c; }, ct))
    .Tap(cfg => writer($"✓ Onboarding complete for {cfg.Provider}"));   // UpdateAsync вернул config
```
→ ЧТО ДЕШЕВЛЕ: 4 ранних возврата исчезают; сценарий визарда читается как список шагов;
добавление шага не требует нового `if (…IsFailure) return …`; UI-вывод собран в Tap на конце.

### Зона E. Transport.Remote

#### П.16 [Harbor.Transport.Remote/RemoteGateway.cs:55-68] ReceiveLoop — немой catch

```csharp
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
            catch
            {
                break;
            }
        }
```
→ CSE-API: `Result.Try(Func<Task>, errorHandler)` + `TapError` — не ради railway, а ради
наблюдаемости: немой catch прячет и отмену, и обрыв, и баг сериализации одинаково.
→ SKETCH:
```csharp
while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
{
    bool closed = false;
    bool keepGoing = await Result.Try(() => ws.ReceiveAsync(buffer, ct), ResultErrors.Message)
        .Tap(msg => { if (msg.MessageType == WebSocketMessageType.Close) closed = true; })
        .TapError(e => _logger.LogDebug(e, "remote receive ended"))
        .Map(_ => true)
        .GetOrDefault();
    if (!keepGoing || closed) break;
}
```
→ ЧТО ДЕШЕВЛЕ: причина разрыва соединения появляется в логах (сейчас — тишина); OCE выходит
через ResultErrors.Message и цикл завершается штатно по ct; состояние сокета после обрыва
отлаживаемо без воспроизведения.

### Зона F. Tools.Builtin

#### П.17 [Harbor.Tools.Builtin/Tools/Edit/EditTool.cs:228-242, 357] ApplyEdit — кастомный Result-подобный тип

```csharp
    private readonly record struct EditResult(bool Ok, string Text, int Count, string? Error)
```
```csharp
        if (string.IsNullOrEmpty(oldStr))
            return EditResult.Fail("oldString must not be empty.");

        if (oldStr == newStr)
            return EditResult.Fail("oldString and newString are identical.");
```
→ CSE-API: `Result<(string Content, int Count)>` + фабрики `Success/Failure` — EditResult дублирует
Result с менее строгой моделью (Ok=true при Error!=null никто не запрещает).
→ SKETCH:
```csharp
private static Result<(string Content, int Count)> ApplyEdit(
    string content, string oldStr, string newStr, bool replaceAll) =>
    Result.FailureIf(string.IsNullOrEmpty(oldStr), "oldString must not be empty")
        .Map(() => content)
        .Ensure(c => oldStr != newStr, "oldString and newString are identical")
        .Map(c => ReplaceAllOrFirst(c, oldStr, newStr, replaceAll));  // внутри CountOccurrences
// вызов: apply.Map(o => ToolResult.Success(o.Content, o.Count));
```
→ ЧТО ДЕШЕВЛЕ: минус один самодельный тип и его Fail/Success-фабрики; невалидные состояния
(Ok+Error одновременно) невыразимы; результат композириуется с остальным railway тула
без конвертации EditResult↔ToolResult вручную.

#### П.18 [Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs:109-115] LoadFromFile — ветвление ради счётчика и лога

```csharp
                    var legacyResult = Register(name, value.GetString() ?? string.Empty);
                    if (legacyResult.IsSuccess)
                        loaded++;
                    else
                        _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, legacyResult.Error);
                    continue;
```
→ CSE-API: `TapError<T>(Action<string>)` (лог) + `Match<T,U>(ok, err)` (счётчик обеих ветвей).
→ SKETCH:
```csharp
loaded += Register(name, value.GetString() ?? string.Empty)
    .TapError(e => _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, e))
    .Match(static _ => 1, static _ => 0);
continue;
```
→ ЧТО ДЕШЕВЛЕ: if/else сведён к выражению; регистрация новых записей не может забыть лог
(он приклеен к результату); тот же приём для основной объектной ветви ниже (строки ~160-170).

### Зона G. Кросс-зонные механики (закрытие вопросов ТЗ)

#### П.19 [Harbor.Application/Agents/ToolDispatcher.cs:122-137] батч-предусловия — FirstFailureOrSuccess

```csharp
    var toolNameResult = ToolName.TryCreate(tc.ToolName);
    if (toolNameResult.IsSuccess)
    {
        var toolResult = tools.GetTool(toolNameResult.Value);
        if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
            return true;
    }
```
→ CSE-API: `Result.FirstFailureOrSuccess(params Result[])` — fail-fast по пакету обязательных
предусловий БЕЗ аллокации списка ошибок (ответ ТЗ; отличие от Combine см. §1.6).
→ SKETCH:
```csharp
// все вызовы батча резолвятся ДО исполнения первого (атомарность батча):
var resolved = toolCalls.Select(tc => ToolName.TryCreate(tc.ToolName).Bind(tools.GetTool)).ToArray();
var gate = Result.FirstFailureOrSuccess(resolved);
if (gate.IsFailure)
    return toolCalls.Select(tc => ToolErrorEntry(tc, gate.Error, tools.GetAllTools())).ToArray();
```
→ ЧТО ДЕШЕВЛЕ: предусловия всего батча проверяются один раз до мутации состояния; первая ошибка
возвращается без конкатенации (дешевле Combine на горячем пути диспетчера); вложенность 4 → 1.

#### П.20 [Harbor.Application/Configuration/AuthStore.cs:35-73] GetApiKeyAsync — fallback-цепочка if'ами → Compensate

```csharp
        // 3. Fall back to conventional env var: PROVIDERID_API_KEY
        string envName = providerId.ToUpperInvariant().Replace('-', '_') + "_API_KEY";
        string? envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(envValue))
        {
            _logger?.LogDebug("Using API key from env var {Name}", envName);
            return Result.Success(envValue);
        }
```
→ CSE-API: `Compensate<T>(this Result<T>, Func<string,Result<T>>)` (+async-перегрузки RightOperand):
fallback выполняется ТОЛЬКО на Failure, успех проходит насквозь. Это дизайн **Compensate для
семантических fallback'ов** (вопрос ТЗ): config → preset-env → conventional-env → aggregated error.
→ SKETCH:
```csharp
return await FromConfigStore(providerId, ct)                       // источник №1
    .Compensate(_ => FromPresetEnv(providerId))                    // источник №2
    .Compensate(_ => FromConventionalEnv(providerId))              // источник №3 (env name выводится)
    .Tap(key => _logger?.LogDebug("API key resolved for {ProviderId}", providerId))
    .MapError(_ => BuildAggregatedHelpMessage(providerId));         // «Run harbor auth set … or set …»
```
→ ЧТО ДЕШЕВЛЕ: три источника становятся тремя одноимёнными private-методами одного контракта
`Task<Result<string>>`; приоритет виден порядком цепочки; финальное «helpful error со всеми
именами env» строится один раз в MapError, а не собирается по ходу флагами.

#### П.21 [Harbor.Application/Resilience/RetryPolicy.cs:37-71] Compensate vs RetryPolicy — граница ответственности

```csharp
            catch (Exception ex)
                when (attempt < options.MaxAttempts
                      && !ct.IsCancellationRequested
                      && IsTransient(ex, out TimeSpan? retryAfter))
            {
                onRetry?.Invoke(ex, attempt);
                TimeSpan delay = retryAfter ?? ComputeDelay(options);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
```
→ CSE-API: `Compensate` НЕ является retry-циклом — это один шаг fallback по уже готовой ошибке
Result. Ответ ТЗ: **RetryPolicy остаётся exception-based политикой повторов (backoff, классификация
transient), Compensate применяется поверх неё для «другого пути», а не «повтора того же»**.
→ SKETCH (мост через Result на границе политики):
```csharp
// RetryPolicy получает Result-адаптер, тело цикла не меняется:
public Task<Result<T>> ExecuteSafeAsync<T>(Func<CancellationToken,Task<T>> op,
    RetryOptions o, Action<Exception,int>? onRetry, CancellationToken ct) =>
    Result.Try(() => ExecuteAsync(op, o, onRetry, ct), ResultErrors.Message);

var client = await _retry.ExecuteSafeAsync(ct => ConnectPrimaryAsync(model, ct), opts, onRetry, ct)
    .Compensate(err => ConnectSecondaryFallback(model, err));  // другой путь, не повтор
```
→ ЧТО ДЕШЕВЛЕ: правило выбора становится механическим: «повторить то же с задержкой» → RetryPolicy;
«если не вышло — сделать иначе» → Compensate (AuthStore П.20, secondary-model П.22); исключения
не протекают через Result-код, а Result не эмулирует backoff-циклы вручную.

#### П.22 [Harbor.Application/Sessions/CompactionService.cs:60-76] TryResolveSecondaryAsync — nullable-кэш + fallback

```csharp
        ResolvedSecondary? cached = _resolvedSecondary;
        if (cached is not null)
        {
            return cached;
        }

        var clientResult = providers.GetClient(_secondaryRef.ProviderId);
        if (clientResult.IsFailure)
        {
            return LogSecondaryFallback(primaryModel);
```
→ CSE-API: `AsMaybe()` ×4 (из nullable) + `Or(Func<T>)` (ленивая загрузка на промахе кэша) +
`GetOrDefault()`; лог промаха — `TapError` до свёртки в Maybe. Вариант с Compensate для всей
цепочки источников — см. П.20.
→ SKETCH:
```csharp
private ResolvedSecondary? ResolveSecondary(ModelInfo primary) =>
    _resolvedSecondary.AsMaybe()                     // кэш-hit
        .Or(() => providers.GetClient(_secondaryRef!.ProviderId)
            .TapError(err => LogSecondaryFallback(primary, err))   // лог на промахе
            .Map(client => new ResolvedSecondary(client, _secondaryRef!))
            .Tap(r => _resolvedSecondary = r)          // memoize на успехе
            .GetOrDefault())                           // null при failure
        .GetOrDefault();                               // Maybe → значение
```
→ ЧТО ДЕШЕВЛЕ: три ранних возврата (null-конфиг → None, кэш-hit → Some, промах → загрузка)
читаются одной цепочкой; memoize выражен Tap'ом (side-effect помечен); вызывающий код на :477
упрощается до `summaryClient = resolved?.Client ?? primaryClient`.

#### П.23 [Harbor.Registries/Providers/ProviderRegistry.cs:64-78, 81-93] GetClient — два одинаковых try/catch

```csharp
            try
            {
                return Result.Success(lazy.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider instantiation failed: {ProviderId}: {Error}", providerId, ex.Message);
                return Result.Failure<ILlmClient>($"Failed to instantiate provider '{providerId}': {ex.Message}");
            }
```
(блок 81-93 — дословная копия для concurrent-dictionary ветви.)
→ CSE-API: `MapTry<T,U>(this Result<T>, Func<T,U> [, errorHandler])` — инстанциация lazy с автоловлей.
→ SKETCH:
```csharp
private Result<ILlmClient> Instantiate(Lazy<ILlmClient> lazy, ProviderId id) =>
    Result.Success(lazy)
        .MapTry(l => l.Value,
                ex => $"Failed to instantiate provider '{id}': {ex.Message}")
        .TapError(e => _logger.LogWarning("Provider instantiation failed: {ProviderId}: {Error}", id, e));

public Result<ILlmClient> GetClient(ProviderId providerId) =>
    _frozenClients is { } f && f.TryGetValue(providerId, out var lazy) ? Instantiate(lazy, providerId)
    : _clients.TryGetValue(providerId, out lazy)                       ? Instantiate(lazy, providerId)
    : Result.Failure<ILlmClient>($"Provider '{providerId}' is not registered.");
```
→ ЧТО ДЕШЕВЛЕ: дубль catch-блока удалён (правка текста ошибки теперь в одном месте); fast-path
frozen-словаря и fallback читаются таблицей ветвей; стек исключения не теряется (сейчас ex
не реthrow'ится вовсе).

#### П.24 [Harbor.Storage.Sqlite/SqliteSessionStore.cs:104-121] GetAsync — «not found» как failure вперемешку со сбоем

```csharp
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Failure<Session>($"Session '{sessionId}' not found.");

            return Result.Success(ReadSession(reader));
```
→ CSE-API: `Maybe.From<T>` + `ToResult(err)` — отсутствие строки это ОТСУТСТВИЕ (Maybe),
сбой запроса это ОШИБКА (Result). Смешивать их в одном Error-канале — потеря информации для UI.
→ SKETCH:
```csharp
// ReadRowAsync: Result<Maybe<Session>> = Result.Try(async () =>
//     await reader.ReadAsync(ct) ? Maybe.From(ReadSession(reader)) : Maybe<Session>.None,
//     ResultErrors.Message);              // сбой запроса ≠ «not found» — разные каналы
return (await ReadRowAsync(sessionId, ct))            // Task<Result<Maybe<Session>>>
    .Bind(m => m.ToResult($"Session '{sessionId}' not found."));
```
→ ЧТО ДЕШЕВЛЕ: слой над стором может спросить `HasValue` (UI показывает «нет такой сессии»,
а не красную ошибку); различие «пусто vs сломано» перестаёт быть парсингом текста Error;
GetMessages/List получают тот же шаблон.

#### П.25 [Harbor.Application/Agents/AgentLoop.cs:179-190] единственный Match репозитория — узаконить как граничный шаблон

```csharp
                    // Railway Oriented Programming: Match dispatches to the
                    // success or failure branch without an explicit
                    // `if (result.IsSuccess)` check, making the
                    // happy-path/error-path split structural rather than
                    // control-flow.
                    await compactionResult.Match(
                        async result =>
                        {
                            await session.AppendMessageAsync(result.SummaryMessage, ct).ConfigureAwait(false);
```
→ CSE-API: `Match<T,U>` / async-зерна BothOperands; пара `Finally` там, где нужен один делегат.
Это ГРАНИЦА слоя: дальше Result не течёт (UI-вывод/IPC-ответ/exit code).
→ SKETCH: не новый код, а правило: Match разрешён (и ожидаем) в трёх местах — TUI-рендер
результата команды, IPC/RemoteGateway-ответ клиенту, финал OnboardingWizard. Внутри домена
вместо Match — продолжение railway (Bind/Tap), потому что Match «схлопывает» railway в значение.
→ ЧТО ДЕШЕВЛЕ: существующий Match в AgentLoop перестаёт быть курьёзом «1 вызов на репо» и
становится образцом границы; исключает антипаттерн Match-посреди-цепочки, после которого
следующий шаг всё равно пишет if.

---

## Часть 4. КАНОНИЧЕСКИЙ СТИЛЬ HARBOR

### 4.1 Матрица выбора комбинатора

| Ситуация | Комбинатор | Не делать |
|---|---|---|
| передать значение следующему падению | `Bind` | лестница `if IsFailure return Failure(x.Error)` |
| преобразовать значение | `Map` | чтение `.Value` + ручная сборка |
| side-effect на успехе (лог debug, кэш, UI) | `Tap` / `TapIf` | присваивание внутри Map |
| лог/метрика на ошибке | `TapError` / `TapErrorIf` | if-блок вокруг лога |
| переформатировать сообщение у источника | `MapError` | конкатенация префиксов наверху |
| постусловие над уже полученным значением | `Ensure` / `EnsureNotNull` | отдельный if после Map |
| кросс-полевая проверка значения | `Check` / `CheckIf` | валидация внутри Map |
| зависимые шаги (B нужен A) | цепочка `Bind` | `Combine` зависимых |
| независимые ОБЯЗАТЕЛЬНЫЕ, нужны все ошибки | `Result.Combine` | errors[] + join руками (П.14) |
| независимые обязательные, fail-fast | `FirstFailureOrSuccess` | N подряд if (П.19) |
| «значения может не быть» | `Maybe<T>` + `Or` | `T?` + тернарники (П.13, П.24) |
| «не вышло — иначе» (fallback) | `Compensate` | флаги + goto-стиль (П.20, П.22) |
| повтор того же с backoff | `RetryPolicy` (исключения) | ручной цикл на Result (П.21) |
| развёртка на границе слоя | `Match` / `Finally` | Match посреди цепочки (П.25) |

### 4.2 Запреты (red list)

1. **Legacy-имена**: `OnSuccessTry` (=TapTry), `OnFailureCompensate` (=Compensate),
   `TryAsyncResult*`-подобные самоделки. Ревью отклоняет.
2. **Чтение `.Value` без доказанного Success** и `.Error` вне Failure-ветки; чтение
   `.Value`/`.Error` легально только после `Deconstruct`/`Match`/`Finally` либо сразу за `IsSuccess`-check в той же области.
3. **`_ = x.Error;`** и любые «заглушочные» выражения — удалить (П.8).
4. **Глотать `OperationCanceledException`** в любой обёртке (П.11, П.16) — см. §4.5.
5. **Немой `catch {}`/`catch { break; }`** — минимум `TapError(LogDebug)` (П.16).
6. **Кастомные Result-подобные типы** (EditResult, …) — новые запрещены, существенные мигрируют (П.17).
7. **`SelectMany` вне query syntax**, `WithTransactionScope*` (нет БД-транзакций).
8. **Result как поле DTO/состояния** (хранить Result между вызовами) — Result это транзит,
   на границе он разворачивается (§4.1 последняя строка).
9. **Combine для >2 последовательных async-шагов с зависимостью** — это цепочка Bind (П.6 —
   исключение: шаги действительно параллельны).
10. **Комбинатор ради комбинатора в циклах с `continue`** (McpRegistry.LoadFromFile основная
   ветвь): перечисляющий код с ранним `continue` остаётся императивным, внутрь — точечные
   TapError/Match (П.18). Railway — для цепочек, не для всех конструкций.

### 4.3 Naming и оформление

- Методы домена возвращают `Result` / `Result<T>` / `Task<Result<T>>`; суффикс Async — только у
  самого метода, комбинаторы Async-суффиксов не удваивают (`await r.Bind(LoadAsync)`).
- Хелпер ошибок: `Harbor.Abstractions.Results.ResultErrors.Message(Exception)` — единственный
  error handler по умолчанию (§4.5); `ResultGuard` удалить вместе с тестами (прод-вызовов 0).
- Текст ошибки: фраза с маленькой буквы, БЕЗ точки в конце, конкретика у источника
  (`"missing 'id'"`), контекст дописывается `MapError` у ближайшей границы слоя
  (`$"Failed to persist prompt: {e}"`). Нельзя заканчивать ошибку на «failed» без причины.
- Цепочка: один комбинатор на строку; терминальный `await` несёт `ConfigureAwait(false)`
  (единый стиль репо); длинные цепочки >5 звеньев дробятся на приватные методы с именем глагола.
- Имена sketch-методов: источник fallback — `FromXxx`, предикат-обёртка — `Required`/`Validate`,
  адаптер политики — `ExecuteSafeAsync`.

### 4.4 Типизированные ошибки — решение вопроса ТЗ

**Рекомендация: `Result<T,string>` (т.е. обычный `Result<T>`) везде внутри; `Result<T,E>` /
`UnitResult<E>` — ТОЛЬКО на границе LLM-транспорта.**

Обоснование:
1. Диспетчеризация по типу ошибки нужна ровно в одном месте: транспорт провайдера, где
   `RetryPolicy.IsTransient` и UI-подсказки различают rate-limit / auth / malformed / timeout.
   Там вводится `enum ProviderErrorKind` + `UnitResult<ProviderErrorKind>`.
2. Внутренние 94 сайта проброса — это агрегация и показ человеку; string объединяется
   `Combine("; ")`, `MapError`, `JoinToString` бесплатно. Переход на E заставит писать
   `ICombine` для каждого E и переписать все TapError/MapError сигнатуры разом — стоимость
   не окупается: потребителей типа ошибки внутри домена нет.
3. Граница LLM конвертирует: `Transport: UnitResult<E>` → `Domain: Result<string>` одним
   `MapError(e => e.Format())`. Обратное направление запрещено (типизированное не собирается
   из строк).

### 4.5 Правило отмены (OCE) и ResultGuard

Канонический error handler для ВСЕХ `Try*`:

```csharp
internal static class ResultErrors
{
    /// <summary>OCE пробрасывается (Esc ≠ ошибка), остальное — сообщение.</summary>
    public static string Message(Exception ex) =>
        ex is OperationCanceledException ? throw ex : ex.Message;
}
```

Этим закрываются: П.9–П.11 (сторы), П.16 (transport), мост RetryPolicy (П.21). `ResultGuard`
(Harbor.Abstractions/Results/ResultGuard.cs) дублирует этот хелпер + встроенный `Result.Try`,
прод-вызовов не имеет — удалить класс и его тестовый файл, тесты перенести на `ResultErrors`.

### 4.6 Порядок внедрения и gate

1. Инфраструктура (PR-1): `ResultErrors` + удаление ResultGuard; правка П.9–П.11/П.16/П.23 —
   механическая, поведение не меняется кроме rethrow OCE (это фикс, отметить в changelog).
2. Точечные комбинаторы (PR-2): П.12/П.15/П.17/П.18/П.22/П.24/П.25 — по одному тулу/сервису за PR.
3. Конфиг (PR-3): П.13/П.14 — Normalize×3 секции + Validate.
4. Механики батчей (PR-4): П.19 + П.6-стиль для оставшихся парных загрузок.
5. Gate на ревью/CI (grep по diff): новый `if (...IsFailure)` вне циклов с `continue` — вопрос
   «почему не комбинатор»; легальные остатки: перечисления (П.18), предикаты, границы Match (§4.1).
   Целевая метрика: ~94 if-лестниц → <30 к концу спринта, 0 в зонах B/C/D.

