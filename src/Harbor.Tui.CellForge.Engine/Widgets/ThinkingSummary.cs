using System.Text;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// O10 (#1179, epic #1155) — steal of opencode's <c>ReasoningPart</c>
/// (<c>packages/tui/src/routes/session/message-parts.tsx</c>) and
/// <c>reasoningSummary()</c> (<c>packages/tui/src/context/thinking.ts</c>):
/// thinking blocks stay collapsed to a single summary line plus duration;
/// click / feed Enter expands through the existing
/// <c>ICollapsibleChatBlock</c> gesture (click route + Enter/Space route in
/// <c>ToolCardTracker</c>), so no new input plumbing was added.
/// Pure helpers (no dependencies) — unit-tested directly.
/// </summary>
public static class ThinkingSummary
{
    /// <summary>
    /// Title budget for the collapsed line. opencode paints the raw title;
    /// Harbor clamps so one overlong first line cannot push the duration
    /// off-screen (the paint clips at width regardless).
    /// </summary>
    public const int MaxTitleChars = 60;

    /// <summary>
    /// Placeholder OpenRouter-style encrypted reasoning blocks use; dropped
    /// exactly like opencode's <c>reasoningContent()</c>.
    /// </summary>
    public const string RedactedMarker = "[REDACTED]";

    public sealed record Summary(string? Title, string Body);

    /// <summary>opencode <c>reasoningContent()</c>: drop redacted markers, trim.</summary>
    public static string CleanContent(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace(RedactedMarker, string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// opencode <c>reasoningSummary()</c> port: a leading
    /// <c>**Title**</c> block (blank line or end after it) becomes the
    /// summary title. Deviation: when no bold title is present opencode
    /// leaves the title null (bare "Thought" header); Harbor falls back to
    /// the first line so every collapsed block stays distinguishable.
    /// </summary>
    public static Summary Summarize(string? text)
    {
        string content = CleanContent(text);
        if (content.Length == 0)
        {
            return new Summary(null, string.Empty);
        }

        if (content.StartsWith("**", StringComparison.Ordinal))
        {
            int close = content.IndexOf("**", 2, StringComparison.Ordinal);
            if (close > 2)
            {
                string candidate = content.Substring(2, close - 2).Trim();
                int rest = close + 2;
                bool titleClean = candidate.Length > 0
                    && candidate.IndexOf('\n') < 0
                    && candidate.IndexOf('*') < 0;
                bool tailOk = rest >= content.Length
                    || content.Substring(rest).StartsWith("\n\n", StringComparison.Ordinal)
                    || content.Substring(rest).StartsWith("\r\n\r\n", StringComparison.Ordinal);
                if (titleClean && tailOk)
                {
                    string body = rest >= content.Length
                        ? string.Empty
                        : content.Substring(rest).Trim();
                    return new Summary(Clamp(candidate), body);
                }
            }
        }

        int nl = content.IndexOf('\n');
        string first = (nl < 0 ? content : content.Substring(0, nl)).Trim();
        return new Summary(first.Length == 0 ? null : Clamp(first), content);
    }

    /// <summary>
    /// opencode <c>ReasoningHeader</c> text port. Done blocks paint
    /// <c>Thought[: title][ · duration]</c>; streaming blocks paint
    /// <c>Thinking[: title]</c> (duration lands only on completion, exactly
    /// like opencode's <c>isDone ? Locale.duration() : undefined</c>).
    /// The <c>+ </c> affordance mirrors opencode's toggleable hide-mode
    /// marker — Harbor thinking is always toggleable.
    /// </summary>
    public static string CollapsedHeader(string? text, string? durationText, bool done)
    {
        var summary = Summarize(text);
        bool hasTitle = summary.Title is { Length: > 0 };
        bool hasDuration = done && !string.IsNullOrEmpty(durationText);

        var sb = new StringBuilder(done ? "+ Thought" : "+ Thinking");
        if (hasTitle || hasDuration)
        {
            sb.Append(": ");
        }

        if (hasTitle)
        {
            sb.Append(summary.Title);
        }

        if (hasDuration)
        {
            if (hasTitle)
            {
                sb.Append(" · ");
            }

            sb.Append(durationText);
        }

        return sb.ToString();
    }

    private static string Clamp(string title) =>
        title.Length <= MaxTitleChars ? title : title.Substring(0, MaxTitleChars).TrimEnd();
}
