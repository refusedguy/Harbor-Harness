using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook <c>add</c>: append content to a key's existing note (or
///     create it), enforcing the combined-length cap, then persist.
/// </summary>
internal sealed class AddNoteCommand : INoteCommand
{
    /// <inheritdoc />
    public NoteAction Action => NoteAction.Add;

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

        if (notes.TryGetValue(key!, out var existing))
        {
            string combined = existing.Content + "\n\n" + content;
            if (combined.Length > NoteLimits.MaxContentChars)
                return ToolResult.Error(
                    $"Combined content would exceed {NoteLimits.MaxContentChars} chars " +
                    $"(currently {existing.Content.Length}, adding {content!.Length}).");
            notes[key!] = existing with { Content = combined, UpdatedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            notes[key!] = new NoteEntry(content!, DateTimeOffset.UtcNow);
        }

        Result saved = await invocation.Store
            .SaveAsync(invocation.NotesPath, notes, ct)
            .ConfigureAwait(false);
        return saved.IsSuccess
            ? ToolResult.Success(
                $"Appended to note '{key}' (now {notes[key!].Content.Length} chars).",
                new { key, chars = notes[key!].Content.Length, totalNotes = notes.Count })
            : ToolResult.Error(saved.Error);
    }
}
