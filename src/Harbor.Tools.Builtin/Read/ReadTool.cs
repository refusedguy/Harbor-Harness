using System.Buffers;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
/// Reads text (optionally a line window) or reports image metadata.
/// Streams lines for offset/limit — never loads whole multi‑MB files just to slice.
/// </summary>
public sealed class ReadTool : ITool
{
    private readonly ILogger<ReadTool> _logger;

    public ReadTool(ILogger<ReadTool> logger) { _logger = logger; }

    private const int MaxChars = 100_000;
    private const int MaxFileBytes = 10 * 1024 * 1024; // 10 MiB hard cap for text
    private const int BinaryProbeBytes = 8192;
    private const int ImageTokenEstimate = 1200;
    private const int DefaultLineLimit = 2000; // full-file safety when no limit given

    public ToolName Name => ToolName.Create("read");
    public string DisplayName => "Read";
    public string Description =>
        "Read a file. Text: returns numbered lines (use offset/limit for large files). " +
        "Images: returns path/mime/size (vision payload depends on host wiring).";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "read: Read file contents (text or image)";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `read` before editing",
        "For large files always pass offset + limit",
        "offset is 1-indexed line number",
        "Binary non-image files are rejected"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":   { "type": "string",  "description": "Absolute or relative file path" },
                                                                          "offset": { "type": "integer", "description": "1-based start line (optional)" },
                                                                          "limit":  { "type": "integer", "description": "Max lines to return (optional)" }
                                                                        },
                                                                        "required": ["path"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("Missing or empty 'path'.");

        if (args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number
                                                     && o.TryGetInt32(out var offset) && offset < 1)
            return Result.Failure("'offset' must be >= 1.");

        if (args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
                                                    && l.TryGetInt32(out var limit) && limit < 1)
            return Result.Failure("'limit' must be >= 1.");

        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var path = args.GetProperty("path").GetString()!;
        int? offset = args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number
            ? o.GetInt32() : null;
        int? limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32() : null;

        _logger.LogDebug("Reading: {Path} (offset={Offset}, limit={Limit})", path, offset, limit);

        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
        else
            path = Path.GetFullPath(path);

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Cannot stat file: {ex.Message}");
        }

        var mime = DetectMimeType(path);

        // ── images ──────────────────────────────────────────────
        if (IsImageMimeType(mime))
        {
            if (info.Length > 20 * 1024 * 1024)
                return ToolResult.Error($"Image too large: {info.Length} bytes");

            // Host can attach bytes via metadata if vision is wired.
            // Don't dump base64 into the text channel by default (blows context).
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var b64 = Convert.ToBase64String(bytes);

            return ToolResult.Success(
                $"Image: {path} ({bytes.Length} bytes, {mime}). " +
                "Base64 is in metadata.imageBase64 for vision-capable hosts.",
                new
                {
                    path,
                    mimeType = mime,
                    sizeBytes = bytes.Length,
                    estimatedTokens = ImageTokenEstimate,
                    imageBase64 = b64
                });
        }

        // ── reject obvious binary ────────────────────────────────
        if (mime == "application/pdf" || mime == "application/octet-stream")
        {
            if (IsBinaryFile(path, info.Length))
            {
                _logger.LogWarning("Binary file detected: {Path}", path);
                return ToolResult.Error(
                    $"Refusing to read binary file: {path} ({mime}, {info.Length} bytes).");
            }
        }
        else if (info.Length > 0 && IsBinaryFile(path, info.Length))
        {
            _logger.LogWarning("Binary file detected: {Path}", path);
            return ToolResult.Error($"Refusing to read binary file: {path} ({info.Length} bytes).");
        }

        if (info.Length > MaxFileBytes)
        {
            return ToolResult.Error(
                $"File too large ({info.Length} bytes). Max {MaxFileBytes}. " +
                "Use offset/limit on a smaller window, or another tool.");
        }

        // ── text, streamed by line ───────────────────────────────
        var startLine = offset ?? 1; // 1-based
        var maxLines = limit ?? DefaultLineLimit; // always capped
        var skip = startLine - 1;

        var sb = new StringBuilder(Math.Min(MaxChars, 16 * 1024));
        var lineNo = 0;
        var taken = 0;
        var truncatedByLines = false;
        var truncatedByChars = false;
        var totalLinesSeen = 0;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var reader = new StreamReader(stream, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                totalLinesSeen++;
                lineNo++;

                if (lineNo <= skip)
                    continue;

                if (taken >= maxLines)
                {
                    truncatedByLines = true;
                    // If user gave explicit window, stop. Else keep counting? stop is fine.
                    break;
                }

                // Strip leftover CR if any weird file
                if (line.EndsWith('\r'))
                    line = line[..^1];

                var numbered = $"[{lineNo:D4}] {line}";

                if (sb.Length + numbered.Length + 1 > MaxChars)
                {
                    truncatedByChars = true;
                    break;
                }

                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(numbered);
                taken++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("read cancelled");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to read: {ex.Message}");
        }

        if (taken == 0 && skip > 0)
        {
            return ToolResult.Error(
                $"Offset {startLine} is beyond file end (~{totalLinesSeen} lines read).");
        }

        if (taken == 0)
            return ToolResult.Success($"[empty file] {path}", new { path, mimeType = mime, sizeBytes = info.Length });

        if (truncatedByLines || truncatedByChars)
        {
            sb.Append("\n\n… truncated");
            if (truncatedByLines)
                sb.Append($" (limit {maxLines} lines; pass offset/limit to continue)");
            if (truncatedByChars)
                sb.Append($" (hit {MaxChars} char cap)");
        }

        _logger.LogDebug("Read complete: {Lines} lines, Truncated={Truncated}", taken, truncatedByLines || truncatedByChars);

        return ToolResult.Success(
            sb.ToString(),
            new
            {
                path,
                mimeType = mime,
                sizeBytes = info.Length,
                startLine,
                linesReturned = taken,
                truncated = truncatedByLines || truncatedByChars
            });
    }

    private static bool IsBinaryFile(string path, long length)
    {
        try
        {
            var toRead = (int)Math.Min(length, BinaryProbeBytes);
            if (toRead == 0) return false;

            var buffer = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    bufferSize: toRead, FileOptions.SequentialScan);
                var n = fs.Read(buffer, 0, toRead);
                return buffer.AsSpan(0, n).IndexOf((byte)0) >= 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return true;
        }
    }

    private static string DetectMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".log" or ".csv" or ".tsv" => "text/plain",
            ".json" or ".jsonc" or ".jsonl" => "application/json",
            ".xml" or ".csproj" or ".fsproj" or ".sln" or ".props" or ".targets" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" or ".scss" or ".less" => "text/css",
            ".js" or ".mjs" or ".cjs" => "application/javascript",
            ".ts" or ".tsx" or ".jsx" => "application/typescript",
            ".cs" => "text/x-csharp",
            ".fs" or ".fsx" => "text/x-fsharp",
            ".py" => "text/x-python",
            ".go" => "text/x-go",
            ".rs" => "text/x-rust",
            ".java" => "text/x-java",
            ".kt" => "text/x-kotlin",
            ".c" or ".h" => "text/x-c",
            ".cpp" or ".cc" or ".hpp" or ".cxx" => "text/x-cpp",
            ".sql" => "application/sql",
            ".yaml" or ".yml" => "application/yaml",
            ".toml" => "application/toml",
            ".sh" or ".bash" or ".zsh" or ".ps1" or ".bat" or ".cmd" => "text/x-shellscript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static bool IsImageMimeType(string mime)
        => mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
           && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    // svg — text XML; often better as text. Toggle if you want vision for svg.
}
