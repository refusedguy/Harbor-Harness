using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Sessions;

/// <summary>
///     Default <see cref="ICompactionView" />: a stateless forwarder over the
///     <see cref="CompactionPolicy" /> pure statics (#1612).
/// </summary>
/// <remarks>
///     <para>
///         No logic lives here on purpose — the truncation and view rules stay
///         pure statics, graded by <c>CompactionPolicyInvariantTests</c>. This
///         class exists so the turn pipeline (<c>TurnRunner</c>,
///         <c>CompactionBehavior</c>) is composed against the
///         <c>ICompactionView</c> contract instead of reaching across the
///         <c>Agents → Sessions</c> boundary for statics. Shared as
///         <see cref="Instance" />: there is no state to isolate per consumer.
///     </para>
/// </remarks>
public sealed class CompactionView : ICompactionView
{
    /// <summary>Shared instance — the forwarder holds no state.</summary>
    public static readonly CompactionView Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<AgentMessage> TruncateToFit(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = ICompactionView.DefaultReserveTokens) =>
        CompactionPolicy.TruncateToFit(messages, model, tokenTracker, reserveTokens);

    /// <inheritdoc />
    public IReadOnlyList<AgentMessage> TruncateToFitStrict(
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        ITokenTracker tokenTracker,
        int reserveTokens = ICompactionView.DefaultReserveTokens) =>
        CompactionPolicy.TruncateToFitStrict(messages, model, tokenTracker, reserveTokens);

    /// <inheritdoc />
    public IReadOnlyList<AgentMessage> MaterializeCompactedView(IReadOnlyList<AgentMessage> messages) =>
        CompactionPolicy.MaterializeCompactedView(messages);
}
