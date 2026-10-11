using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook <c>set</c>: replace (or create) a note, enforcing the
///     per-session quota, then persist.
/// </summary>
internal sealed class SetNoteCommand : INoteCommand
{
    /// <inheritdoc />
    public NoteAction Action => NoteAction.Set;

    /// <inheritdoc />
    public bool RequiresKey => true;

    /// <inheritdoc />
    public bool RequiresContent => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(NoteInvocation invocation, CancellationToken ct)
    {
        var notes = invocation.Notes;
        string? key = invocation.Key;
        string? content = invocation.Content;

        if (notes.Count >= NoteLimits.MaxNotesPerSession && !notes.ContainsKey(key!))
            return ToolResult.Error($"Too many notes (max {NoteLimits.MaxNotesPerSession}).");
        notes[key!] = new NoteEntry(content!, DateTimeOffset.UtcNow);
        SetNoteCommandLog.NoteSet(invocation.Logger, key, content!.Length, invocation.SessionId);

        Result saved = await invocation.Store
            .SaveAsync(invocation.NotesPath, notes, ct)
            .ConfigureAwait(false);
        return saved.IsSuccess
            ? ToolResult.Success(
                $"Set note '{key}' ({content.Length} chars).",
                new { key, chars = content.Length, totalNotes = notes.Count })
            : ToolResult.Error(saved.Error);
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="SetNoteCommand" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SetNoteCommandLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Notebook set {Key} ({Chars} chars) for {Session}")]
    public static partial void NoteSet(ILogger logger, string? key, int chars, string session);
}
