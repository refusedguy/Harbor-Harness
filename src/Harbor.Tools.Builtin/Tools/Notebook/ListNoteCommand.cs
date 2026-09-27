using Harbor.Abstractions.Extensions;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook <c>list</c>: return all keys with the first line of each
///     note. Read-only — never touches the store.
/// </summary>
internal sealed class ListNoteCommand : INoteCommand
{
    /// <inheritdoc />
    public NoteAction Action => NoteAction.List;

    /// <inheritdoc />
    public bool RequiresKey => false;

    /// <inheritdoc />
    public bool RequiresContent => false;

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(NoteInvocation invocation, CancellationToken ct)
    {
        var notes = invocation.Notes;
        if (notes.Count == 0)
            return Task.FromResult(ToolResult.Success("(no notes in this session)"));
        // #53 audit: cap the *initial* rent — the builder grows as
        // needed, but Rent(N * 64) for a large N would pre-size a huge
        // (possibly LOH) buffer up front. 128 * 64 = 8 KB initial max.
        using var sb = StringBuilderPool.Rent(Math.Min(notes.Count, 128) * 64);
        var b = sb.Builder;
        b.Append(notes.Count).Append(" note(s):");
        foreach (var kv in notes)
        {
            string preview = kv.Value.Content;
            int nl = preview.IndexOf('\n');
            if (nl >= 0) preview = preview[..nl];
            if (preview.Length > 80) preview = preview[..80] + "…";
            b.Append("\n  • ").Append(kv.Key).Append(" — ").Append(preview);
        }
        return Task.FromResult(ToolResult.Success(
            b.ToString(),
            new { count = notes.Count, keys = notes.Keys.ToArray() }));
    }
}
