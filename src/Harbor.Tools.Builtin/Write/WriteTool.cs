namespace Harbor.Tools.Builtin;
/// <summary>
///     Writes content to a file. Creates parent directories if needed.
/// </summary>
public sealed class WriteTool : ITool
{
    public ToolName Name => ToolName.Create("write");
    public string DisplayName => "Write";
    public string Description => "Write content to a file. Creates the file if it doesn't exist. Overwrites if it exists. Creates parent directories if needed.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "write: Write file contents";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `write` to create new files or replace existing ones",
        "Always read a file first if you only need to make small changes — prefer `edit`",
        "Specify absolute paths or paths relative to the working directory"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path": { "type": "string", "description": "File path to write to" },
                                                                          "content": { "type": "string", "description": "File content to write" },
                                                                          "createDirs": { "type": "boolean", "description": "Create parent directories if they don't exist (default: true)" }
                                                                        },
                                                                        "required": ["path", "content"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'path'.");
        if (!args.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'content'.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string path = args.GetProperty("path").GetString()!;
        string content = args.GetProperty("content").GetString()!;
        bool createDirs = !args.TryGetProperty("createDirs", out var cd) || cd.ValueKind != JsonValueKind.False || cd.GetBoolean();

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Environment.CurrentDirectory, path);

        if (createDirs)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        return ToolResult.Success($"Wrote {content.Length} chars to {path}", new { path, bytes = content.Length });
    }
}
