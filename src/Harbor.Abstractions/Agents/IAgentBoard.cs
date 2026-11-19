// #3384: the seam through which the agent loop publishes agent messages to a
// shared board. The port lives in the Domain layer so Application (AgentLoop)
// can post without referencing the Infrastructure assembly that owns the
// concrete board; the companion message kinds/states stay behind the board's
// implementation, reached through this port.
namespace Harbor.Abstractions.Agents;

/// <summary>
///     Port for publishing agent messages to a shared, in-memory board (#3384).
///     The concrete board lives in Infrastructure; the loop knows only this
///     contract, so the layering direction stays Application → Domain.
/// </summary>
public interface IAgentBoard
{
    /// <summary>
    ///     Posts an agent message. <paramref name="kind" /> is one of the
    ///     board's well-known kinds (<c>info</c>, <c>ask</c>, <c>result</c>,
    ///     <c>hold</c>, <c>veto</c>); an unknown kind is treated as info.
    ///     Returns true when the board accepted the message.
    /// </summary>
    bool Post(string kind, string sourceSession, string? targetSession, string body);
}
