# Последняя миля ROP: каждый оставшийся try/catch, IsFailure-лесенка, switch-by-string

> **СТАТУС (2026-08-27): план выполнен** волнами ROP-B/C/D — см. ADR-002 в [DECISIONS.md](../DECISIONS.md).
> `ResultGuard` удалён как дубликат канона (`9e954a5`); канон = CSE `Result.Try` + `ResultErrors.Message`,
> legacy `.GetResult()` запрещены BannedApi (`be81e42`). Легитимные (c)-сайты из оси 1 зафиксированы
> как осознанные отказы. Дальше файл — исторический снимок @ dev/5b01b2d.

READ-ONLY. Репо /mnt/projects/Harbor-Harness @ dev/5b01b2d. Территория: Harbor.Application + Harbor.Hosting + Harbor.Registries.
Не дублирует fp-solid-audit.md §1-3 (метрики), deep2-application.md A1-A6, arch2-application.md F1-F16 — пересечения помечены «→ см.».

**ГЛАВНЫЙ ФАКТ**: спринт 8' отдал `ResultGuard.TryAsync/Try` (`src/Harbor.Abstractions/Results/ResultGuard.cs`) —
grep по src/+apps/+tests: **0 продовых вызовов**. Единственные потребители — его же тесты
(`tests/Harbor.Abstractions.Tests/ResultGuardTests.cs`). Канонический конверсор стоит без работы:
ни один catch→Failure в территории на него не переведён. Ниже — каждый кандидат и каждая осознанная отказная.

---

## Ось 1. Полный census try/catch территории (25 сайтов, каждый классифицирован)

Категории: **(a)** уже ResultGuard — таких нет; **(b)** catch→Failure вручную → переводится; **(c)** легитимный (OCE-дисциплина, cleanup, изоляция) — оставить.

### [src/Harbor.Application/Agents/AgentLoop.cs:104→390] (c — граница рана)
```csharp
        try { /* тело RunAsync :105-389 */ }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            string failure = string.Format(CultureInfo.InvariantCulture,
                CoreResources.GetError("AgentFailed"), ex.Message);
            _logger.LogError(ex, "Agent run failed: session={SessionId} error={Error}", session.Session.Id, failure);
            await _eventBus.PublishAsync(new AgentErrorEvent(ex.Message, ex.ToString()), CancellationToken.None)...
            return Result.Failure(ex.Message);
        }
```
→ НЕ переводить: catch делает 4 вещи помимо конверсии (телеметрия, локализованный формат, AgentErrorEvent, лог).
ResultGuard.TryAsync сжёг бы публикацию события. Это boundary-handler, не конверсия.
→ ВЫИГРЫШ от фиксации: будущему рефакторщику не придётся гадать, почему «лесенка не свёрнута».

### [src/Harbor.Application/Agents/AgentLoop.cs:280→289] (c — типизированный канал терминальных ошибок)
```csharp
        try
        {
            streamed = await _retryPolicy.ExecuteAsync(
                attemptCt => ConsumeTurnStreamAsync(client, request, session, model, turn, attemptCt),
                StreamRetryOptions, ...);
        }
        catch (LlmStreamErrorException lex)
        {
            return Result.Failure(lex.Message);
        }
```
→ НЕ переводить на TryAsync: TryAsync ловит ВСЕ исключения и превратил бы баги (NRE и пр.) в тихий Failure.
Здесь ловится ровно один доменный сигнал «провайдер ответил ошибкой» после того, как retry-политика
отфильтровала transient. Catch-all здесь = регрессия диагностики.
→ ВЫИГРЫШ: документировано отличие семантики TryAsync (catch-all) от typed-catch.

### [src/Harbor.Application/Agents/AgentLoop.cs:437→533] (c — cleanup при отмене)
```csharp
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (coalescer.HasPendingText) { partial = partial.AppendText(coalescer.FlushText()); }
            ...
            coalescer.DiscardPendingToolCalls();
            partial = partial.WithFinish(StopReason.Aborted, finalUsage ?? new Usage(0, 0));
```
→ НЕ переводить: тело catch — возврат пулов и финализация партиала, не конверсия. Flush-танец внутри — отдельная находка F15 (→ см. arch2).

### [src/Harbor.Application/Agents/DefaultAgent.cs:112→116] (c — изоляция слушателей)
```csharp
                try
                {
                    await snapshot[i](evt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Listener failed: session={SessionId}", State?.SessionId ?? "unbound");
                }
```
→ НЕ переводить: fault-isolation fan-out'а (один упавший рендерер не убивает шину). Канон event-bus, тот же паттерн что InMemoryEventBus.

### [src/Harbor.Application/Agents/DefaultAgent.cs:249→256] (c — анти-кандидат: OCE→Failure ЗАПРЕЩЁН каноном)
```csharp
        bool acquired;
        try
        {
            // Zero-timeout acquire: a single atomic attempt — never blocks.
            acquired = await _runGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Agent was cancelled.");
        }
```
→ НЕ переводить: ResultGuard.TryAsync **специально** пробрасывает OCE (контракт спринта 8': «Cancellation is NOT swallowed»).
Этот сайт сознательно мапит отмену в Result — решение уровня политики, не механика. Если канон должен покрыть
и этот случай — нужен отдельный `ResultGuard.TryAllowCancelled`, а не искажение TryAsync.
→ ВЫИГРЫШ: вскрыто СКРЫТОЕ ПРОТИВОРЕЧИЕ канона: F17-решение CompactionService (:559, ниже) делает то же самое
и тоже противоречит «rethrow». Два сайта уже голосуют за второй вариант конверсора.

### [src/Harbor.Application/Agents/DefaultAgent.cs:278+313] (c — state-machine rollback ×2 ветки)
```csharp
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                State = State with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
                var cancelled = Result.Failure("Agent was cancelled.");
                completion.TrySetResult(cancelled);
                return cancelled;
            }
            catch (Exception ex)
            {
                State = State with { IsRunning = false, ... };
                _logger.LogError(ex, "Agent run failed: session={SessionId}", State.SessionId);
                var failed = Result.Failure(ex.Message);
                completion.TrySetException(ex);   // ← заметь: Exception-ветка сигналит ИСКЛЮЧЕНИЕМ, OCE — результатом
```
→ НЕ переводить: rollback состояния + completion-source протокол в обеих ветках. Плюс асимметрия TrySetResult/TrySetException
— отдельный вопрос к WaitForIdleAsync, не к ROP.

### [src/Harbor.Application/Agents/ToolDispatcher.cs:87→99] (c — ArrayPool cleanup)
try/finally вокруг Rent/Return. Механика пула, не обработка ошибок. Оставить.

### [src/Harbor.Application/Agents/ToolDispatcher.cs:205→290,297] (c — OCE-дисциплина + публикация событий)
```csharp
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = ToolResult.Error("Tool execution was cancelled.");
            await eventBus.PublishAsync(new ToolExecutionEndEvent(toolCall.Id, cancelled, true), ct)...
            return ToolResultEntry.From(toolCall.Id, toolCall.ToolName, cancelled);
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
        {
            // A9: the per-call deadline fired (outer token NOT cancelled) —
```
→ НЕ переводить: два разных OCE-смысла (отмена юзера vs таймаут тула) разводятся фильтрами + ToolExecutionEndEvent.
Это образцовая OCE-гигиена — лучше среднего по репо.

### [src/Harbor.Application/Agents/ToolDispatcher.cs:265→270] (c — изоляция прогресса)
catch→LogWarning вокруг PublishAsync прогресса. Изоляция побочного канала. Оставить.

### [src/Harbor.Application/Agents/StreamingCoalescer.cs:181→200] (c — Dispose пула)
try/finally args.Dispose(). Оставить.

### [src/Harbor.Application/Agents/StreamingCoalescer.cs:232→238] (c — TryParse-конвенция)
```csharp
        try
        {
            using var doc = JsonDocument.Parse(builder.ToString());
            parsedArgs = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            parsedArgs = default;
            return false;
        }
```
→ НЕ переводить: это каноничный .NET Try*-паттерн (bool + out), не Result. Перевод в Result<T> сломал бы zero-alloc путь
горячего стрима ради стиля. Оставить.

### [src/Harbor.Application/Permissions/PermissionService.cs:135→140] (c — fail-closed политика)
```csharp
        try
        {
            var response = await _userAsker(request, ct).ConfigureAwait(false);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, CoreResources.GetError("PermissionDenied"), request.Permission, request.Pattern);
            return Result.Success(new PermissionResponse(PermissionAction.Deny, false));
        }
```
→ НЕ переводить: catch возвращает Success(Deny) — fail-closed пермишенов, а не Failure. TryAsync выдал бы Failure и сломал G3-семантику.

### [src/Harbor.Application/Permissions/PermissionService.cs:184→196] (c — defensive wildcard)
bare `catch { return "*"; }` вокруг switch-by-string — сам fallback и есть фича (→ дедуп/мёртвые arms: deep2 A3, arch2 F4). Оставить.

### [src/Harbor.Application/Permissions/PermissionService.cs:217→223] и [:239→245] (c — path-парсинг fallback)
```csharp
                try
                {
                    return NormalizePath(
                        args.TryGetProperty("path", out var p) ? p.GetString() : null,
                        workspaceRoot);
                }
                catch
                {
                    return new PathExtraction("*", true);
                }
```
→ НЕ переводить: «не смогли распарсить путь → форсим user-decision» — security-политика, не ошибка. Path.GetFullPath кидается
на мусорном вводе регулярно; это управление потоком по данным, исключение тут дешевле TryGetFullPath-велосипеда.

### [src/Harbor.Application/Resilience/RetryPolicy.cs:54→58] (c — САМ retry-механизм)
catch-with-filter `when (attempt < options.MaxAttempts && !ct.IsCancellationRequested && IsTransient(...))`.
Это и есть ретрай-цикл. Оставить. (Статический классификатор IsTransient — arch2 F6.)

### [src/Harbor.Registries/Events/InMemoryEventBus.cs:152→162] (c — изоляция middleware)
```csharp
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Middleware {Middleware} threw — event dropped", mw.Name);
                    return;
                }
```
Изоляция шагов пайплайна: middleware-баг не должен ронять публикацию. Оставить.

### [src/Harbor.Registries/Events/InMemoryEventBus.cs:187+193] (c — изоляция подписчиков + finally-пул)
Внешний try/finally возвращает ArrayPool, внутренний catch собирает мёртвые подписки. Fault-isolation fan-out'а. Оставить.

### [src/Harbor.Hosting/Modules/JsonProviderDiscovery.cs:84→113] (c — discovery-резильенс)
```csharp
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load provider config '{File}'", file);
                }
```
Перебор файлов конфигов: битый файл = skip-and-log, не Failure всего discovery. Оставить. Тот же вердикт для :147→166 и :179→195 (RegisterOne).

### [src/Harbor.Registries/Providers/ProviderRegistry.cs:112+123→147,152] (c + НАЙДЕН БАГ OCE-гигиены!)
```csharp
                    catch (OperationCanceledException ex)
                    {
                        _logger.LogWarning(ex, "Model fetch timed out for provider: {ProviderId}", pid);
                        return new ModelBatch(pid, Array.Empty<ModelInfo>(), "timeout");
                    }
```
→ Фильтра НЕТ: catch ловит ЛЮБОЙ OperationCanceledException, включая отмену ВЫЗЫВАЮЩЕГО через `cancellationToken`
(прилинкован в linkedCts :137). Отмена юзера во время GetAllModelsAsync логируется как «timed out», глотается
в батч-ошибку, и Task.WhenAll завершается «успешным» частичным результатом — токен отмены не наблюдается наверху.
Это прямое нарушение OCE-канона, который ResultGuard зафиксировал («cancellation propagates»).
→ ДИФФ (минимальный, до ROP-перевода):
```csharp
-                    catch (OperationCanceledException ex)
-                    {
-                        _logger.LogWarning(ex, "Model fetch timed out for provider: {ProviderId}", pid);
-                        return new ModelBatch(pid, Array.Empty<ModelInfo>(), "timeout");
-                    }
+                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
+                    {
+                        _logger.LogWarning("Model fetch timed out for provider: {ProviderId}", pid);
+                        return new ModelBatch(pid, Array.Empty<ModelInfo>(), "timeout");
+                    }
```
(отмена вызывающего теперь пробрасывается из WhenAll естественно). Полный ROP-вариант: тело Task.Run обернуть в
ResultGuard.TryAsync с тем же фильтром-различением — но TryAsync сейчас умеет только «rethrow если token просит»,
а тут различение инвертированное (timeout vs caller), поэтому сначала чинить фильтром, потом думать над API.
→ ВЫИГРЫШ: корректная реакция UI на Esc во время загрузки моделей; −1 строка.

---

## Ось 1б. Категория (b): catch→Failure вручную — точные диффы перевода на ResultGuard

### [B1] src/Harbor.Registries/Providers/ProviderRegistry.cs:64-97 — ДВА дословных catch→Failure, Lazy-материализация
Категория: **(b)**. Пересечение с deep2 A4 там предложен хелпер Materialize; здесь — то же самое, но через канон спринта 8'.
```csharp
        if (frozen is not null && frozen.TryGetValue(providerId, out var lazy))
        {
            try
            {
                return Result.Success(lazy.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider instantiation failed: {ProviderId}: {Error}", providerId, ex.Message);
                return Result.Failure<ILlmClient>($"Failed to instantiate provider '{providerId}': {ex.Message}");
            }
        }
```
→ ДИФФ:
```csharp
+    private Result<ILlmClient> Materialize(Lazy<ILlmClient> lazy, ProviderId providerId)
+    {
+        var result = ResultGuard.Try(lazy.Value);          // OCE — не бывает: фабрика синхронная
+        if (result.IsFailure)
+            _logger.LogWarning("Provider instantiation failed: {ProviderId}: {Error}",
+                providerId, result.Error);
+        return result;
+    }
```
```csharp
         var frozen = _frozenClients;
         if (frozen is not null && frozen.TryGetValue(providerId, out var lazy))
-        {
-            try { ... 9 строк ... }
-        }
+            return Materialize(lazy, providerId);
         if (_clients.TryGetValue(providerId, out var lazyClient))
-        {
-            try { ... те же 9 строк ... }
-        }
+            return Materialize(lazyClient, providerId);
```
Честные потери: текст Failure меняется c `"Failed to instantiate provider '{id}': {msg}"` на `{msg}`
(ResultGuard не интерполирует). Если текст зовут наружу — обернуть `.MapError(e => $"Failed to instantiate provider '{providerId}': {e}")`.
→ ВЫИГРЫШ: −18 строк в файле; первый продовый вызов ResultGuard.Try; единый текст ошибки из двух веток исчезает как класс.

### [B2] src/Harbor.Application/Configuration/ConfigStore.cs:73-110 LoadAsync
Категория: **(b)** — два ручных catch (JsonException → особый текст, Exception → ex.Message) вокруг синхронного тела под lock.
```csharp
            try
            {
                if (!File.Exists(_configPath)) { ... return Task.FromResult(Result.Success(HarborConfig.Default)); }
                string json = File.ReadAllText(_configPath);
                ...
                return Task.FromResult(normalized.Value.Validate());
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to parse config");
                return Task.FromResult(Result.Failure<HarborConfig>($"config.json is corrupt: {ex.Message}"));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load config");
                return Task.FromResult(Result.Failure<HarborConfig>(ex.Message));
            }
```
→ ДИФФ (ядро вынести, guard поверх):
```csharp
     public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
     {
         lock (_lock)
-        {
-            try { /* :79-97 */ }
-            catch (JsonException ex) { /* :99-103 */ }
-            catch (Exception ex) { /* :104-108 */ }
-        }
+        {
+            var loaded = LoadCore();
+            if (loaded.IsFailure)
+                _logger?.LogError("Failed to load config: {Error}", loaded.Error);
+            return Task.FromResult(loaded);
+        }
     }
+
+    private Result<HarborConfig> LoadCore()
+    {
+        if (!File.Exists(_configPath)) return Result.Success(HarborConfig.Default);
+        return ResultGuard.Try(() =>
+        {
+            string json = File.ReadAllText(_configPath);
+            var raw = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.RawConfigDto);
+            if (raw is null) throw new InvalidDataException("config.json is empty");
+            var normalized = ConfigNormalizer.Normalize(raw);
+            if (normalized.IsFailure) throw new InvalidDataException(normalized.Error);
+            return normalized.Value.Validate().Value;   // Validate уже Result — исключение только для «проброса»
+        }).MapError(ex => ex is InvalidDataException ide ? ide.Message : $"config.json is corrupt: {ex.Message}");
+    }
```
Честная оценка: JsonException-ветка даёт особый префикс «config.json is corrupt:»; чтобы его сохранить при catch-all
TryAsync, приходится либо MapError-различение (выше), либо принять потерю префикса. Тело Validate() уже возвращает Result,
так что try нужен только вокруг File/Deserialize — альтернативный, ещё более узкий вариант:
`ResultGuard.Try(() => File.ReadAllText(...)).Bind(json => ResultGuard.Try(() => Deserialize(json)))...`.
→ ВЫИГРЫШ: −14 строк; лог уезжает из catch в одно место; LoadCore тестируется без logger.

### [B3] src/Harbor.Application/Configuration/ConfigStore.cs:113-137 SaveAsync
Категория: **(b)** — тот же рисунок.
```csharp
            try
            {
                string? dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(config.ToRaw()!, ConfigJsonContext.Default.RawConfigDto);
                File.WriteAllText(_configPath, json);
                _logger?.LogDebug("Config saved to {Path}", _configPath);
                return Task.FromResult(Result.Success());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save config");
                return Task.FromResult(Result.Failure(ex.Message));
            }
```
→ ДИФФ:
```csharp
         lock (_lock)
-        {
-            try { /* выше */ }
-            catch (Exception ex) { /* выше */ }
-        }
+            return Task.FromResult(
+                ResultGuard.Try(SaveCore)
+                    .TapError(ex => _logger?.LogError(ex, "Failed to save config")));
+
+    private void SaveCore()
+    {
+        string? dir = Path.GetDirectoryName(_configPath);
+        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
+            Directory.CreateDirectory(dir);
+        string json = JsonSerializer.Serialize(ToRaw()!, ConfigJsonContext.Default.RawConfigDto);
+        File.WriteAllText(_configPath, json);
+        _logger?.LogDebug("Config saved to {Path}", _configPath);
+    }
```
(TapError есть в CSE 3.7.0 — версия подтверждена по Directory.Packages.props:150.)
→ ВЫИГРЫШ: −8 строк; второй продовый вызов ResultGuard.

### [B4-спорный] src/Harbor.Application/Sessions/CompactionService.cs:449→559,569 — НЕ переводить вслепую
Категория: **(c) с решением наверху**. Exception-ветка выглядит как каноничный (b):
```csharp
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // F17: cancellation is not a compaction failure. Treating Esc during
            // summarisation as a generic Exception made the caller flip the
            // session into destructive truncation fallback and report a spurious
            // error — the run is simply ending.
            stopwatch.Stop();
            logger.LogInformation(ex, "Compaction cancelled for session {SessionId}", sessionId);
            return Result.Failure<CompactionResult>("Compaction cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Compaction failed for session {SessionId}", sessionId);
            return Result.Failure<CompactionResult>($"Compaction failed: {ex.Message}");
        }
```
→ ОПАСНОСТЬ слепого перевода: `ResultGuard.TryAsync(..., ct)` ПРОБРОСИЛ бы OCE (канон!), а здесь F17 сознательно
мапит отмену в Failure — иначе caller включает destructive-truncation fallback. Механическая конверсия = регрессия F17.
Тело между :452 и :557 содержит 3 внутренних early-Failure (:456, :463, :469, :506, :522) — их лесенка разбирается ниже [L2].
→ РЕШЕНИЕ для канона (нужен один PR-документ, не код): зафиксировать в ResultGuard.xml-doc правило
«catch(OCE)→Failure остаётся ручным и требует комментария F17-style», либо добавить
`ResultGuard.TryAsyncAllowCancelled<T>(...)`. Сейчас канон и два живых сайта (DefaultAgent :254, CompactionService :559)
голосуют в разные стороны.
→ ВЫИГРЫШ от фиксации: следующий автор CompactionService-подобного кода не выберет случайно не ту семантику отмены.

---

## Ось 2. IsSuccess/IsFailure-лесенки ≥2 шагов → Bind/Map/Combine

55 вхождений IsSuccess|IsFailure в территории (16 файлов). Лесенки ≥2 вложенных if — ниже; одиночные if-return
не трогаем по «границе честности» fp-audit («Чего НЕ делать»).

### [L1] src/Harbor.Application/Agents/AgentLoop.cs:110-134 — 4-шаговый setup, каждый шаг `if IsFailure return Failure`
Пересечение: arch2 F16 дал sketch без дословного диффа — здесь точный. IModelResolver (F9) ещё НЕ в коде (grep = 0), поэтому дифф без него.
```csharp
var providerIdResult = ProviderId.TryCreate(agent.ProviderId);
if (providerIdResult.IsFailure)
    return Result.Failure(providerIdResult.Error);

var clientResult = _providers.GetClient(providerIdResult.Value);
if (clientResult.IsFailure)
    return Result.Failure(clientResult.Error);

var client = clientResult.Value;
...
            var modelsResult = await client.GetModelsAsync(ct).ConfigureAwait(false);
            if (modelsResult.IsFailure)
                return Result.Failure(modelsResult.Error);
```
→ ДИФФ:
```csharp
+    private readonly record struct ResolvedClient(ProviderId Id, ILlmClient Client);
+
+    // Ошибки маршрутизируются структурой цепочки: любой шаг Failure — ранний выход автоматом.
+    var resolved = await ProviderId.TryCreate(agent.ProviderId)
+        .Bind(id => _providers.GetClient(id).Map(c => new ResolvedClient(id, c)))
+        .Bind(async x =>
+        {
+            if (_modelCatalogCache.TryGetValue(x.Id.Value, out var cached)
+                && cached.ExpiresAt > DateTimeOffset.UtcNow)
+                return Result.Success(x);
+
+            var models = await x.Client.GetModelsAsync(ct).ConfigureAwait(false);
+            return models.IsSuccess ? Result.Success(x)
+                : Result.Failure<ResolvedClient>(models.Error);
+        });
+    if (resolved.IsFailure)
+        return Result.Failure(resolved.Error);      // ← ЕДИНСТВЕННЫЙ if вместо трёх
```
(модели достаются из кэша отдельным TryGetValue после цепочки — TTL-логика :126-138 остаётся как есть).
→ ВЫИГРЫШ: −8 строк; новый 5-й шаг setup'а = ещё один .Bind, а не копипаста пары «if + return»;
ошибка всегда проходит один выход (:397) — легче добавить телеметрию провала.

### [L2] src/Harbor.Application/Sessions/CompactionService.cs:460-470 — тот же рисунок внутри CompactAsync
```csharp
            var providerIdResult = ProviderId.TryCreate(model.ProviderId);
            if (providerIdResult.IsFailure)
            {
                return Result.Failure<CompactionResult>(providerIdResult.Error);
            }

            var clientResult = providers.GetClient(providerIdResult.Value);
            if (clientResult.IsFailure)
            {
                return Result.Failure<CompactionResult>(clientResult.Error);
            }
```
→ ДИФФ:
```csharp
-            var providerIdResult = ProviderId.TryCreate(model.ProviderId);
-            if (providerIdResult.IsFailure) { return Result.Failure<CompactionResult>(providerIdResult.Error); }
-            var clientResult = providers.GetClient(providerIdResult.Value);
-            if (clientResult.IsFailure) { return Result.Failure<CompactionResult>(clientResult.Error); }
-            ILlmClient summaryClient = clientResult.Value;
+            ILlmClient summaryClient = ProviderId.TryCreate(model.ProviderId)
+                .Bind(providers.GetClient).Value;   // ← только если выше проверили IsFailure
+            // канонично:
+            var client = ProviderId.TryCreate(model.ProviderId).Bind(providers.GetClient);
+            if (client.IsFailure) return Result.Failure<CompactionResult>(client.Error);
+            ILlmClient summaryClient = client.Value;
```
(второй вариант — рекомендуемый: одна проверка, −5 строк; первый показан как анти-пример `.Value` без проверки,
который уже есть в репо — HostBuilder.Registration.cs:135, см. fp-audit §3.)
→ ВЫИГРЫШ: −5 строк и устранение соблазна `.Value`; при появлении F9/IModelResolver оба места схлопываются в один вызов.

### [L3] src/Harbor.Application/Configuration/HarborConfig.cs:276-325 ConfigNormalizer.Normalize — ЧЕТЫРЕ независимых TryCreate-гарда
```csharp
        string? providerStr = !string.IsNullOrEmpty(raw.Provider) ? raw.Provider : raw.DefaultProvider;
        if (!string.IsNullOrEmpty(providerStr))
        {
            var pr = ProviderId.TryCreate(providerStr);
            if (pr.IsFailure) return Result.Failure<HarborConfig>(pr.Error);
            config.Identity = config.Identity with { Provider = pr.Value };
        }
        ... // то же для ModelRef(raw.Model ?? raw.DefaultModel) :289-294,
            // AgentName(raw.Agent) :296-301, ModelRef(raw.SecondaryModel) :320-325
```
Особенность: поля НЕЗАВИСИМЫ, но сейчас пользователь узнаёт об ошибке ТОЛЬКО в первом невалидном поле.
→ ДИФФ:
```csharp
+    private static Result<T?> ParseOpt<T>(string? raw, Func<string, Result<T>> parse) where T : class
+        => string.IsNullOrEmpty(raw) ? Result.Success<T?>(null) : parse(raw).Map(v => (T?)v);
+
+    var provider = ParseOpt(raw.Provider is { Length: > 0 } ? raw.Provider : raw.DefaultProvider, ProviderId.TryCreate);
+    var model    = ParseOpt(raw.Model is { Length: > 0 } ? raw.Model : raw.DefaultModel, ModelRef.TryParse);
+    var agent    = ParseOpt(raw.Agent, AgentName.TryCreate);
+    var secondary= ParseOpt(raw.SecondaryModel, ModelRef.TryParse);
+
+    var combined = Result.Combine(provider, model, agent, secondary);   // ВСЕ ошибки разом
+    if (combined.IsFailure)
+        return Result.Failure<HarborConfig>(string.Join("; ",
+            new[] { provider, model, agent, secondary }.Where(r => r.IsFailure).Select(r => r!.Error)));
+
+    config.Identity = config.Identity with { Provider = provider.Value, Model = model.Value, Agent = agent.Value };
+    if (secondary.Value is not null) config.SecondaryModel = raw.SecondaryModel;
```
→ ВЫИГРЫШ: −20 строк; UX-выигрыш главный: битый конфиг с тремя опечатками теперь диагностируется ЗА ОДИН запуск
(сейчас — три запуска «исправь → перезапусти → следующая ошибка»). Combine есть в CSE 3.7.0.

### [L4] src/Harbor.Application/Agents/ToolDispatcher.cs:120-136 HasSequentialTool — 2-уровневое гнездо IsSuccess
(fp-audit §2 уже отметил молчаливое глотание невалидных имён здесь — дифф сохраняет эту семантику.)
```csharp
            var tc = toolCalls[i];
            var toolNameResult = ToolName.TryCreate(tc.ToolName);
            if (toolNameResult.IsSuccess)
            {
                var toolResult = tools.GetTool(toolNameResult.Value);
                if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
                {
                    return true;
                }
            }
```
→ ДИФФ:
```csharp
-        for (int i = 0; i < toolCalls.Count; i++)
-        {
-            var tc = toolCalls[i];
-            var toolNameResult = ToolName.TryCreate(tc.ToolName);
-            if (toolNameResult.IsSuccess)
-            {
-                var toolResult = tools.GetTool(toolNameResult.Value);
-                if (toolResult.IsSuccess && toolResult.Value.ExecutionMode == ExecutionMode.Sequential)
-                    return true;
-            }
-        }
-        return false;
+        return toolCalls
+            .Select(static tc => ToolName.TryCreate(tc.ToolName))
+            .Where(static n => n.IsSuccess)                      // глотание невалидных имён — как было
+            .Select(n => tools.GetTool(n.Value))
+            .Any(static t => t.IsSuccess && t.Value.ExecutionMode == ExecutionMode.Sequential);
```
→ ВЫИГРЫШ: −6 строк; интенция «есть хоть один последовательный тул» читается в одну строку Any.
Оговорка: аллокация итераторов; путь вызывается раз на батч тулов (до fan-out), N мал — не hot-path.

### [L5] src/Harbor.Application/Agents/ToolDispatcher.cs:155-166 — формальная лесенка, ОСТАВИТЬ (диагностика различается)
```csharp
        var toolNameResult = ToolName.TryCreate(toolCall.ToolName);
        if (toolNameResult.IsFailure)
        {
            return new ToolResultEntry(toolCall.Id, toolCall.ToolName,
                $"Invalid tool name: {toolNameResult.Error}", true);
        }
        var toolResult = tools.GetTool(toolNameResult.Value);
        if (toolResult.IsFailure)
        {   // ... строит СПИСОК доступных тулов через StringBuilderPool ...
```
→ НЕ сворачивать: Bind склеил бы оба Failure в одну строку и убил бы различение «имя кривое» vs
«инструмент не найден (+ список доступных)». Это две разные диагностики одного слоя. Граница честности.

### [L6] src/Harbor.Application/Agents/DefaultAgent.cs:448-459 LoadSessionContextAsync — АНТИ-ROP: Result разворачивают В исключение
```csharp
        var session = await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure)
            throw new InvalidOperationException(session.Error);

        var messages = await _sessionStore.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (messages.IsFailure)
            throw new InvalidOperationException(messages.Error);

        return new DefaultSessionContext(session.Value, messages.Value, _sessionStore, _steeringQueue);
```
Единственный потребитель — PromptAsync:315 внутри try :313, чей catch(Exception) :334 снова заворачивает
`ex.Message` в Result.Failure. Т.е. Failure → throw → catch → Failure: двойная конверсия через границу метода.
→ ДИФФ:
```csharp
-    private async Task<ISessionContext> LoadSessionContextAsync(string sessionId, CancellationToken ct)
-    {
-        var session = await _sessionStore.GetAsync(sessionId, ct)...;
-        if (session.IsFailure) throw new InvalidOperationException(session.Error);
-        var messages = await _sessionStore.GetMessagesAsync(sessionId, ct)...;
-        if (messages.IsFailure) throw new InvalidOperationException(messages.Error);
-        return new DefaultSessionContext(...);
-    }
+    private async Task<Result<ISessionContext>> LoadSessionContextAsync(string sessionId, CancellationToken ct)
+        => await _sessionStore.GetAsync(sessionId, ct).ConfigureAwait(false)
+            .Bind(session => _sessionStore.GetMessagesAsync(sessionId, ct)
+                .Map(messages => (ISessionContext)new DefaultSessionContext(
+                    session, messages, _sessionStore, _steeringQueue)));
```
и в PromptAsync:
```csharp
-                var session = await LoadSessionContextAsync(State.SessionId, ct).ConfigureAwait(false);
-                var result = await _agentLoop.RunAsync(session, State.Agent, linkedCts.Token).ConfigureAwait(false);
+                var loaded = await LoadSessionContextAsync(State.SessionId, linkedCts.Token).ConfigureAwait(false);
+                if (loaded.IsFailure)
+                    throw new InvalidOperationException(loaded.Error);   // ← единственный мост на границе рана
+                var result = await _agentLoop.RunAsync(loaded.Value, State.Agent, linkedCts.Token).ConfigureAwait(false);
```
→ ВЫИГРЫШ: −5 строк; два throw-моста заменены одним явным на границе (там, где catch всё равно нужен ради RunAsync);
Bind-цепочка готова к третьему шагу загрузки (например, attachments — F1-повестка).
Замечание честности: catch :334 всё равно останется (RunAsync может кинуть), так что полный отказ от исключений здесь невозможен.

### [L7] src/Harbor.Hosting/Modules/ConfigurationModule.cs:41-61 — паттерн «load → IsFailure → warn → default» ×2
(fp-audit §2 видел этот же паттерн в HostBuilder.Registration.cs:90-120 — это его двойник в модуле Hosting.)
```csharp
        var commonResult = commonStore.LoadAsync().GetAwaiter().GetResult();
        if (commonResult.IsFailure)
            ctx.Logger.LogWarning("Failed to load CommonConfig, using defaults: {Error}", commonResult.Error);
        ctx.Common = commonResult.IsSuccess ? commonResult.Value : new CommonConfig();
        ...
        var harborResult = harborStore.LoadAsync().GetAwaiter().GetResult();
        if (harborResult.IsSuccess)
        { ctx.Harbor = harborResult.Value; }
        else
        {
            ctx.Logger.LogWarning("Failed to load HarborConfig, using defaults: {Error}", harborResult.Error);
            ctx.Harbor = new HarborConfig();
        }
```
Обратите внимание: два блока решают одно и то же РАЗНЫМ синтаксисом (тернарник vs if/else) — живой дрейф копипасты.
→ ДИФФ:
```csharp
+    private static T LoadOrDefault<T>(Func<Result<T>> load, T fallback, string name, HarborCompositionContext ctx)
+    {
+        var r = load();
+        if (r.IsFailure)
+            ctx.Logger.LogWarning("Failed to load {Name}, using defaults: {Error}", name, r.Error);
+        return r.IsSuccess ? r.Value : fallback;
+    }
+
-        /* блоки :41-61 */
+        ctx.Common = LoadOrDefault(
+            () => commonStore.LoadAsync().GetAwaiter().GetResult(), new CommonConfig(), "CommonConfig", ctx);
+        ctx.Harbor = LoadOrDefault(
+            () => harborStore.LoadAsync().GetAwaiter().GetResult(), new HarborConfig(), "HarborConfig", ctx);
```
→ ВЫИГРЫШ: −12 строк; третий конфиг-стор (следующий по F5-повестке) = одна строка, а не 10-строчный блок.

### [L8] src/Harbor.Registries/Tools/CompositeToolRegistry.cs:85-94 GetTool — fold «первый успех», ОСТАВИТЬ (осознанно)
```csharp
        foreach (var source in _sources)
        {
            var result = source.GetTool(name);
            if (result.IsSuccess)
            {
                return result;
            }
        }
        return Result.Failure<ITool>($"Tool '{name}' is not registered.");
```
→ LINQ-эквивалент `_sources.Select(s => s.GetTool(name)).FirstOrDefault(r => r.IsSuccess)` требует Result?-нуля
(CSE Result<T> — класс, FirstOrDefault даёт null → NRE на .IsSuccess) или ToArray+Match — обе версии либо опаснее,
либо дороже текущего цикла на горячем пути поиска тула. Вердикт: foreach тут — правильный императивный fold. Оставить.

---

## Ось 3. Switch-by-type/string: полный census территории (13 сайтов)

### [S1] src/Harbor.Hosting/Modules/IpcModule.cs:20-35 — HARBOR_MODE switch-by-string → enum-парсер
(двойник HostBuilder.Registration.cs:323-338 из fp-audit §2 — второе место диспетчеризации той же переменной окружения.)
```csharp
        switch (mode.ToLowerInvariant())
        {
            case "inprocess":
                services.UseInProcessHarborClient();
                break;
            case "ipc-server":
                services.UseInProcessHarborClient();
                services.UseHarborIpcServer(pipeName);
                break;
            case "ipc-client":
                services.UseIpcHarborClient(pipeName);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown HARBOR_MODE: '{mode}'. Expected one of: inprocess, ipc-server, ipc-client.");
        }
```
→ ДИФФ:
```csharp
+    internal enum HarborIpcMode { InProcess, IpcServer, IpcClient }
+
+    if (!Enum.TryParse<HarborIpcMode>(mode, ignoreCase: true, out var ipcMode))
+        throw new ArgumentException(
+            $"Unknown HARBOR_MODE: '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<HarborIpcMode>())}.");
+    switch (ipcMode)
+    {
+        case HarborIpcMode.InProcess: services.UseInProcessHarborClient(); break;
+        case HarborIpcMode.IpcServer: services.UseInProcessHarborClient(); services.UseHarborIpcServer(pipeName); break;
+        case HarborIpcMode.IpcClient: services.UseIpcHarborClient(pipeName); break;
+    }
```
→ ВЫИГРЫШ: −2 строки + список валидных значений генерируется из enum (сейчас дублируется руками в сообщении об ошибке);
enum можно переиспользовать в HostBuilder.Registration, когда его :323-338 пойдут по тому же пути.
DU-полиморфизм (иерархия классов на режим) здесь ИЗЛИШЕН: 3 варианта без поведения — enum исчерпывает.

### [S2-S8] Классифицированные, НЕ переводимые (каждый — с причиной):
- **[S2] AgentLoop.cs:449** switch-by-type над StreamEvent — arch2 F15 (flush-инвариант ×5 копий). DU уже есть; фикс — F15, не «последняя миля».
- **[S3] PermissionService.cs:213 + :186** switch-by-string на имена тулов — arch2 F4 (+мёртвые arms — deep2 A3). Фик — F4 ResourcePathArgName.
- **[S4] RetryPolicy.cs:88** `switch (ex)` по типам исключений (OCE/HRE/default) — транслятор transient/fatal; исключения .NET не сделать DU,
  стратегия-per-provider — arch2 F6. Оставить.
- **[S5] CompactionService.cs:639,673 + MessageConverter.cs:37,73** switch-by-type над message/part-DU → wire/текст.
  arch2 «Чего НЕ покрыто»: «switch-by-type по message-DU уместен». Это исчерпывающий fold замкнутого DU — канон, оставить.
- **[S6] StorageModule.cs:29 / TuiModule.cs:32** switch-expression по конфиг-строке → выбор DI-фабрики в composition root.
  Единственная точка сборки, дефолтная arm есть, вариантов ≤4 — словарь фабрик усложнит без выгоды. Оставить.
- **[S7] OnboardingWizard.cs:184** парсинг меню-инпута (`"1"/"code"` → режим агента): UI-скрипт, локальный, self-contained. Оставить.
- **[S8] StopReasonToLower MessageConverter.cs:96** enum→wire-string: исчерпывающий fold enum-DU с fallback-arm. Канон. Оставить.
→ ВЫИГРЫШ от census: единственный НОВЫЙ кандидат оси 3 — S1 (IpcModule); остальное уже размечено предыдущими аудитами
или является правильной формой fold'а.

---

## Ось 4. Null-check церемония: остатки после guard-спринта

Контекст: fp-solid-audit §1 насчитал 1787 guard-throw'ов (Application 570, Registries 569) и 9 ThrowIfNull.
Сейчас по ВСЕМУ src/: **81** ArgumentNullException (−95%), в моей территории осталось:

### [N1] src/Harbor.Application/Resilience/RetryPolicy.cs:44 — ПОСЛЕДНИЙ ручной guard территории
```csharp
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (options.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.BaseDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
```
→ ДИФФ:
```csharp
-        if (operation is null) throw new ArgumentNullException(nameof(operation));
+        ArgumentNullException.ThrowIfNull(operation);
```
→ ВЫИГРЫШ: −1 строка × каждый будущий guard; прецедент уже в территории — SystemPromptBuilder.cs:47.

### [N2] Census-факт для отчёта спринта P0
Территория: Application — 1 ручной guard (RetryPolicy.cs:44), Hosting — 0, Registries — 0; ThrowIfNull — 1
(SystemPromptBuilder.cs:47). Guard-генератор/ручной перевод фактически ДОБИЛ церемонию в трёх проектах;
остаток — одна строка. Пункт закрытия P0 из fp-audit §5.

### [N3] src/Harbor.Application/Agents/DefaultAgent.cs:531-536 — мёртвый код вместо обработки ошибки
```csharp
        var stats = await _store.GetStatsAsync(Session.Id, ct).ConfigureAwait(false);
        if (stats.IsFailure)
        {
            _ = stats.Error;
            return;
        }
```
`_ = stats.Error;` — discard, НЕ читающий значение: строка ничего не делает (ни лога, ни счётчика).
Это остаток незавершённого решения «что делать при провале статистики». Минимум:
```csharp
-            _ = stats.Error;
+            // stats недоступны (стор закрыт/ошибка IO) — usage этой сессии будет потерян, это best-effort путь.
```
либо LogDebug. → ВЫИГРЫШ: устраняет ложный сигнал «ошибка обработана».

---

## Ось 5. LINQ-недоиспользование: ручные циклы → Select/Where/ToArray (вне hot-path)

### [Q1] src/Harbor.Application/Agents/DefaultAgent.cs:99-108 — ручное копирование снапшота слушателей
```csharp
            lock (_listenersLock)
            {
                int count = _listeners.Count;
                if (count == 0) return;
                snapshot = new Func<AgentEvent, CancellationToken, ValueTask>[count];
                for (int i = 0; i < count; i++)
                {
                    snapshot[i] = _listeners[i];
                }
            }
```
Комментарий выше (:96-97) объясняет отказ от ToList() — но ToArray() под lock быстрее ОБЕИХ версий
(один Array.Copy вместо по-элементного Add с проверками границ).
→ ДИФФ:
```csharp
             lock (_listenersLock)
             {
-                int count = _listeners.Count;
-                if (count == 0) return;
-                snapshot = new Func<AgentEvent, CancellationToken, ValueTask>[count];
-                for (int i = 0; i < count; i++)
-                {
-                    snapshot[i] = _listeners[i];
-                }
+                if (_listeners.Count == 0) return;
+                snapshot = _listeners.ToArray();   // один Array.Copy, без итератора List<T>
             }
```
→ ВЫИГРЫШ: −7 строк и БЫСТРЕЕ (это путь КАЖДОГО опубликованного события — редкий случай, где LINQ-замена ещё и перформанс).

### [Q2] src/Harbor.Registries/Providers/ProviderRegistry.cs:44-61 GetRegisteredProviderIds
```csharp
        int count = _clients.Count;
        if (count == 0)
        {
            return Array.Empty<ProviderId>();
        }
        var result = new ProviderId[count];
        int i = 0;
        foreach (var key in _clients.Keys)
        {
            result[i++] = key;
        }
        return result;
```
→ ДИФФ:
```csharp
-        int count = _clients.Count;
-        if (count == 0) return Array.Empty<ProviderId>();
-        var result = new ProviderId[count];
-        int i = 0;
-        foreach (var key in _clients.Keys) { result[i++] = key; }
-        return result;
+        var keys = _clients.Keys.ToArray();      // ConcurrentDictionary.Keys уже делает снапшот в массив
+        return keys.Length == 0 ? Array.Empty<ProviderId>() : keys;
```
Доказательство легитимности: соседний GetAllModelsAsync:103 УЖЕ пишет `_clients.Keys.ToArray()`, хотя комментарий
над ним обещает «pooled buffer» — т.е. команда сама считает Keys.ToArray() приемлемым вне hot-path.
→ ВЫИГРЫШ: −7 строк; снимается расхождение комментарий↔код в двух методах одного класса.

### [Q3] src/Harbor.Application/Permissions/PermissionService.cs:168-176 GetRuleset — копия значений словаря вручную
```csharp
            var persistedRules = new PermissionRule[byRule.Count];
            int index = 0;
            foreach (var kvp in byRule)
            {
                persistedRules[index++] = kvp.Value;
            }
            ruleset = ruleset.Merge(new PermissionRuleset(persistedRules));
```
→ ДИФФ:
```csharp
-            var persistedRules = new PermissionRule[byRule.Count];
-            int index = 0;
-            foreach (var kvp in byRule) { persistedRules[index++] = kvp.Value; }
-            ruleset = ruleset.Merge(new PermissionRuleset(persistedRules));
+            ruleset = ruleset.Merge(new PermissionRuleset([.. byRule.Values]));
```
Семантика идентична: порядок обхода ConcurrentDictionary и так нестабилен, Merge порядок правил не различает.
→ ВЫИГРЫШ: −5 строк.

### [Q4] src/Harbor.Application/Configuration/AuthStore.cs:113-117 ListApiKeysAsync — проекция словаря циклом
```csharp
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in configResult.Value.ApiKeys)
        {
            result[kv.Key] = !string.IsNullOrEmpty(kv.Value);
        }
```
→ ДИФФ:
```csharp
-        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
-        foreach (var kv in configResult.Value.ApiKeys)
-        {
-            result[kv.Key] = !string.IsNullOrEmpty(kv.Value);
-        }
+        Dictionary<string, bool> result = configResult.Value.ApiKeys.ToDictionary(
+            static kv => kv.Key,
+            static kv => !string.IsNullOrEmpty(kv.Value),
+            StringComparer.OrdinalIgnoreCase);
```
→ ВЫИГРЫШ: −4 строки; comparer сохранён (важно: env-цикл ниже ищет ContainsKey по lowercase-pid).

### [Q5-опционально] src/Harbor.Application/Sessions/CompactionService.cs:647-651 и :657-662 — separator-циклы форматтера
```csharp
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    AppendFormattedPart(builder, parts[i]);
                }
```
→ Возможный дифф: `builder.Append(string.Join('\n', a.Parts.Select(FormatPart)))` после перевода
AppendFormattedPart → string FormatPart. Файл сам документирует (:682-684), что форматтер вызывается редко
(раз за компакцию), аллокации допустимы. Но выигрыш −4 строки против рефакторинга двух методов —
рекомендация: НЕ делать сейчас, вернуться при следующей правке этого файла.

### Анти-примеры (hot-path, НЕ переводить — фиксируем чтобы не «улучшили» случайно):
- CompactionService.cs:526-530 headTokens-аккумулятор и :534-539 first-kept-id — комментарии файла прямо запрещают
Skip().FirstOrDefault()/Sum()-аллокации;
- InMemoryEventBus.cs:190-206 fan-out — await внутри цикла + сбор мёртвых подписчиков, LINQ неприменим структурно.

---

## Сводка

| # | Место | Тип | Действие | Строк |
|---|---|---|---|---|
| B1 | ProviderRegistry.cs:64-97 | try/catch (b) | ResultGuard.Try + Materialize | −18 |
| B2 | ConfigStore.cs:73-110 LoadAsync | try/catch (b) | ResultGuard.Try + MapError | −14 |
| B3 | ConfigStore.cs:113-137 SaveAsync | try/catch (b) | ResultGuard.Try + TapError | −8 |
| B4 | CompactionService.cs:559 | OCE→Failure | НЕ переводить; решить судьбу канона (TryAsyncAllowCancelled?) | 0 |
| L1 | AgentLoop.cs:110-134 | лесенка 4× | Bind-цепочка (без F9) | −8 |
| L2 | CompactionService.cs:460-482 | лесенка 2× | Bind | −5 |
| L3 | HarborConfig.cs:276-325 Normalize | 4 независимых гарда | ParseOpt + Result.Combine (все ошибки разом) | −20 |
| L4 | ToolDispatcher.cs:120-136 HasSequentialTool | вложенная лесенка | Select+Where+Any | −6 |
| L5 | ToolDispatcher.cs:155-166 | формальная лесенка | ОСТАВИТЬ (диагностики различаются) | 0 |
| L6 | DefaultAgent.cs:448-459 | анти-ROP Result→throw→Failure | Result<ISessionContext> + Bind | −5 |
| L7 | ConfigurationModule.cs:41-61 | дубль load→default | LoadOrDefault<T> | −12 |
| L8 | CompositeToolRegistry.cs:85-94 | fold первого успеха | ОСТАВИТЬ (Result?-null ловушка) | 0 |
| S1 | IpcModule.cs:20-35 | switch-by-string | enum TryParse (список ошибок из enum) | −2 |
| N1 | RetryPolicy.cs:44 | ручной guard | ThrowIfNull | −1 |
| N3 | DefaultAgent.cs:534 | `_ = stats.Error` | удалить/задокументировать | ±0 |
| Q1 | DefaultAgent.cs:99-108 | ручной снапшот | ToArray() (быстрее!) | −7 |
| Q2 | ProviderRegistry.cs:44-61 | ручные Keys | Keys.ToArray() | −7 |
| Q3 | PermissionService.cs:168-176 | ручная копия Values | `[.. byRule.Values]` | −5 |
| Q4 | AuthStore.cs:113-117 | проекция циклом | ToDictionary | −4 |
| BUG | ProviderRegistry.cs:147 | OCE без фильтра | фильтр `when (!cancellationToken.IsCancellationRequested)` | −1 |

**20 пунктов в таблице: 17 конверсий/фиксов + 3 осознанных «не трогать» (L5/L8/B4); суммарно ~−123 строки.**
Плюс полный census с вердиктами: 25 try-сайтов территории (21 × (c) с причиной, 4 × (b) с диффом),
13 switch-сайтов (11 «оставить» с причиной, 1 кандидат S1), null-guard церемония сведена к одной строке.

### Три системных вывода для спринта
1. **ResultGuard простаивает**: 0 продовых вызовов через спринт после релиза. Первые три вызова — B1/B2/B3, все механические.
2. **Канон отмены противоречив**: TryAsync пробрасывает OCE, но два живых сайта (DefaultAgent:254, CompactionService:559/F17)
   сознательно мапят отмену в Failure, а ProviderRegistry:147 вообще её глотает как «timeout» (баг). Нужен документ-решение:
   либо «catch(OCE)→Failure разрешён с комментарием», либо второй конверсор.
3. **Ось 3 почти пуста**: из 13 switch'ей территории 11 — правильные fold'ы DU/enum или уже размечены F4/F15/F6.
   Последняя миля switch-by-string — это S1 (HARBOR_MODE) и не более.
