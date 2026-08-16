# ADR-003: AgentLoop Decomposition — RetryPolicy, TokenTracker, ToolDispatcher

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

`AgentLoop` — 388-line orchestrator с 11 зависимостями в конструкторе. Уже частично декомпозирован:
- `ToolDispatcher` — извлечён (R32)
- `StreamingCoalescer` — извлечён (R32)

Остаётся:
1. **Нет retry-логики** — все provider calls (`StreamAsync`, `GetModelsAsync`) и tool executions не имеют transient-fault handling. При 429/5xx — crash.
2. **Token tracking scattered** — `ITokenEstimator` (heuristic), `ICompactionService` (compaction gating), inline `AgentLoop` usage capture — три разных места для одной концепции.
3. **Constructor bloat** — 11 параметров, включая `_tokenEstimator` который используется только косвенно через `ICompactionService`.

---

## 2. Решение

### 2.1 `RetryPolicy` (новая)

```csharp
// src/Harbor.Core/Resilience/IRetryPolicy.cs
public interface IRetryPolicy
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, 
                            RetryOptions options, 
                            CancellationToken ct);
}

// src/Harbor.Core/Resilience/RetryOptions.cs
public sealed record RetryOptions(int MaxAttempts, TimeSpan BaseDelay, bool UseJitter);
```

**Где применяется:**
- `AgentLoop`: `client.StreamAsync(request, ct)` → `_retryPolicy.ExecuteAsync(..., ct)`
- `AgentLoop`: `client.GetModelsAsync(ct)` → retry при startup
- `ToolDispatcher`: `tool.ExecuteAsync(...)` → retry только на transient errors

**Правила:**
- Exponential backoff + jitter
- Max 3 attempts для LLM calls, max 2 для tools
- Не retry на 4xx (кроме 429) — сразу fail
- AOT-safe: no reflection, no Polly

### 2.2 `TokenTracker` (новая)

```csharp
// src/Harbor.Abstractions/Sessions/ITokenTracker.cs
public interface ITokenTracker
{
    void RecordTurnUsage(Usage usage);
    int EstimateTokens(IReadOnlyList<AgentMessage> messages);
    bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model);
    TokenStats GetStats();
}

// src/Harbor.Core/Sessions/TokenTracker.cs
public sealed class TokenTracker : ITokenTracker
{
    // Объединяет:
    // - HeuristicTokenEstimator (chars/4)
    // - CompactionService.ShouldCompact
    // - Usage persistence
}
```

**Что меняется:**
- `AgentLoop` больше не держит `ITokenEstimator` напрямую
- `ICompactionService` получает `ITokenTracker` вместо `ITokenEstimator`
- `AgentLoop` ctor: `ITokenEstimator` → `ITokenTracker` (1 параметр вместо 2)

### 2.3 `ToolDispatcher` — уже извлечён ✅

Остаётся без изменений. Параллельность через `ArrayPool<T>` + `Task.WhenAll`.

---

## 3. Последствия

### 3.1 Что меняется

| Компонент | Изменение |
|---|---|
| `AgentLoop` ctor | 11 → 8 параметров (убираем `ITokenEstimator`, добавляем `IRetryPolicy`, `ITokenTracker`) |
| `ICompactionService` | Зависит от `ITokenTracker` вместо `ITokenEstimator` |
| `ToolDispatcher` | Оборачивает tool execution в retry для transient errors |
| Новые файлы | `IRetryPolicy`, `RetryPolicy`, `RetryOptions`, `ITokenTracker`, `TokenTracker` |

### 3.2 Что остаётся

| Компонент | Причина |
|---|---|
| `StreamingCoalescer` | Уже извлечён, работает |
| `ToolDispatcher` | Уже извлечён, работает |
| `MessageConverter` | Остаётся в `AgentLoop` — слишком специфичен для извлечения |

### 3.3 Риски

| Риск | Митигация |
|---|---|
| Retry на не-transient ошибках | Strict retry conditions: только 429, 5xx, timeout |
| TokenTracker race conditions | Thread-safe `ConcurrentDictionary` для stats |
| AOT-breaking в RetryPolicy | No reflection, no dynamic codegen |

---

## 4. Правила

1. **Retry только на transient faults** — 429, 5xx, network timeouts
2. **Не retry на 4xx** (кроме 429) — сразу fail
3. **TokenTracker — single source of truth** для всей token-арифметики
4. **AgentLoop — orchestrator only** — не содержит бизнес-логики
