using Harbor.Abstractions.Events;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     The single dispatch over the <see cref="LlmEvent"/> sum type (issue #1494).
/// </summary>
/// <remarks>
///     <para>
///         Two UI consumers used to rewrite the same nested <c>switch</c> over
///         <c>MessageUpdateEvent.LlmEvent</c>: the CellForge live stream bridge
///         and <see cref="ViewModels.ChatHistoryViewModel"/>. A new delta kind had
///         to be added in both places, and forgetting the history half silently
///         lost text in the transcript while the live stream looked fine.
///     </para>
///     <para>
///         <c>Accept</c> is now the only place that branches on the runtime type.
///         Every member is named, so adding one to the union is a compile error in
///         this dispatcher; the abstract <c>Visit</c> arms then force every walker
///         to decide what that member means for it. An unrecognised runtime type
///         is refused by <see cref="VisitUnknown"/>, never dropped in silence.
///     </para>
///     <para>
///         Dispatch is a switch statement over reference types plus virtual calls.
///         No delegates, no dictionary lookup, no boxing, no per-event allocation:
///         <c>Accept</c> runs once per streamed token on the render hot path, so a
///         walker is created once and reused for the lifetime of its owner.
///     </para>
/// </remarks>
public abstract class LlmEventVisitor
{
    /// <summary>Handle one <see cref="TextStartEvent"/>.</summary>
    public abstract void Visit(TextStartEvent e);

    /// <summary>Handle one <see cref="TextDeltaEvent"/>.</summary>
    public abstract void Visit(TextDeltaEvent e);

    /// <summary>Handle one <see cref="TextEndEvent"/>.</summary>
    public abstract void Visit(TextEndEvent e);

    /// <summary>Handle one <see cref="ThinkingStartEvent"/>.</summary>
    public abstract void Visit(ThinkingStartEvent e);

    /// <summary>Handle one <see cref="ThinkingDeltaEvent"/>.</summary>
    public abstract void Visit(ThinkingDeltaEvent e);

    /// <summary>Handle one <see cref="ThinkingEndEvent"/>.</summary>
    public abstract void Visit(ThinkingEndEvent e);

    /// <summary>Handle one <see cref="ToolCallStartEvent"/>.</summary>
    public abstract void Visit(ToolCallStartEvent e);

    /// <summary>Handle one <see cref="ToolCallDeltaEvent"/>.</summary>
    public abstract void Visit(ToolCallDeltaEvent e);

    /// <summary>Handle one <see cref="ToolCallEndEvent"/>.</summary>
    public abstract void Visit(ToolCallEndEvent e);

    /// <summary>Handle one <see cref="StepStartEvent"/>.</summary>
    public abstract void Visit(StepStartEvent e);

    /// <summary>Handle one <see cref="StepFinishEvent"/>.</summary>
    public abstract void Visit(StepFinishEvent e);

    /// <summary>Handle one <see cref="FinishEvent"/>.</summary>
    public abstract void Visit(FinishEvent e);

    /// <summary>Handle one <see cref="ErrorEvent"/>.</summary>
    public abstract void Visit(ErrorEvent e);

    /// <summary>
    ///     Handle an event whose runtime type is not part of the closed union.
    /// </summary>
    /// <remarks>
    ///     Throws by default. Override to <em>report</em> the event instead, never
    ///     to drop it without a trace.
    /// </remarks>
    /// <param name="e">The unrecognised event.</param>
    protected virtual void VisitUnknown(LlmEvent e) =>
        throw new NotSupportedException(
            $"{GetType().Name} has no arm for LLM event '{e.GetType().Name}'. "
            + "A new LlmEvent subtype must ship with a Visit arm on LlmEventVisitor, "
            + "an arm in Accept, and an explicit decision in every walker.");

    /// <summary>
    ///     Dispatch one LLM event to the arm for its kind. The one place in the
    ///     UI layer that pattern-matches on the <see cref="LlmEvent"/> union.
    /// </summary>
    /// <param name="e">The event to dispatch.</param>
    public void Accept(LlmEvent e)
    {
        switch (e)
        {
            case TextStartEvent textStart:
                Visit(textStart);
                return;
            case TextDeltaEvent textDelta:
                Visit(textDelta);
                return;
            case TextEndEvent textEnd:
                Visit(textEnd);
                return;
            case ThinkingStartEvent thinkingStart:
                Visit(thinkingStart);
                return;
            case ThinkingDeltaEvent thinkingDelta:
                Visit(thinkingDelta);
                return;
            case ThinkingEndEvent thinkingEnd:
                Visit(thinkingEnd);
                return;
            case ToolCallStartEvent toolCallStart:
                Visit(toolCallStart);
                return;
            case ToolCallDeltaEvent toolCallDelta:
                Visit(toolCallDelta);
                return;
            case ToolCallEndEvent toolCallEnd:
                Visit(toolCallEnd);
                return;
            case StepStartEvent stepStart:
                Visit(stepStart);
                return;
            case StepFinishEvent stepFinish:
                Visit(stepFinish);
                return;
            case FinishEvent finish:
                Visit(finish);
                return;
            case ErrorEvent error:
                Visit(error);
                return;
        }

        VisitUnknown(e);
    }
}
