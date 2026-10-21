using System.Text;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Reads back a tool output that was too large for the model's context
///     (issue #1748).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a separate tool and not the <c>read</c> tool.</b> A spilled
///         output lives in Harbor's private directory, OUTSIDE the workspace,
///         and the permission service downgrades an out-of-workspace read from
///         "allow" to "ask" — so pointing the model at the <c>read</c> tool
///         would ask the user for permission on every re-read, and a headless
///         run (no user to ask) would simply be denied. This tool is confined
///         to the current session's spill directory by the store, so it needs
///         no permission prompt and cannot be pointed at anything else.
///     </para>
///     <para>
///         <b>Why the window.</b> Reading a spilled output back in full would
///         re-create the very overflow the spill solved, so the read is paged
///         by line: the model asks for the window it needs and continues with
///         the offset the answer names.
///     </para>
/// </remarks>
[ToolInfo(
    "spill_read",
    "Spill Read",
    "Read back a tool output that did not fit in the context. The path is the one in the '[spilled: ...]' marker of the tool result.",
    Glyph = "⇩",
    PromptSnippet = "spill_read: read back a spilled tool output by its path",
    PromptGuidelines = new[]
    {
        "the path comes from the '[spilled: ...]' marker in the tool result",
        "the read is paged — continue with the offset the answer names"
    })]
[ToolSchema]
[ToolParam("path", "string", Description = "Path of the spilled output, from the '[spilled: ...]' marker", Required = true)]
[ToolParam("offset", "integer", Description = "1-based start line (optional)")]
[ToolParam("limit", "integer", Description = "Max lines to return (optional)")]
public sealed partial class SpillReadTool : ITool
{
    /// <summary>How many lines a read returns when the model asks for no window.</summary>
    private const int DefaultLineLimit = 2000;

    /// <summary>The hard cap on the lines one read may return.</summary>
    private const int MaxLineLimit = 20_000;

    /// <summary>The hard cap on the characters one read may return.</summary>
    private const int MaxChars = 100_000;

    private readonly IToolOutputSpillStore _spills;
    private readonly ILogger<SpillReadTool> _logger;

    /// <summary>
    ///     Construct the tool.
    /// </summary>
    /// <param name="spills">The spill area to read from.</param>
    /// <param name="logger">Logger for the failure trail.</param>
    public SpillReadTool(IToolOutputSpillStore spills, ILogger<SpillReadTool> logger)
    {
        _spills = spills;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Opaque, and deliberately so. The argument IS a path, but declaring it
    ///     as one would put the tool under the path-traversal guard, which
    ///     downgrades an out-of-workspace path from "allow" to "ask" — the
    ///     spill area is by definition outside the workspace, so every re-read
    ///     would ask the user, and a headless run would be denied. The
    ///     confinement this tool needs is the store's: it refuses any path that
    ///     is not inside the CURRENT session's spill directory.
    /// </remarks>
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> path = JsonArgValidator.RequiredString(
            args, "path", "Missing required argument 'path'.");
        if (path.IsFailure)
        {
            return path;
        }

        Result offset = JsonArgValidator.OptionalIntAtLeast(args, "offset", 1, "'offset' must be >= 1.");
        if (offset.IsFailure)
        {
            return offset;
        }

        return JsonArgValidator.OptionalIntInRange(
            args, "limit", 1, MaxLineLimit, $"'limit' must be an integer between 1 and {MaxLineLimit}.");
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string path = args.GetProperty("path").GetString()!;
        int offset = JsonArgs.GetInt(args, "offset") ?? 1;
        int limit = JsonArgs.GetInt(args, "limit") ?? DefaultLineLimit;
        limit = Math.Clamp(limit, 1, MaxLineLimit);

        Result<string> spilled = await _spills
            .ReadAsync(context.SessionId, path, cancellationToken)
            .ConfigureAwait(false);

        if (spilled.IsFailure)
        {
            SpillReadToolLog.ReadFailed(_logger, path, spilled.Error);
            return ToolResult.Error(spilled.Error);
        }

        string window = Window(spilled.Value, offset, limit, out int nextOffset, out bool more);
        var answer = new StringBuilder(window.Length + 128);
        answer.Append("[spilled output: ").Append(path).AppendLine("]");
        answer.Append(window);
        if (more)
        {
            answer.Append("… more lines — call spill_read again with offset=")
                .Append(nextOffset)
                .Append('.');
        }

        return ToolResult.Success(answer.ToString());
    }

    /// <summary>
    ///     The window of <paramref name="text" /> from <paramref name="startLine" />,
    ///     at most <paramref name="lineLimit" /> lines.
    /// </summary>
    /// <param name="text">The full spilled text.</param>
    /// <param name="startLine">The 1-based first line of the window.</param>
    /// <param name="lineLimit">The maximum number of lines.</param>
    /// <param name="nextOffset">The offset the next window starts at.</param>
    /// <param name="more">Whether lines remain after the window.</param>
    /// <returns>The window, newline-terminated.</returns>
    private static string Window(string text, int startLine, int lineLimit, out int nextOffset, out bool more)
    {
        var window = new StringBuilder(Math.Min(text.Length, 16 * 1024));
        int line = 0;
        int index = 0;
        int taken = 0;
        nextOffset = startLine;
        more = false;

        while (index <= text.Length)
        {
            int breakAt = text.IndexOf('\n', index);
            int lineEnd = breakAt < 0 ? text.Length : breakAt;
            line++;

            if (line >= startLine)
            {
                if (taken >= lineLimit)
                {
                    more = true;
                    break;
                }

                window.Append(text, index, lineEnd - index).Append('\n');
                taken++;
                nextOffset = line + 1;
            }

            if (breakAt < 0)
            {
                break;
            }

            index = breakAt + 1;
        }

        if (window.Length > MaxChars)
        {
            // A window of very long lines still has to fit the context: the
            // spill exists so the model can read a piece of a huge output, not
            // so it can take the whole thing back in one call.
            return string.Concat(
                window.ToString(0, MaxChars),
                $"\n…[window cut at {MaxChars} chars — narrow the window with offset/limit]");
        }

        return window.ToString();
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for the spill-read tool.
/// </summary>
internal static partial class SpillReadToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "spill_read: cannot read {Path}: {Error}")]
    public static partial void ReadFailed(ILogger logger, string path, string error);
}
