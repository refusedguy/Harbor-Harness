# ADR-007: Observability — System.Diagnostics.ActivitySource

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

Текущее состояние:
- **Zero telemetry** — нет OpenTelemetry, нет ActivitySource
- `System.Diagnostics` используется только для `Process`, `Stopwatch`, `Debug`
- Нет distributed tracing, нет semantic conventions для LLM calls

Нужно добавить:
1. `HarborTelemetry.Source` — `System.Diagnostics.ActivitySource`
2. Hierarchical Activity spans для Agent Turns, LLM API calls, Tool Executions
3. GenAI semantic convention tags (`gen_ai.prompt.tokens`, `gen_ai.completion.tokens`)
4. Zero allocations когда нет listeners

---

## 2. Решение

### 2.1 Architecture

```csharp
// src/Harbor.Core/Telemetry/HarborTelemetry.cs
public static class HarborTelemetry
{
    public static readonly ActivitySource Source = new("Harbor");
    
    public static Activity? StartAgentRun(string agentName, string model) =>
        Source.StartActivity("Agent.Run", ActivityKind.Internal,
            tags: new ActivityTagsCollection
            {
                ["gen_ai.agent.name"] = agentName,
                ["gen_ai.request.model"] = model
            });
    
    public static Activity? StartLlmCall(string provider, string model) =>
        Source.StartActivity("LLM.Call", ActivityKind.Client,
            tags: new ActivityTagsCollection
            {
                ["gen_ai.provider.name"] = provider,
                ["gen_ai.request.model"] = model
            });
    
    public static Activity? StartToolExecution(string toolName) =>
        Source.StartActivity("Tool.Execute", ActivityKind.Internal,
            tags: new ActivityTagsCollection
            {
                ["gen_ai.tool.name"] = toolName
            });
}
```

### 2.2 Integration Points

| Component | Where to add |
|---|---|
| `AgentLoop.RunAsync` | Wrap entire run in `Agent.Run` activity |
| `OpenAiCompatibleLlmClient.StreamAsync` | Wrap HTTP call in `LLM.Call` activity |
| `ToolDispatcher.ExecuteAsync` | Wrap each tool in `Tool.Execute` activity |
| `ICompactionService` | Add `Compaction` activity |

### 2.3 Zero Allocation Strategy

- `ActivitySource.StartActivity` returns `null` when no listeners — zero cost
- `ActivityTagsCollection` — pooled internally by `System.Diagnostics`
- No string allocations for tags when no listeners (Activity optimizes this)

---

## 3. Последствия

| Что меняется | Что нет |
|---|---|
| `AgentLoop` — Activity wrapping | Core logic — не меняется |
| `OpenAiCompatibleLlmClient` — Activity wrapping | SSE parsing — не меняется |
| `ToolDispatcher` — Activity wrapping | Tool execution — не меняется |
| `HarborTelemetry.cs` — новый файл | `IEventBus` — остаётся |

---

## 4. Правила

1. **Activity = optional** — core logic не зависит от наличия listeners
2. **No OpenTelemetry SDK dependency** — только `System.Diagnostics` (BCL)
3. **GenAI semantic conventions** — follow https://opentelemetry.io/docs/specs/semconv/gen-ai/
4. **No reflection** — Activity tags via strongly-typed `ActivityTagsCollection`
