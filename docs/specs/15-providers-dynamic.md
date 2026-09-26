# 15 — Dynamic Providers

> Документ: конфигурация LLM-провайдеров через JSON файлы + `modelsUrl` для dynamic model discovery. Generic OpenAI-compatible adapter. Без hardcoded models.json, без перекомпиляции для нового провайдера.

> **Supersedes часть `03-providers.md` §9-11.** Builtin providers (Anthropic, OpenAI, Google, Ollama) остаются для special cases. Все остальные — через dynamic config.

## 1. Цели

1. **Zero-code provider addition** — добавить OpenRouter / Mistral / DeepSeek / Together / Groq / etc. через JSON файл, без пересборки.
2. **Dynamic model discovery** — `modelsUrl` endpoint, откуда тянем список моделей. Обновляем кэш раз в N часов.
3. **Generic adapters** — `openai-compatible`, `anthropic-compatible`, `google-compatible` api types. Покрывают 90%+ провайдеров.
4. **Plugin providers** — для truly special cases (Bedrock SigV4, GitHub Copilot OAuth) — C# plugin через `IProviderPlugin`.
5. **User config** — `~/.harbor/providers/*.json` для personal providers, `.harbor/providers/*.json` для project-local.
6. **Built-in marketplace** (future) — `harbor provider install openrouter` тянет с центрального репо.

## 2. Provider config schema

### 2.1. Минимальный пример — OpenAI-compatible

```jsonc
// ~/.harbor/providers/openrouter.json
{
  "$schema": "https://harbor.sh/schema/provider.json",
  "id": "openrouter",
  "displayName": "OpenRouter",
  "description": "Multi-provider router with 200+ models",
  
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiType": "openai-compatible",
  "apiVersion": "v1",
  
  "authType": "bearer",
  "authEnvVar": "OPENROUTER_API_KEY",
  "authFile": null,  // или "~/.harbor/credentials/openrouter.key"
  
  "modelsUrl": "https://openrouter.ai/api/v1/models",
  "modelsRefreshHours": 24,
  "modelsPath": "data",  // JSONPath к массиву моделей в response
  "modelMapping": {
    "id": "id",
    "displayName": "name",
    "contextWindow": "context_length",
    "maxOutputTokens": "top_provider.max_completion_tokens",
    "pricing.input": "pricing.prompt",
    "pricing.output": "pricing.completion",
    "pricing.cacheRead": "pricing.prompt_cache_write",  // OpenRouter-specific
    "supportsVision": "architecture.input_modalities",
    "supportsToolUse": "supported_parameters"  // array contains "tools"
  },
  
  "headers": {
    "HTTP-Referer": "https://harbor.sh",
    "X-Title": "Harbor"
  },
  
  "requestTransform": null,  // опциональный JMESPath или JSONata
  "responseTransform": null,
  
  "capabilities": {
    "streaming": true,
    "toolUse": true,
    "vision": "auto",  // "auto" = detect from model capabilities, true, false
    "reasoning": "auto",
    "cacheControl": false,
    "systemPrompt": "system"  // "system" | "instructions" | "first_user"
  },
  
  "rateLimits": {
    "requestsPerMinute": 60,
    "tokensPerMinute": 100000
  },
  
  "timeout": 60,
  "retries": 3
}
```

### 2.2. Anthropic-compatible (для Anthropic proxies / AWS Bedrock без special auth)

```jsonc
// ~/.harbor/providers/anthropic-proxy.json
{
  "id": "anthropic-proxy",
  "displayName": "Anthropic Proxy (internal)",
  "baseUrl": "https://internal-proxy.corp.com/anthropic",
  "apiType": "anthropic-compatible",
  "apiVersion": "2023-06-01",
  
  "authType": "header",
  "authHeader": "x-api-key",
  "authEnvVar": "ANTHROPIC_PROXY_KEY",
  
  "modelsUrl": null,  // hardcoded models
  "models": [
    {
      "id": "claude-opus-4",
      "displayName": "Claude Opus 4",
      "contextWindow": 200000,
      "maxOutputTokens": 32000,
      "supportsReasoning": true,
      "supportsVision": true,
      "supportsToolUse": true,
      "pricing": { "input": 15.0, "output": 75.0, "cacheRead": 1.5, "cacheWrite": 18.75 }
    }
  ],
  
  "headers": {
    "anthropic-version": "2023-06-01",
    "anthropic-beta": "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14"
  },
  
  "capabilities": {
    "streaming": true,
    "toolUse": true,
    "vision": true,
    "reasoning": true,
    "cacheControl": true,
    "systemPrompt": "system"
  }
}
```

### 2.3. Google-compatible

```jsonc
// ~/.harbor/providers/vertex-ai.json
{
  "id": "vertex-ai",
  "displayName": "Google Vertex AI",
  "baseUrl": "https://us-central1-aiplatform.googleapis.com/v1/projects/{PROJECT_ID}/locations/us-central1",
  "apiType": "google-compatible",
  "apiVersion": "v1beta",
  
  "authType": "oauth",
  "authScopes": ["https://www.googleapis.com/auth/cloud-platform"],
  "authEnvVar": "GOOGLE_APPLICATION_CREDENTIALS",  // path to service account JSON
  
  "modelsUrl": null,
  "models": [
    {
      "id": "gemini-2.0-flash",
      "displayName": "Gemini 2.0 Flash",
      "contextWindow": 1000000,
      "maxOutputTokens": 8192,
      "supportsVision": true,
      "supportsToolUse": true,
      "pricing": { "input": 0.075, "output": 0.30 }
    }
  ],
  
  "urlTemplate": "/publishers/google/models/{modelId}:streamGenerateContent?alt=sse",
  
  "capabilities": {
    "streaming": true,
    "toolUse": true,
    "vision": true,
    "reasoning": "auto",
    "systemPrompt": "systemInstruction"
  }
}
```

### 2.4. Special case — Bedrock (через plugin)

Bedrock требует SigV4 auth — слишком сложно для JSON config. Через plugin:

```csharp
// Harbor.Providers.Bedrock.dll
public sealed class BedrockProviderPlugin : IProviderPlugin
{
    public string Name => "bedrock";
    
    public void RegisterProviders(IProviderRegistryBuilder builder)
    {
        builder.AddProvider<BedrockLlmClient>();
    }
}

public sealed class BedrockLlmClient : ILlmClient
{
    public string ProviderId => "bedrock";
    
    public async IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // SigV4 auth through AWSSDK
        var credentials = await _awsCredentials.GetCredentialsAsync();
        var signedRequest = SigV4Signer.Sign(request, credentials, region: "us-east-1");
        
        // Stream via Bedrock Converse Stream API
        // ...
    }
}
```

## 3. Generic adapters

### 3.1. `openai-compatible` adapter

Покрывает: OpenRouter, DeepSeek, Groq, Together, Mistral, xAI, Cohere, Fireworks, Anyscale, Perplexity, Lemonade, NVIDIA NIM, vLLM, LM Studio, Ollama (OpenAI-compat endpoint), Azure OpenAI (v1), и десятки других.

```csharp
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly IAuthResolver _auth;
    
    public string ProviderId => _config.Id;
    
    public async IAsyncEnumerable<LLMEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var httpRequest = BuildRequest(request);
        
        using var response = await _http.SendAsync(httpRequest, 
            HttpCompletionOption.ResponseHeadersRead, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            yield return new ErrorEvent($"API error {(int)response.StatusCode}: {error}", null);
            yield break;
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        
        await foreach (var sse in SseParser.ReadAsync(stream, ct))
        {
            if (sse.EventType != "data") continue;
            if (sse.Data == "[DONE]") 
            {
                yield return new FinishEvent();
                yield break;
            }
            
            foreach (var evt in MapOpenAiChunk(sse.Data, request))
                yield return evt;
        }
    }
    
    private HttpRequestMessage BuildRequest(LlmRequest request)
    {
        var url = $"{_config.BaseUrl}/chat/completions";
        
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        
        if (request.Tools.Count > 0)
        {
            payload["tools"] = BuildTools(request.Tools);
            if (request.ToolChoice != null)
                payload["tool_choice"] = BuildToolChoice(request.ToolChoice);
        }
        
        if (request.MaxOutputTokens.HasValue)
        {
            // Some providers use max_tokens, others max_completion_tokens
            var field = _config.Capabilities.GetValueOrDefault("maxTokensField", "max_tokens");
            payload[field] = request.MaxOutputTokens;
        }
        
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature;
        if (request.TopP.HasValue) payload["top_p"] = request.TopP;
        
        // Provider-specific compat flags
        ApplyCompatFlags(payload, request);
        
        // Custom headers from config
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        
        foreach (var (k, v) in _config.Headers ?? new())
            msg.Headers.TryAddWithoutValidation(k, v);
        
        // Auth
        var apiKey = _auth.ResolveApiKeyAsync(ProviderId).GetAwaiter().GetResult();
        ApplyAuth(msg, apiKey);
        
        return msg;
    }
    
    private void ApplyCompatFlags(JsonObject payload, LlmRequest request)
    {
        // Detect provider quirks based on _config.Id
        switch (_config.Id)
        {
            case "deepseek":
                // DeepSeek: reasoning models don't support temperature
                if (request.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
                    payload.Remove("temperature");
                break;
            
            case "groq":
                // Groq: requires max_tokens for some models
                if (!payload.ContainsKey("max_tokens") && !payload.ContainsKey("max_completion_tokens"))
                    payload["max_tokens"] = 4096;
                break;
            
            case "mistral":
                // Mistral: uses different tool format
                if (payload["tools"] is JsonArray tools)
                {
                    payload["tools"] = new JsonArray(tools.Select(t => 
                    {
                        var fn = t!["function"]!;
                        return new JsonObject { ["type"] = "function", ["function"] = fn };
                    }).ToArray());
                }
                break;
        }
    }
    
    private IEnumerable<LLMEvent> MapOpenAiChunk(string data, LlmRequest request)
    {
        var chunk = JsonNode.Parse(data);
        if (chunk == null) yield break;
        
        var choices = chunk["choices"]?.AsArray();
        if (choices == null || choices.Count == 0)
        {
            // Maybe usage-only chunk
            if (chunk["usage"] is JsonNode usage)
            {
                yield return new StepFinishEvent(
                    Index: 0,
                    FinishReason: "stop",
                    Usage: new Usage(
                        InputTokens: usage["prompt_tokens"]?.GetValue<int>() ?? 0,
                        OutputTokens: usage["completion_tokens"]?.GetValue<int>() ?? 0,
                        ReasoningTokens: usage["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<int>(),
                        CacheReadTokens: null,
                        CacheWriteTokens: null),
                    Metadata: null);
            }
            yield break;
        }
        
        var choice = choices[0]!;
        var delta = choice["delta"];
        var finishReason = choice["finish_reason"]?.GetValue<string>();
        
        if (delta?["content"] is JsonNode content && !string.IsNullOrEmpty(content.GetValue<string>()))
        {
            yield return new TextDeltaEvent("0", content.GetValue<string>());
        }
        
        if (delta?["reasoning_content"] is JsonNode reasoning && !string.IsNullOrEmpty(reasoning.GetValue<string>()))
        {
            yield return new ThinkingDeltaEvent("0", reasoning.GetValue<string>());
        }
        
        if (delta?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                var id = tc!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                var fn = tc!["function"]!;
                
                if (tc!["index"]?.GetValue<int>() == 0 && !string.IsNullOrEmpty(fn["name"]?.GetValue<string>()))
                {
                    yield return new ToolCallStartEvent(id, fn["name"]!.GetValue<string>());
                }
                
                if (fn["arguments"] is JsonNode args && !string.IsNullOrEmpty(args.GetValue<string>()))
                {
                    yield return new ToolCallDeltaEvent(id, args.GetValue<string>());
                }
            }
        }
        
        if (finishReason != null)
        {
            var usage = chunk["usage"];
            yield return new StepFinishEvent(
                Index: 0,
                FinishReason: finishReason,
                Usage: usage != null ? new Usage(
                    InputTokens: usage["prompt_tokens"]?.GetValue<int>() ?? 0,
                    OutputTokens: usage["completion_tokens"]?.GetValue<int>() ?? 0,
                    ReasoningTokens: null,
                    CacheReadTokens: null,
                    CacheWriteTokens: null) : null,
                Metadata: null);
        }
    }
}
```

### 3.2. `anthropic-compatible` adapter

Покрывает: Anthropic direct, internal Anthropic proxies, AWS Bedrock (если auth handled separately).

Реализует Anthropic Messages API protocol (см. `03-providers.md` §4). Quirks:
- `system` field (не message).
- `tool_result` as content block in user message.
- `cache_control` на content blocks.
- `thinking` parameter для extended thinking.
- Beta headers.

### 3.3. `google-compatible` adapter

Покрывает: Google AI Studio, Vertex AI.

Реализует Google Generative AI protocol (см. `03-providers.md` §6). Quirks:
- `systemInstruction` вместо `system` role.
- `role: "model"` вместо `role: "assistant"`.
- `functionCall`/`functionResponse` в `parts`.
- `?alt=sse` query param для streaming.

## 4. Model discovery

### 4.1. `modelsUrl` fetch + cache

```csharp
public sealed class DynamicModelCatalog
{
    private readonly HttpClient _http;
    private readonly string _cacheDir = "~/.harbor/cache/providers";
    
    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(
        ProviderConfig config, 
        CancellationToken ct = default)
    {
        if (config.ModelsUrl == null)
            return config.Models ?? Array.Empty<ModelInfo>();
        
        var cachePath = Path.Combine(_cacheDir, $"{config.Id}.json");
        var cacheAge = GetCacheAge(cachePath);
        
        if (cacheAge < TimeSpan.FromHours(config.ModelsRefreshHours) &&
            await TryReadCacheAsync(cachePath, ct) is { } cached)
        {
            return cached;
        }
        
        // Fetch fresh
        try
        {
            var response = await _http.GetStringAsync(config.ModelsUrl, ct);
            var models = ParseModelsResponse(response, config);
            
            // Update cache
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(cachePath, response, ct);
            
            return models;
        }
        catch (Exception ex)
        {
            // Fallback to stale cache if fetch fails
            if (await TryReadCacheAsync(cachePath, ct) is { } stale)
            {
                _logger.LogWarning(ex, "Failed to fetch models for {Provider}, using stale cache", config.Id);
                return stale;
            }
            throw;
        }
    }
    
    private IReadOnlyList<ModelInfo> ParseModelsResponse(string json, ProviderConfig config)
    {
        var root = JsonNode.Parse(json);
        var modelsNode = config.ModelsPath != null 
            ? root!.Path(config.ModelsPath)
            : root!["data"] ?? root!["models"];
        
        var mapping = config.ModelMapping ?? new ModelMapping();
        var result = new List<ModelInfo>();
        
        foreach (var modelNode in modelsNode!.AsArray())
        {
            try
            {
                var model = new ModelInfo(
                    Id: modelNode!.Path(mapping.Id ?? "id")!.GetValue<string>(),
                    ProviderId: config.Id,
                    DisplayName: modelNode.Path(mapping.DisplayName ?? "name")?.GetValue<string>() ?? "unknown",
                    ContextWindow: (int)(modelNode.Path(mapping.ContextWindow ?? "context_length")?.GetValue<long>() ?? 4096),
                    MaxOutputTokens: (int)(modelNode.Path(mapping.MaxOutputTokens ?? "max_output_tokens")?.GetValue<long>() ?? 4096),
                    SupportsReasoning: DetectReasoning(modelNode, mapping),
                    SupportsVision: DetectVision(modelNode, mapping),
                    SupportsToolUse: DetectToolUse(modelNode, mapping),
                    Pricing: ParsePricing(modelNode, mapping),
                    PromptTemplate: config.ApiType switch
                    {
                        "openai-compatible" => "openai",
                        "anthropic-compatible" => "anthropic",
                        "google-compatible" => "gemini",
                        _ => "default"
                    });
                result.Add(model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse model {Node}: {Error}", modelNode, ex.Message);
            }
        }
        
        return result;
    }
    
    private bool DetectVision(JsonNode modelNode, ModelMapping mapping)
    {
        // Check "supportsVision" mapping
        var path = mapping.SupportsVision;
        if (string.IsNullOrEmpty(path)) return false;
        
        var node = modelNode.Path(path);
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        
        // Some providers list modalities as array
        if (node is JsonArray arr)
        {
            return arr.Any(n => n?.GetValue<string>() == "image");
        }
        
        return false;
    }
    
    // ... DetectReasoning, DetectToolUse, ParsePricing аналогично
}

public sealed class ModelMapping
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? ContextWindow { get; set; }
    public string? MaxOutputTokens { get; set; }
    public string? SupportsVision { get; set; }
    public string? SupportsToolUse { get; set; }
    public string? SupportsReasoning { get; set; }
    public Dictionary<string, string>? Pricing { get; set; }
}
```

### 4.2. Cache invalidation

```bash
harbor provider refresh openrouter  # принудительно обновить кэш
harbor provider refresh --all
harbor provider cache clear          # очистить весь кэш
harbor provider cache list           # показать кэш
```

### 4.3. Rate limiting при model fetch

```csharp
// Don't fetch models too aggressively
public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(...)
{
    var cachePath = ...;
    var cacheAge = GetCacheAge(cachePath);
    
    var minRefresh = TimeSpan.FromHours(1);  // never refresh more often than 1h
    if (cacheAge < minRefresh)
        return await TryReadCacheAsync(cachePath, ct) ?? Array.Empty<ModelInfo>();
    
    // ... rest
}
```

## 5. Provider discovery flow

```
At startup:
1. Load builtin provider configs (embedded resources)
   - anthropic.json
   - openai.json
   - google.json
   - ollama.json
   
2. Load ~/.harbor/providers/*.json (global, user-configured)
   - openrouter.json
   - deepseek.json
   - groq.json
   - etc.
   
3. Load .harbor/providers/*.json (project-local, after trust prompt)
   
4. Load provider plugins from ~/.harbor/plugins/*Provider*.dll
   - BedrockProviderPlugin
   - AzureProviderPlugin
   - CustomProviderPlugin
   
5. For each provider:
   - Register in ProviderRegistry
   - If has modelsUrl: fetch + cache (background, with timeout 5s)
   - If has hardcoded models: use them
   - Mark as "ready" when models available
   
6. ProviderRegistry.GetAvailableProviders() returns all registered
7. ProviderRegistry.GetAllModelsAsync() returns aggregated model list
```

## 6. Provider registry

```csharp
public sealed class DynamicProviderRegistry : IProviderRegistry
{
    private readonly ConcurrentDictionary<string, ProviderEntry> _providers = new();
    private readonly DynamicModelCatalog _modelCatalog;
    
    public void Register(ProviderConfig config)
    {
        var client = config.ApiType switch
        {
            "openai-compatible" => new OpenAiCompatibleLlmClient(config, _httpFactory, _authResolver),
            "anthropic-compatible" => new AnthropicCompatibleLlmClient(config, _httpFactory, _authResolver),
            "google-compatible" => new GoogleCompatibleLlmClient(config, _httpFactory, _authResolver),
            _ when config.PluginType != null => _pluginHost.CreateProviderClient(config.PluginType),
            _ => throw new InvalidOperationException($"Unknown apiType: {config.ApiType}")
        };
        
        _providers[config.Id] = new ProviderEntry(config, client);
    }
    
    public ILlmClient GetClient(string providerId)
    {
        return _providers.TryGetValue(providerId, out var entry)
            ? entry.Client
            : throw new ProviderNotFoundException(providerId);
    }
    
    public async Task<IReadOnlyList<ModelInfo>> GetAllModelsAsync(CancellationToken ct = default)
    {
        var tasks = _providers.Values.Select(async entry =>
        {
            try
            {
                return await _modelCatalog.GetModelsAsync(entry.Config, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get models for {Provider}", entry.Config.Id);
                return Array.Empty<ModelInfo>();
            }
        });
        
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(x => x).ToList();
    }
}
```

## 7. CLI commands

```bash
harbor provider list                           # list configured providers
harbor provider show openrouter                # show config + cached models
harbor provider refresh openrouter             # refresh model cache
harbor provider refresh --all                  # refresh all
harbor provider test openrouter                # test connection (1 token completion)
harbor provider add ./my-provider.json         # add provider from JSON file
harbor provider remove openrouter              # remove provider
harbor provider edit openrouter                # open config in $EDITOR
harbor provider cache list                     # show cache state
harbor provider cache clear                    # clear all cache

# Future (marketplace):
harbor provider install openrouter             # install from marketplace
harbor provider search "openai compatible"     # search marketplace
```

## 8. Marketplace (future)

Central repo of provider configs:

```bash
harbor provider install openrouter
# → fetches https://registry.harbor.sh/providers/openrouter.json
# → saves to ~/.harbor/providers/openrouter.json
# → fetches models cache
```

Registry — простой static JSON index:

```json
// https://registry.harbor.sh/providers/index.json
{
  "providers": [
    { "id": "openrouter", "version": "1.0.0", "description": "Multi-provider router" },
    { "id": "deepseek", "version": "1.0.0", "description": "DeepSeek models" },
    { "id": "groq", "version": "1.0.0", "description": "Fast inference" },
    { "id": "together", "version": "1.0.0", "description": "Together AI" },
    { "id": "mistral", "version": "1.0.0", "description": "Mistral AI" },
    { "id": "anthropic", "version": "1.0.0", "description": "Anthropic (builtin)" },
    { "id": "openai", "version": "1.0.0", "description": "OpenAI (builtin)" }
  ]
}
```

Community contribution — PR в `harbor-sh/registry` репозиторий.

## 9. Auth resolution

### 9.1. Auth sources (по приоритету)

1. CLI flag: `--provider openrouter --api-key sk-or-v1-...`
2. Env var: `OPENROUTER_API_KEY` (specified in `authEnvVar`)
3. Config file: `providers.openrouter.apiKey` in `~/.harbor/config.json`
4. Credential file: `~/.harbor/credentials/openrouter.key`
5. OS keychain: `harbor auth set openrouter` (через `keyring` NuGet)
6. OAuth token (if `authType: "oauth"`)

### 9.2. Auth types

| `authType` | Что делает |
|---|---|
| `bearer` | `Authorization: Bearer <key>` |
| `header` | `<authHeader>: <key>` (custom header name) |
| `query` | `?key=<key>` in URL |
| `basic` | HTTP Basic auth |
| `oauth` | OAuth 2.0 flow (separate implementation) |
| `aws-sigv4` | AWS SigV4 (only via plugin, needs region) |
| `none` | No auth (for local Ollama) |

## 10. Capability detection

Некоторые провайдеры не отдают capabilities в `/models` response. Решение:

```jsonc
{
  "id": "my-provider",
  "capabilities": {
    "streaming": true,  // hardcoded
    "toolUse": "auto",  // detect from /models response (look for "tools" in supported_parameters)
    "vision": "auto",
    "reasoning": "auto"
  },
  "capabilityOverrides": {
    // Force-disable for specific models
    "deepseek-reasoner": { "toolUse": false },
    "claude-3-haiku": { "vision": false }
  }
}
```

## 11. Request/response transforms

Для совсем нестандартных провайдеров:

```jsonc
{
  "id": "weird-provider",
  "requestTransform": {
    "type": "jmespath",
    "script": "..."
  },
  "responseTransform": {
    "type": "jsonata",
    "script": "..."
  }
}
```

Или через C# plugin:

```csharp
public sealed class WeirdProviderPlugin : IProviderPlugin
{
    public void RegisterProviders(IProviderRegistryBuilder builder)
    {
        builder.AddProvider(new WeirdProviderClient());
    }
}

public sealed class WeirdProviderClient : ILlmClient
{
    // Custom protocol implementation
}
```

## 12. Влияние на harbor

### 12.1. Что это даёт пользователю

- Добавить нового провайдера — 5 минут, JSON файл.
- Не нужно ждать upstream release для нового провайдера.
- Can use ANY OpenAI-compatible endpoint (local, internal corporate, etc.).
- Models auto-discovered, всегда актуальные.

### 12.2. Что это даёт мейнтейнеру

- Не нужно тащить 20+ `@ai-sdk/*` packages (как kilocode).
- Не нужно поддерживать models.dev integration.
- Community может добавлять провайдеры без PR в harbor.
- Builtin providers — только special cases (4-5 штук).

### 12.3. Memory impact

- Lazy provider instantiation — клиент создаётся только при первом использовании.
- Model catalog кэшируется в `~/.harbor/cache/`, не в памяти.
- Только активные провайдеры loaded, не все 30+.

## 13. Что осталось builtin

| Provider | Почему builtin |
|---|---|
| `anthropic` | cache_control, extended thinking, stealth OAuth, fine-grained tool streaming |
| `openai` | Responses API для o1/o3, Chat Completions, Codex OAuth |
| `google` | systemInstruction, functionCall/Response, Vertex AI auth |
| `ollama` | Local, NDJSON (не SSE), `keep_alive` parameter |

Всё остальное — через dynamic config или generic adapter.

---

**Next**: обновлённый `02-plugins.md` (SharpTS plugin path) и `07-tui.md` (event bus + Terminal.Gui v2).
