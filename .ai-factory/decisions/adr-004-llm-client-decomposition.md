# ADR-004: OpenAiCompatibleLlmClient Decomposition — OpenAiSseParser

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

`OpenAiCompatibleLlmClient` — 376-line класс с 5 ответственностями:

| Responsibility | Lines | Methods |
|---|---|---|
| Auth & HTTP plumbing | 42–209 | `StreamAsync`, `BuildRequest` |
| Request serialization | 143–271 | `BuildMessages`, `ApplyCompatFlags` |
| **SSE framing** | 98–116 | Inline `while (ReadLineAsync...)` loop |
| **Chunk JSON→event mapping** | 273–375 | `MapChunk`, `MapChunkFromDocument` |
| Provider compat flags | 211–228 | `ApplyCompatFlags` (Strategy, уже extracted) |

Проблемы:
1. **Двойное кодирование JSON:** `JsonSerializer.Serialize(payload, string)` → `new StringContent(json, Encoding.UTF8)` — string → UTF-8 bytes, лишняя аллокация
2. **`JsonDocument.Parse(string)` на каждом SSE chunk** — ~1–2 KB DOM allocation × hundreds of chunks
3. **`StreamReader` + `ReadLineAsync()`** — внутренний char buffer, string allocation per line
4. **`MapChunk` → `.ToList()`** — materializes List<LlmEvent> чтобы сохранить JsonElement за пределами `using`
5. **Zero `PipeReader` / `Utf8JsonReader` usage** — хотя паттерн уже есть в `WireCodec.cs`

---

## 2. Решение

### 2.1 Extract `OpenAiSseParser` (internal static)

```csharp
// src/Harbor.Providers.OpenAiCompatible/OpenAiSseParser.cs
internal static class OpenAiSseParser
{
    public static IEnumerable<LlmEvent> ParseChunk(
        ReadOnlySpan<char> data,
        Dictionary<int, string> indexToId,
        ILogger logger);
}
```

**Что извлекаем:**
- `MapChunk(string, Dictionary<int,string>)` → `ParseChunk(ReadOnlySpan<char>, ...)`
- `MapChunkFromDocument(JsonDocument, ...)` → inline в ParseChunk с `Utf8JsonReader`

**Что НЕ извлекаем:**
- HTTP request/response lifecycle
- `Task.Run` + `Channel<LlmEvent>` orchestration
- `IAuthResolver` / `IModelCatalog` calls
- `ApplyCompatFlags` (уже Strategy)

### 2.2 SSE Line Framing → PipeReader (follow-up)

Пока оставляем `StreamReader.ReadLineAsync()`. Follow-up таск:
- Заменить на `PipeReader.ReadAsync()` + `Utf8JsonReader`
- Pattern из `WireCodec.cs` (MessagePack IPC framing)

### 2.3 Request Serialization → Utf8JsonWriter (follow-up)

```csharp
// В stead of:
JsonSerializer.Serialize(payload, JsonOptions)
→ new StringContent(json, Encoding.UTF8)

// Use:
var buffer = new ArrayBufferWriter<byte>();
using var writer = new Utf8JsonWriter(buffer);
writer.WriteStartObject();
...
writer.Flush();
httpContent = new ByteArrayContent(buffer.WrittenSpan.ToArray());
```

---

## 3. Последствия

### 3.1 Что меняется

| Компонент | Изменение |
|---|---|
| `OpenAiCompatibleLlmClient.cs` | -100 строк, делегирует chunk parsing в `OpenAiSseParser` |
| `OpenAiSseParser.cs` | Новый файл, internal static, pure function |
| `MapChunk` / `MapChunkFromDocument` | Удаляются из клиента, перемещаются в parser |

### 3.2 Что остаётся

| Компонент | Причина |
|---|---|
| `StreamAsync` orchestration | Часть `ILlmClient` контракта |
| `BuildRequest` | Provider-specific |
| `ApplyCompatFlags` | Уже Strategy |

### 3.3 Риски

| Риск | Митигация |
|---|---|
| ParseChunk не покрывает edge cases | Сохраняем старый код как fallback на 1 релиз |
| AOT-breaking в JSON parsing | `Utf8JsonReader` + source-generated `JsonSerializerContext` |
| Performance regression | Benchmarks до/после |

---

## 4. Правила

1. **OpenAiSseParser — pure function** — no DI, no I/O, no side effects
2. **ReadOnlySpan<char> вместо string** — zero allocation на hot path
3. **Utf8JsonReader вместо JsonDocument** — zero DOM allocation
4. **Никакого reflection** — AOT-safe
