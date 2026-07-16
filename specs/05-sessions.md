# 05 — Сессии и компакция

> Документ: хранение сессий (SQLite vs JSONL), data model, compaction strategy, branching, snapshot/revert, token estimation.

## 1. Цели

1. **Persistence** — сессия переживает рестарт harbor, можно `harbor session resume <id>`.
2. **Efficient streaming** — запись partial message в БД без full rewrite.
3. **Compaction** — автоматическое сжатие контекста при приближении к context window.
4. **Branching** — альтернативные ветки разговора без копирования.
5. **Human-readable export** — JSONL mirror для git diff, grep, archival.
6. **Low memory** — не держать всю историю в RAM.

## 2. Storage options

### 2.1. SQLite (default)

**Плюсы**:
- Нормализованная schema, индексы, FK.
- WAL mode → concurrent reads во время write.
- Эффективный paged storage, не загружает всё в память.
- Dapper.AOT → AOT-compatible.

**Минусы**:
- Бинарный файл, не diff-friendly.
- 64 МБ page cache по умолчанию (мы уменьшим до 8 МБ).
- Требует миграций.

### 2.2. JSONL (alternative)

**Плюсы**:
- Human-readable, git-friendly.
- Append-only — атомарная запись.
- Branching через `parentId` pointers без копирования.
- Нет миграций (forward-compatible).

**Минусы**:
- Нет индексов → медленные queries для больших сессий.
- Вся история в памяти при работе (`List<Entry>`) → рост RAM.
- Нет efficient pagination.

### 2.3. Решение: dual storage

**MVP**: SQLite (primary).
**Export**: JSONL mirror для grep/diff/archival.
**Future**: JSONL-only для embedded scenarios (e.g., portable single-file harbor).

```csharp
public interface ISessionStore
{
    Task<Session> CreateAsync(string directory, string agentName, string modelId, CancellationToken ct);
    Task<Session?> GetAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<Session>> ListAsync(string? projectId = null, CancellationToken ct = default);
    Task AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct);
    Task UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct);
    Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(string sessionId, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
    Task<SessionStats> GetStatsAsync(string sessionId, CancellationToken ct);
}
```

## 3. SQLite schema

```sql
-- migrations/0001_initial.sql

PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;
PRAGMA cache_size = -8000;  -- 8 MB (not 64 MB!)

CREATE TABLE projects (
    id TEXT PRIMARY KEY,
    directory TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    vcs TEXT,  -- 'git' | null
    created_at TEXT NOT NULL,
    last_session_at TEXT
);

CREATE TABLE sessions (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    parent_id TEXT REFERENCES sessions(id) ON DELETE SET NULL,  -- for fork
    directory TEXT NOT NULL,
    title TEXT NOT NULL,
    agent TEXT NOT NULL,
    model TEXT NOT NULL,
    version TEXT NOT NULL,  -- harbor version
    cost REAL NOT NULL DEFAULT 0,
    tokens_input INTEGER NOT NULL DEFAULT 0,
    tokens_output INTEGER NOT NULL DEFAULT 0,
    tokens_reasoning INTEGER NOT NULL DEFAULT 0,
    tokens_cache_read INTEGER NOT NULL DEFAULT 0,
    tokens_cache_write INTEGER NOT NULL DEFAULT 0,
    time_compacting_ms INTEGER NOT NULL DEFAULT 0,
    summary_message_id TEXT,  -- last compaction message
    revert_state TEXT,  -- JSON: file snapshots at message N
    metadata TEXT,  -- JSON
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX idx_sessions_project ON sessions(project_id);
CREATE INDEX idx_sessions_updated ON sessions(updated_at DESC);
CREATE INDEX idx_sessions_parent ON sessions(parent_id);

CREATE TABLE messages (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    parent_id TEXT REFERENCES messages(id) ON DELETE SET NULL,  -- for branching
    role TEXT NOT NULL,  -- 'user' | 'assistant' | 'tool_result' | 'system' | 'compaction_summary'
    agent TEXT,
    model TEXT,
    cost REAL NOT NULL DEFAULT 0,
    tokens_input INTEGER NOT NULL DEFAULT 0,
    tokens_output INTEGER NOT NULL DEFAULT 0,
    finish_reason TEXT,  -- 'stop' | 'length' | 'tool_use' | 'error' | 'aborted'
    is_summary INTEGER NOT NULL DEFAULT 0,  -- 1 if this is a compaction summary
    summary_first_kept_id TEXT,  -- for compaction: first retained message
    metadata TEXT,  -- JSON
    created_at TEXT NOT NULL
);

CREATE INDEX idx_messages_session ON messages(session_id, created_at);
CREATE INDEX idx_messages_parent ON messages(parent_id);
CREATE INDEX idx_messages_summary ON messages(session_id, is_summary) WHERE is_summary = 1;

CREATE TABLE message_parts (
    id TEXT PRIMARY KEY,
    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    session_id TEXT NOT NULL,  -- denormalized for fast delete
    seq INTEGER NOT NULL,  -- order within message
    type TEXT NOT NULL,  -- 'text' | 'thinking' | 'tool_call' | 'tool_result' | 'file' | 'image'
    content TEXT,  -- text content (for text/thinking)
    tool_call_id TEXT,
    tool_name TEXT,
    tool_args TEXT,  -- JSON
    tool_output TEXT,
    is_error INTEGER NOT NULL DEFAULT 0,
    file_path TEXT,
    file_mime_type TEXT,
    file_size_bytes INTEGER,
    metadata TEXT,  -- JSON
    is_compacted INTEGER NOT NULL DEFAULT 0,  -- 1 if pruned by compaction
    created_at TEXT NOT NULL,
    FOREIGN KEY (message_id) REFERENCES messages(id) ON DELETE CASCADE
);

CREATE INDEX idx_parts_message ON message_parts(message_id, seq);
CREATE INDEX idx_parts_session ON message_parts(session_id);
CREATE INDEX idx_parts_compacted ON message_parts(session_id, is_compacted) WHERE is_compacted = 1;

-- For file snapshots (write/edit history)
CREATE TABLE file_snapshots (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    original_path TEXT NOT NULL,
    snapshot_path TEXT NOT NULL,  -- path in ~/.harbor/snapshots/
    file_size_bytes INTEGER,
    created_at TEXT NOT NULL
);

CREATE INDEX idx_snapshots_session_message ON file_snapshots(session_id, message_id);

-- For todos (per session)
CREATE TABLE todos (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    content TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',  -- 'pending' | 'in_progress' | 'completed'
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT
);

CREATE INDEX idx_todos_session ON todos(session_id);

-- For settings/metadata
CREATE TABLE session_metadata (
    session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
    key TEXT NOT NULL,
    value TEXT
);
```

### 3.1. Dapper.AOT queries

```csharp
[DapperAot]
public static class SessionQueries
{
    public const string InsertSession = """
        INSERT INTO sessions (id, project_id, directory, title, agent, model, version, created_at, updated_at)
        VALUES (@Id, @ProjectId, @Directory, @Title, @Agent, @Model, @Version, @CreatedAt, @UpdatedAt)
        """;
    
    public const string GetMessageParts = """
        SELECT p.* FROM message_parts p
        JOIN messages m ON p.message_id = m.id
        WHERE m.session_id = @SessionId AND p.is_compacted = 0
        ORDER BY m.created_at, p.seq
        """;
    
    public const string AppendToolCallPart = """
        INSERT INTO message_parts (id, message_id, session_id, seq, type, tool_call_id, tool_name, tool_args, created_at)
        VALUES (@Id, @MessageId, @SessionId, @Seq, 'tool_call', @ToolCallId, @ToolName, @ToolArgs, @CreatedAt)
        """;
    
    public const string MarkPartCompacted = """
        UPDATE message_parts SET is_compacted = 1, metadata = json_patch(COALESCE(metadata, '{}'), json('{"compactedAt": @Timestamp}'))
        WHERE id IN @PartIds
        """;
}
```

### 3.2. Streaming write strategy

Во время LLM-стриминга пишем в БД лениво:

```
LLM stream starts:
  → INSERT INTO messages (id, ..., finish_reason=NULL) VALUES (...);
  
On each TextDelta:
  → Buffer in memory (in AgentMessage.Parts[0].Text)
  
On MessageEnd (full assistant message):
  → INSERT INTO message_parts (...) VALUES (...) for each content part
  → UPDATE messages SET finish_reason = @Reason, cost = @Cost, tokens_* = @Usage WHERE id = @Id;
  → UPDATE sessions SET cost = cost + @Cost, tokens_input = tokens_input + @In, ... WHERE id = @SessionId;
```

Это даёт атомарность: partial messages не появляются в БД, только complete.

Альтернатива — стримить в БД каждые N токенов (для crash recovery):

```csharp
public sealed class StreamingMessageWriter
{
    private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(2);
    private readonly int _flushTokenThreshold = 500;
    
    public async Task StreamToDatabaseAsync(
        IAsyncEnumerable<LLMEvent> stream,
        string sessionId,
        string messageId,
        CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var lastFlush = DateTimeOffset.UtcNow;
        var tokenCount = 0;
        
        await foreach (var evt in stream)
        {
            if (evt is TextDeltaEvent td)
            {
                buffer.Append(td.Delta);
                tokenCount++;
                
                var shouldFlush = 
                    buffer.Length > 1000 ||
                    DateTimeOffset.UtcNow - lastFlush > _flushInterval ||
                    tokenCount >= _flushTokenThreshold;
                
                if (shouldFlush)
                {
                    await FlushAsync(sessionId, messageId, buffer.ToString(), ct);
                    lastFlush = DateTimeOffset.UtcNow;
                    buffer.Clear();
                    tokenCount = 0;
                }
            }
            // ...
        }
        
        if (buffer.Length > 0)
            await FlushAsync(sessionId, messageId, buffer.ToString(), ct);
    }
}
```

В MVP — буферизация в памяти + flush on MessageEnd. Crash recovery — v1.

## 4. Compaction strategy

### 4.1. Когда compact'ать

```csharp
public sealed class CompactionService
{
    public bool ShouldCompact(ISessionContext session, ModelInfo model)
    {
        var estimatedTokens = EstimateContextTokens(session);
        var reserve = _options.Compaction.ReserveTokens;  // default 16384
        return estimatedTokens > model.ContextWindow - reserve;
    }
    
    private int EstimateContextTokens(ISessionContext session)
    {
        // Если у нас есть usage от последнего assistant message — используем его
        var lastAssistant = session.Messages.LastOfType<AssistantMessage>();
        if (lastAssistant?.Usage != null)
        {
            var baseTokens = lastAssistant.Usage.InputTokens + lastAssistant.Usage.OutputTokens;
            // + tokens from messages after lastAssistant
            var trailingTokens = session.Messages
                .SkipWhile(m => m.Id != lastAssistant.Id)
                .Skip(1)
                .Sum(EstimateMessageTokens);
            return baseTokens + trailingTokens;
        }
        
        // Fallback: estimate all from scratch
        return session.Messages.Sum(EstimateMessageTokens);
    }
    
    private int EstimateMessageTokens(AgentMessage message)
    {
        return message switch
        {
            UserMessage u => u.Content.Length / 4 + 100,  // heuristic
            AssistantMessage a => a.Parts.Sum(p => p switch
            {
                TextPart t => t.Text.Length / 4,
                ThinkingPart th => th.Text.Length / 4,
                ToolCallPart tc => tc.ToolName.Length + tc.Args.GetRawText().Length / 4,
                _ => 50
            }) + 100,
            ToolResultMessage tr => tr.Results.Sum(r => r.Output.Length / 4) + 100,
            _ => 50
        };
    }
}
```

### 4.2. Алгоритм compaction

```csharp
public async Task<CompactionResult> RunAsync(
    ISessionContext session,
    ModelInfo model,
    CancellationToken ct)
{
    var stopwatch = Stopwatch.StartNew();
    
    // 1. Find cut point
    var (headMessages, tailMessages) = FindCutPoint(
        session.Messages, 
        keepRecentTokens: _options.Compaction.KeepRecentTokens,  // default 20000
        tailTurns: _options.Compaction.TailTurns);  // default 2
    
    // 2. Get previous summary (if exists) — for incremental update
    var previousSummary = session.Messages
        .LastOrDefault(m => m is { Role: "assistant", IsSummary: true }) 
        as AssistantMessage;
    
    // 3. Extract file operations from history (cumulative)
    var fileOps = ExtractFileOperations(headMessages, previousSummary?.Metadata?.FileOps);
    
    // 4. Build summarization prompt
    var prompt = BuildSummarizationPrompt(
        headMessages: headMessages,
        previousSummary: previousSummary?.Parts.OfType<TextPart>().FirstOrDefault()?.Text,
        fileOps: fileOps,
        cwd: session.Directory);
    
    // 5. Call LLM (hidden "compaction" agent)
    var compactionModel = model;  // or use smaller model for cost
    var llmClient = _providerRegistry.GetClient(compactionModel.ProviderId);
    
    var summaryRequest = new LlmRequest(
        Model: compactionModel.Id,
        Messages: new[] 
        { 
            new UserMessage("user", new[] { new TextBlock(prompt) })
        },
        SystemPrompt: SUMMARIZATION_SYSTEM_PROMPT,
        Tools: Array.Empty<ToolDefinition>(),
        ToolChoice: null,
        MaxOutputTokens: 4096,
        Temperature: 0.3m,
        TopP: null,
        TopK: null,
        ReasoningEffort: null,
        CacheStrategy: null,
        ExtraHeaders: null);
    
    var summaryContent = new StringBuilder();
    Usage? summaryUsage = null;
    
    await foreach (var evt in llmClient.StreamAsync(summaryRequest, ct))
    {
        if (evt is TextDeltaEvent td)
            summaryContent.Append(td.Delta);
        else if (evt is StepFinishEvent sf)
            summaryUsage = sf.Usage;
    }
    
    // 6. Create summary message
    var summaryMessage = new AssistantMessage(
        Id: Guid.NewGuid().ToString(),
        SessionId: session.Id,
        CreatedAt: DateTimeOffset.UtcNow,
        Parts: new[] { new TextPart(summaryContent.ToString()) },
        StopReason: "stop",
        Usage: summaryUsage ?? new Usage(0, 0, 0, 0, 0),
        Model: compactionModel.Id)
    {
        IsSummary = true,
        SummaryFirstKeptId = tailMessages.FirstOrDefault()?.Id,
        Metadata = new MessageMetadata { FileOps = fileOps }
    };
    
    // 7. Mark head messages as compacted (in DB)
    var headPartIds = headMessages.SelectMany(m => m.Parts.Select(p => p.Id)).ToList();
    await _sessionStore.MarkCompactedAsync(session.Id, headPartIds, ct);
    
    // 8. Insert summary message
    await _sessionStore.AppendMessageAsync(session.Id, summaryMessage, ct);
    
    // 9. Update session metadata
    await _sessionStore.UpdateSessionAsync(session.Id, new {
        summary_message_id = summaryMessage.Id,
        time_compacting_ms = (int)stopwatch.Elapsed.TotalMilliseconds
    }, ct);
    
    return new CompactionResult(
        Summary: summaryContent.ToString(),
        PrunedMessageCount: headMessages.Count,
        TokensSaved: headMessages.Sum(EstimateMessageTokens) - EstimateMessageTokens(summaryMessage),
        Duration: stopwatch.Elapsed);
}

private (IReadOnlyList<AgentMessage> head, IReadOnlyList<AgentMessage> tail) FindCutPoint(
    IReadOnlyList<AgentMessage> messages,
    int keepRecentTokens,
    int tailTurns)
{
    // Walk backwards, accumulate tokens, stop at keepRecentTokens
    var tailTokens = 0;
    var tailStart = messages.Count;
    
    for (int i = messages.Count - 1; i >= 0; i--)
    {
        var msgTokens = EstimateMessageTokens(messages[i]);
        if (tailTokens + msgTokens > keepRecentTokens) break;
        
        // Don't cut in the middle of a turn (tool_call ↔ tool_result pair)
        if (messages[i] is ToolResultMessage) continue;
        
        tailTokens += msgTokens;
        tailStart = i;
    }
    
    // Also enforce tail_turns minimum
    var minTailStart = messages.Count - (tailTurns * 4);  // ~4 messages per turn (user+assistant+tool_result)
    if (minTailStart < tailStart) tailStart = Math.Max(0, minTailStart);
    
    return (messages.Take(tailStart).ToList(), messages.Skip(tailStart).ToList());
}
```

### 4.3. Summarization prompt

```
You are creating a summary of the conversation so far to provide context to a teammate who is taking over the task.

The summary should preserve ALL important information needed to continue the work, including:
- The original goal and current state
- Decisions made and their rationale  
- Files read and modified (with paths)
- Commands run and their outcomes
- Errors encountered and how they were resolved
- Outstanding questions or blockers

Output the summary in this exact Markdown structure:

## Goal
[What the user is trying to accomplish]

## Constraints & Preferences
[Any constraints, preferences, or rules discovered]

## Progress
### Done
- [Completed tasks]

### In Progress
- [Currently being worked on]

### Blocked
- [Items blocked, with reason]

## Key Decisions
- [Decision: rationale]

## Next Steps
- [Immediate next actions]

## Critical Context
[Any other information needed to continue]

## Files
### Read
- `path/to/file`

### Modified  
- `path/to/file` — what was changed

Rules:
- Keep every section, even when empty (use "None" if no content).
- Preserve exact file paths, commands, error strings, identifiers.
- Do not mention the summary process or that context was compacted.
- Be concise but complete — every detail matters.
{previous_summary_section}
```

Where `{previous_summary_section}` is:

```
## Previous Summary
Below is the previous summary. Update it with the latest information:

{previous_summary_text}
```

### 4.4. Background pruning

Compaction вызывается **только при overflow**. Для регулярной очистки старых tool outputs (которые могут быть огромными) — background prune:

```csharp
public sealed class PruningService : IHostedService
{
    public async Task PruneAfterTurnAsync(string sessionId, CancellationToken ct)
    {
        var options = _compactionOptions.Value;
        
        // Get total tokens of tool outputs
        var toolParts = await _sessionStore.GetToolPartsAsync(sessionId, ct);
        var totalToolTokens = toolParts.Sum(p => p.Output?.Length ?? 0) / 4;
        
        if (totalToolTokens < options.PruneMinimum)  // default 20000
            return;
        
        // Protect last N tokens from pruning
        var protectedTokens = 0;
        var toPrune = new List<string>();
        
        for (int i = toolParts.Count - 1; i >= 0; i--)
        {
            var part = toolParts[i];
            var tokens = (part.Output?.Length ?? 0) / 4;
            
            if (protectedTokens < options.PruneProtect)  // default 40000
            {
                protectedTokens += tokens;
                continue;
            }
            
            // Don't prune "skill" tool outputs (rare)
            if (part.ToolName == "skill") continue;
            
            toPrune.Add(part.Id);
        }
        
        if (toPrune.Count > 0)
        {
            await _sessionStore.MarkPartsCompactedAsync(sessionId, toPrune, ct);
            // The output is replaced with: "[pruned: original was N tokens]"
        }
    }
}
```

## 5. Branching (forking sessions)

Пользователь может "fork" сессию с любого message:

```bash
harbor session fork <message-id> --title "Alternative approach"
```

В БД:
- Создаётся новая `sessions` строка с `parent_id = @originalSessionId`.
- Не копируем messages — они available через `parent_id` lookup.
- Новая сессия начинается с `messages.parent_id = @forkedMessageId` для первой записи.

```csharp
public async Task<Session> ForkAsync(
    string sourceSessionId,
    string fromMessageId,
    string title,
    CancellationToken ct)
{
    var source = await GetAsync(sourceSessionId, ct) 
        ?? throw new SessionNotFoundException(sourceSessionId);
    
    var forkedMessage = await GetMessageAsync(fromMessageId, ct)
        ?? throw new MessageNotFoundException(fromMessageId);
    
    var newSession = new Session(
        Id: Guid.NewGuid().ToString(),
        ProjectId: source.ProjectId,
        ParentId: source.Id,  // link to parent
        Directory: source.Directory,
        Title: title,
        Agent: source.Agent,
        Model: source.Model,
        Version: HarborVersion.Current.ToString(),
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);
    
    await InsertAsync(newSession, ct);
    
    // Insert a "fork_point" marker message
    var forkMarker = new UserMessage(
        Id: Guid.NewGuid().ToString(),
        SessionId: newSession.Id,
        CreatedAt: DateTimeOffset.UtcNow,
        Content: $"[Forked from session {source.Id} at message {fromMessageId}]",
        Agent: source.Agent,
        Model: source.Model)
    {
        ParentId = forkedMessage.Id,  // link to source message
        IsForkMarker = true
    };
    
    await AppendMessageAsync(newSession.Id, forkMarker, ct);
    
    // Generate branch summary (optional, for context)
    if (_options.GenerateBranchSummaries)
    {
        var branchSummary = await GenerateBranchSummaryAsync(
            sourceSessionId, fromMessageId, ct);
        // ... insert as compaction_summary message
    }
    
    return newSession;
}
```

При чтении messages для форка:

```csharp
public async Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(string sessionId, CancellationToken ct)
{
    var messages = new List<AgentMessage>();
    var currentSessionId = sessionId;
    
    // Walk up the parent chain
    while (currentSessionId != null)
    {
        var sessionMessages = await _sessionStore.GetMessagesAsync(currentSessionId, ct);
        
        // If this is the current session, take all messages up to first fork marker
        if (currentSessionId == sessionId)
        {
            messages.AddRange(sessionMessages);
        }
        else
        {
            // For parent sessions, take messages up to fork point
            var forkMarker = sessionMessages.FirstOrDefault(m => m.IsForkMarker);
            if (forkMarker?.ParentId != null)
            {
                var cutOff = sessionMessages.First(m => m.Id == forkMarker.ParentId);
                var idx = sessionMessages.IndexOf(cutOff);
                messages.InsertRange(0, sessionMessages.Take(idx + 1));
            }
        }
        
        var session = await GetAsync(currentSessionId, ct);
        currentSessionId = session?.ParentId;
    }
    
    return messages.OrderBy(m => m.CreatedAt).ToList();
}
```

## 6. JSONL export

```csharp
public sealed class JsonlExporter
{
    public async Task ExportAsync(string sessionId, string outputPath, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        var messages = await _sessionStore.GetMessagesAsync(sessionId, ct);
        
        using var writer = new StreamWriter(outputPath);
        
        // Header
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "session",
            version = "1",
            id = session.Id,
            directory = session.Directory,
            title = session.Title,
            agent = session.Agent,
            model = session.Model,
            createdAt = session.CreatedAt,
            parentSessionId = session.ParentId
        }, HarborJsonContext.Default.SessionHeader));
        
        foreach (var msg in messages)
        {
            var entry = new JsonlEntry(
                type: "message",
                id: msg.Id,
                parentId: msg.ParentId,
                role: msg.Role,
                agent: msg.Agent,
                model: msg.Model,
                content: msg switch
                {
                    UserMessage u => u.Content,
                    AssistantMessage a => a.Parts.Select(p => p switch
                    {
                        TextPart t => new { type = "text", text = t.Text },
                        ThinkingPart th => new { type = "thinking", text = th.Text },
                        ToolCallPart tc => new { type = "tool_call", id = tc.Id, name = tc.ToolName, args = tc.Args },
                        _ => null
                    }).Where(p => p != null).ToList(),
                    ToolResultMessage tr => tr.Results.Select(r => new
                    {
                        type = "tool_result",
                        toolCallId = r.ToolCallId,
                        toolName = r.ToolName,
                        output = r.Output,
                        isError = r.IsError
                    }).ToList(),
                    _ => null
                },
                finishReason: msg is AssistantMessage a ? a.StopReason : null,
                usage: msg is AssistantMessage au ? au.Usage : null,
                isSummary: msg.IsSummary,
                createdAt: msg.CreatedAt);
            
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, HarborJsonContext.Default.JsonlEntry));
        }
    }
}
```

Format — совместим с pi-agent (для interoperability):

```jsonl
{"type":"session","version":"1","id":"abc","directory":"/home/user/project","title":"Fix bug","agent":"code","model":"anthropic/claude-opus-4","createdAt":"2026-07-16T10:30:00Z"}
{"type":"message","id":"msg1","role":"user","content":"fix the login bug","createdAt":"2026-07-16T10:30:01Z"}
{"type":"message","id":"msg2","role":"assistant","content":[{"type":"text","text":"I'll investigate..."},{"type":"tool_call","id":"tc1","name":"read","args":{"path":"src/login.js"}}],"finishReason":"tool_use","usage":{"inputTokens":500,"outputTokens":50},"createdAt":"2026-07-16T10:30:02Z"}
{"type":"message","id":"msg3","role":"tool_result","content":[{"type":"tool_result","toolCallId":"tc1","toolName":"read","output":"export function login() {\n...","isError":false}],"createdAt":"2026-07-16T10:30:03Z"}
```

## 7. Session lifecycle

```
harbor session new                    # создать новую
harbor session list                   # показать все
harbor session resume <id>            # возобновить
harbor session fork <message-id>      # форкнуть с message
harbor session export <id> > out.jsonl # экспорт в JSONL
harbor session import < in.jsonl      # импорт из JSONL
harbor session delete <id>            # удалить
harbor session stats <id>             # статистика (tokens, cost, duration)
harbor session revert <message-id>    # откатить ФС к моменту перед message
```

## 8. Token estimation (без tokenizer)

Использовать tiktoken-like библиотеку для точной оценки — дорого (вклинивает Rust dependency). Используем эвристику:

```csharp
public static class TokenEstimator
{
    // Простая эвристика: chars / 4 для английского, chars / 2 для CJK
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        var cjkCount = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var otherCount = text.Length - cjkCount;
        
        return (int)Math.Ceiling(cjkCount / 2.0 + otherCount / 4.0);
    }
    
    public static int EstimateImage(long sizeBytes, string mimeType)
    {
        // Приблизительная оценка — Claude: ~4800 chars = 1200 tokens
        return mimeType switch
        {
            "image/png" or "image/jpeg" or "image/webp" => 1200,
            "image/gif" => 800,
            _ => (int)(sizeBytes / 1000)  // fallback
        };
    }
    
    public static int EstimateToolCall(string toolName, JsonElement args)
    {
        return toolName.Length / 4 + args.GetRawText().Length / 4 + 50;  // overhead
    }
}
```

Это совпадает с подходом pi и kilocode. Не идеально, но достаточно для триггеров compaction.

## 9. Memory bounds

Цель — даже длинная сессия (10K messages) не должна выжрать >100 МБ RAM.

Стратегия:
1. **Не держать messages в памяти**. `ISessionContext.Messages` возвращает `IReadOnlyList<AgentMessage>`, но под капотом — lazy paged load из SQLite.
2. **Lru cache на последние N messages**. Например, 100 messages в кэше, остальное — из БД.
3. **Streaming для LLM conversion**. `MessageConverter.ToLlmMessagesAsync` — `IAsyncEnumerable<LlmMessage>`, не материализует весь список.
4. **Pruning после каждого turn'а** — `PruningService.PruneAfterTurnAsync` запускается в background.

```csharp
public sealed class PagedMessageStore
{
    private readonly ConcurrentDictionary<string, MessagePage> _cache = new();
    private readonly int _pageSize = 50;
    private readonly int _maxCachedPages = 4;  // 200 messages in RAM
    
    public async Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
        string sessionId, 
        int offset, 
        int limit,
        CancellationToken ct)
    {
        var pageIndex = offset / _pageSize;
        var cacheKey = $"{sessionId}:{pageIndex}";
        
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached.Messages.Skip(offset % _pageSize).Take(limit).ToList();
        
        // Load from DB
        var messages = await _db.QueryAsync(
            "SELECT * FROM messages WHERE session_id = @sid ORDER BY created_at LIMIT @limit OFFSET @offset",
            new { sid = sessionId, limit = _pageSize, offset = pageIndex * _pageSize });
        
        var page = new MessagePage(messages);
        _cache[cacheKey] = page;
        
        // Evict oldest if cache full
        if (_cache.Count > _maxCachedPages)
        {
            var oldest = _cache.OrderBy(p => p.Value.LastAccess).First();
            _cache.TryRemove(oldest.Key, out _);
        }
        
        return page.Messages.Skip(offset % _pageSize).Take(limit).ToList();
    }
}
```

## 10. Migration strategy

EF Core не используется (не AOT-compatible). Вместо него — простой migrator:

```csharp
public sealed class MigrationRunner
{
    private readonly SqliteConnection _connection;
    
    public async Task RunAsync(CancellationToken ct)
    {
        // Create migrations table if not exists
        await _connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS __migrations (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            )
            """);
        
        var applied = (await _connection.QueryAsync<int>("SELECT id FROM __migrations")).ToHashSet();
        
        foreach (var migration in _migrations.OrderBy(m => m.Id))
        {
            if (applied.Contains(migration.Id)) continue;
            
            using var tx = _connection.BeginTransaction();
            try
            {
                await migration.UpAsync(_connection, ct);
                await _connection.ExecuteAsync(
                    "INSERT INTO __migrations (id, name, applied_at) VALUES (@id, @name, @at)",
                    new { id = migration.Id, name = migration.Name, at = DateTimeOffset.UtcNow });
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}

// Migrations are embedded resources
public sealed class Migration_0001_Initial : IMigration
{
    public int Id => 1;
    public string Name => "initial";
    
    public async Task UpAsync(SqliteConnection conn, CancellationToken ct)
    {
        var sql = await EmbeddedResourceReader.ReadAsync("Harbor.Storage.Sqlite.Migrations.0001_initial.sql", ct);
        await conn.ExecuteAsync(sql);
    }
}
```

## 11. Backup и corruption recovery

```bash
harbor db backup ./harbor-backup.db  # full copy with VACUUM INTO
harbor db integrity-check            # PRAGMA integrity_check
harbor db vacuum                     # VACUUM (rebuild file, reclaim space)
harbor db export --all ./sessions/   # JSONL export всех sessions
harbor db import ./sessions/         # import из JSONL
```

## 12. Performance targets

| Operation | Target | Notes |
|---|---|---|
| Create session | <5 ms | INSERT one row |
| Append message | <10 ms | INSERT message + UPDATE session stats |
| Append message part | <5 ms | INSERT one row |
| Get messages (100) | <20 ms | Indexed query, paged |
| Get messages (10K) | <2 s | Paged, lazy |
| Mark compacted (1000 parts) | <50 ms | Batch UPDATE |
| Compaction (10K tokens → 2K) | <3 s | LLM call dominates |
| Session list | <10 ms | Indexed by updated_at DESC |

---

**Next**: `06-mcp.md` — Model Context Protocol client integration.
