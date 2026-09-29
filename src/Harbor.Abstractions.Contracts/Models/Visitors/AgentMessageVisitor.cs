namespace Harbor.Abstractions.Models;
/// <summary>
///     The single traversal over the <see cref="AgentMessage" /> sum type
///     (GoF Visitor, #461).
/// </summary>
/// <remarks>
///     <para>
///         The message union is three cases — <see cref="UserMessage" />,
///         <see cref="AssistantMessage" />, <see cref="ToolResultMessage" /> —
///         and the message walk used to be a sixth-hand copy of the content-part
///         walk's switch. Walkers that also need per-part behaviour hold their own
///         <see cref="ContentPartVisitor{TResult}" /> and call its
///         <see cref="ContentPartVisitor{TResult}.Walk" /> from the
///         <see cref="AssistantMessage" /> arm, so both levels stay single-source.
///     </para>
///     <para>
///         As with the part union, an unrecognised message is refused by
///         <see cref="VisitUnknown" /> rather than ignored — the LLM converter
///         used to skip an unknown message entirely, which would have dropped a
///         turn from the model context with no trace at all.
///     </para>
/// </remarks>
/// <typeparam name="TResult">
///     What one visit yields — the rendered line, the converted message, the
///     estimated token count, the next UI state; or <c>this</c> for a walker
///     that accumulates into a sink.
/// </typeparam>
public abstract class AgentMessageVisitor<TResult>
{
    /// <summary>Handle one <see cref="UserMessage" />.</summary>
    /// <param name="message">The message being visited.</param>
    public abstract TResult Visit(UserMessage message);

    /// <summary>Handle one <see cref="AssistantMessage" /> (its parts are the caller's business).</summary>
    /// <param name="message">The message being visited.</param>
    public abstract TResult Visit(AssistantMessage message);

    /// <summary>Handle one <see cref="ToolResultMessage" />.</summary>
    /// <param name="message">The message being visited.</param>
    public abstract TResult Visit(ToolResultMessage message);

    /// <summary>
    ///     Handle a message whose runtime type is not part of the closed union.
    /// </summary>
    /// <remarks>
    ///     Throws by default — an unknown role is a model change no walker was
    ///     taught about. Override to report it instead; never to drop it silently.
    /// </remarks>
    /// <param name="message">The unrecognised message.</param>
    protected virtual TResult VisitUnknown(AgentMessage message) =>
        throw new NotSupportedException(
            $"{GetType().Name} has no arm for message '{message.GetType().Name}' (role '{message.Role}'). "
            + "A new AgentMessage subtype must ship with a Visit arm on AgentMessageVisitor<TResult>, an Accept arm, "
            + "and an explicit decision in every walker.");

    /// <summary>
    ///     Dispatch one message to the arm for its kind — the one place in Harbor
    ///     that pattern-matches on the <see cref="AgentMessage" /> union.
    /// </summary>
    /// <param name="message">The message to dispatch.</param>
    public TResult Accept(AgentMessage message) => message switch
    {
        UserMessage user => Visit(user),
        AssistantMessage assistant => Visit(assistant),
        ToolResultMessage tool => Visit(tool),
        _ => VisitUnknown(message)
    };

    /// <summary>
    ///     Walk an ordered message sequence in order. Index-based on purpose: the
    ///     walk sits on the LLM-conversion and token-estimation hot paths.
    /// </summary>
    /// <param name="messages">The messages to walk; may be empty, never null.</param>
    /// <returns>The result of the last visited message.</returns>
    public TResult Walk(IReadOnlyList<AgentMessage> messages)
    {
        TResult last = default!;
        for (int i = 0; i < messages.Count; i++)
        {
            last = Accept(messages[i]);
        }

        return last;
    }
}
