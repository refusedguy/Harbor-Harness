using System.Text;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Writes/overwrites a text file. Sequential. Creates parent dirs by default.
/// </summary>
public sealed class WriteTool : ITool
{

    private const int MaxContentChars = 5_000_000; // hard safety vs runaway model output
    private readonly ILogger<WriteTool> _logger;

    public WriteTool(ILogger<WriteTool> logger) { _logger = logger; }

    public ToolName Name => ToolName.Create("write");
    public string DisplayName => "Write";
    public string Description =>
        "Write content to a file (create or overwrite). Creates parent directories by default.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "write: Write file contents";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `write` to create new files or fully replace existing ones",
        "For small changes prefer `edit` after `read`",
        "Paths are absolute or relative to the working directory"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":       { "type": "string",  "description": "File path to write" },
                                                                          "content":    { "type": "string",  "description": "Full file content" },
                                                                          "createDirs": { "type": "boolean", "description": "Create parent dirs (default: true)" }
                                                                        },
                                                                        "required": ["path", "content"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("Missing or empty 'path'.");

        if (!args.TryGetProperty("content", out var contentEl)
            || contentEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'content'.");

        string content = contentEl.GetString() ?? string.Empty;
        if (content.Length > MaxContentChars)
            return Result.Failure(
                $"content too large ({content.Length} chars; max {MaxContentChars}).");

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string rawPath = args.GetProperty("path").GetString()!;

        if (SymlinkGuard.ContainsTraversalSegments(rawPath))
            return ToolResult.Error(
                "Path traversal ('..') is not allowed; provide a direct path without '..' segments.");

        string content = args.GetProperty("content").GetString() ?? string.Empty;
        bool createDirs = GetBoolDefaultTrue(args, "createDirs");

        if (content.Length > MaxContentChars)
            return ToolResult.Error(
                $"content too large ({content.Length} chars; max {MaxContentChars}).");

        var resolvedPath = ToolPaths.Resolve(rawPath);
        if (resolvedPath.IsFailure)
            return ToolResult.Error(resolvedPath.Error);
        string path = resolvedPath.Value;

        _logger.LogInformation("Writing: {Path} ({Chars} chars)", path, content.Length);

        var symlinkCheck = SymlinkGuard.Check(path);
        if (symlinkCheck.IsFailure)
            return ToolResult.Error(symlinkCheck.Error);

        if (Directory.Exists(path))
            return ToolResult.Error($"Path is a directory, not a file: {path}");

        bool existed = File.Exists(path);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            if (createDirs)
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir); // no-op if exists
            }
            else
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    return ToolResult.Error($"Parent directory does not exist: {dir}");
            }

            // Explicit UTF-8 no BOM — stable across runtimes

            await File.WriteAllTextAsync(path, content, encoding, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("write cancelled");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Error($"Access denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Error($"I/O error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write error: {Path}: {Error}", path, ex.Message);
            return ToolResult.Error($"Failed to write: {ex.Message}");
        }

        int byteCount = encoding.GetByteCount(content); // need encoding in scope — fix below
        // use Encoding.UTF8.GetByteCount(content) instead if encoding local only in try

        string action = existed ? "Overwrote" : "Created";
        int bytes = Encoding.UTF8.GetByteCount(content);

        return ToolResult.Success(
            $"{action} {path} ({content.Length} chars, {bytes} bytes)",
            new
            {
                path,
                chars = content.Length,
                bytes,
                created = !existed,
                overwritten = existed
            });
    }

    private static bool GetBoolDefaultTrue(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el))
            return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.True) return true;
        return true; // weird types → default
    }
}
