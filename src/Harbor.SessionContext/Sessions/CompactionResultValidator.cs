// CompactionResultValidator.cs — #2680 F039: decide whether a compaction LLM
// reply is fit to become the session's summary. The helper is a pure function
// over a string: it starts no process, calls no LLM, reads no store and changes
// no product path. It is the classification half of F039 — the part that tells
// a caller "accept this summary" or "this reply was empty/truncated, retry" —
// leaving the chunking and summarization wiring for a separate slice.
namespace Harbor.Application.Sessions;

/// <summary>
///     Why a compaction LLM reply was accepted or rejected (#2680 F039).
/// </summary>
public enum CompactionResultKind
{
    /// <summary>The reply carries usable summary text.</summary>
    Valid,

    /// <summary>The reply is blank or a known no-content marker.</summary>
    Empty,

    /// <summary>The reply stops mid-structure or mid-sentence.</summary>
    Truncated
}

/// <summary>
///     Classifies a raw compaction reply as <see cref="CompactionResultKind.Valid" />,
///     <see cref="CompactionResultKind.Empty" /> or <see cref="CompactionResultKind.Truncated" />
///     (#2680 F039). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         This is deliberately a shallow, deterministic check, not a parser. A
///         structured reply is still parsed by <see cref="CompactionSummary.Parse" />;
///         this validator runs first and only answers the question "would feeding
///         this text back into history be worse than retrying?". It never rewrites
///         the reply, so a caller that accepts it stays lossless.
///     </para>
///     <para>
///         Empty detection covers whitespace and the short sentinels a model uses
///         when it decides there is nothing to summarize (<c>null</c>, <c>[]</c>,
///         <c>{}</c>, <c>(empty)</c> and friends). Truncation detection covers the
///         observable shapes of a cut-off reply: a trailing ellipsis, an unclosed
///         Markdown fence, unbalanced JSON braces/brackets on a JSON-looking
///         reply, and a trailing continuation punctuation.
///     </para>
///     <para>
///         All comparisons are ordinal and culture-invariant. No reflection, no
///         regular expressions, no dependency outside the BCL.
///     </para>
/// </remarks>
public static class CompactionResultValidator
{
    /// <summary>Short replies that mean "the model produced no summary".</summary>
    private static readonly string[] EmptyMarkers =
    [
        "null", "[]", "{}", "(empty)", "[empty]", "<empty>", "n/a", "none"
    ];

    /// <summary>
    ///     Returns the verdict for <paramref name="response" />.
    /// </summary>
    /// <param name="response">Raw text of the compaction LLM reply.</param>
    public static CompactionResultKind Classify(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return CompactionResultKind.Empty;

        string text = response!.Trim();
        if (IsEmptyMarker(text)) return CompactionResultKind.Empty;
        if (HasTruncationSignal(text)) return CompactionResultKind.Truncated;
        return CompactionResultKind.Valid;
    }

    /// <summary>
    ///     True when <paramref name="kind" /> means the summarizer should be
    ///     called again instead of poisoning history with a bad summary.
    /// </summary>
    public static bool ShouldRetry(CompactionResultKind kind) =>
        kind is CompactionResultKind.Empty or CompactionResultKind.Truncated;

    /// <summary>
    ///     A stable, human-readable explanation of <paramref name="kind" />,
    ///     suitable for a log line or a diagnostic shown to the user.
    /// </summary>
    public static string Describe(CompactionResultKind kind)
    {
        if (kind == CompactionResultKind.Empty)
            return "Compaction produced an empty summary; retrying.";
        if (kind == CompactionResultKind.Truncated)
            return "Compaction summary looks truncated; retrying.";
        return "Compaction summary is usable.";
    }

    private static bool IsEmptyMarker(string text)
    {
        for (int i = 0; i < EmptyMarkers.Length; i++)
        {
            if (string.Equals(text, EmptyMarkers[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool HasTruncationSignal(string text)
    {
        if (EndsWithEllipsis(text)) return true;
        if (HasOddFenceCount(text)) return true;
        if (LooksLikeJson(text) && !BalancesJson(text)) return true;
        return EndsWithContinuation(text);
    }

    private static bool EndsWithEllipsis(string text) =>
        text.EndsWith("...", StringComparison.Ordinal) ||
        text.EndsWith('\u2026');

    private static bool HasOddFenceCount(string text)
    {
        int fences = 0;
        int index = 0;
        while ((index = text.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            fences++;
            index += 3;
        }

        return (fences & 1) == 1;
    }

    private static bool LooksLikeJson(string text) =>
        text.Length > 0 && (text[0] == '{' || text[0] == '[');

    private static bool BalancesJson(string text)
    {
        int braces = 0;
        int brackets = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{': braces++; break;
                case '}': braces--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
            }

            if (braces < 0 || brackets < 0) return false;
        }

        return braces == 0 && brackets == 0;
    }

    private static bool EndsWithContinuation(string text)
    {
        char last = text[^1];
        return last is ',' or ';' or ':';
    }
}
