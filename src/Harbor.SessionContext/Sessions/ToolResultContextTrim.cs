namespace Harbor.Application.Sessions;
/// <summary>
///     Caps tool-result payloads on the domain → LLM path (issue #235).
///     A single provider error blob (e.g. HTTP 429 metadata) carries KBs of
///     nested detail; multiplied by retries it can saturate the model context
///     while carrying no actionable signal past the head. Trimming lives here —
///     the single choke point before provider wire mapping — so session stores,
///     event logs and TUI rendering keep the full text untouched.
/// </summary>
/// <remarks>
///     <para>
///         <b>#1748 — a cut output became a spilled one.</b> The marker this
///         class used to append told the model the tail was "retained in
///         session log": a place the model has no path to and no pointer to, so
///         the promise was silence. With a spill store wired, an output that
///         does not fit the inline budget is written to a file in Harbor's
///         private spill area, and the model receives a preview plus the
///         locator it needs to read the rest back.
///     </para>
///     <para>
///         <b>When the store fails, the original goes through.</b> A full disk
///         or a permission denial must not turn into a silently cut output:
///         an overfull context is visible and recoverable, a truncated one is
///         not. That is why the failure arm returns the untouched text rather
///         than the historical marker.
///     </para>
/// </remarks>
public static class ToolResultContextTrim
{
    /// <summary>
    ///     Default maximum tool-result output characters forwarded to the model
    ///     per entry. The default value of the configurable inline budget.
    /// </summary>
    public const int MaxToolResultChars = ToolOutputSpillPolicy.DefaultMaxInlineChars;

    /// <summary>
    ///     Trims <paramref name="output" /> head-kept / tail-cut: the actionable
    ///     head (status line, first error detail) is preserved verbatim, the tail
    ///     is replaced by a marker carrying the original length. Short payloads
    ///     pass through untouched (same reference, zero allocation).
    /// </summary>
    /// <param name="output">Full tool-result output text.</param>
    /// <returns>Original text when within budget, otherwise head + marker.</returns>
    public static string Trim(string output) => Trim(output, null, MaxToolResultChars);

    /// <summary>
    ///     Trims <paramref name="output" /> against the inline budget, spilling
    ///     the full text to the store when it does not fit.
    /// </summary>
    /// <param name="output">Full tool-result output text.</param>
    /// <param name="spill">
    ///     Where the output would be spilled, or <see langword="null" /> when no
    ///     store is wired — the historical head + marker behaviour.
    /// </param>
    /// <param name="maxInlineChars">
    ///     The inline budget, in characters. Zero spills every output.
    /// </param>
    /// <returns>
    ///     The original text when it fits the budget; otherwise a preview plus
    ///     the locator of the spilled output, or — when the store could not take
    ///     the bytes — the original text, uncut.
    /// </returns>
    public static string Trim(string output, ToolOutputSpillRequest? spill, int maxInlineChars)
    {
        if (output.Length <= maxInlineChars)
        {
            return output;
        }

        if (spill is { } request)
        {
            Result<ToolOutputSpill> stored = request.Store.Spill(request.SessionId, request.ToolCallId, output);
            if (stored.IsFailure)
            {
                // The store could not take the bytes. The original goes to the
                // model: an overfull context is recoverable, a silently cut one
                // is not.
                return output;
            }

            return Preview(output, stored.Value, maxInlineChars);
        }

        return string.Concat(
            output.AsSpan(0, maxInlineChars),
            $"\n…[truncated {output.Length - maxInlineChars} chars; showing first {maxInlineChars} — full output retained in session log]");
    }

    /// <summary>
    ///     The head of the output plus the pointer to the spilled full text.
    /// </summary>
    /// <param name="output">Full tool-result output text.</param>
    /// <param name="spill">Where the full output went.</param>
    /// <param name="maxInlineChars">The inline budget, in characters.</param>
    /// <returns>The preview and the locator.</returns>
    private static string Preview(string output, ToolOutputSpill spill, int maxInlineChars)
    {
        string note =
            $"\n…[spilled: this output is {spill.CharCount} chars and did not fit the context. "
            + $"Full output: {spill.Path} ({spill.SizeBytes} bytes). "
            + $"Read it back with spill_read {{\"path\": \"{spill.Path}\"}}]";

        // The head is concatenated as a SPAN: the preview is a fresh string
        // either way, and slicing the source would allocate a second one.
        return output.Length <= maxInlineChars
            ? string.Concat(output, note)
            : string.Concat(output.AsSpan(0, maxInlineChars), note);
    }
}
