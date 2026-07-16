using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugin.TodoWrite;
/// <summary>
///     TodoWrite plugin — adds a `todo` tool for task management.
///     Demonstrates stateful plugin with in-memory todo list.
/// </summary>
public sealed class TodoWritePlugin : IToolPlugin
{

    internal static readonly ConcurrentDictionary<string, List<TodoItem>> TodosBySession = new();
    public string Name => "todowrite";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Todo list management for agents";

    public void Initialize(PluginContext context) => context.CreateLogger<TodoWritePlugin>().LogInformation("TodoWrite plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<TodoWriteTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class TodoWriteTool : ITool
{
    public ToolName Name => ToolName.Create("todo");
    public string DisplayName => "Todo";
    public string Description => "Manage a todo list for the current task. Supports add, update, list, complete, and clear operations. Todos persist across tool calls within the same session.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "todo: Manage task list (add/update/complete/list)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `todo` to track progress on multi-step tasks",
        "Add items at the start, mark in_progress when working on them, complete when done",
        "Helps maintain context across long tasks"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "action": {
                                                                            "type": "string",
                                                                            "enum": ["add", "update", "list", "complete", "clear"],
                                                                            "description": "Action to perform"
                                                                          },
                                                                          "content": { "type": "string", "description": "Todo content (for add)" },
                                                                          "id": { "type": "string", "description": "Todo ID (for update/complete)" },
                                                                          "status": {
                                                                            "type": "string",
                                                                            "enum": ["pending", "in_progress", "completed"],
                                                                            "description": "New status (for update)"
                                                                          }
                                                                        },
                                                                        "required": ["action"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'action' argument.");

        string? action = actionEl.GetString();
        if (action is not ("add" or "update" or "list" or "complete" or "clear"))
            return Result.Failure($"Unknown action: '{action}'.");

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

                    if (!string.IsNullOrEmpty(newStatus) && Enum.TryParse<TodoStatus>(newStatus, true, out var status))
                    {
                        todos.Remove(toUpdate);
                        todos.Add(toUpdate with { Status = status });
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

                case "complete":
                    string? completeId = args.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                    if (string.IsNullOrEmpty(completeId))
                        return Task.FromResult(ToolResult.Error("'id' required for complete action."));

                    var toComplete = todos.FirstOrDefault(t => t.Id == completeId);
                    if (toComplete is null)
                        return Task.FromResult(ToolResult.Error($"Todo '{completeId}' not found."));

                    todos.Remove(toComplete);
                    todos.Add(toComplete with { Status = TodoStatus.Completed });
                    return Task.FromResult(ToolResult.Success($"Completed: {toComplete.Content}"));

                case "clear":
                    int cleared = todos.Count;
                    todos.Clear();
                    return Task.FromResult(ToolResult.Success($"Cleared {cleared} todos."));

                default:
                    return Task.FromResult(ToolResult.Error($"Unknown action: {action}"));
            }
        }
    }
}

public sealed record TodoItem(string Id, string Content, TodoStatus Status);

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed
}
