using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One immutable session entry in the sessions list.
/// </summary>
/// <remarks>
///     <para>
///         Moved here from <c>SessionsViewState.cs</c> by #597. That file declared
///         this type alongside the producer-less <c>SessionsViewState</c> record,
///         which meant deleting the record without taking a live type down with it
///         required splitting the file first. This type is on the live read path:
///         <see cref="ChatDomainState.Sessions" /> holds it,
///         <c>PanelRows.SessionRows</c> projects it, the CellForge sidebar reads
///         it, and the CLI's <c>SessionSwitchManager</c> constructs it.
///     </para>
///     <para>
///         A file that holds both a dead record and a live type is the shape a
///         path-based guard cannot grade: exempting the file leaves the dead
///         record ungoverned, and banning it takes the live type with it.
///     </para>
/// </remarks>
/// <param name="SessionId">Stable unique identifier of the session.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="CreatedAt">UTC timestamp when the session was created.</param>
/// <param name="LastActivityAt">UTC timestamp of the last activity in the session.</param>
/// <param name="Status">Current status: "active", "archived", etc.</param>
/// <param name="IsSubagent">Whether this is an isolated sub-agent run (hidden from the jump palette).</param>
public sealed record SessionInfo(
    SessionId SessionId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string Status,
    bool IsSubagent = false);