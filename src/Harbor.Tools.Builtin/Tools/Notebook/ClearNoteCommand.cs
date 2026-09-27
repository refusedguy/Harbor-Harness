using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook <c>clear</c>: remove a single key — or every note when no
///     key is given — then persist.
/// </summary>
internal sealed class ClearNoteCommand : INoteCommand
{
    /// <inheritdoc />
    public NoteAction Action => NoteAction.Clear;

    /// <inheritdoc />
    public bool RequiresKey => true;

    /// <inheritdoc />
    public bool RequiresContent => false;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(NoteInvocation invocation, CancellationToken ct)
    {
        var notes = invocation.Notes;
        string? key = invocation.Key;

        if (key is null)
        {
            int removed = notes.Count;
            notes.Clear();
            Result cleared = await invocation.Store
                .SaveAsync(invocation.NotesPath, notes, ct)
                .ConfigureAwait(false);
            return cleared.IsSuccess
                ? ToolResult.Success($"Cleared {removed} note(s).", new { removed })
                : ToolResult.Error(cleared.Error);
        }
        if (!notes.Remove(key))
            return ToolResult.Error($"No note with key '{key}'.");
        Result saved = await invocation.Store
            .SaveAsync(invocation.NotesPath, notes, ct)
            .ConfigureAwait(false);
        return saved.IsSuccess
            ? ToolResult.Success($"Cleared note '{key}'.", new { key, remaining = notes.Count })
            : ToolResult.Error(saved.Error);
    }
}
