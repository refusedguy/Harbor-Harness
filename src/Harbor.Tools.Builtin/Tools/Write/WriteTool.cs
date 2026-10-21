using System.Text;
using AutoCtor;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Writes/overwrites a text file. Sequential. Creates parent dirs by default.
/// </summary>
[ToolInfo(
    "write",
    "Write",
    "Write content to a file (create or overwrite). Creates parent directories by default.",
    Glyph = "✚",
    ExecutionMode = ExecutionMode.Sequential,
    PromptSnippet = "write: Write file contents",
    PromptGuidelines = new[]
    {
        "Use `write` to create new files or fully replace existing ones",
        "For small changes prefer `edit` after `read`",
        "Paths are absolute or relative to the working directory",
        "Pass expectedVersion (the [file-version: …] token from your last read/write/edit) so an external change is rejected instead of overwritten"
    })]
[AutoConstruct]
[ToolSchema]
[ToolParam("path", "string", Description = "File path to write", Required = true)]
[ToolParam("content", "string", Description = "Full file content", Required = true)]
[ToolParam("createDirs", "boolean", Description = "Create parent dirs (default: true)")]
[ToolParam("expectedVersion", "string", Description = "Version of the file as you last read it (the [file-version: sha256:…] token). The write is rejected when the file changed on disk since")]
public sealed partial class WriteTool : ITool
{

    private const int MaxContentChars = 5_000_000; // hard safety vs runaway model output
    private readonly ILogger<WriteTool> _logger;

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Path();

    public Result ValidateArguments(JsonElement args)
    {
        Result path = JsonArgValidator.RequiredPath(args);
        if (path.IsFailure)
            return path;

        Result<string> contentResult = JsonArgValidator.RequiredStringPresent(
            args, "content", "Missing required argument 'content'.");
        if (contentResult.IsFailure)
            return contentResult;

        string content = contentResult.Value;
        if (content.Length > MaxContentChars)
            return Result.Failure(
                $"content too large ({content.Length} chars; max {MaxContentChars}).");

        // #1757: the compare-and-swap base. Validated here so a malformed
        // token fails before the file is touched, not after. The argument is
        // optional — absent means "no freshness constraint", which is the
        // pre-#1757 behaviour.
        string? rawExpected = JsonArgs.GetString(args, "expectedVersion");
        if (!string.IsNullOrWhiteSpace(rawExpected))
        {
            Result<string> expectedVersion = FileVersion.NormalizeExpected(rawExpected);
            if (expectedVersion.IsFailure)
                return expectedVersion.ConvertFailure();
        }

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

        // S2 (#376): relative paths resolve against the context working
        // directory (the isolated worktree for sub-agent runs).
        string baseDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory!
            : Environment.CurrentDirectory;
        var resolvedPath = ToolPaths.ResolveAgainst(baseDir, rawPath)
            .Bind(p => SymlinkGuard.Check(p, baseDir).Map(() => p));
        if (resolvedPath.IsFailure)
            return ToolResult.Error(resolvedPath.Error);
        string path = resolvedPath.Value;

        WriteToolLog.Writing(_logger, path, content.Length);

        if (Directory.Exists(path))
            return ToolResult.Error($"Path is a directory, not a file: {path}");

        // #1757: compare-and-swap. When the caller states which version of the
        // file it observed, a file that moved on in the meantime is rejected
        // instead of silently overwritten. The digest is taken BEFORE the
        // write, so a mismatch is reported against the bytes that are on disk
        // right now.
        string? expectedVersion = JsonArgs.GetString(args, "expectedVersion");
        if (!string.IsNullOrWhiteSpace(expectedVersion))
        {
            string? currentDigest;
            try
            {
                currentDigest = await FileVersion.ComputeDigestAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Error(
                    $"Failed to read {path} for the version check: {ex.Message}");
            }

            Result cas = FileVersion.Check(path, expectedVersion, currentDigest);
            if (cas.IsFailure)
            {
                WriteToolLog.VersionMismatch(_logger, path, cas.Error);
                return ToolResult.Error(cas.Error);
            }
        }

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
            WriteToolLog.WriteError(_logger, ex, path, ex.Message);
            return ToolResult.Error($"Failed to write: {ex.Message}");
        }

        int byteCount = encoding.GetByteCount(content); // need encoding in scope — fix below
        // use Encoding.UTF8.GetByteCount(content) instead if encoding local only in try

        string action = existed ? "Overwrote" : "Created";
        int bytes = Encoding.UTF8.GetByteCount(content);

        // #1757: the version of what was just written, so the model can keep
        // writing against the file it now knows without re-reading it.
        string digest = FileVersion.ComputeDigest(content.AsSpan(), encoding);

        return ToolResult.Success(
            $"{action} {path} ({content.Length} chars, {bytes} bytes) {FileVersion.Annotate(digest)}",
            new
            {
                path,
                chars = content.Length,
                bytes,
                created = !existed,
                overwritten = existed,
                fileVersion = FileVersion.Token(digest)
            });
    }

    private static bool GetBoolDefaultTrue(JsonElement args, string name)
        => JsonArgs.GetBoolOrNull(args, name) ?? true;
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="WriteTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class WriteToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Writing: {Path} ({Chars} chars)")]
    public static partial void Writing(ILogger logger, string path, int chars);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Write error: {Path}: {Error}")]
    public static partial void WriteError(ILogger logger, Exception ex, string path, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Write rejected — file version changed: {Path}: {Reason}")]
    public static partial void VersionMismatch(ILogger logger, string path, string reason);
}
