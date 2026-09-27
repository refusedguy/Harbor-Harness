namespace Harbor.Tools.Builtin;

/// <summary>
///     A single persisted note: markdown content plus last-write timestamp.
/// </summary>
/// <param name="Content">Markdown content of the note.</param>
/// <param name="UpdatedAt">Last write time (UTC).</param>
internal sealed record NoteEntry(string Content, DateTimeOffset UpdatedAt);
