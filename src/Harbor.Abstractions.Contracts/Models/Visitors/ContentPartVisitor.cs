namespace Harbor.Abstractions.Models;
/// <summary>
///     The single traversal over the <see cref="ContentPart" /> sum type
///     (GoF Visitor, #461).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ContentPart" /> is a closed sum — <see cref="TextPart" />,
///         <see cref="ThinkingPart" />, <see cref="ToolCallPart" />,
///         <see cref="FilePart" />. Before this type existed every consumer
///         rewrote the same <c>switch</c> over it, and most of those switches had
///         no <c>default:</c> arm: a fifth subtype would have been dropped
///         <em>silently</em> by the LLM converter, the compaction formatter, the
///         token estimator, the session supervisor, the UI replay reducer and the
///         SQLite codec — six silent regressions out of one model change.
///     </para>
///     <para>
///         Every walk now goes through <see cref="Accept" /> or <see cref="Walk" />.
///         A walker declares what it does for <em>every</em> kind: the arms are
///         <c>abstract</c>, so the compiler rejects a walker that forgets one, and
///         a part whose runtime type is not in the union is refused by
///         <see cref="VisitUnknown" /> rather than vanishing.
///     </para>
/// </remarks>
/// <typeparam name="TResult">
///     What one visit yields. A folding walker picks the folded value
///     (<see cref="int" />, a state record); a walker that accumulates into a sink
///     returns <c>this</c> and reads its accumulator afterwards.
/// </typeparam>
public abstract class ContentPartVisitor<TResult>
{
    /// <summary>Handle one <see cref="TextPart" />.</summary>
    /// <param name="part">The part being visited.</param>
    public abstract TResult Visit(TextPart part);

    /// <summary>Handle one <see cref="ThinkingPart" />.</summary>
    /// <param name="part">The part being visited.</param>
    public abstract TResult Visit(ThinkingPart part);

    /// <summary>Handle one <see cref="ToolCallPart" />.</summary>
    /// <param name="part">The part being visited.</param>
    public abstract TResult Visit(ToolCallPart part);

    /// <summary>Handle one <see cref="FilePart" />.</summary>
    /// <param name="part">The part being visited.</param>
    public abstract TResult Visit(FilePart part);

    /// <summary>
    ///     Handle a part whose runtime type is not part of the closed union.
    /// </summary>
    /// <remarks>
    ///     Throws by default: an unknown kind means the domain model grew a
    ///     subtype that this walker was never taught about, and the whole point
    ///     of this type is that such a part cannot pass unnoticed. Override to
    ///     <em>report</em> the part (log it, mark a dirty flag) instead of
    ///     refusing it — never to drop it in silence.
    /// </remarks>
    /// <param name="part">The unrecognised part.</param>
    protected virtual TResult VisitUnknown(ContentPart part) =>
        throw new NotSupportedException(
            $"{GetType().Name} has no arm for content part '{part.GetType().Name}' (type discriminator '{part.Type}'). "
            + "A new ContentPart subtype must ship with a Visit arm on ContentPartVisitor<TResult>, an Accept arm, "
            + "and an explicit decision in every walker.");

    /// <summary>
    ///     Dispatch one part to the arm for its kind — the one place in Harbor
    ///     that pattern-matches on the <see cref="ContentPart" /> union.
    /// </summary>
    /// <param name="part">The part to dispatch.</param>
    public TResult Accept(ContentPart part) => part switch
    {
        TextPart text => Visit(text),
        ThinkingPart thinking => Visit(thinking),
        ToolCallPart call => Visit(call),
        FilePart file => Visit(file),
        _ => VisitUnknown(part)
    };

    /// <summary>
    ///     Walk an ordered part sequence in order. Index-based on purpose: the
    ///     walk sits on the LLM-conversion and token-estimation hot paths, where a
    ///     <c>foreach</c> enumerator is an allocation.
    /// </summary>
    /// <param name="parts">The parts to walk; may be empty, never null.</param>
    /// <returns>The result of the last visited part.</returns>
    public TResult Walk(IReadOnlyList<ContentPart> parts)
    {
        TResult last = default!;
        for (int i = 0; i < parts.Count; i++)
        {
            last = Accept(parts[i]);
        }

        return last;
    }
}
