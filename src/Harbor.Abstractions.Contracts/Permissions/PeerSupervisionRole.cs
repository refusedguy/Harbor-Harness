namespace Harbor.Abstractions.Permissions;

/// <summary>
///     Which leg of cross-session peer supervision (issue #165) a tool plays.
///     Declared per tool, on <see cref="ToolSafetyDeclaration.PeerSupervision" />.
/// </summary>
/// <remarks>
///     <para>
///         The peer-supervision recipe in the system prompt is a PROTOCOL, not a
///         pair of names: sessions are peers, a neighbour may be stuck, and the
///         model is told to read it, form a verdict, and steer it only if it is
///         stuck. The two tools that make that possible happen to be called
///         <c>session_read</c> and <c>session_steer</c>, and the prompt builder
///         used to string-match exactly those two names — so renaming either tool
///         silently stopped the recipe rendering (#793).
///     </para>
///     <para>
///         The names are therefore the IDENTITY, not the reason. What the prompt
///         actually needs is the behaviour, and this enum is where that behaviour
///         is stated: <see cref="Observe" /> can look at a peer,
///         <see cref="Direct" /> can change what a peer is doing. Neither is
///         derivable from the axes the permission system already carries —
///         <c>session_read</c> is <c>Read</c> and <c>session_steer</c> is
///         <c>Write</c>, so no single <see cref="ToolCategory" /> selects them,
///         and both are <see cref="ToolSafetyProfile.Opaque" />, so the safety
///         axis cannot tell them apart either. That is also why the two
///         unclassified session-IPC tools must stay unclassified: classifying
///         them was a bug #595 records.
///     </para>
///     <para>
///         The recipe is composed from the legs that actually resolved, so a turn
///         that can only observe a peer is never told to steer one.
///     </para>
/// </remarks>
public enum PeerSupervisionRole
{
    /// <summary>
    ///     The tool takes no part in peer supervision — the default, and true of
    ///     every tool that does not reach another session.
    /// </summary>
    None = 0,

    /// <summary>
    ///     The tool can inspect a peer session: its status, outcome and recent
    ///     transcript. The reading half of the recipe.
    /// </summary>
    Observe = 1,

    /// <summary>
    ///     The tool can direct a peer session: a message, a redirect, or a
    ///     restart. The steering half of the recipe.
    /// </summary>
    Direct = 2
}
