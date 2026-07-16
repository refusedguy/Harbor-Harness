namespace Harbor.Tools.Builtin;
/// <summary>
///     Reads file contents. Supports text and image files.
/// </summary>
public sealed class ReadTool : ITool
{
    private const int MaxChars = 100_000;
    private const int ImageTokenEstimate = 1200;

    public ToolName Name => ToolName.Create("read");
    public string DisplayName => "Read";
    public string Description => "Read contents of a file. Supports text and image files. For text files, returns content as string. For images, returns vision-compatible data.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "read: Read file contents (text or image)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `read` to examine file contents before editing",
        "For binary files (images), `read` returns vision-compatible data",
        "Use `offset` and `limit` for large files to read only the relevant section"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path": { "type": "string", "description": "Absolute or relative file path to read" },
                                                                          "offset": { "type": "integer", "description": "Line number to start reading from (1-indexed). Optional." },
                                                                          "limit": { "type": "integer", "description": "Maximum number of lines to read. Optional." }
                                                                        },
                                                                        "required": ["path"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'path'.");

        if (string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("'path' cannot be empty.");

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string path = args.GetProperty("path").GetString()!;
        int? offset = args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null;
        int? limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Environment.CurrentDirectory, path);

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        string mimeType = DetectMimeType(path);

        // Image file
        if (IsImageMimeType(mimeType))
        {
            byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return ToolResult.Success($"Image: {path} ({data.Length} bytes, {mimeType})", new { path, mimeType, sizeBytes = data.Length, estimatedTokens = ImageTokenEstimate });
        }

        // Text file
        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        if (offset.HasValue || limit.HasValue)
        {
            string[] lines = content.Split('\n');
            int start = (offset ?? 1) - 1;
            int count = limit ?? lines.Length - start;

            if (start < 0) start = 0;
            if (start >= lines.Length)
                return ToolResult.Error($"Offset {offset} is beyond file ({lines.Length} lines).");

            var selected = lines.Skip(start).Take(count);
            var numbered = selected.Select((line, i) => $"[{start + i + 1:D4}] {line}");
            content = string.Join('\n', numbered);
        }
        else
        {
            // Add line numbers for full file
            string[] lines = content.Split('\n');
            var numbered = lines.Select((line, i) => $"[{i + 1:D4}] {line}");
            content = string.Join('\n', numbered);
        }

        if (content.Length > MaxChars)
        {
            content = content[..MaxChars] + $"\n\n... truncated ({content.Length - MaxChars} more chars)";
        }

        return ToolResult.Success(content, new { path, mimeType, sizeBytes = content.Length });
    }

    private static string DetectMimeType(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".log" => "text/plain",
            ".json" or ".jsonc" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "application/javascript",
            ".ts" => "application/typescript",
            ".cs" => "text/csharp",
            ".fs" => "text/fsharp",
            ".vb" => "text/vb",
            ".py" => "text/x-python",
            ".go" => "text/x-go",
            ".rs" => "text/x-rust",
            ".java" => "text/x-java",
            ".kt" => "text/x-kotlin",
            ".swift" => "text/x-swift",
            ".c" or ".h" => "text/x-c",
            ".cpp" or ".cc" or ".hpp" => "text/x-cpp",
            ".sql" => "application/sql",
            ".yaml" or ".yml" => "application/yaml",
            ".toml" => "application/toml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static bool IsImageMimeType(string mime) => mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
