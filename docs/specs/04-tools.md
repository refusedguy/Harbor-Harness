# 04 — Tools и tool-calling

> Документ: tool schema, JSON Schema generation из C# types, tool-calling loop, permission model, builtin tools (read/write/edit/bash/glob/grep/ls).

## 1. Tool contract

### 1.1. `ITool` interface

```csharp
// Harbor.Abstractions/Tools/ITool.cs

public interface ITool
{
    /// <summary>Уникальное имя (используется в LLM tool_calls, должно быть lowercase).</summary>
    string Id { get; }
    
    /// <summary>Display name для TUI.</summary>
    string DisplayName { get; }
    
    /// <summary>Описание для LLM (что tool делает, когда использовать).</summary>
    string Description { get; }
    
    /// <summary>JSON Schema для параметров (draft 7).</summary>
    JsonDocument ParameterSchema { get; }
    
    /// <summary>Режим выполнения — параллельно с другими tool calls или последовательно.</summary>
    ExecutionMode ExecutionMode { get; }
    
    /// <summary>Краткое описание для инъекции в system prompt (one-liner).</summary>
    string? PromptSnippet { get; }
    
    /// <summary>Правила использования для инъекции в Guidelines.</summary>
    IReadOnlyList<string>? PromptGuidelines { get; }
    
    /// <summary>Выполнить tool.</summary>
    Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext ctx,
        CancellationToken ct = default);
    
    /// <summary>Валидация args перед execute (опционально, для специфичных правил).</summary>
    ValidationResult? ValidateArguments(JsonElement args) => null;
}

public enum ExecutionMode
{
    Parallel,    // может выполняться параллельно с другими tool calls
    Sequential   // должен выполняться последовательно (например, `bash` с side effects)
}

public sealed record ToolResult(
    string Output,
    bool IsError,
    object? Metadata = null,
    IReadOnlyList<FileAttachment>? Attachments = null);

public sealed record FileAttachment(
    string Path,
    string MimeType,
    byte[] Data);

public sealed record ToolContext(
    string SessionId,
    string MessageId,
    string? CallId,
    string Agent,
    CancellationToken Abort,
    IReadOnlyList<AgentMessage> Messages,
    Func<ToolProgressUpdate, CancellationToken, Task> ReportProgress,
    Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> Ask,
    IServiceProvider Services);

public sealed record ToolProgressUpdate(
    string? Status = null,
    int? PercentComplete = null,
    object? PartialResult = null);

public sealed record PermissionRequest(
    string Permission,    // "bash" | "edit" | "write" | ...
    string Pattern,       // glob pattern matched
    JsonElement Args,     // tool call args
    IReadOnlyList<string> AlwaysOptions);  // ["allow", "deny"] — для persistent decisions

public sealed record PermissionResponse(
    PermissionAction Action,
    bool PersistDecision);
```

### 1.2. Базовый класс для типизированных tools

Большинство tools имеют typed параметры. Предоставляем `ToolBase<TArgs>`:

```csharp
public abstract class ToolBase<TArgs> : ITool
    where TArgs : class, new()
{
    public abstract string Id { get; }
    public virtual string DisplayName => Id;
    public abstract string Description { get; }
    public abstract ExecutionMode ExecutionMode { get; }
    public virtual string? PromptSnippet => null;
    public virtual IReadOnlyList<string>? PromptGuidelines => null;
    
    /// <summary>JSON Schema — генерируется из TArgs через source generator.</summary>
    public JsonDocument ParameterSchema => SchemaGenerator.For<TArgs>();
    
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext ctx,
        CancellationToken ct = default)
    {
        TArgs typedArgs;
        try
        {
            typedArgs = args.Deserialize<TArgs>(HarborJsonContext.Default.Options) 
                ?? throw new JsonException("Deserialized to null");
        }
        catch (JsonException ex)
        {
            return new ToolResult(
                Output: $"Invalid arguments: {ex.Message}. Schema: {ParameterSchema.RootElement.GetRawText()}",
                IsError: true);
        }
        
        return await ExecuteAsync(typedArgs, ctx, ct);
    }
    
    protected abstract Task<ToolResult> ExecuteAsync(TArgs args, ToolContext ctx, CancellationToken ct);
}
```

### 1.3. JSON Schema generation

В .NET 9+ есть `JsonSerializerOptions.GetJsonSchemaAsNode()`. Для AOT-friendly — `NJsonSchema` с source generator.

```csharp
public static class SchemaGenerator
{
    private static readonly ConcurrentDictionary<Type, JsonDocument> _cache = new();
    
    public static JsonDocument For<T>()
    {
        return _cache.GetOrAdd(typeof(T), _ =>
        {
            var options = HarborJsonContext.Default.Options;
            var schemaNode = options.GetJsonSchemaAsNode(typeof(T));
            var json = schemaNode.ToJsonString();
            return JsonDocument.Parse(json);
        });
    }
}
```

### 1.4. Пример типизированного tool

```csharp
public sealed class ReadTool : ToolBase<ReadTool.Args>
{
    public override string Id => "read";
    public override string DisplayName => "Read";
    public override string Description => 
        "Read contents of a file. Supports text and image files. " +
        "For text files, returns content as string. " +
        "For images, returns base64-encoded data.";
    public override ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public override string? PromptSnippet => "read: Read file contents";
    public override IReadOnlyList<string>? PromptGuidelines => new[]
    {
        "Use `read` to examine file contents before editing",
        "For binary files (images), `read` returns vision-compatible data"
    };
    
    public sealed class Args
    {
        [JsonPropertyName("path")]
        [Description("Absolute or relative file path to read")]
        [JsonRequired]
        public string Path { get; set; } = "";
        
        [JsonPropertyName("offset")]
        [Description("Line number to start reading from (1-indexed). Optional.")]
        [Range(1, int.MaxValue)]
        public int? Offset { get; set; }
        
        [JsonPropertyName("limit")]
        [Description("Maximum number of lines to read. Optional.")]
        [Range(1, 10000)]
        public int? Limit { get; set; }
    }
    
    protected override async Task<ToolResult> ExecuteAsync(Args args, ToolContext ctx, CancellationToken ct)
    {
        if (!Path.IsPathRooted(args.Path))
            args.Path = System.IO.Path.Combine(Environment.CurrentDirectory, args.Path);
        
        if (!File.Exists(args.Path))
            return new ToolResult($"File not found: {args.Path}", IsError: true);
        
        var mimeType = DetectMimeType(args.Path);
        
        if (IsImageMimeType(mimeType))
        {
            var data = await File.ReadAllBytesAsync(args.Path, ct);
            return new ToolResult(
                Output: $"Image: {args.Path} ({data.Length} bytes)",
                IsError: false,
                Attachments: new[] { new FileAttachment(args.Path, mimeType, data) });
        }
        
        var content = await File.ReadAllTextAsync(args.Path, ct);
        
        // Apply offset/limit
        if (args.Offset.HasValue || args.Limit.HasValue)
        {
            var lines = content.Split('\n');
            var start = (args.Offset ?? 1) - 1;
            var count = args.Limit ?? lines.Length - start;
            content = string.Join('\n', lines.Skip(start).Take(count));
        }
        
        // Truncate if too long
        const int MaxChars = 100_000;
        if (content.Length > MaxChars)
        {
            content = content[..MaxChars] + $"\n\n... truncated ({content.Length - MaxChars} more chars)";
        }
        
        return new ToolResult(content, IsError: false, 
            Metadata: new { path = args.Path, mimeType, sizeBytes = content.Length });
    }
    
    private static string DetectMimeType(string path) => /* ... */;
    private static bool IsImageMimeType(string mime) => mime.StartsWith("image/");
}
```

## 2. Tool registry

```csharp
public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> GetAvailableTools();
    IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, ISessionContext session);
    ITool? GetTool(string toolId);
}

internal sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new();
    
    public void Register(ITool tool)
    {
        if (!_tools.TryAdd(tool.Id, tool))
            throw new InvalidOperationException($"Tool '{tool.Id}' already registered");
    }
    
    public IReadOnlyList<ToolDescriptor> GetAvailableTools() =>
        _tools.Values.Select(t => new ToolDescriptor(
            Id: t.Id,
            DisplayName: t.DisplayName,
            Description: t.Description,
            Schema: t.ParameterSchema,
            ExecutionMode: t.ExecutionMode,
            PromptSnippet: t.PromptSnippet,
            PromptGuidelines: t.PromptGuidelines)).ToList();
    
    public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, ISessionContext session)
    {
        var agent = _agentRegistry.Get(agentName);
        var permissions = _permissionService.MergeRules(agent.Permission, session.Permission);
        
        return _tools.Values
            .Where(t => permissions.IsAllowed(t.Id, globPattern: "*"))
            .Select(t => new ToolDescriptor(/* ... */))
            .ToList();
    }
}

public sealed record ToolDescriptor(
    string Id,
    string DisplayName,
    string Description,
    JsonDocument Schema,
    ExecutionMode ExecutionMode,
    string? PromptSnippet,
    IReadOnlyList<string>? PromptGuidelines);
```

## 3. Tool-calling loop (детально)

```csharp
public sealed class AgentLoop
{
    public async Task RunAsync(ISessionContext session, CancellationToken ct)
    {
        var turn = 0;
        
        while (!ct.IsCancellationRequested)
        {
            turn++;
            await _eventBus.PublishAsync(new TurnStartEvent(turn), ct);
            
            // ── 1. Overflow check ──
            var model = _modelRegistry.Get(session.LastUserMessage.Model);
            if (_compactionService.ShouldCompact(session, model))
            {
                await _eventBus.PublishAsync(new CompactionStartedEvent(session.Id), ct);
                await _compactionService.RunAsync(session, model, ct);
                await _eventBus.PublishAsync(new CompactionCompletedEvent(session.Id), ct);
            }
            
            // ── 2. Build system prompt ──
            var systemPrompt = await _systemPromptBuilder.BuildAsync(
                agent: session.Agent,
                model: model,
                tools: _toolRegistry.ResolveTools(session.Agent.Name, session),
                contextFiles: await _contextFilesLoader.LoadAsync(session, ct),
                skills: await _skillsLoader.LoadAsync(session, ct),
                mcpInstructions: await _mcpClient?.GetInstructionsAsync(ct) ?? "",
                ct: ct);
            
            // ── 3. Resolve tools ──
            var tools = _toolRegistry.ResolveTools(session.Agent.Name, session);
            
            // ── 4. Convert messages to LLM format ──
            var llmMessages = await _messageConverter.ToLlmMessagesAsync(
                session.Messages, model, ct);
            
            // ── 5. Add MAX_STEPS reminder if last turn ──
            var isLastStep = turn >= session.Agent.MaxSteps;
            if (isLastStep)
            {
                llmMessages.Add(new AssistantMessage(
                    "assistant",
                    new[] { new TextBlock(MAX_STEPS_REMINDER) },
                    StopReason: null));
            }
            
            // ── 6. Stream LLM ──
            var llmClient = _providerRegistry.GetClient(model.ProviderId);
            var partialAssistant = new AssistantMessage(
                Id: Guid.NewGuid().ToString(),
                SessionId: session.Id,
                CreatedAt: DateTimeOffset.UtcNow,
                Parts: new List<ContentPart>(),
                StopReason: "",
                Usage: new Usage(0, 0, 0, 0, 0),
                Model: model.Id);
            
            await _eventBus.PublishAsync(
                new MessageStartEvent(partialAssistant), ct);
            
            var toolCalls = new List<ToolCallPart>();
            Usage? finalUsage = null;
            string? stopReason = null;
            
            try
            {
                await foreach (var evt in llmClient.StreamAsync(
                    new LlmRequest(
                        Model: model.Id,
                        Messages: llmMessages,
                        SystemPrompt: systemPrompt,
                        Tools: tools.Select(t => new ToolDefinition(t.Id, t.Description, t.Schema)).ToList(),
                        ToolChoice: null,
                        MaxOutputTokens: model.MaxOutputTokens,
                        Temperature: session.Agent.Temperature,
                        TopP: null,
                        ReasoningEffort: session.Agent.ReasoningEffort,
                        CacheStrategy: CacheStrategy.Ephemeral,
                        ExtraHeaders: null),
                    ct).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case TextDeltaEvent td:
                            partialAssistant = partialAssistant.AppendText(td.Delta);
                            await _eventBus.PublishAsync(
                                new MessageUpdateEvent(evt, partialAssistant), ct);
                            break;
                        
                        case ThinkingDeltaEvent thd:
                            partialAssistant = partialAssistant.AppendThinking(thd.Delta);
                            await _eventBus.PublishAsync(
                                new MessageUpdateEvent(evt, partialAssistant), ct);
                            break;
                        
                        case ToolCallStartEvent tcs:
                            var newToolCall = new ToolCallPart(
                                Id: tcs.Id,
                                ToolName: tcs.ToolName,
                                Args: JsonDocument.Parse("{}").RootElement);
                            partialAssistant = partialAssistant.AppendToolCall(newToolCall);
                            await _eventBus.PublishAsync(
                                new MessageUpdateEvent(evt, partialAssistant), ct);
                            break;
                        
                        case ToolCallDeltaEvent tcd:
                            // Accumulate partial JSON
                            var existing = partialAssistant.Parts
                                .OfType<ToolCallPart>()
                                .FirstOrDefault(p => p.Id == tcd.Id);
                            if (existing != null)
                            {
                                var newArgs = (existing.Args.ToString() + tcd.ArgsDelta);
                                // Note: may not be valid JSON yet — keep as string until End
                                partialAssistant = partialAssistant.UpdateToolCallArgs(
                                    tcd.Id, newArgs);
                            }
                            break;
                        
                        case ToolCallEndEvent tce:
                            partialAssistant = partialAssistant.FinalizeToolCall(
                                tce.Id, tce.Args);
                            toolCalls.Add(new ToolCallPart(
                                tce.Id, tce.ToolName, tce.Args));
                            await _eventBus.PublishAsync(
                                new MessageUpdateEvent(evt, partialAssistant), ct);
                            break;
                        
                        case StepFinishEvent sf:
                            finalUsage = sf.Usage;
                            stopReason = sf.FinishReason;
                            partialAssistant = partialAssistant.WithFinish(
                                sf.FinishReason, sf.Usage);
                            break;
                        
                        case ErrorEvent err:
                            await _eventBus.PublishAsync(
                                new AgentErrorEvent(err.Message, err.Exception), ct);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                partialAssistant = partialAssistant.WithFinish("aborted", finalUsage ?? new Usage(0,0,0,0,0));
            }
            
            await _eventBus.PublishAsync(
                new MessageEndEvent(partialAssistant), ct);
            
            await session.AppendMessageAsync(partialAssistant, ct);
            
            // ── 7. No tool calls? done ──
            if (toolCalls.Count == 0 || stopReason is "stop" or "length" or "aborted")
            {
                await _eventBus.PublishAsync(
                    new TurnEndEvent(partialAssistant, Array.Empty<ToolResultMessage>()), ct);
                break;
            }
            
            // ── 8. Handle truncated message (stop_reason: length) ──
            if (stopReason == "length")
            {
                // Fail all tool calls with informative error
                var failedResults = toolCalls.Select(tc => new ToolResultMessage(
                    Id: Guid.NewGuid().ToString(),
                    SessionId: session.Id,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Results: new[]
                    {
                        new ToolResult(
                            tc.Id,
                            tc.ToolName,
                            "Tool call was truncated due to output token limit. Please re-issue with complete arguments.",
                            IsError: true,
                            Metadata: null)
                    })).ToList();
                
                foreach (var fr in failedResults)
                    await session.AppendMessageAsync(fr, ct);
                
                await _eventBus.PublishAsync(
                    new TurnEndEvent(partialAssistant, failedResults), ct);
                continue;  // retry turn
            }
            
            // ── 9. Execute tool calls ──
            var toolResults = await ExecuteToolCallsAsync(
                toolCalls, session, partialAssistant, ct);
            
            await _eventBus.PublishAsync(
                new TurnEndEvent(partialAssistant, toolResults), ct);
            
            // ── 10. Doom loop detection ──
            if (DetectDoomLoop(session, threshold: 3))
            {
                var continueOrStop = await _permissionService.AskDoomLoopAsync(
                    session.Id, lastToolCall: toolCalls[^1], ct);
                
                if (continueOrStop == PermissionAction.Deny)
                    break;
            }
            
            // ── 11. Steering check ──
            if (session.SteeringQueue.TryDequeue(out var steerMsg))
            {
                await session.AppendMessageAsync(steerMsg, ct);
            }
        }
        
        await _eventBus.PublishAsync(
            new AgentEndEvent(session.NewMessages), ct);
    }
    
    private async Task<IReadOnlyList<ToolResultMessage>> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCallPart> toolCalls,
        ISessionContext session,
        AgentMessage partialAssistant,
        CancellationToken ct)
    {
        var hasSequential = toolCalls.Any(tc =>
            _toolRegistry.GetTool(tc.ToolName)?.ExecutionMode == ExecutionMode.Sequential);
        
        if (hasSequential)
        {
            // Sequential execution
            var results = new List<ToolResult>();
            foreach (var tc in toolCalls)
            {
                var result = await ExecuteSingleToolCallAsync(tc, session, partialAssistant, ct);
                results.Add(result);
            }
            return WrapInMessage(results, session);
        }
        else
        {
            // Parallel execution
            var tasks = toolCalls.Select(tc => 
                ExecuteSingleToolCallAsync(tc, session, partialAssistant, ct));
            var results = await Task.WhenAll(tasks);
            return WrapInMessage(results.ToList(), session);
        }
    }
    
    private async Task<ToolResult> ExecuteSingleToolCallAsync(
        ToolCallPart toolCall,
        ISessionContext session,
        AgentMessage partialAssistant,
        CancellationToken ct)
    {
        var tool = _toolRegistry.GetTool(toolCall.ToolName);
        if (tool == null)
        {
            return new ToolResult(
                ToolCallId: toolCall.Id,
                ToolName: toolCall.ToolName,
                Output: $"Unknown tool: '{toolCall.ToolName}'. Available: {string.Join(", ", _toolRegistry.GetAvailableTools().Select(t => t.Id))}",
                IsError: true,
                Metadata: null);
        }
        
        await _eventBus.PublishAsync(new ToolExecutionStartEvent(
            toolCall.Id, toolCall.ToolName, toolCall.Args), ct);
        
        try
        {
            // Permission check
            var permResponse = await _permissionService.CheckAsync(
                session.Agent, toolCall.ToolName, toolCall.Args, ct);
            
            if (permResponse.Action == PermissionAction.Deny)
            {
                return new ToolResult(
                    toolCall.Id, toolCall.ToolName,
                    Output: $"Permission denied: {permResponse.Reason}",
                    IsError: true);
            }
            
            // Execute
            var ctx = new ToolContext(
                SessionId: session.Id,
                MessageId: partialAssistant.Id,
                CallId: toolCall.Id,
                Agent: session.Agent.Name,
                Abort: ct,
                Messages: session.Messages,
                ReportProgress: (update, c) => 
                {
                    _eventBus.PublishAsync(new ToolExecutionUpdateEvent(
                        toolCall.Id, update), c).AsTask();
                    return Task.CompletedTask;
                },
                Ask: (req, c) => _permissionService.AskUserAsync(req, c),
                Services: _services);
            
            var result = await tool.ExecuteAsync(toolCall.Args, ctx, ct);
            
            await _eventBus.PublishAsync(new ToolExecutionEndEvent(
                toolCall.Id, result, result.IsError), ct);
            
            return new ToolResult(
                toolCall.Id, toolCall.ToolName, result.Output, 
                result.IsError, result.Metadata);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolResult(
                toolCall.Id, toolCall.ToolName,
                Output: "Tool execution was cancelled.",
                IsError: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} failed", toolCall.ToolName);
            return new ToolResult(
                toolCall.Id, toolCall.ToolName,
                Output: $"Tool execution failed: {ex.Message}",
                IsError: true);
        }
    }
    
    private bool DetectDoomLoop(ISessionContext session, int threshold)
    {
        var recentToolCalls = session.Messages
            .Skip(Math.Max(0, session.Messages.Count - threshold * 2))
            .OfType<AssistantMessage>()
            .SelectMany(m => m.Parts.OfType<ToolCallPart>())
            .TakeLast(threshold)
            .ToList();
        
        if (recentToolCalls.Count < threshold) return false;
        
        var first = recentToolCalls[0];
        return recentToolCalls.All(tc => 
            tc.ToolName == first.ToolName && 
            JsonElement.DeepEquals(tc.Args, first.Args));
    }
}
```

## 4. Permission model

### 4.1. Ruleset

```csharp
public sealed record PermissionRule(
    string Permission,    // tool name: "bash", "edit", "read", "webfetch", ...
    string Pattern,       // glob pattern for arg matching: "*", "src/*", "*.env*"
    PermissionAction Action);

public enum PermissionAction
{
    Allow,
    Ask,
    Deny
}

public sealed record PermissionRuleset(IReadOnlyList<PermissionRule> Rules)
{
    private readonly List<PermissionRule> _sortedRules;
    
    public PermissionRuleset(IReadOnlyList<PermissionRule> rules)
    {
        // Sort by pattern specificity (more specific first)
        Rules = rules;
        _sortedRules = rules
            .OrderByDescending(r => PatternSpecificity(r.Pattern))
            .ThenByDescending(r => r.Action == PermissionAction.Deny ? 1 : 0)
            .ToList();
    }
    
    public PermissionAction Evaluate(string permission, string argPath)
    {
        foreach (var rule in _sortedRules)
        {
            if (rule.Permission != permission && rule.Permission != "*") continue;
            if (MatchesGlob(argPath, rule.Pattern))
                return rule.Action;
        }
        return PermissionAction.Ask;  // default
    }
    
    private static int PatternSpecificity(string pattern)
    {
        // "*" = 0, "src/*" = 1, "*.env*" = 2, "src/config.json" = 3
        if (pattern == "*") return 0;
        var stars = pattern.Count(c => c == '*');
        var length = pattern.Length;
        return length - stars * 2;  // heuristic
    }
    
    private static bool MatchesGlob(string path, string pattern) =>
        GlobMatcher.Match(pattern, path);  // Microsoft.Extensions.FileSystemGlobbing
}
```

### 4.2. Default rulesets per agent

```csharp
public static class DefaultPermissions
{
    public static readonly PermissionRuleset CodeAgent = new(new[]
    {
        new PermissionRule("read", "*", PermissionAction.Allow),
        new PermissionRule("glob", "*", PermissionAction.Allow),
        new PermissionRule("grep", "*", PermissionAction.Allow),
        new PermissionRule("ls", "*", PermissionAction.Allow),
        new PermissionRule("write", "src/*", PermissionAction.Allow),
        new PermissionRule("write", "*", PermissionAction.Ask),
        new PermissionRule("edit", "src/*", PermissionAction.Allow),
        new PermissionRule("edit", "*.env", PermissionAction.Deny),
        new PermissionRule("edit", "*.env.*", PermissionAction.Deny),
        new PermissionRule("edit", "*", PermissionAction.Ask),
        new PermissionRule("bash", "ls *", PermissionAction.Allow),
        new PermissionRule("bash", "cat *", PermissionAction.Allow),
        new PermissionRule("bash", "grep *", PermissionAction.Allow),
        new PermissionRule("bash", "rg *", PermissionAction.Allow),
        new PermissionRule("bash", "find *", PermissionAction.Allow),
        new PermissionRule("bash", "git status", PermissionAction.Allow),
        new PermissionRule("bash", "git diff *", PermissionAction.Allow),
        new PermissionRule("bash", "git log *", PermissionAction.Allow),
        new PermissionRule("bash", "rm -rf *", PermissionAction.Deny),
        new PermissionRule("bash", "sudo *", PermissionAction.Deny),
        new PermissionRule("bash", "*", PermissionAction.Ask),
        new PermissionRule("webfetch", "*", PermissionAction.Ask),
        new PermissionRule("task", "*", PermissionAction.Allow),
    });
    
    public static readonly PermissionRuleset PlanAgent = new(new[]
    {
        new PermissionRule("read", "*", PermissionAction.Allow),
        new PermissionRule("glob", "*", PermissionAction.Allow),
        new PermissionRule("grep", "*", PermissionAction.Allow),
        new PermissionRule("ls", "*", PermissionAction.Allow),
        new PermissionRule("bash", "ls *", PermissionAction.Allow),
        new PermissionRule("bash", "cat *", PermissionAction.Allow),
        new PermissionRule("bash", "git *", PermissionAction.Allow),
        new PermissionRule("bash", "*", PermissionAction.Deny),
        new PermissionRule("edit", "*", PermissionAction.Deny),
        new PermissionRule("write", "*", PermissionAction.Deny),
    });
    
    public static readonly PermissionRuleset ExploreAgent = new(new[]
    {
        new PermissionRule("read", "*", PermissionAction.Allow),
        new PermissionRule("glob", "*", PermissionAction.Allow),
        new PermissionRule("grep", "*", PermissionAction.Allow),
        new PermissionRule("ls", "*", PermissionAction.Allow),
        new PermissionRule("bash", "ls *", PermissionAction.Allow),
        new PermissionRule("bash", "cat *", PermissionAction.Allow),
        new PermissionRule("bash", "find *", PermissionAction.Allow),
        new PermissionRule("bash", "rg *", PermissionAction.Allow),
        new PermissionRule("bash", "git status", PermissionAction.Allow),
        new PermissionRule("bash", "git diff *", PermissionAction.Allow),
        new PermissionRule("bash", "git log *", PermissionAction.Allow),
        new PermissionRule("bash", "*", PermissionAction.Deny),
        new PermissionRule("edit", "*", PermissionAction.Deny),
        new PermissionRule("write", "*", PermissionAction.Deny),
        new PermissionRule("webfetch", "*", PermissionAction.Allow),
    });
}
```

### 4.3. Pattern matching для bash tool

Для `bash` tool pattern matching сложнее — нужно парсить команду. Решение: простой шелл-парсер, который извлекает command + args и matcher'ит по args:

```csharp
public sealed class BashPermissionMatcher
{
    public PermissionAction Evaluate(
        PermissionRuleset ruleset, 
        string command)
    {
        // 1. Parse command into argv
        var (cmd, args) = BashCommandParser.Parse(command);
        var fullCommand = $"{cmd} {string.Join(' ', args)}".Trim();
        
        // 2. Check "dangerous" patterns first
        if (IsDangerousPattern(fullCommand))
            return PermissionAction.Deny;
        
        // 3. Match against ruleset
        return ruleset.Evaluate("bash", fullCommand);
    }
    
    private static bool IsDangerousPattern(string command)
    {
        // Always-deny patterns
        var dangerous = new[]
        {
            "rm -rf /",
            "rm -rf ~",
            "rm -rf $HOME",
            "mkfs",
            "dd if=/dev/zero of=/dev/sda",
            ":(){:|:&};:",  // fork bomb
            "> /dev/sda",
            "chmod -R 777 /"
        };
        
        return dangerous.Any(d => command.Contains(d));
    }
}

public static class BashCommandParser
{
    public static (string cmd, string[] args) Parse(string command)
    {
        // Simple shell-like parsing: split by spaces, respecting quotes
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inSingleQuote = false, inDoubleQuote = false;
        
        foreach (char c in command)
        {
            if (c == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; continue; }
            if (c == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; continue; }
            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        
        if (tokens.Count == 0) return ("", Array.Empty<string>());
        return (tokens[0], tokens.Skip(1).ToArray());
    }
}
```

### 4.4. Permission ask UI

Когда action = `Ask`, harbor должен спросить пользователя. В TUI — модальный диалог:

```
┌─ Permission Required ────────────────────────────────────┐
│                                                           │
│  Tool: bash                                               │
│  Command: rm -rf node_modules                             │
│                                                           │
│  [y] Allow once   [a] Always allow   [n] Deny   [N] Always deny │
│                                                           │
└───────────────────────────────────────────────────────────┘
```

В CLI/print mode — read from stdin: `[y/a/n/N]`.

В RPC mode (future) — `permission_request` event → client responds.

## 5. Builtin tools

### 5.1. `read`

(см. пример в §1.4)

Дополнительно:
- Поддержка PDF (через `PdfPig` или `iText7` — community edition).
- Поддержка DOCX (через `NPOI` или `OpenXML SDK`).
- Auto-detect encoding (UTF-8 / Windows-1251 / etc.) через `UTF8Encoding` + BOM detection.
- Line numbering: `[001] first line\n[002] second line\n...` для удобного referencing в edit.

### 5.2. `write`

```csharp
public sealed class WriteTool : ToolBase<WriteTool.Args>
{
    public override string Id => "write";
    public override string Description => 
        "Write content to a file. Creates the file if it doesn't exist. " +
        "Overwrites if it exists. Creates parent directories if needed.";
    public override ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    
    public sealed class Args
    {
        [JsonPropertyName("path")]
        [Description("File path to write to")]
        [JsonRequired]
        public string Path { get; set; } = "";
        
        [JsonPropertyName("content")]
        [Description("File content to write")]
        [JsonRequired]
        public string Content { get; set; } = "";
        
        [JsonPropertyName("createDirs")]
        [Description("Create parent directories if they don't exist (default: true)")]
        public bool CreateDirs { get; set; } = true;
    }
    
    protected override async Task<ToolResult> ExecuteAsync(Args args, ToolContext ctx, CancellationToken ct)
    {
        if (!Path.IsPathRooted(args.Path))
            args.Path = System.IO.Path.Combine(Environment.CurrentDirectory, args.Path);
        
        if (args.CreateDirs)
        {
            var dir = System.IO.Path.GetDirectoryName(args.Path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
        
        // Backup if file exists
        if (File.Exists(args.Path))
        {
            // Harbor keeps snapshots in ~/.harbor/snapshots/<sessionId>/<messageId>/<original-path>
            await _snapshotService.SnapshotAsync(args.Path, ctx.SessionId, ctx.MessageId, ct);
        }
        
        await File.WriteAllTextAsync(args.Path, args.Content, ct);
        
        return new ToolResult(
            Output: $"Wrote {args.Content.Length} chars to {args.Path}",
            IsError: false,
            Metadata: new { path = args.Path, bytes = args.Content.Length });
    }
}
```

### 5.3. `edit` (string replacement)

```csharp
public sealed class EditTool : ToolBase<EditTool.Args>
{
    public override string Id => "edit";
    public override string Description =>
        "Make a string replacement in a file. " +
        "Either `oldString` → `newString` (single replacement) or " +
        "`edits` array (multi-edit). The `oldString` must be unique in the file " +
        "unless `replaceAll` is true.";
    
    public sealed class Args
    {
        [JsonPropertyName("path")]
        [JsonRequired]
        public string Path { get; set; } = "";
        
        [JsonPropertyName("oldString")]
        [Description("String to find in the file. Must be unique unless replaceAll=true.")]
        public string? OldString { get; set; }
        
        [JsonPropertyName("newString")]
        [Description("Replacement string. Use empty string to delete.")]
        public string? NewString { get; set; }
        
        [JsonPropertyName("replaceAll")]
        [Description("Replace all occurrences of oldString (default: false)")]
        public bool ReplaceAll { get; set; } = false;
        
        [JsonPropertyName("edits")]
        [Description("Multiple edits to apply in sequence")]
        public IReadOnlyList<SingleEdit>? Edits { get; set; }
        
        [JsonPropertyName("createIfMissing")]
        [Description("Create the file if it doesn't exist (default: false)")]
        public bool CreateIfMissing { get; set; } = false;
    }
    
    public sealed class SingleEdit
    {
        [JsonRequired] public string OldString { get; set; } = "";
        [JsonRequired] public string NewString { get; set; } = "";
        public bool ReplaceAll { get; set; } = false;
    }
    
    protected override async Task<ToolResult> ExecuteAsync(Args args, ToolContext ctx, CancellationToken ct)
    {
        if (!File.Exists(args.Path) && !args.CreateIfMissing)
            return new ToolResult($"File not found: {args.Path}", IsError: true);
        
        var originalContent = File.Exists(args.Path) 
            ? await File.ReadAllTextAsync(args.Path, ct) 
            : "";
        
        await _snapshotService.SnapshotAsync(args.Path, ctx.SessionId, ctx.MessageId, ct);
        
        string newContent;
        var changesCount = 0;
        
        if (args.Edits != null)
        {
            newContent = originalContent;
            foreach (var edit in args.Edits)
            {
                var (newText, count) = ApplyEdit(newContent, edit.OldString, edit.NewString, edit.ReplaceAll);
                if (count == 0)
                    return new ToolResult(
                        $"oldString not found: {edit.OldString[..Math.Min(50, edit.OldString.Length)]}...",
                        IsError: true);
                newContent = newText;
                changesCount += count;
            }
        }
        else
        {
            if (args.OldString == null || args.NewString == null)
                return new ToolResult("Either `edits` or both `oldString`+`newString` required", IsError: true);
            
            var (newText, count) = ApplyEdit(originalContent, args.OldString, args.NewString, args.ReplaceAll);
            if (count == 0)
                return new ToolResult("oldString not found", IsError: true);
            if (count > 1 && !args.ReplaceAll)
                return new ToolResult(
                    $"oldString found {count} times, but replaceAll=false. " +
                    "Make oldString more specific or set replaceAll=true.",
                    IsError: true);
            newContent = newText;
            changesCount = count;
        }
        
        await File.WriteAllTextAsync(args.Path, newContent, ct);
        
        // Generate diff preview
        var diff = DiffGenerator.Generate(originalContent, newContent);
        
        return new ToolResult(
            Output: $"Edited {args.Path}: {changesCount} replacement(s)\n\nDiff:\n{diff}",
            IsError: false,
            Metadata: new { path = args.Path, changes = changesCount });
    }
    
    private static (string result, int count) ApplyEdit(
        string content, string oldStr, string newStr, bool replaceAll)
    {
        if (replaceAll)
        {
            var count = content.Count(oldStr);
            return (content.Replace(oldStr, newStr), count);
        }
        
        var idx = content.IndexOf(oldStr);
        if (idx < 0) return (content, 0);
        
        // Check uniqueness
        var nextIdx = content.IndexOf(oldStr, idx + 1);
        if (nextIdx >= 0) return (content, 2);  // ambiguous
        
        return (content.Remove(idx, oldStr.Length).Insert(idx, newStr), 1);
    }
}
```

### 5.4. `bash`

```csharp
public sealed class BashTool : ToolBase<BashTool.Args>
{
    public override string Id => "bash";
    public override string Description =>
        "Execute a shell command. Output is captured and returned. " +
        "Commands run in the current working directory. " +
        "Use `cd` is supported via `cwd` parameter.";
    public override ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    
    public sealed class Args
    {
        [JsonPropertyName("command")]
        [JsonRequired]
        [Description("Shell command to execute")]
        public string Command { get; set; } = "";
        
        [JsonPropertyName("cwd")]
        [Description("Working directory (default: current)")]
        public string? Cwd { get; set; }
        
        [JsonPropertyName("timeout")]
        [Description("Timeout in seconds (default: 30, max: 600)")]
        [Range(1, 600)]
        public int? Timeout { get; set; } = 30;
        
        [JsonPropertyName("env")]
        [Description("Additional environment variables")]
        public Dictionary<string, string>? Env { get; set; }
    }
    
    protected override async Task<ToolResult> ExecuteAsync(Args args, ToolContext ctx, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetShell(),
            Arguments = GetShellArgs(args.Command),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = args.Cwd ?? Environment.CurrentDirectory
        };
        
        foreach (var (k, v) in args.Env ?? new())
            psi.Environment[k] = v;
        
        using var process = new Process { StartInfo = psi };
        
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(args.Timeout ?? 30));
        
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        
        if (!process.Start()) return new ToolResult("Failed to start process", IsError: true);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return new ToolResult(
                    $"Command timed out after {args.Timeout}s and was killed.\n" +
                    $"Stdout so far:\n{stdout}\nStderr:\n{stderr}",
                    IsError: true);
            }
        }
        
        var output = new StringBuilder();
        if (stdout.Length > 0) output.AppendLine(stdout.ToString());
        if (stderr.Length > 0) output.AppendLine($"[stderr]\n{stderr}");
        output.AppendLine($"[exit code: {process.ExitCode}]");
        
        // Truncate if too long
        if (output.Length > 50_000)
            output.Length = 50_000;
        
        return new ToolResult(
            Output: output.ToString(),
            IsError: process.ExitCode != 0,
            Metadata: new { exitCode = process.ExitCode, duration = process.ExitTime - process.StartTime });
    }
    
    private static string GetShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
    
    private static string GetShellArgs(string command) =>
        OperatingSystem.IsWindows() ? $"/c {command}" : $"-c {command}";
}
```

### 5.5. `glob`, `grep`, `ls`

**`glob`** — file pattern matching:
- Использует `Microsoft.Extensions.FileSystemGlobbing.Matcher`.
- Honors `.gitignore` через `Ignore` NuGet (cross-platform).
- Returns paths (relative to cwd), one per line.

**`grep`** — content search:
- Wraps `ripgrep` binary (bundled or system).
- Falls back to managed `Regex` if ripgrep not available.
- Returns matching lines with file:line:content format.
- Honors `.gitignore`.

**`ls`** — directory listing:
- Cross-platform `Directory.GetFileSystemEntries`.
- Returns entries with type (file/dir), size, modified date.
- Sorted by name (default), or by size/date with `--sort` flag.

## 6. Tool definition injection в system prompt

```csharp
public sealed class SystemPromptBuilder
{
    public async Task<string> BuildAsync(
        AgentInfo agent,
        ModelInfo model,
        IReadOnlyList<ToolDescriptor> tools,
        IReadOnlyList<ContextFile> contextFiles,
        IReadOnlyList<SkillDescriptor> skills,
        string? mcpInstructions,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        
        // 1. Base prompt (per provider)
        var basePrompt = await LoadProviderPromptAsync(model.PromptTemplate, ct);
        sb.AppendLine(basePrompt);
        sb.AppendLine();
        
        // 2. Environment
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Working directory: `{Environment.CurrentDirectory}`");
        sb.AppendLine($"- Platform: {Environment.OSVersion}");
        sb.AppendLine($"- Today: {DateTimeOffset.Now:yyyy-MM-dd}");
        sb.AppendLine($"- Git repo: {(IsGitRepo() ? "yes" : "no")}");
        if (IsGitRepo())
            sb.AppendLine($"- Git branch: {GetCurrentGitBranch()}");
        sb.AppendLine();
        
        // 3. Available tools
        sb.AppendLine("## Available Tools");
        foreach (var tool in tools)
        {
            sb.AppendLine($"- `{tool.Id}`: {tool.PromptSnippet ?? tool.Description}");
            
            if (tool.PromptGuidelines != null)
            {
                foreach (var g in tool.PromptGuidelines)
                    sb.AppendLine($"  - {g}");
            }
        }
        sb.AppendLine();
        
        // 4. MCP instructions
        if (!string.IsNullOrEmpty(mcpInstructions))
        {
            sb.AppendLine("## MCP Servers");
            sb.AppendLine(mcpInstructions);
            sb.AppendLine();
        }
        
        // 5. Skills (if any)
        if (skills.Count > 0)
        {
            sb.AppendLine("## Available Skills");
            sb.AppendLine("The following skills provide specialized instructions:");
            sb.AppendLine();
            sb.AppendLine("<available_skills>");
            foreach (var skill in skills)
            {
                sb.AppendLine($"  <skill>");
                sb.AppendLine($"    <name>{skill.Name}</name>");
                sb.AppendLine($"    <description>{skill.Description}</description>");
                sb.AppendLine($"    <location>{skill.FilePath}</location>");
                sb.AppendLine($"  </skill>");
            }
            sb.AppendLine("</available_skills>");
            sb.AppendLine();
            sb.AppendLine("Use the `read` tool to read a skill file when the task matches its description.");
            sb.AppendLine();
        }
        
        // 6. Context files (AGENTS.md, CLAUDE.md, etc.)
        if (contextFiles.Count > 0)
        {
            sb.AppendLine("## Project Context");
            sb.AppendLine();
            sb.AppendLine("<project_context>");
            foreach (var file in contextFiles)
            {
                sb.AppendLine($"<file path=\"{file.Path}\">");
                sb.AppendLine(file.Content);
                sb.AppendLine("</file>");
            }
            sb.AppendLine("</project_context>");
            sb.AppendLine();
        }
        
        // 7. Agent-specific prompt append
        if (!string.IsNullOrEmpty(agent.SystemPromptAppend))
        {
            sb.AppendLine("## Additional Instructions");
            sb.AppendLine(agent.SystemPromptAppend);
        }
        
        return sb.ToString();
    }
}
```

## 7. Structured output (forced tool use)

Иногда нужно, чтобы LLM обязательно вернул tool call (например, для structured output):

```csharp
public sealed class StructuredOutputTool<T> : ITool
{
    public string Id => "structured_output";
    public JsonDocument ParameterSchema => SchemaGenerator.For<T>();
    
    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var result = args.Deserialize<T>(HarborJsonContext.Default.Options);
        ctx.Services.GetRequiredService<StructuredOutputHolder<T>>().SetResult(result!);
        return new ToolResult("OK", IsError: false);
    }
}

// В request: tool_choice = { type: "tool", name: "structured_output" }
// AgentLoop проверяет, что этот tool вызван, и использует его args как structured output.
```

## 8. Tool execution performance

| Tool | Avg latency | Bottleneck |
|---|---|---|
| `read` (10KB file) | <1 ms | FS read |
| `read` (1MB file) | ~10 ms | FS read + truncation |
| `write` (10KB) | <5 ms | FS write + snapshot |
| `edit` (single replacement) | <2 ms | Read + replace + write |
| `bash` (simple, e.g. `ls`) | ~50 ms | Process spawn |
| `bash` (npm install) | varies | External |
| `glob` (1000 files) | ~5 ms | FS walk |
| `grep` (ripgrep, 1000 files) | ~20 ms | Subprocess + FS scan |
| `grep` (regex fallback, 1000 files) | ~200 ms | Regex + FS scan |

**Process spawn overhead** (~50ms) — основная bottleneck для `bash`. Решение: **persistent shell session** (как у crush) — держим shell process, шлём команды через stdin. Экономия ~45ms на каждый вызов.

```csharp
public sealed class PersistentShellSession : IDisposable
{
    private readonly Process _shell;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    
    public PersistentShellSession(string shell = "/bin/bash")
    {
        _shell = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-i",  // interactive mode
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _shell.Start();
        _stdin = _shell.StandardInput;
        _stdout = _shell.StandardOutput;
        _stderr = _shell.StandardError;
        
        // Send initialization (disable prompt, set marker for command end)
        _stdin.WriteLine("export PS1=''");
        _stdin.WriteLine("export PROMPT_COMMAND=''");
        _stdin.WriteLine("echo '__HARBOR_READY__'");
        _stdout.ReadLine();  // wait for ready marker
    }
    
    public async Task<(string stdout, string stderr, int exitCode)> ExecuteAsync(
        string command, CancellationToken ct)
    {
        var endMarker = $"__HARBOR_END_{Guid.NewGuid():N}__";
        await _stdin.WriteLineAsync($"{command}; echo '{endMarker}':$?");
        
        var stdout = new StringBuilder();
        string line;
        while ((line = await _stdout.ReadLineAsync(ct)) != null)
        {
            if (line.StartsWith(endMarker))
            {
                var exitCode = int.Parse(line[(endMarker.Length + 1)..]);
                return (stdout.ToString(), "", exitCode);
            }
            stdout.AppendLine(line);
        }
        
        return (stdout.ToString(), "", -1);
    }
    
    public void Dispose()
    {
        _stdin.Close();
        if (!_shell.HasExited) _shell.Kill();
        _shell.Dispose();
    }
}
```

## 9. Snapshot/revert (для `write`/`edit`)

Каждый `write`/`edit` делает snapshot оригинального файла. Если пользователь хочет `revert`:

```bash
harbor session revert <message-id>  # откатить ФС к моменту перед этой message
```

Реализация — simple file copy в `~/.harbor/snapshots/<session>/<message>/<original-path>`:

```csharp
public sealed class FileSnapshotService
{
    public async Task SnapshotAsync(string filePath, string sessionId, string messageId, CancellationToken ct)
    {
        if (!File.Exists(filePath)) return;
        
        var snapshotPath = Path.Combine(
            _snapshotRoot,
            sessionId,
            messageId,
            filePath.Replace(Path.DirectorySeparatorChar, '_').TrimStart('_'));
        
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await Task.Run(() => File.Copy(filePath, snapshotPath, overwrite: true), ct);
    }
    
    public async Task RevertToAsync(string sessionId, string messageId, CancellationToken ct)
    {
        var snapshotDir = Path.Combine(_snapshotRoot, sessionId, messageId);
        if (!Directory.Exists(snapshotDir)) return;
        
        // Restore all files snapshotted at this message
        foreach (var snapshotFile in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var originalPath = DecodePath(Path.GetRelativePath(snapshotDir, snapshotFile));
            File.Copy(snapshotFile, originalPath, overwrite: true);
        }
    }
}
```

## 10. Future: advanced tools

| Tool | Status | Зачем |
|---|---|---|
| `task` | MVP | Delegate к subagent (explore/general/scout). См. `12-roadmap.md`. |
| `todo` | v1 | Todo-list management для plan mode. |
| `webfetch` | v1 | HTTP fetch (через `HttpClient`). |
| `websearch` | v1 | Web search (через external API). |
| `skill` | v1 | Load skill file from `.harbor/skills/`. |
| `question` | v1 | Ask user for clarification. |
| `diagnostics` (LSP) | v1 (plugin) | Get LSP diagnostics for current file. |
| `lsp_*` | v2 | Full LSP integration (definition, references, hover, etc.). |

---

**Next**: `05-sessions.md` — хранение, компакция, branching, snapshot.
