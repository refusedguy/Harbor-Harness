namespace Harbor.Tools.Builtin;

/// <summary>
///     The five notebook actions. Parsed exactly once by
///     <see cref="NotebookTool" />; validation requirements and dispatch both
///     derive from it, so an unknown action cannot reach a command.
/// </summary>
internal enum NoteAction
{
    Get,
    Set,
    Add,
    Clear,
    List,
}
