namespace Harbor.Application.Sessions;
/// <summary>
///     Caps tool-result payloads on the domain → LLM path (issue #235).
///     A single provider error blob (e.g. HTTP 429 metadata) carries KBs of
///     nested detail; multiplied by retries it can saturate the model context
///     while carrying no actionable signal past the head. Trimming lives here —
///     the single choke point before provider wire mapping — so session stores,
///     event logs and TUI rendering keep the full text untouched.
/// </summary>
public static class ToolResultContextTrim
{
    /// <summary>
    ///     Maximum tool-result output characters forwarded to the model per entry.
    ///     Single constant for the whole tool-result → model-context path.
    /// </summary>
    public const int MaxToolResultChars = 2000;

    /// <summary>
    ///     Trims <paramref name="output" /> head-kept / tail-cut: the actionable
    ///     head (status line, first error detail) is preserved verbatim, the tail
    ///     is replaced by a marker carrying the original length. Short payloads
    ///     pass through untouched (same reference, zero allocation).
    /// </summary>
    /// <param name="output">Full tool-result output text.</param>
    /// <returns>Original text when within budget, otherwise head + marker.</returns>
    public static string Trim(string output)
    {
        if (output.Length <= MaxToolResultChars)
        {
            return output;
        }

        return string.Concat(
            output.AsSpan(0, MaxToolResultChars),
            $"\n…[truncated {output.Length - MaxToolResultChars} chars; showing first {MaxToolResultChars} — full output retained in session log]");
    }
}
