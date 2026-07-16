# 06 — MCP (Model Context Protocol)

> Документ: интеграция MCP-клиента. Transports, lifecycle, tool aggregation, prompts, resources, OAuth. Опциональная фича — грузится как плагин.

## 1. Что такое MCP и зачем

**MCP (Model Context Protocol)** — открытый протокол (Anthropic, 2024) для коммуникации LLM-клиентов с внешними tool-серверами. Стандарт де-факто в AI-инструментарии 2025+.

**Сценарии**:
- Подключить GitHub MCP server → LLM может искать issues, создавать PR, читать репозитории.
- Подключить filesystem MCP server → безопасный scoped доступ к файлам.
- Подключить postgres MCP server → LLM делает SQL queries.
- Подключить Slack MCP server → LLM читает/пишет сообщения.

**Почему как плагин, а не в ядре**:
1. Не всем нужен MCP — лишний dependency и память.
2. Pi-agent сознательно не имеет MCP (статья автора: "What if you don't need MCP?"). Альтернатива — Skills (CLI-тулзы с README).
3. Crush, kilocode, opencode — имеют MCP, но как опциональный транспорт.
4. **MVP harbor**: без MCP. **v1**: MCP как `Harbor.Mcp` плагин.

**Архитектурное решение**: если плагин `Harbor.Mcp` зарегистрирован — он добавляет MCP-tools в `IToolRegistry`, MCP-instructions в system prompt, MCP-команды в slash-command router.

## 2. Транспорт

MCP определяет 3 транспорта:

### 2.1. Stdio (local subprocess)

MCP server — отдельный процесс, общается через stdin/stdout JSON-RPC.

```jsonc
// config.json
{
  "mcp": {
    "filesystem": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
      "env": { "NODE_OPTIONS": "--max-old-space-size=256" },
      "cwd": "/home/user"
    }
  }
}
```

```csharp
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    
    public StdioMcpTransport(McpServerConfig config)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = string.Join(' ', config.Args ?? Array.Empty<string>()),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = config.Cwd ?? Environment.CurrentDirectory
            },
            EnableRaisingEvents = true
        };
        
        foreach (var (k, v) in config.Env ?? new())
            _process.StartInfo.Environment[k] = v;
        
        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        
        // Stderr → log
        _process.ErrorDataReceived += (_, e) => 
        { if (e.Data != null) _logger.LogWarning("MCP stderr: {Line}", e.Data); };
        _process.BeginErrorReadLine();
    }
    
    public async Task SendAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, HarborJsonContext.Default.JsonRpcRequest);
        await _stdin.WriteLineAsync(json);
        await _stdin.FlushAsync(ct);
    }
    
    public async Task<JsonNode?> ReceiveAsync(CancellationToken ct)
    {
        var line = await _stdout.ReadLineAsync(ct);
        if (line == null) return null;
        return JsonNode.Parse(line);
    }
    
    public async ValueTask DisposeAsync()
    {
        await _stdin.DisposeAsync();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}
```

### 2.2. Streamable HTTP

MCP server — HTTP endpoint. Request → POST, response → либо single JSON, либо SSE stream.

```jsonc
{
  "mcp": {
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp/",
      "headers": { "Authorization": "Bearer ${GITHUB_TOKEN}" }
    }
  }
}
```

```csharp
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _http;
    private readonly string _url;
    
    public async Task<JsonNode?> SendAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(request, HarborJsonContext.Default.JsonRpcRequest),
            Encoding.UTF8,
            "application/json");
        
        var response = await _http.PostAsync(_url, content, ct);
        
        var contentType = response.Content.Headers.ContentType?.MediaType;
        
        if (contentType == "text/event-stream")
        {
            // SSE — multiple events until we get the response
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var sse in SseParser.ReadAsync(stream, ct))
            {
                if (sse.EventType == "message")
                {
                    return JsonNode.Parse(sse.Data);
                }
            }
            return null;
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(body);
        }
    }
}
```

### 2.3. SSE (legacy)

Старый протокол (до Streamable HTTP). Используется в некоторых MCP servers. Поддерживаем для совместимости.

## 3. Lifecycle

```
1. parse config → list of MCP server configs
2. for each server (parallel):
   a. create transport
   b. send "initialize" request
   c. receive capabilities (tools, prompts, resources, completions)
   d. send "initialized" notification
3. for each server (parallel):
   - if has tools: call "tools/list" → register as ITool adapters
   - if has prompts: call "prompts/list" → register as slash-commands
   - if has resources: register `list_mcp_resources` + `read_mcp_resource` tools
4. subscribe to "notifications/tools/list_changed" → re-fetch
5. on shutdown: send "shutdown" → "exit" to each
```

### 3.1. Initialize handshake

```csharp
public sealed class McpClient : IMcpClient
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        // 1. Send initialize request
        var initRequest = new JsonRpcRequest(
            Id: NextId(),
            Method: "initialize",
            Params: new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject
                {
                    ["roots"] = new JsonObject { ["listChanged"] = true }
                    // ["sampling"] = {} — off (we don't let MCP server call our LLM)
                    // ["elicitation"] = {} — off
                },
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "harbor",
                    ["version"] = HarborVersion.Current.ToString()
                }
            });
        
        var response = await SendAndWaitAsync(initRequest, ct);
        
        _serverInfo = response["result"]?["serverInfo"]?.Deserialize<ServerInfo>();
        _capabilities = response["result"]?["capabilities"]?.Deserialize<ServerCapabilities>();
        
        // 2. Send initialized notification
        await SendAsync(new JsonRpcRequest(
            Id: null,
            Method: "notifications/initialized",
            Params: null), ct);
        
        // 3. Discover tools/prompts/resources (if supported)
        if (_capabilities?.Tools != null)
            await RefreshToolsAsync(ct);
        
        if (_capabilities?.Prompts != null)
            await RefreshPromptsAsync(ct);
        
        if (_capabilities?.Resources != null)
            await RefreshResourcesAsync(ct);
    }
}
```

### 3.2. Tool discovery

```csharp
public async Task RefreshToolsAsync(CancellationToken ct)
{
    var request = new JsonRpcRequest(
        Id: NextId(),
        Method: "tools/list",
        Params: new JsonObject { });
    
    var response = await SendAndWaitAsync(request, ct);
    
    var toolsArray = response["result"]?["tools"]?.AsArray();
    if (toolsArray == null) return;
    
    _tools.Clear();
    foreach (var toolNode in toolsArray)
    {
        var tool = toolNode!.Deserialize<McpToolInfo>(HarborJsonContext.Default.McpToolInfo)!;
        _tools[tool.Name] = tool;
    }
    
    // Notify harbor that tools changed
    _toolsChanged?.Invoke(this, EventArgs.Empty);
}
```

### 3.3. Calling MCP tools

```csharp
public sealed class McpToolAdapter : ITool
{
    private readonly IMcpClient _client;
    private readonly McpToolInfo _toolInfo;
    private readonly string _serverName;
    
    public string Id => $"mcp_{Sanitize(_serverName)}_{Sanitize(_toolInfo.Name)}";
    
    public string Description => _toolInfo.Description ?? "";
    
    public JsonDocument ParameterSchema => _toolInfo.InputSchema ?? JsonDocument.Parse("{}");
    
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext ctx,
        CancellationToken ct)
    {
        var callRequest = new JsonRpcRequest(
            Id: _client.NextId(),
            Method: "tools/call",
            Params: new JsonObject
            {
                ["name"] = _toolInfo.Name,
                ["arguments"] = args.Deserialize<JsonNode>()
            });
        
        JsonNode? response;
        try
        {
            response = await _client.SendAndWaitAsync(callRequest, ct);
        }
        catch (McpTransportException ex)
        {
            return new ToolResult(
                Output: $"MCP server '{_serverName}' transport error: {ex.Message}",
                IsError: true);
        }
        
        var result = response["result"];
        if (result == null)
        {
            var error = response["error"];
            return new ToolResult(
                Output: $"MCP error: {error?["message"]?.GetValue<string>() ?? "unknown"}",
                IsError: true);
        }
        
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        var content = result["content"]?.AsArray();
        
        var output = new StringBuilder();
        IReadOnlyList<FileAttachment>? attachments = null;
        var attachmentList = new List<FileAttachment>();
        
        if (content != null)
        {
            foreach (var item in content)
            {
                var type = item?["type"]?.GetValue<string>();
                switch (type)
                {
                    case "text":
                        output.AppendLine(item!["text"]?.GetValue<string>());
                        break;
                    case "image":
                        var data = item!["data"]?.GetValue<string>();
                        var mimeType = item!["mimeType"]?.GetValue<string>() ?? "image/png";
                        if (data != null)
                            attachmentList.Add(new FileAttachment(
                                Path: $"{_toolInfo.Name}_image_{Guid.NewGuid():N}",
                                MimeType: mimeType,
                                Data: Convert.FromBase64String(data)));
                        break;
                    case "resource":
                        var uri = item?["resource"]?["uri"]?.GetValue<string>();
                        if (uri != null)
                            output.AppendLine($"[resource: {uri}]");
                        break;
                }
            }
        }
        
        if (attachmentList.Count > 0)
            attachments = attachmentList;
        
        return new ToolResult(
            Output: output.ToString(),
            IsError: isError,
            Attachments: attachments);
    }
    
    private static string Sanitize(string name) => 
        Regex.Replace(name, @"[^a-zA-Z0-9_-]", "_");
}
```

## 4. Capabilities

### 4.1. Что поддерживает harbor как MCP client

| Capability | Status | Notes |
|---|---|---|
| `tools` | ✅ MVP | Main use case |
| `prompts` | ✅ MVP | As slash-commands |
| `resources` | ✅ MVP | As `read_mcp_resource` tool |
| `roots` | ✅ MVP | Harbor сообщает workspace roots |
| `logging` | ✅ MVP | MCP → ILogger |
| `sampling` | ❌ No | Don't let MCP call our LLM |
| `elicitation` | ❌ No | Don't let MCP ask user |
| `completion` | v2 | Autocomplete for prompt args |

### 4.2. `roots` — workspace reporting

```csharp
// When MCP server requests roots:
public async Task HandleRootsListAsync(JsonRpcRequest request, CancellationToken ct)
{
    var roots = new JsonArray
    {
        new JsonObject
        {
            ["uri"] = $"file://{_session.Directory}",
            ["name"] = "project"
        }
    };
    
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = request.Id,
        ["result"] = new JsonObject { ["roots"] = roots }
    };
    
    await _transport.SendAsync(response, ct);
}

// Subscribe to roots/list_changed from MCP server
// (server wants us to re-send our roots)
```

## 5. System prompt injection

MCP server может возвращать `instructions` в initialize response:

```csharp
// During initialize:
var instructions = response["result"]?["instructions"]?.GetValue<string>();
if (!string.IsNullOrEmpty(instructions))
{
    _instructions = instructions;
}
```

Инжектируем в system prompt (через `ISystemPromptBuilder`):

```
## MCP Servers

The following MCP servers are connected and provide additional tools:

<mcp_instructions>
  <server name="filesystem">
    Provides file access to /tmp directory.
    Available tools: read_file, write_file, list_directory
  </server>
  <server name="github">
    Provides GitHub integration.
    Available tools: search_repos, get_issue, create_pr
  </server>
</mcp_instructions>
```

## 6. MCP resources

Resources — read-only данные, которые MCP server exposes. Могут быть static (`file://path`) или templated (`file://path/{name}`).

Доступ через builtin tools:

```csharp
public sealed class ListMcpResourcesTool : ITool
{
    public string Id => "list_mcp_resources";
    public string Description => "List all resources from connected MCP servers";
    
    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (serverName, client) in _mcpHost.Clients)
        {
            sb.AppendLine($"## {serverName}");
            var resources = await client.ListResourcesAsync(ct);
            foreach (var r in resources)
            {
                sb.AppendLine($"- `{r.Uri}` — {r.Description ?? r.Name}");
            }
            sb.AppendLine();
        }
        return new ToolResult(sb.ToString(), IsError: false);
    }
}

public sealed class ReadMcpResourceTool : ITool
{
    public string Id => "read_mcp_resource";
    public string Description => "Read content of an MCP resource by URI";
    
    public sealed class Args
    {
        [JsonRequired] public string Uri { get; set; } = "";
        public string? Server { get; set; }  // optional, auto-resolved if missing
    }
    
    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var typed = args.Deserialize<Args>(HarborJsonContext.Default.Args)!;
        
        IMcpClient? client = null;
        if (typed.Server != null)
            client = _mcpHost.Clients.GetValueOrDefault(typed.Server);
        else
        {
            // Try each server
            foreach (var (name, c) in _mcpHost.Clients)
            {
                try
                {
                    var resource = await c.ReadResourceAsync(typed.Uri, ct);
                    if (resource != null) { client = c; break; }
                }
                catch { /* try next */ }
            }
        }
        
        if (client == null)
            return new ToolResult($"No MCP server can provide resource: {typed.Uri}", IsError: true);
        
        var result = await client.ReadResourceAsync(typed.Uri, ct);
        // Format result as text + attachments
        // ...
    }
}
```

## 7. MCP prompts

MCP server может предоставлять "prompts" — parameterized templates. Expose как slash-commands:

```csharp
public sealed class McpPromptCommand : ISlashCommand
{
    public string Name => $"mcp:{_serverName}:{_prompt.Name}";
    public string Description => _prompt.Description ?? "";
    
    public async Task ExecuteAsync(IReadOnlyList<string> args, ICommandContext ctx, CancellationToken ct)
    {
        // Map args to prompt parameters (by position or by name)
        var arguments = new JsonObject();
        for (int i = 0; i < _prompt.Arguments?.Count && i < args.Count; i++)
        {
            arguments[_prompt.Arguments[i].Name] = args[i];
        }
        
        var result = await _client.GetPromptAsync(_prompt.Name, arguments, ct);
        
        // Result is a list of messages — inject as user input
        foreach (var msg in result.Messages)
        {
            await ctx.Session.PromptAsync(new UserMessage(
                Id: Guid.NewGuid().ToString(),
                SessionId: ctx.Session.Id,
                CreatedAt: DateTimeOffset.UtcNow,
                Content: msg.Content.Text ?? "",
                Agent: ctx.Session.Agent.Name,
                Model: ctx.Session.LastModel));
        }
    }
}
```

## 8. OAuth для MCP servers

Некоторые MCP servers (github.com/mcp/, etc.) требуют OAuth:

```csharp
public sealed class McpOAuthFlow
{
    public async Task<OAuthToken> AuthenticateAsync(McpServerConfig config, CancellationToken ct)
    {
        // 1. Discover OAuth metadata
        var metadata = await _http.GetFromJsonAsync<OAuthMetadata>(
            $"{config.Url}/.well-known/oauth-authorization-server", 
            HarborJsonContext.Default.OAuthMetadata, ct);
        
        // 2. PKCE
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        
        // 3. Start local callback server
        var callbackPort = FindFreePort();
        var callbackUri = $"http://localhost:{callbackPort}/callback";
        var tcs = new TaskCompletionSource<string>();
        
        using var callbackServer = StartCallbackServer(callbackPort, code => tcs.SetResult(code));
        
        // 4. Open browser
        var authUrl = BuildAuthUrl(metadata.AuthorizationEndpoint, config.ClientId, 
            callbackUri, codeChallenge, config.Scopes);
        OpenBrowser(authUrl);
        
        // 5. Wait for callback (timeout 5 min)
        var authCode = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
        
        // 6. Exchange code for token
        var token = await ExchangeCodeAsync(
            metadata.TokenEndpoint, config.ClientId, 
            authCode, codeVerifier, callbackUri, ct);
        
        return token;
    }
}
```

Токены сохраняются в `~/.harbor/credentials.json`:

```jsonc
{
  "mcp_tokens": {
    "github": {
      "accessToken": "...",
      "refreshToken": "...",
      "expiresAt": "2026-07-16T11:30:00Z"
    }
  }
}
```

Auto-refresh при истечении:

```csharp
public async Task<string> EnsureValidTokenAsync(string serverName, CancellationToken ct)
{
    var token = await _store.GetMcpTokenAsync(serverName, ct);
    
    if (token == null)
        return await AuthenticateAsync(serverName, ct);
    
    if (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
    {
        var refreshed = await RefreshTokenAsync(serverName, token.RefreshToken, ct);
        await _store.SetMcpTokenAsync(serverName, refreshed, ct);
        return refreshed.AccessToken;
    }
    
    return token.AccessToken;
}
```

## 9. Reconnect и error handling

```csharp
public sealed class McpReconnectService : IHostedService
{
    public async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var (name, client) in _mcpHost.Clients)
            {
                if (client.Status != McpStatus.Connected && client.Status != McpStatus.Connecting)
                {
                    _logger.LogInformation("Reconnecting MCP server {Name}...", name);
                    try
                    {
                        await client.ReconnectAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reconnect MCP {Name}", name);
                    }
                }
            }
            
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

Status state machine:

```
Disconnected → Connecting → Connected
                    ↓          ↓
                  Failed    Disconnected (transport closed)
                    ↓
              (retry after delay)
```

## 10. Hot-reload MCP config

Если `config.json` изменился (пользователь добавил/удалил MCP server):

```csharp
public sealed class McpConfigWatcher : IHostedService, IDisposable
{
    private FileSystemWatcher? _watcher;
    
    public Task StartAsync(CancellationToken ct)
    {
        var configPath = Path.Combine(_configRoot, "config.json");
        if (!File.Exists(configPath)) return Task.CompletedTask;
        
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite
        };
        _watcher.Changed += async (_, e) => await OnConfigChangedAsync();
        
        return Task.CompletedTask;
    }
    
    private async Task OnConfigChangedAsync()
    {
        await Task.Delay(500);  // debounce
        
        var newConfig = await LoadConfigAsync();
        var oldServerNames = _mcpHost.Clients.Keys.ToHashSet();
        var newServerNames = newConfig.Mcp.Keys.ToHashSet();
        
        // Removed servers
        foreach (var name in oldServerNames.Except(newServerNames))
        {
            await _mcpHost.DisconnectAsync(name);
        }
        
        // Added servers
        foreach (var name in newServerNames.Except(oldServerNames))
        {
            await _mcpHost.ConnectAsync(name, newConfig.Mcp[name]);
        }
        
        // Changed servers (different config)
        foreach (var name in newServerNames.Intersect(oldServerNames))
        {
            if (!ConfigEquals(newConfig.Mcp[name], _currentConfig.Mcp[name]))
            {
                await _mcpHost.DisconnectAsync(name);
                await _mcpHost.ConnectAsync(name, newConfig.Mcp[name]);
            }
        }
    }
}
```

## 11. Permission model для MCP tools

MCP tools по умолчанию проходят через тот же permission system, что и builtin tools:

```jsonc
{
  "permissions": {
    "mcp_filesystem_read_file": { "*": "allow" },
    "mcp_filesystem_write_file": { "*": "ask" },
    "mcp_github_create_pr": { "*": "ask" },
    "mcp_github_*": { "*": "ask" }
  }
}
```

Также можно настроить per-MCP-server defaults:

```jsonc
{
  "mcp": {
    "filesystem": {
      "permissions": { "*": "allow" }  // allow all tools from this server
    },
    "github": {
      "permissions": { 
        "create_pr": "ask",
        "delete_*": "deny",
        "*": "allow"
      }
    }
  }
}
```

## 12. Memory и CPU overhead

| Component | Memory | CPU |
|---|---|---|
| MCP client instance | ~1 МБ | idle 0% |
| Stdio transport (one subprocess) | ~20–80 МБ (subprocess!) | depends on server |
| HTTP transport | ~1 МБ | negligible |
| Tool adapter (per tool) | <10 KB | negligible |
| Total per connected MCP server | ~25–100 МБ | depends on usage |

**Важно**: каждый stdio MCP server — это отдельный процесс. Если MCP server написан на Node.js — он сам по себе жрёт 30–50 МБ. Это может легко удвоить RSS harbor.

**Mitigation**:
- Lazy connect: MCP серверы подключаются только при первом использовании (если `lazyConnect: true` в config).
- Disconnect after idle: если MCP server не использовался N минут — disconnect (но оставляем config для re-connect).
- Limit concurrent MCP servers: max 5 по умолчанию.

## 13. Testing MCP integration

Встроенный mock MCP server для тестов:

```csharp
public sealed class MockMcpServer
{
    [McpTool("echo", "Echoes the input")]
    public static object Echo(string message) => new { content = new[] { new { type = "text", text = message } } };
    
    [McpTool("add", "Adds two numbers")]
    public static object Add(int a, int b) => new { content = new[] { new { type = "text", text = (a + b).ToString() } } };
}
```

Запускается как subprocess в integration tests:

```csharp
public sealed class McpIntegrationTests
{
    [Fact]
    public async Task CanCallEchoTool()
    {
        await using var mockServer = await MockMcpServer.StartAsync();
        
        var harbor = TestHostBuilder.Create()
            .WithMcp("test", new McpServerConfig 
            { 
                Type = "stdio", 
                Command = mockServer.ExecutablePath 
            })
            .Build();
        
        var result = await harbor.Agent
            .PromptAsync("Use the mcp_test_echo tool with message 'hello'")
            .WaitForToolCallAsync("mcp_test_echo");
        
        result.Output.Should().Be("hello");
    }
}
```

## 14. Совместимость с ecosystem

### 14.1. Известные MCP servers для тестирования

| Server | Что делает | Transport |
|---|---|---|
| `@modelcontextprotocol/server-filesystem` | File access (scoped) | stdio |
| `@modelcontextprotocol/server-github` | GitHub API | stdio / http |
| `@modelcontextprotocol/server-postgres` | PostgreSQL queries | stdio |
| `@modelcontextprotocol/server-slack` | Slack API | stdio |
| `@modelcontextprotocol/server-memory` | Knowledge graph | stdio |
| `@modelcontextprotocol/server-puppeteer` | Browser automation | stdio |

### 14.2. Документация MCP

- Спецификация: https://modelcontextprotocol.io/specification
- C# SDK: https://github.com/modelcontextprotocol/csharp-sdk (официальный от Microsoft)
- Серверы: https://github.com/modelcontextprotocol/servers

**Решение**: в v1 используем официальный `ModelContextProtocol` NuGet, не пишем свой client с нуля. Это сэкономит время и даст совместимость с ecosystem.

```xml
<PackageReference Include="ModelContextProtocol" Version="0.1.0-preview" />
```

Adapтация к `ITool`/`ILlmClient` — наш код (~500 строк), поверх SDK.

---

**Next**: `07-tui.md` — терминальный UI, streaming render, slash-commands, split-panes.
