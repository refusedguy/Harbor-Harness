using System.Collections.Immutable;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     Immutable UI state for the sessions sidebar / list view.
/// </summary>
/// <remarks>
///     <para>
///         Produced only by <see cref="SessionsReducer" /> — never mutated inside a
///         renderer. Renderers project this into their framework-specific session list
///         widgets (Avalonia, WPF, Blazor, SpectreTui).
///     </para>
///     <para>
///         Designed for NativeAOT and zero-reflection: all members are value types
///         or <see cref="ImmutableArray{T}" />. No <see cref="List{T}" />, no
///         reflection-based binding.
///     </para>
/// </remarks>
public sealed record SessionsViewState
{
    /// <summary>The full list of known sessions, oldest first.</summary>
    public ImmutableArray<SessionInfo> Sessions { get; init; } = ImmutableArray<SessionInfo>.Empty;

    /// <summary>Id of the currently active session, or null if none.</summary>
    public SessionId? ActiveSessionId { get; init; }

    /// <summary>Whether a session list refresh is in progress.</summary>
    public bool IsLoading { get; init; }
}

/// <summary>
///     One immutable session entry in the sessions list.
/// </summary>
/// <param name="SessionId">Stable unique identifier of the session.</param>
/// <param name="Title">Human-readable session title.</param>
/// <param name="CreatedAt">UTC timestamp when the session was created.</param>
/// <param name="LastActivityAt">UTC timestamp of the last activity in the session.</param>
/// <param name="Status">Current status: "active", "archived", etc.</param>
public sealed record SessionInfo(
    SessionId SessionId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string Status);
