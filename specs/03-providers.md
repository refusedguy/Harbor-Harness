# 03 — LLM Провайдеры

> Документ: абстракция LLM-провайдеров, SSE streaming, протоколы tool-calling, конкретные реализации для Anthropic/OpenAI/Google/Ollama. Lazy-loading. Auth (API key, OAuth).

## 1. Цели

1. **Единый интерфейс** — `ILlmClient` поверх всех провайдеров.
2. **Streaming-first** — `IAsyncEnumerable<LLMEvent>` для real-time рендеринга.
3. **Lazy-loading** — провайдеры грузятся только при первом использовании, не при старте.
4. **Auth abstraction** — API key / OAuth / SigV4 — единый интерфейс.
5. **Provider-specific quirks** — `cache_control` для Anthropic, `reasoning_effort` для OpenAI o-моделей, и т.п.
6. **Tool-calling protocol unification** — Anthropic tool_use, OpenAI function_call, Google functionCall — приведены к одному `LLMEvent`.

## 2. Абстракция

### 2.1. `ILlmClient`

```csharp
// Harbor.Abstractions/Llm/ILlmClient.cs

public interface ILlmClient
{
    /// <summary>Идентификатор провайдера ("anthropic", "openai", "google", "ollama").</summary>
    string ProviderId { get; }
    
    /// <summary>Список поддерживаемых моделей (lazy-loaded из каталога или hardcoded).</summary>
    Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default);
    
    /// <summary>Streaming-запрос к LLM.</summary>
    IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        CancellationToken ct = default);
}

public sealed record ModelInfo(
    string Id,                    // "claude-opus-4-20250514"
    string ProviderId,            // "anthropic"
    string DisplayName,           // "Claude Opus 4"
    int ContextWindow,            // 200000
    int MaxOutputTokens,          // 32000
    bool SupportsReasoning,       // true for Claude w/ extended thinking, o1, Gemini Pro
    bool SupportsVision,          // true for most modern
    bool SupportsToolUse,         // true for all except some small models
    Pricing Pricing,
    string PromptTemplate);      // "anthropic" | "openai" | "gemini" | "codex" | etc.

public sealed record Pricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion,
    decimal? CacheWritePerMillion);
```

### 2.2. `LlmRequest`

```csharp
public sealed record LlmRequest(
    string Model,                           // "claude-opus-4-20250514"
    IReadOnlyList<LlmMessage> Messages,
    string SystemPrompt,                    // единственная system message
    IReadOnlyList<ToolDefinition> Tools,
    ToolChoice? ToolChoice,                 // null = auto, "required", "specific"
    int? MaxOutputTokens,
    decimal? Temperature,
    decimal? TopP,
    int? TopK,
    ReasoningEffort? ReasoningEffort,       // для reasoning моделей
    CacheStrategy? CacheStrategy,           // prompt caching
    IReadOnlyDictionary<string, string>? ExtraHeaders);
```

### 2.3. `LlmMessage` (упрощённый, provider-agnostic)

```csharp
public abstract record LlmMessage(string Role);

public sealed record UserMessage(string Role, IReadOnlyList<ContentBlock> Content) 
    : LlmMessage("user");

public sealed record AssistantMessage(string Role, IReadOnlyList<ContentBlock> Content, string? StopReason) 
    : LlmMessage("assistant");

public sealed record ToolResultMessage(string Role, string ToolCallId, string ToolName, string Output, bool IsError) 
    : LlmMessage("user");  // Anthropic and OpenAI both put tool results in user role

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;
public sealed record ImageBlock(string MimeType, byte[] Data) : ContentBlock;
public sealed record ToolCallBlock(string Id, string Name, JsonElement Arguments) : ContentBlock;
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;
public sealed record ThinkingBlock(string Text) : ContentBlock;
```

### 2.4. `LLMEvent` — streaming protocol

Унифицированный event stream (вдохновлён `LLMEvent` из opencode/kilo):

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextStartEvent), "text_start")]
[JsonDerivedType(typeof(TextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(TextEndEvent), "text_end")]
[JsonDerivedType(typeof(ThinkingStartEvent), "thinking_start")]
[JsonDerivedType(typeof(ThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(ThinkingEndEvent), "thinking_end")]
[JsonDerivedType(typeof(ToolCallStartEvent), "tool_call_start")]
[JsonDerivedType(typeof(ToolCallDeltaEvent), "tool_call_delta")]
[JsonDerivedType(typeof(ToolCallEndEvent), "tool_call_end")]
[JsonDerivedType(typeof(StepStartEvent), "step_start")]
[JsonDerivedType(typeof(StepFinishEvent), "step_finish")]
[JsonDerivedType(typeof(FinishEvent), "finish")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record LLMEvent;

public sealed record TextStartEvent(string Id) : LLMEvent;
public sealed record TextDeltaEvent(string Id, string Delta) : LLMEvent;
public sealed record TextEndEvent(string Id, string FinalText) : LLMEvent;

public sealed record ThinkingStartEvent(string Id) : LLMEvent;
public sealed record ThinkingDeltaEvent(string Id, string Delta) : LLMEvent;
public sealed record ThinkingEndEvent(string Id, string FinalText) : LLMEvent;

public sealed record ToolCallStartEvent(string Id, string ToolName) : LLMEvent;
public sealed record ToolCallDeltaEvent(string Id, string ArgsDelta) : LLMEvent;
public sealed record ToolCallEndEvent(string Id, string ToolName, JsonElement Args) : LLMEvent;

public sealed record StepStartEvent(int Index) : LLMEvent;
public sealed record StepFinishEvent(
    int Index, 
    string FinishReason,  // "stop" | "length" | "tool_use" | "content_filter"
    Usage Usage,
    ProviderMetadata? Metadata) : LLMEvent;

public sealed record FinishEvent() : LLMEvent;
public sealed record ErrorEvent(string Message, Exception? Exception) : LLMEvent;

public sealed record Usage(
    int InputTokens,
    int OutputTokens,
    int? ReasoningTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens);

public sealed record ProviderMetadata(
    string ModelId,  // actual model returned by provider (may differ from requested)
    string? ProviderRequestId);
```

### 2.5. `ToolDefinition` (передаётся в LLM)

```csharp
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonDocument InputSchema);  // JSON Schema (draft 7)
```

```csharp
// Пример:
new ToolDefinition(
    Name: "read",
    Description: "Read contents of a file",
    InputSchema: JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute or relative file path" },
            "offset": { "type": "integer", "description": "Line to start from (1-indexed)" },
            "limit": { "type": "integer", "description": "Max lines to read" }
          },
          "required": ["path"]
        }
        """));
```

## 3. Streaming implementation

### 3.1. SSE parsing

.NET 10 имеет встроенный `System.Net.ServerSentEvents`. Используем:

```csharp
using System.Net.ServerSentEvents;

public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    
    public string ProviderId => "anthropic";
    
    public async IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        CancellationToken ct = default)
    {
        var httpRequest = BuildHttpRequest(request);
        
        using var response = await _http.SendAsync(
            httpRequest, 
            HttpCompletionOption.ResponseHeadersRead, 
            ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            yield return new ErrorEvent(
                $"Anthropic API error {(int)response.StatusCode}: {errorBody}",
                Exception: null);
            yield break;
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        
        await foreach (var sseEvent in SseParser.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            if (sseEvent.EventType == "message_start" || 
                sseEvent.EventType == "content_block_start" ||
                sseEvent.EventType == "content_block_delta" ||
                sseEvent.EventType == "content_block_stop" ||
                sseEvent.EventType == "message_delta" ||
                sseEvent.EventType == "message_stop")
            {
                var json = JsonNode.Parse(sseEvent.Data);
                foreach (var evt in MapAnthropicEvent(json!))
                {
                    yield return evt;
                }
            }
            else if (sseEvent.EventType == "error" || sseEvent.EventType == "ping")
            {
                // ping = heartbeat, ignore
                // error = handle
            }
        }
    }
    
    private IEnumerable<LLMEvent> MapAnthropicEvent(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>();
        switch (type)
        {
            case "message_start":
                var msg = node["message"]!;
                yield return new StepStartEvent(Index: 0);
                break;
            
            case "content_block_start":
                var block = node["content_block"]!;
                var blockType = block["type"]?.GetValue<string>();
                var index = node["index"]!.GetValue<int>();
                var id = block["id"]?.GetValue<string>() ?? index.ToString();
                
                if (blockType == "text")
                    yield return new TextStartEvent(id);
                else if (blockType == "thinking")
                    yield return new ThinkingStartEvent(id);
                else if (blockType == "tool_use")
                {
                    var name = block["name"]!.GetValue<string>();
                    yield return new ToolCallStartEvent(id, name);
                }
                break;
            
            case "content_block_delta":
                var delta = node["delta"]!;
                var deltaType = delta["type"]?.GetValue<string>();
                var deltaIndex = node["index"]!.GetValue<int>();
                var deltaId = deltaIndex.ToString();  // simplified
                
                if (deltaType == "text_delta")
                    yield return new TextDeltaEvent(deltaId, delta["text"]!.GetValue<string>());
                else if (deltaType == "thinking_delta")
                    yield return new ThinkingDeltaEvent(deltaId, delta["thinking"]!.GetValue<string>());
                else if (deltaType == "input_json_delta")
                    yield return new ToolCallDeltaEvent(deltaId, delta["partial_json"]!.GetValue<string>());
                break;
            
            case "content_block_stop":
                // Convert to corresponding End event (need state per content block)
                // In practice, we maintain a state machine in the parser
                break;
            
            case "message_delta":
                var stopReason = node["delta"]?["stop_reason"]?.GetValue<string>();
                var usage = node["usage"]!;
                yield return new StepFinishEvent(
                    Index: 0,
                    FinishReason: stopReason ?? "stop",
                    Usage: new Usage(
                        InputTokens: usage["input_tokens"]?.GetValue<int>() ?? 0,
                        OutputTokens: usage["output_tokens"]?.GetValue<int>() ?? 0,
                        ReasoningTokens: null,
                        CacheReadTokens: null,
                        CacheWriteTokens: null),
                    Metadata: null);
                break;
            
            case "message_stop":
                yield return new FinishEvent();
                break;
        }
    }
}
```

### 3.2. Partial JSON repair для tool call args

LLM стримит tool call args как partial JSON. Нужно уметь parse'ить даже неполный JSON (для UI preview), и финальный — для execution.

```csharp
public sealed class PartialJsonParser
{
    /// <summary>Возвращает best-effort parsed object, даже если JSON не полный.</summary>
    public static JsonNode? ParsePartial(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Попробовать восстановить
            return RepairJson(json);
        }
    }
    
    private static JsonNode? RepairJson(string json)
    {
        // Простейший repair: закрыть незакрытые строки/объекты/массивы
        var sb = new StringBuilder(json);
        int openObjects = 0, openArrays = 0, inString = 0;
        bool escape = false;
        
        foreach (char c in json)
        {
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString++; continue; }
            if (inString % 2 == 1) continue;  // inside string
            
            if (c == '{') openObjects++;
            else if (c == '}') openObjects--;
            else if (c == '[') openArrays++;
            else if (c == ']') openArrays--;
        }
        
        if (inString % 2 == 1) sb.Append('"');  // close unclosed string
        for (int i = 0; i < openArrays; i++) sb.Append(']');
        for (int i = 0; i < openObjects; i++) sb.Append('}');
        
        try { return JsonNode.Parse(sb.ToString()); }
        catch { return null; }
    }
}
```

## 4. Anthropic Messages API

### 4.1. Endpoint

```
POST https://api.anthropic.com/v1/messages
Headers:
  x-api-key: <key>
  anthropic-version: 2023-06-01
  anthropic-beta: interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14
  content-type: application/json
Body:
{
  "model": "claude-opus-4-20250514",
  "max_tokens": 32000,
  "system": "<system prompt>",
  "messages": [...],
  "tools": [...],
  "stream": true,
  "thinking": { "type": "enabled", "budget_tokens": 10000 }  // optional
}
```

### 4.2. Message conversion

```csharp
// AnthropicLlmClient.cs
public sealed class AnthropicMessageConverter
{
    public static JsonArray ToAnthropicMessages(IReadOnlyList<LlmMessage> messages)
    {
        var result = new JsonArray();
        
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage user:
                    result.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = ToAnthropicContent(user.Content)
                    });
                    break;
                
                case AssistantMessage assistant:
                    result.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = ToAnthropicContent(assistant.Content)
                    });
                    break;
                
                case ToolResultMessage toolResult:
                    // Anthropic expects tool_result as a content block in a user message
                    result.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = toolResult.ToolCallId,
                                ["content"] = toolResult.Output,
                                ["is_error"] = toolResult.IsError
                            }
                        }
                    });
                    break;
            }
        }
        
        return result;
    }
    
    private static JsonNode ToAnthropicContent(IReadOnlyList<ContentBlock> blocks)
    {
        // If single text block, return as string (saves tokens)
        if (blocks.Count == 1 && blocks[0] is TextBlock text)
            return text.Text;
        
        var arr = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock t:
                    arr.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                    break;
                case ImageBlock img:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = img.MimeType,
                            ["data"] = Convert.ToBase64String(img.Data)
                        }
                    });
                    break;
                case ToolCallBlock tc:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id,
                        ["name"] = tc.Name,
                        ["input"] = tc.Arguments.Deserialize<JsonNode>()
                    });
                    break;
                case ToolResultBlock tr:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = tr.ToolUseId,
                        ["content"] = tr.Content,
                        ["is_error"] = tr.IsError
                    });
                    break;
                case ThinkingBlock th:
                    arr.Add(new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = th.Text
                    });
                    break;
            }
        }
        return arr;
    }
}
```

### 4.3. Prompt caching

Anthropic поддерживает cache_control — помечаем system prompt и длинные сообщения:

```csharp
private static JsonObject BuildSystemWithCache(string systemPrompt, bool useCache)
{
    var system = new JsonObject
    {
        ["type"] = "text",
        ["text"] = systemPrompt
    };
    if (useCache)
    {
        system["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
    }
    return system;
}

// Также для последних 2 user messages (или для сообщений с большим контекстом)
private static void AddCacheToLastMessages(JsonArray messages, int lastN)
{
    for (int i = Math.Max(0, messages.Count - lastN); i < messages.Count; i++)
    {
        var msg = messages[i].AsObject();
        var content = msg["content"];
        if (content is JsonArray arr && arr.Count > 0)
        {
            var lastBlock = arr[arr.Count - 1]!.AsObject();
            lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        }
    }
}
```

### 4.4. Extended thinking

```csharp
public sealed record ThinkingConfig(
    ThinkingType Type,           // Enabled | Disabled
    int? BudgetTokens);          // max thinking tokens

// В request:
if (request.ReasoningEffort.HasValue)
{
    var budget = request.ReasoningEffort switch
    {
        ReasoningEffort.Low => 5000,
        ReasoningEffort.Medium => 10000,
        ReasoningEffort.High => 20000,
        _ => 10000
    };
    payload["thinking"] = new JsonObject
    {
        ["type"] = "enabled",
        ["budget_tokens"] = budget
    };
}
```

## 5. OpenAI Chat Completions / Responses API

### 5.1. Chat Completions

```
POST https://api.openai.com/v1/chat/completions
Authorization: Bearer <key>
Body:
{
  "model": "gpt-4o",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "..." },
    { "role": "assistant", "tool_calls": [...] },
    { "role": "tool", "tool_call_id": "...", "content": "..." }
  ],
  "tools": [...],
  "stream": true,
  "stream_options": { "include_usage": true }
}
```

SSE events: `chat.completion.chunk` (с `delta`), `chat.completion` (финальный с usage).

### 5.2. Responses API (для o1, o3, GPT-5+)

Новый протокол, отличается структурой messages и tool-calling:

```
POST https://api.openai.com/v1/responses
Body:
{
  "model": "o3",
  "input": [
    { "role": "user", "content": [...] },
    { "role": "assistant", "output": [...] }
  ],
  "tools": [...],
  "reasoning": { "effort": "high" },
  "stream": true
}
```

Выбор между Chat Completions и Responses:
- GPT-4o, GPT-4-turbo, GPT-3.5 → Chat Completions
- o1, o3, GPT-5+ → Responses API (reasoning models)
- Codex (Cursor-специфичный) → Responses API

### 5.3. Tool-calling mapping

| Harbor | OpenAI |
|---|---|
| `ToolCallBlock` | `tool_calls: [{ id, type: "function", function: { name, arguments } }]` |
| `ToolResultBlock` | отдельное message `{ role: "tool", tool_call_id, content }` |
| `ToolDefinition` | `{ type: "function", function: { name, description, parameters } }` |

### 5.4. Reasoning models (o1, o3)

Для reasoning-моделей:
- `temperature` должен быть 1 (или не задан)
- `top_p` не поддерживается
- `tools` поддерживаются, но reasoning идёт до tool calls
- Streaming может содержать `reasoning` content

## 6. Google Gemini

### 6.1. Endpoint

```
POST https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-exp:streamGenerateContent?alt=sse
Headers:
  x-goog-api-key: <key>
Body:
{
  "contents": [
    { "role": "user", "parts": [{ "text": "..." }] },
    { "role": "model", "parts": [{ "text": "..." }, { "functionCall": { "name": "...", "args": {...} } }] },
    { "role": "user", "parts": [{ "functionResponse": { "name": "...", "response": { "result": "..." } } }] }
  ],
  "systemInstruction": { "parts": [{ "text": "..." }] },
  "tools": [{ "functionDeclarations": [...] }],
  "generationConfig": { "maxOutputTokens": 8192, "temperature": 0.7 }
}
```

### 6.2. Quirks

- `systemInstruction` вместо `system` role.
- `role: "model"` вместо `role: "assistant"`.
- `functionCall`/`functionResponse` в `parts`, не отдельные message types.
- Streaming через `?alt=sse` query param.
- Function response должен быть wrapped в `response: { result: ... }`.
- Vision: `inlineData: { mimeType, data }` (base64).

## 7. Ollama (local)

### 7.1. Endpoint

```
POST http://localhost:11434/api/chat
Body:
{
  "model": "llama3.2",
  "messages": [...],
  "tools": [...],
  "stream": true,
  "options": { "temperature": 0.7 }
}
```

Ollama — OpenAI-compatible, но streaming не SSE, а **NDJSON** (каждая строка — отдельный JSON-объект).

### 7.2. NDJSON parsing

```csharp
public async IAsyncEnumerable<LLMEvent> StreamOllamaAsync(
    LlmRequest request, 
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
    using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(stream);
    
    while (!reader.EndOfStream)
    {
        ct.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) continue;
        
        var chunk = JsonNode.Parse(line)!;
        var message = chunk["message"]!;
        var role = message["role"]?.GetValue<string>();
        
        if (message["content"]?["text"] is JsonNode text && !string.IsNullOrEmpty(text.GetValue<string>()))
        {
            yield return new TextDeltaEvent("0", text.GetValue<string>());
        }
        
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                var fn = tc!["function"]!;
                yield return new ToolCallEndEvent(
                    Id: tc!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                    ToolName: fn["name"]!.GetValue<string>(),
                    Args: fn["arguments"]!.AsObject().Deserialize<JsonElement>());
            }
        }
        
        if (chunk["done"]?.GetValue<bool>() == true)
        {
            yield return new StepFinishEvent(
                Index: 0,
                FinishReason: "stop",
                Usage: new Usage(
                    InputTokens: chunk["prompt_eval_count"]?.GetValue<int>() ?? 0,
                    OutputTokens: chunk["eval_count"]?.GetValue<int>() ?? 0,
                    ReasoningTokens: null,
                    CacheReadTokens: null,
                    CacheWriteTokens: null),
                Metadata: null);
            yield return new FinishEvent();
        }
    }
}
```

## 8. Auth strategies

### 8.1. Auth resolver interface

```csharp
public interface IAuthResolver
{
    Task<string> ResolveApiKeyAsync(string providerId, CancellationToken ct = default);
    Task<OAuthToken?> ResolveOAuthTokenAsync(string providerId, CancellationToken ct = default);
    Task RefreshOAuthTokenAsync(string providerId, CancellationToken ct = default);
}

public sealed record OAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt);
```

### 8.2. Источники API key (по приоритету)

1. CLI flag: `--anthropic-api-key=...`
2. Env var: `ANTHROPIC_API_KEY`
3. Config file: `providers.anthropic.apiKey`
4. OS keychain: `harbor auth set anthropic` (через `Microsoft.Extensions.AI.OAuth` или сторонний lib)
5. OAuth token (если есть)

### 8.3. Credential store

```csharp
public interface ICredentialStore
{
    Task<string?> GetAsync(string providerId, CancellationToken ct = default);
    Task SetAsync(string providerId, string apiKey, CancellationToken ct = default);
    Task DeleteAsync(string providerId, CancellationToken ct = default);
}
```

**Реализации**:
- `FileCredentialStore` — plain text в `~/.harbor/credentials.json` (chmod 600). Cross-platform, simple.
- `KeychainCredentialStore` — OS keychain (macOS Keychain, Windows Credential Manager, Linux Secret Service via `keyring` NuGet).

### 8.4. OAuth flows

| Provider | Flow | Library |
|---|---|---|
| Anthropic Claude Pro/Max | OAuth 2.0 with PKCE | Manual (Anthropic не имеет C# SDK с OAuth) |
| OpenAI Codex | OAuth 2.0 | Manual |
| GitHub Copilot | OAuth 2.0 device flow | Manual или `GitHubJwt` |
| Google | OAuth 2.0 | `Google.Apis.Auth` |

OAuth — отдельная подсистема, в MVP только API key. OAuth — v1.

## 9. Provider registry и lazy loading

### 9.1. Registry

```csharp
public interface IProviderRegistry
{
    ILlmClient GetClient(string providerId);
    Task<ILlmClient> GetClientAsync(string providerId, CancellationToken ct = default);
    Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync(CancellationToken ct = default);
    IReadOnlyList<string> GetAvailableProviderIds();
}

internal sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, Lazy<ILlmClient>> _clients = new();
    private readonly IServiceProvider _services;
    private readonly PluginCatalog _plugins;
    
    public ILlmClient GetClient(string providerId)
    {
        return _clients.GetOrAdd(providerId, id => new Lazy<ILlmClient>(() =>
        {
            // 1. Builtin providers
            var client = id switch
            {
                "anthropic" => ActivatorUtilities.CreateInstance<AnthropicLlmClient>(_services),
                "openai"    => ActivatorUtilities.CreateInstance<OpenAILlmClient>(_services),
                "google"    => ActivatorUtilities.CreateInstance<GoogleLlmClient>(_services),
                "ollama"    => ActivatorUtilities.CreateInstance<OllamaLlmClient>(_services),
                _ => null
            };
            
            // 2. Plugin providers
            if (client == null)
            {
                client = _plugins.CreateProviderClient(id);
            }
            
            return client ?? throw new ProviderNotFoundException(id);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
```

### 9.2. Builtin providers vs plugin providers

- **Builtin**: Anthropic, OpenAI, Google, Ollama, OpenRouter (major), Bedrock (через `AWSSDK`), Azure OpenAI. Все statically linked в `Harbor.Providers.*` сборки.
- **Plugin**: Mistral, xAI, Groq, Together, DeepSeek, Cohere, etc. — через `IProviderPlugin`.

### 9.3. Model catalog

Каждый провайдер возвращает hardcoded список моделей с metadata. Это позволяет `harbor models list` работать без network calls.

```csharp
public static class AnthropicModels
{
    public static readonly ModelInfo ClaudeOpus4 = new(
        Id: "claude-opus-4-20250514",
        ProviderId: "anthropic",
        DisplayName: "Claude Opus 4",
        ContextWindow: 200_000,
        MaxOutputTokens: 32_000,
        SupportsReasoning: true,
        SupportsVision: true,
        SupportsToolUse: true,
        Pricing: new Pricing(
            InputPerMillion: 15m,
            OutputPerMillion: 75m,
            CacheReadPerMillion: 1.5m,
            CacheWritePerMillion: 18.75m),
        PromptTemplate: "anthropic");
    
    public static readonly ModelInfo ClaudeSonnet4 = new(/* ... */);
    public static readonly ModelInfo ClaudeHaiku35 = new(/* ... */);
    
    public static IReadOnlyList<ModelInfo> All => new[] { ClaudeOpus4, ClaudeSonnet4, ClaudeHaiku35 };
}
```

### 9.4. Models.dev integration (опционально)

Как у kilo/opencode — тянуть каталог моделей с `models.dev`. В MVP — hardcoded. В v1 — опциональное обновление из `models.dev`.

```csharp
public sealed class ModelsDevCatalog
{
    public async Task<IReadOnlyList<ModelInfo>> FetchAsync(CancellationToken ct)
    {
        var response = await _http.GetStringAsync("https://models.dev/models.json", ct);
        return ParseModels(response);
    }
}
```

Кэшируется в `~/.harbor/cache/models-dev.json` с TTL 24h.

## 10. Retry и error handling

```csharp
public sealed class ResilientLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    
    public ResilientLlmClient(ILlmClient inner)
    {
        _inner = inner;
        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => 
                r.StatusCode == HttpStatusCode.TooManyRequests ||  // 429
                r.StatusCode == HttpStatusCode.InternalServerError ||  // 500
                r.StatusCode == HttpStatusCode.BadGateway ||  // 502
                r.StatusCode == HttpStatusCode.ServiceUnavailable)  // 503
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetryAsync: (outcome, timespan, attempt, ctx) =>
                {
                    // log retry
                    return Task.CompletedTask;
                });
    }
    
    public async IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var hasError = false;
            Exception? lastError = null;
            
            await foreach (var evt in _inner.StreamAsync(request, ct))
            {
                if (evt is ErrorEvent err)
                {
                    hasError = true;
                    lastError = err.Exception ?? new Exception(err.Message);
                    
                    if (attempt < 3 && IsRetryable(err))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                        break;  // retry
                    }
                    
                    yield return evt;
                    yield break;
                }
                else
                {
                    yield return evt;
                }
            }
            
            if (!hasError) yield break;
            if (attempt >= 3) yield break;
        }
    }
    
    private static bool IsRetryable(ErrorEvent err) => 
        err.Message.Contains("429") || 
        err.Message.Contains("500") || 
        err.Message.Contains("502") || 
        err.Message.Contains("503");
}
```

## 11. Cost tracking

Каждый `StepFinishEvent` содержит `Usage`. `SessionManager` суммирует:

```csharp
public sealed class CostTracker
{
    private readonly Dictionary<string, ModelPricing> _pricing;
    
    public decimal CalculateCost(string modelId, Usage usage)
    {
        if (!_pricing.TryGetValue(modelId, out var pricing))
            return 0;
        
        return (usage.InputTokens * pricing.InputPerMillion / 1_000_000m) +
               (usage.OutputTokens * pricing.OutputPerMillion / 1_000_000m) +
               ((usage.CacheReadTokens ?? 0) * (pricing.CacheReadPerMillion ?? 0) / 1_000_000m) +
               ((usage.CacheWriteTokens ?? 0) * (pricing.CacheWritePerMillion ?? 0) / 1_000_000m);
    }
}
```

TUI показывает в status bar: `Cost: $0.1234 | Tokens: 12.5K in / 3.2K out`.

## 12. Provider-specific quirks (cheat sheet)

| Quirk | Provider | Что делать |
|---|---|---|
| Tool name case sensitivity | OpenAI | Lowercase required (Anthropic allows PascalCase) |
| `system` role | OpenAI (Chat) | Separate message; OpenAI Responses → `instructions` field; Anthropic → `system` field; Google → `systemInstruction` |
| Empty assistant message | Anthropic | Must have at least one content block; OpenAI allows empty |
| Tool result format | Anthropic | `tool_result` block in user message; OpenAI → separate `tool` role message; Google → `functionResponse` part |
| `max_tokens` field | OpenAI | `max_completion_tokens` (new) vs `max_tokens` (deprecated); Anthropic → `max_tokens`; Google → `maxOutputTokens` |
| Stop sequences | All | `stop` (OpenAI), `stop_sequences` (Anthropic), `stopSequences` (Google) |
| Image input | All | Different formats: OpenAI `image_url`, Anthropic `source.base64`, Google `inlineData` |
| Reasoning effort | OpenAI o1/o3 | `reasoning_effort: "low"|"medium"|"high"` in request; Anthropic → `thinking.budget_tokens`; Google → `thinkingConfig.thinkingBudget` |
| Cache control | Anthropic only | `cache_control: { type: "ephemeral" }` on content blocks; OpenAI has automatic caching; Google has explicit context caching |
| Vision support | All modern | All support; format differences only |

## 13. Stream consumer example (TUI integration)

```csharp
public sealed class StreamingRenderer
{
    public async Task RenderAsync(IAsyncEnumerable<LLMEvent> stream, CancellationToken ct)
    {
        await foreach (var evt in stream)
        {
            switch (evt)
            {
                case TextDeltaEvent td:
                    Console.Write(td.Delta);  // simple streaming
                    break;
                
                case ThinkingDeltaEvent thd:
                    // dim color, italic
                    Console.Write($"\x1b[2;3m{thd.Delta}\x1b[0m");
                    break;
                
                case ToolCallStartEvent tcs:
                    Console.WriteLine($"\n\x1b[36m→ {tcs.ToolName}\x1b[0m");
                    break;
                
                case ToolCallEndEvent tce:
                    // Show parsed args (pretty-printed)
                    var args = JsonSerializer.Serialize(tce.Args, new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine($"\x1b[90m{args}\x1b[0m");
                    break;
                
                case StepFinishEvent sf:
                    if (sf.Usage != null)
                    {
                        Console.WriteLine($"\n\x1b[90m[tokens: {sf.Usage.InputTokens} in / {sf.Usage.OutputTokens} out]\x1b[0m");
                    }
                    break;
                
                case ErrorEvent err:
                    Console.WriteLine($"\n\x1b[31mError: {err.Message}\x1b[0m");
                    break;
            }
        }
    }
}
```

## 14. Microsoft.Extensions.AI integration

.NET 10 имеет `Microsoft.Extensions.AI` — унифицированный `IChatClient`. Мы можем **реализовать `ILlmClient` как адаптер поверх `IChatClient`**:

```csharp
public sealed class MicrosoftExtensionsAIClient : ILlmClient
{
    private readonly IChatClient _chatClient;
    
    public async IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ToChatMessages(request.Messages);
        var chatOptions = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens,
            Tools = request.Tools.Select(t => AIFunctionFactory.Create(
                name: t.Name,
                description: t.Description,
                jsonSchema: t.InputSchema,
                method: (args) => { /* dispatch */ })).ToList()
        };
        
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, chatOptions, ct))
        {
            foreach (var evt in MapUpdateToEvent(update))
            {
                yield return evt;
            }
        }
    }
}
```

**Плюсы**: унификация с .NET ecosystem, `OpenAI` / `Azure.AI.OpenAI` / `Ollama` / `Mistral` NuGet-пакеты — все предоставляют `IChatClient`.

**Минусы**: abstraction накладывает ограничения — нет fine-grained control над provider-specific features (например, `cache_control` для Anthropic).

**Решение**: 
- **MVP**: использовать `Microsoft.Extensions.AI` + Microsoft-провайдеры для OpenAI/Azure/Ollama.
- **Custom implementations** для Anthropic (т.к. critical для нас) и Google.
- **Plugin providers** — могут либо реализовать `ILlmClient` напрямую, либо использовать `IChatClient` adapter.

---

**Next**: `04-tools.md` — tool schema, tool-calling loop, permission model, builtin tools.
