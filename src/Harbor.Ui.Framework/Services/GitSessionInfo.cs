namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Git status snapshot for a session working directory.
/// </summary>
public sealed record GitSessionInfo(string? Branch, bool IsDirty, int DirtyCount, string? LastCommit)
{
    /// <summary>Empty / not-a-repo sentinel.</summary>
    public static GitSessionInfo Empty { get; } = new(null, false, 0, null);
}
