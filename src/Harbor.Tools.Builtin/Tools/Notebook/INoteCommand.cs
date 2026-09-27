namespace Harbor.Tools.Builtin;

/// <summary>
///     One notebook action handler. The requirement flags drive
///     <see cref="NotebookTool" /> validation, so a 6th action only adds a
///     new implementation plus a registry entry — no switch edits.
/// </summary>
internal interface INoteCommand
{
    /// <summary>Which action this command handles.</summary>
    NoteAction Action { get; }

    /// <summary>Whether the action requires a non-empty <c>key</c>.</summary>
    bool RequiresKey { get; }

    /// <summary>Whether the action requires a <c>content</c> string.</summary>
    bool RequiresContent { get; }

    /// <summary>
    ///     Execute against the loaded notes in <paramref name="invocation" />.
    /// </summary>
    Task<ToolResult> ExecuteAsync(NoteInvocation invocation, CancellationToken ct);
}
