using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Persistent per-session markdown notes. The agent can stash small bits of context
///     (file paths, decisions, intermediate findings) and pull them back later. Notes
///     are stored as JSON in <c>~/.harbor/notes/&lt;sessionId&gt;.json</c> and can be
///     surfaced into the next turn's system prompt by the host.
/// </summary>
public sealed class NotebookTool : ITool
{
    private readonly ILogger<NotebookTool> _logger;
    private readonly NoteStore _store;
    private readonly FrozenDictionary<NoteAction, INoteCommand> _commands;

    /// <summary>
    ///     Construct a <see cref="NotebookTool" /> rooted at <c>~/.harbor/notes</c>.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public NotebookTool(ILogger<NotebookTool> logger) : this(logger, NoteStore.GetDefaultNotesRoot())
    {
    }

    /// <summary>
    ///     Construct a <see cref="NotebookTool" /> with a custom root directory.
    ///     Used in tests to point at a temp directory.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="notesRoot">Directory where per-session note JSON files live.</param>
    public NotebookTool(ILogger<NotebookTool> logger, string notesRoot)
    {
        _logger = logger;
        _store = new NoteStore(notesRoot);
        _commands = new INoteCommand[]
        {
            new GetNoteCommand(),
            new SetNoteCommand(),
            new AddNoteCommand(),
            new ClearNoteCommand(),
            new ListNoteCommand(),
        }.ToFrozenDictionary(c => c.Action);
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("notebook");

    /// <inheritdoc />
    public string DisplayName => "Notebook";

    /// <inheritdoc />
    public string Description =>
        "Persistent markdown notes keyed by string. Actions: get/set/add/clear/list. " +
        "Stored per session at ~/.harbor/notes/<sessionId>.json. " +
        "Use for decisions, file lists, intermediate findings across long tasks.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "notebook: Persistent per-session notes (get/set/add/clear/list)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `notebook` to remember things across many turns — file lists, decisions, TODOs",
        "set replaces a key; add appends to a key's existing content",
        "list returns all keys with the first line of each note",
        "clear removes a single key (or all if no key given)",
        "Notes are scoped to the current session id — they don't leak across sessions"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "action":  { "type": "string", "description": "One of: get | set | add | clear | list" },
                                                                          "key":     { "type": "string", "description": "Note key (required for get/set/add/clear)" },
                                                                          "content": { "type": "string", "description": "Note content (required for set; appended for add)" }
                                                                        },
                                                                        "required": ["action"]
                                                                      }
                                                                      """);

    /// <summary>
    ///     ROP-A Z1 п.9: the action string parses into an enum exactly once —
    ///     validation, dispatch and the per-command requirement matrix below
    ///     all derive from it, so an unknown action cannot reach a command.
    /// </summary>
    private static NoteAction? ParseAction(string raw) => raw.ToLowerInvariant() switch
    {
        "get" => NoteAction.Get,
        "set" => NoteAction.Set,
        "add" => NoteAction.Add,
        "clear" => NoteAction.Clear,
        "list" => NoteAction.List,
        _ => null
    };

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> actionResult = JsonArgValidator.RequiredString(args, "action", "Missing or empty 'action'.");
        if (actionResult.IsFailure)
            return actionResult;

        string action = actionResult.Value;
        var parsed = ParseAction(action);
        if (parsed is null)
            return Result.Failure($"Unknown action '{action}'. Valid: get, set, add, clear, list.");

        // The requirement matrix lives on the commands (RequiresKey /
        // RequiresContent), so a new action only adds a command class —
        // this method never grows another branch.
        INoteCommand command = _commands[parsed.Value];

        if (command.RequiresKey)
        {
            Result<string> keyResult = JsonArgValidator.RequiredString(
                args, "key", $"Action '{action}' requires non-empty 'key'.");
            if (keyResult.IsFailure)
                return keyResult;
            if (keyResult.Value.Length > NoteLimits.MaxKeyChars)
                return Result.Failure($"'key' too long (max {NoteLimits.MaxKeyChars} chars).");
        }

        if (command.RequiresContent)
        {
            Result<string> contentResult = JsonArgValidator.RequiredStringPresent(
                args, "content", $"Action '{action}' requires 'content' string.");
            if (contentResult.IsFailure)
                return contentResult;
            if (contentResult.Value.Length > NoteLimits.MaxContentChars)
                return Result.Failure($"'content' too long (max {NoteLimits.MaxContentChars} chars).");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Validation already pinned the action and its required fields
        // (fail-closed dispatcher runs ValidateArguments first), so the
        // dispatch below is exhaustive over the enum and needs no null re-checks.
        var action = ParseAction(args.GetProperty("action").GetString()!) ?? NoteAction.List;
        string? key = JsonArgs.GetString(args, "key");
        string? content = JsonArgs.GetString(args, "content");

        string sessionId = NoteStore.SanitizeSessionId(context.SessionId);
        string path = _store.ResolvePath(context.SessionId);

        // ROP-A Z1 п.10: Load and Save are guarded symmetrically now — a write
        // failure surfaces as a tool error instead of escaping the contract.
        Result<Dictionary<string, NoteEntry>> loaded = await _store
            .LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsFailure)
            return ToolResult.Error(loaded.Error);
        Dictionary<string, NoteEntry> notes = loaded.Value;

        if (!_commands.TryGetValue(action, out var command))
            // Unreachable: ParseAction admits only the five known actions.
            return ToolResult.Error("Unknown notebook action.");

        var invocation = new NoteInvocation(notes, key, content, sessionId, path, _store, _logger);
        return await command.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
    }
}
