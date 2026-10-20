namespace Harbor.Application.Observability;

/// <summary>
///     What the harness saw happen to one workspace path during a turn
///     (issue #1745).
/// </summary>
/// <remarks>
///     <para>
///         Every value here is a verdict the harness reached from the
///         filesystem, never from what the agent said. The agent's prose is
///         not an input to any of these decisions: a turn whose assistant
///         message claims three edited files and whose tools touched none
///         reports <see cref="Unchanged" />, not three edits.
///     </para>
/// </remarks>
public enum WorkspaceChangeKind
{
    /// <summary>The path did not exist before the turn and exists after it.</summary>
    Created = 0,

    /// <summary>The path existed before the turn and its bytes differ now.</summary>
    Modified = 1,

    /// <summary>The path existed before the turn and is gone now.</summary>
    Deleted = 2,

    /// <summary>
    ///     The path was declared by a mutating tool and its bytes are
    ///     identical to what they were before the turn. This is the verdict
    ///     for «the agent said it wrote the file and the file says otherwise».
    /// </summary>
    Unchanged = 3,

    /// <summary>
    ///     The harness could not read the path, so it says nothing about its
    ///     content: it is outside the workspace, unreadable, or too large to
    ///     hash. An honest marker beats a silent one.
    /// </summary>
    Unverified = 4,
}

/// <summary>
///     One workspace path the harness watched during a turn, and the verdict it
///     reached.
/// </summary>
/// <param name="Path">
///     The path, relative to the workspace root when it is inside it.
/// </param>
/// <param name="Kind">What the harness saw.</param>
/// <param name="ToolName">
///     The tool that declared the path. Empty when the path came from a call
///     the harness could not attribute to a tool.
/// </param>
public sealed record WorkspaceChange(string Path, WorkspaceChangeKind Kind, string ToolName);

/// <summary>
///     What one turn changed in the workspace, as the harness saw it
///     (issue #1745).
/// </summary>
/// <param name="Sequence">
///     A monotonically increasing stamp, so a consumer can ask for the reports
///     it has not seen yet without comparing turn indices across sessions.
/// </param>
/// <param name="TurnIndex">The turn the report belongs to.</param>
/// <param name="SessionId">The session the turn ran in, when the emitter named one.</param>
/// <param name="Files">
///     Every path a mutating tool declared during the turn, with the verdict
///     the harness reached. Paths whose bytes did not move are in here too:
///     dropping them would hide the one case the oracle exists for.
/// </param>
/// <param name="UnobservedTools">
///     The tools that ran in the turn and whose effect on the workspace the
///     harness cannot see through — <c>bash</c>, <c>task</c>, an MCP server, a
///     plugin tool. A turn with a row here is INCOMPLETE: the report says what
///     it saw, not that nothing else changed.
/// </param>
public sealed record TurnChangeReport(
    long Sequence,
    int TurnIndex,
    string? SessionId,
    IReadOnlyList<WorkspaceChange> Files,
    IReadOnlyList<string> UnobservedTools)
{
    /// <summary>
    ///     True when every tool that ran in the turn declared its paths, so the
    ///     report covers the whole workspace. False means the harness is
    ///     reporting what it saw and saying so.
    /// </summary>
    public bool IsComplete => UnobservedTools.Count == 0;
}
