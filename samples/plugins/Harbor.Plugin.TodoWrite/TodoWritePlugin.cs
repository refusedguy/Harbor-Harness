using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugin.TodoWrite;
/// <summary>
///     TodoWrite plugin — adds a `todo` tool for task management plus a
///     <c>todo-list</c> TUI panel. Showcase slice-1 (#1434): persistent
///     cross-session todos with search, on the existing axes only
///     (tools + <c>ITuiPanelPlugin</c> — no new axis, so nothing new to
///     declare in <c>PluginAxisRegistry</c>).
/// </summary>
public sealed class TodoWritePlugin : IToolPlugin, ITuiPanelPlugin
{

    internal static readonly ConcurrentDictionary<string, List<TodoItem>> TodosBySession = new();

    internal static readonly TodoStore Store = new();

    public string Name => "todowrite";
    public Version Version => new(1, 1, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Persistent todo list management for agents (with search + panel)";

    public void Initialize(PluginContext context)
    {
        var logger = context.CreateLogger<TodoWritePlugin>();
        try
        {
            Store.Configure(context.DataDirectory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TodoWrite persistence disabled");
            Store.Disable();
        }
        logger.LogInformation("TodoWrite plugin initialized");
    }

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<TodoWriteTool>();

    public void RegisterPanels(IPanelRegistry registry) => registry.Register(new TodoListPanel());

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    internal static TodoStatus ParseStatus(string? value) => value switch
    {
        "pending" => TodoStatus.Pending,
        "in_progress" => TodoStatus.InProgress,
        "completed" => TodoStatus.Completed,
        _ when Enum.TryParse<TodoStatus>(value, true, out var s) => s,
        _ => TodoStatus.Pending,
    };
}

/// <summary>
///     Best-effort JSON persistence for the todo cache. All state lives in the
///     instance; the plugin owns one <c>static readonly</c> instance so the
///     parameterless tool can reach it. Every failure degrades to in-memory
///     state and never breaks startup or tool results.
/// </summary>
internal sealed class TodoStore
{
    private readonly object _gate = new();
    private string? _dir;

    public void Configure(string dir)
    {
        lock (_gate)
        {
            _dir = dir;
        }
        Directory.CreateDirectory(dir);
        Load();
    }

    public void Disable()
    {
        lock (_gate)
        {
            _dir = null;
        }
    }

    private string? PersistPath()
    {
        lock (_gate)
        {
            return _dir is null ? null : Path.Combine(_dir, "todos.json");
        }
    }

    public void Load()
    {
        string? path = PersistPath();
        if (path is null || !File.Exists(path))
            return;
        try
        {
            string json = File.ReadAllText(path);
            var rows = JsonSerializer.Deserialize<List<PersistedTodo>>(json);
            if (rows is null)
                return;
            TodoWritePlugin.TodosBySession.Clear();
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.SessionId) || string.IsNullOrEmpty(row.Id))
                    continue;
                var item = new TodoItem(row.Id, row.Content ?? "", (TodoStatus)row.Status);
                TodoWritePlugin.TodosBySession.GetOrAdd(row.SessionId, _ => new List<TodoItem>()).Add(item);
            }
        }
        catch
        { /* ignore: corrupt cache resets to empty in-memory state */
        }
    }

    public void Save()
    {
        string? path = PersistPath();
        if (path is null)
            return;
        try
        {
            List<PersistedTodo> rows;
            lock (_gate)
            {
                rows = new List<PersistedTodo>();
                foreach (var kv in TodoWritePlugin.TodosBySession.ToArray())
                {
                    List<TodoItem> snapshot;
                    lock (kv.Value)
                    {
                        snapshot = kv.Value.ToList();
                    }
                    foreach (var t in snapshot)
                        rows.Add(new PersistedTodo(kv.Key, t.Id, t.Content, (int)t.Status));
                }
                File.WriteAllText(path, JsonSerializer.Serialize(rows));
            }
        }
        catch
        { /* ignore: persistence is best-effort, the in-memory result already succeeded */
        }
    }
}

public sealed class TodoWriteTool : ITool
{
    public ToolName Name => ToolName.Create("todo");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Todo";
    public string Description => "Manage a persistent todo list. Supports add, update, list, complete, clear, and cross-session search operations. Todos persist across sessions via the plugin data directory.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "todo: Manage persistent task list (add/update/complete/list/search)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `todo` to track progress on multi-step tasks",
        "Add items at the start, mark in_progress when working on them, complete when done",
        "Use action=search with a query to find todos across sessions"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "action": {
                                                                            "type": "string",
                                                                            "enum": ["add", "update", "list", "complete", "clear", "search"],
                                                                            "description": "Action to perform"
                                                                          },
                                                                          "content": { "type": "string", "description": "Todo content (for add)" },
                                                                          "id": { "type": "string", "description": "Todo ID (for update/complete)" },
                                                                          "status": {
                                                                            "type": "string",
                                                                            "enum": ["pending", "in_progress", "completed"],
                                                                            "description": "New status (for update)"
                                                                          },
                                                                          "query": { "type": "string", "description": "Substring to match (for search, across sessions)" }
                                                                        },
                                                                        "required": ["action"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'action' argument.");

        string? action = actionEl.GetString();
        if (action is not ("add" or "update" or "list" or "complete" or "clear" or "search"))
            return Result.Failure($"Unknown action: '{action}'.");

        if (action == "search")
        {
            if (!args.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(q.GetString()))
                return Result.Failure("'query' required for search action.");
        }

        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string action = args.GetProperty("action").GetString()!;
        var todos = TodoWritePlugin.TodosBySession.GetOrAdd(context.SessionId, _ => new List<TodoItem>());

        lock (todos)
        {
            switch (action)
            {
                case "add":
                    string content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()!
                        : "";
                    if (string.IsNullOrEmpty(content))
                        return Task.FromResult(ToolResult.Error("'content' required for add action."));

                    var item = new TodoItem(Guid.NewGuid().ToString("N"), content, TodoStatus.Pending);
                    todos.Add(item);
                    TodoWritePlugin.Store.Save();
                    return Task.FromResult(ToolResult.Success($"Added todo: {item.Id} — {content}", new { id = item.Id }));

                case "update":
                    string? updateId = args.TryGetProperty("id", out var uid) ? uid.GetString() : null;
                    string? newStatus = args.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()!
                        : null;
                    if (string.IsNullOrEmpty(updateId))
                        return Task.FromResult(ToolResult.Error("'id' required for update action."));

                    var toUpdate = todos.FirstOrDefault(t => t.Id == updateId);
                    if (toUpdate is null)
                        return Task.FromResult(ToolResult.Error($"Todo '{updateId}' not found."));

                    if (!string.IsNullOrEmpty(newStatus))
                    {
                        todos.Remove(toUpdate);
                        todos.Add(toUpdate with { Status = TodoWritePlugin.ParseStatus(newStatus) });
                        TodoWritePlugin.Store.Save();
                    }
                    return Task.FromResult(ToolResult.Success($"Updated todo: {updateId} → {newStatus}"));

                case "list":
                    if (todos.Count == 0)
                        return Task.FromResult(ToolResult.Success("No todos. Use action=add to create one."));

                    var sb = new StringBuilder();
                    sb.AppendLine($"Todos ({todos.Count}):");
                    foreach (var t in todos.OrderBy(t => t.Status))
                    {
                        string statusIcon = t.Status switch
                        {
                            TodoStatus.Pending => "[ ]",
                            TodoStatus.InProgress => "[~]",
                            TodoStatus.Completed => "[x]",
                            _ => "[?]"
                        };
                        sb.AppendLine($"  {statusIcon} {t.Id} — {t.Content}");
                    }
                    return Task.FromResult(ToolResult.Success(sb.ToString(), new { count = todos.Count }));

                case "search":
                    string query = args.GetProperty("query").GetString()!;
                    var hits = new List<(string Session, TodoItem Item)>();
                    foreach (var kv in TodoWritePlugin.TodosBySession.ToArray())
                    {
                        List<TodoItem> snapshot;
                        lock (kv.Value)
                        {
                            snapshot = kv.Value.ToList();
                        }
                        foreach (var t in snapshot)
                        {
                            if (t.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                                hits.Add((kv.Key, t));
                        }
                    }
                    if (hits.Count == 0)
                        return Task.FromResult(ToolResult.Success($"No todos match '{query}'."));
                    var ssb = new StringBuilder();
                    ssb.AppendLine($"Matches ({hits.Count}) for '{query}':");
                    foreach (var (session, t) in hits.OrderBy(h => h.Item.Status).Take(50))
                    {
                        string statusIcon = t.Status switch
                        {
                            TodoStatus.Pending => "[ ]",
                            TodoStatus.InProgress => "[~]",
                            TodoStatus.Completed => "[x]",
                            _ => "[?]"
                        };
                        ssb.AppendLine($"  {statusIcon} {t.Id} — {t.Content} (session {ShortSession(session)})");
                    }
                    return Task.FromResult(ToolResult.Success(ssb.ToString(), new { count = hits.Count }));

                case "complete":
                    string? completeId = args.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                    if (string.IsNullOrEmpty(completeId))
                        return Task.FromResult(ToolResult.Error("'id' required for complete action."));

                    var toComplete = todos.FirstOrDefault(t => t.Id == completeId);
                    if (toComplete is null)
                        return Task.FromResult(ToolResult.Error($"Todo '{completeId}' not found."));

                    todos.Remove(toComplete);
                    todos.Add(toComplete with { Status = TodoStatus.Completed });
                    TodoWritePlugin.Store.Save();
                    return Task.FromResult(ToolResult.Success($"Completed: {toComplete.Content}"));

                case "clear":
                    int cleared = todos.Count;
                    todos.Clear();
                    TodoWritePlugin.Store.Save();
                    return Task.FromResult(ToolResult.Success($"Cleared {cleared} todos."));

                default:
                    return Task.FromResult(ToolResult.Error($"Unknown action: {action}"));
            }
        }
    }

    private static string ShortSession(string session) =>
        session.Length <= 8 ? session : session.Substring(0, 8);
}

/// <summary>
///     Dockable <c>todo-list</c> panel: read-only text rows over the persisted
///     store snapshot. Side-effect free; returns strings only (CellForge flattens
///     anything else to a type name).
/// </summary>
public sealed class TodoListPanel : IPanelProvider
{
    public string Id => "todo-list";
    public string Title => "Todos";
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;
    public int DefaultSize => 30;

    public object? Build(PanelContext ctx)
    {
        int total = 0, pending = 0, active = 0, done = 0;
        var recent = new List<TodoItem>();
        foreach (var kv in TodoWritePlugin.TodosBySession.ToArray())
        {
            List<TodoItem> snapshot;
            lock (kv.Value)
            {
                snapshot = kv.Value.ToList();
            }
            foreach (var t in snapshot)
            {
                total++;
                switch (t.Status)
                {
                    case TodoStatus.Pending: pending++; break;
                    case TodoStatus.InProgress: active++; break;
                    case TodoStatus.Completed: done++; break;
                }
                if (recent.Count < 10)
                    recent.Add(t);
            }
        }

        var rows = new List<string>
        {
            "Todos",
            $"  total {total}  pending {pending}  active {active}  done {done}",
        };
        if (recent.Count == 0)
        {
            rows.Add("  No todos. Add one with the todo tool.");
        }
        else
        {
            rows.Add(string.Empty);
            foreach (var t in recent.OrderBy(t => t.Status).Take(10))
            {
                string icon = t.Status switch
                {
                    TodoStatus.Pending => "[ ]",
                    TodoStatus.InProgress => "[~]",
                    TodoStatus.Completed => "[x]",
                    _ => "[?]"
                };
                string content = t.Content.Length > 40 ? t.Content.Substring(0, 40) + "…" : t.Content;
                rows.Add($"  {icon} {content}");
            }
        }

        int width = Math.Max(ctx.Width, 1);
        int height = Math.Max(ctx.Height, 1);
        var clipped = new List<string>(Math.Min(rows.Count, height));
        for (int i = 0; i < rows.Count && clipped.Count < height; i++)
        {
            string row = rows[i];
            clipped.Add(row.Length > width ? row.Substring(0, width) : row);
        }
        return clipped;
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;
}

internal sealed record PersistedTodo(string SessionId, string Id, string Content, int Status);

public sealed record TodoItem(string Id, string Content, TodoStatus Status);

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed
}
