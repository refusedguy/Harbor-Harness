// FileHistoryToolDecorator.cs — issue #3107, atomic slice 3.
//
// FileHistoryTracker (and its blob store + path guard) had zero production
// callers. This decorator is that caller: applied to the write and edit tools
// at the composition root, it captures the current bytes of the target file
// BEFORE the inner tool writes, storing them as a content-addressed pre-image
// under the current session's `<workspace>/.rewind/<sessionId>` directory.
//
// A path the tracker rejects (absolute, parent traversal, symlink, or outside
// the workspace) is refused here, so no write reaches the disk. The decorator
// lives in the composition root because Harbor.Hosting is the only assembly
// allowed to reference both Harbor.Tools.Builtin and Harbor.Storage.Jsonl;
// the tools themselves stay free of an infrastructure dependency.

using System.Text.Json;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Hosting;

/// <summary>
///     Wraps a filesystem-writing tool so that its target's pre-image is
///     captured through <see cref="FileHistoryTracker.TrackBeforeWrite" />
///     before the tool is allowed to touch the file (#3107). All contract
///     members forward to the wrapped tool, so descriptors, the #557 safety
///     policy derivation and registry name resolution see nothing new.
/// </summary>
/// <remarks>
///     The tracker is created per call and owns no shared mutable state, so
///     the decorator preserves the wrapped tool's thread-safety guarantee.
///     Capture is active only when the context carries an explicit working
///     directory (the production case: <c>session.Session.Directory</c>);
///     a context without one degrades to a plain pass-through.
/// </remarks>
public sealed class FileHistoryToolDecorator : ITool
{
    private readonly ITool _preImageTarget;
    private readonly ILogger<FileHistoryToolDecorator> _preImageLog;

    /// <summary>Wrap <paramref name="target" /> with pre-image capture.</summary>
    public FileHistoryToolDecorator(ITool target, ILogger<FileHistoryToolDecorator> log)
    {
        _preImageTarget = target ?? throw new ArgumentNullException(nameof(target));
        _preImageLog = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The wrapped tool's name, used in capture diagnostics.</summary>
    private string TargetName() => _preImageTarget.Name.Value;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args) => _preImageTarget.ValidateArguments(args);

    /// <inheritdoc />
    public ToolName Name => _preImageTarget.Name;

    /// <inheritdoc />
    public string DisplayName => _preImageTarget.DisplayName;

    /// <inheritdoc />
    public string Glyph => _preImageTarget.Glyph;

    /// <inheritdoc />
    public string Description => _preImageTarget.Description;

    /// <inheritdoc />
    public JsonDocument ParameterSchema => _preImageTarget.ParameterSchema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => _preImageTarget.ExecutionMode;

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => _preImageTarget.SafetyProfile;

    /// <inheritdoc />
    public string? PromptSnippet => _preImageTarget.PromptSnippet;

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines => _preImageTarget.PromptGuidelines;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string? rejection = TryCapturePreImage(args, context);
        if (rejection is not null)
            return ToolResult.Error(rejection);

        return await _preImageTarget.ExecuteAsync(args, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Captures the target's pre-image. Returns a rejection reason when the
    ///     path is unsafe or capture failed (the caller then refuses the write),
    ///     and <see langword="null"/> when the write may proceed.
    /// </summary>
    private string? TryCapturePreImage(JsonElement args, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(context.WorkingDirectory)
            || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("path", out JsonElement rawPathElement)
            || rawPathElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string rawPath = rawPathElement.GetString()!;
        string workspaceRoot = Path.GetFullPath(context.WorkingDirectory!);
        var resolution = ToolPaths.ResolveAgainst(workspaceRoot, rawPath);
        if (resolution.IsFailure)
            return resolution.Error;
        string fullPath = resolution.Value;

        try
        {
            string relative = Path.GetRelativePath(workspaceRoot, fullPath);
            var blobs = new RewindBlobStore(Path.Combine(workspaceRoot, ".rewind", context.SessionId));
            var tracker = new FileHistoryTracker(workspaceRoot, blobs);
            FileHistoryTrackResult track = tracker.TrackBeforeWrite(relative);

            if (track.IsRejected)
            {
                FileHistoryToolDecoratorLog.FileHistoryRejected(_preImageLog, TargetName(), track.Reason!);
                return $"Pre-image capture refused the write: {track.Reason}";
            }

            return null;
        }
        catch (Exception ex)
        {
            // Fail closed: a write whose pre-image could not be stored must not
            // reach the disk, otherwise a rewind would have nothing to restore.
            FileHistoryToolDecoratorLog.FileHistoryCaptureFailed(_preImageLog, ex, TargetName());
            return $"Pre-image capture failed: {ex.Message}";
        }
    }
}

/// <summary>SG1-style logging surface for <see cref="FileHistoryToolDecorator" />.</summary>
internal static partial class FileHistoryToolDecoratorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Pre-image capture rejected a {Tool} write: {Reason}")]
    public static partial void FileHistoryRejected(ILogger logger, string tool, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Pre-image capture failed for {Tool}; the write was refused")]
    public static partial void FileHistoryCaptureFailed(ILogger logger, Exception ex, string tool);
}
