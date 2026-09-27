namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook <c>get</c>: return the full content of one note by key.
///     Read-only — never touches the store.
/// </summary>
internal sealed class GetNoteCommand : INoteCommand
{
    /// <inheritdoc />
    public NoteAction Action => NoteAction.Get;

    /// <inheritdoc />
    public bool RequiresKey => true;

    /// <inheritdoc />
    public bool RequiresContent => false;

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(NoteInvocation invocation, CancellationToken ct)
    {
        string? key = invocation.Key;
        if (!invocation.Notes.TryGetValue(key!, out var entry))
            return Task.FromResult(ToolResult.Error($"No note with key '{key}'."));
        return Task.FromResult(ToolResult.Success(
            $"# {key}\n\n{entry.Content}",
            new { key, content = entry.Content, updatedAt = entry.UpdatedAt }));
    }
}
