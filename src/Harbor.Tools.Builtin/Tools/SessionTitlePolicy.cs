// SessionTitlePolicy.cs: #2680 F114. The pure decision layer for automatic
// session-title generation: when a title is due, how many naming attempts are
// left and how to build a bounded naming prompt. No model call, clock, store,
// event or product wiring lives here, so the slice stays AOT-safe and
// golden-neutral. A future hosted caller can own the small-model call while
// this type remains a testable value.

using System.Text;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Decides whether a session still owes a generated title (#2680 F114).
///     Pure, stateless and thread-safe.
/// </summary>
public static class SessionTitlePolicy
{
    /// <summary>User messages required before the first naming attempt.</summary>
    public const int DefaultMinUserMessages = 2;

    /// <summary>Hard cap on naming attempts for one session.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Most recent user messages included in a naming prompt.</summary>
    public const int DefaultPromptMessages = 6;

    /// <summary>Character cap applied to each message in a naming prompt.</summary>
    public const int DefaultMessageChars = 500;

    /// <summary>
    ///     Whether a naming attempt is due. A session with a title never needs
    ///     one, and an unusable bound fails closed instead of retrying forever.
    /// </summary>
    /// <param name="currentTitle">The stored title, if the session has one.</param>
    /// <param name="userMessageCount">Number of user messages so far.</param>
    /// <param name="attempts">Failed attempts already spent, or <see langword="null" /> for none.</param>
    /// <param name="minUserMessages">Messages required before the first attempt.</param>
    /// <param name="maxAttempts">Total attempts allowed for this session.</param>
    public static bool ShouldGenerate(
        string? currentTitle,
        int userMessageCount,
        TitleAttempts? attempts = null,
        int minUserMessages = DefaultMinUserMessages,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (!string.IsNullOrWhiteSpace(currentTitle))
            return false;
        if (minUserMessages < 1 || maxAttempts < 1)
            return false;
        if (userMessageCount < minUserMessages)
            return false;

        return !(attempts ?? TitleAttempts.None).IsExhausted(maxAttempts);
    }

    /// <summary>
    ///     Builds a deterministic naming prompt from the most recent user
    ///     messages. A message is skipped when blank and truncated to
    ///     <paramref name="messageChars" />, so the result is bounded by
    ///     <c>promptMessages * messageChars</c>.
    /// </summary>
    /// <param name="userMessages">User messages in chronological order.</param>
    /// <param name="promptMessages">Maximum number of recent messages to include.</param>
    /// <param name="messageChars">Character cap applied to each message.</param>
    /// <returns>The prompt, or <see cref="string.Empty" /> when no usable message exists.</returns>
    public static string BuildPrompt(
        IReadOnlyList<string>? userMessages,
        int promptMessages = DefaultPromptMessages,
        int messageChars = DefaultMessageChars)
    {
        if (userMessages is null || userMessages.Count == 0 || promptMessages < 1 || messageChars < 1)
            return string.Empty;

        var selected = new List<string>(Math.Min(promptMessages, userMessages.Count));
        for (int i = userMessages.Count - 1; i >= 0 && selected.Count < promptMessages; i--)
        {
            string? message = userMessages[i];
            if (string.IsNullOrWhiteSpace(message))
                continue;

            string trimmed = message.Trim();
            selected.Add(trimmed.Length <= messageChars ? trimmed : trimmed[..messageChars]);
        }

        if (selected.Count == 0)
            return string.Empty;

        var prompt = new StringBuilder();
        for (int i = selected.Count - 1; i >= 0; i--)
        {
            if (prompt.Length > 0)
                prompt.Append('\n');
            prompt.Append("- ").Append(selected[i]);
        }

        return prompt.ToString();
    }
}

/// <summary>
///     Immutable count of failed naming attempts for one session (#2680 F114).
///     Reaching the cap is the retry limit; <see cref="Clear" /> is the reset a
///     new session (or an explicit retitle) performs.
/// </summary>
/// <param name="Failed">Number of failed naming attempts so far.</param>
public sealed record TitleAttempts(int Failed)
{
    /// <summary>A fresh counter with no failures.</summary>
    public static TitleAttempts None { get; } = new(0);

    /// <summary>Returns the counter after one more failed naming attempt.</summary>
    public TitleAttempts RecordFailure() => new(Failed + 1);

    /// <summary>Returns a fresh counter, discarding previous failures.</summary>
    public TitleAttempts Clear() => None;

    /// <summary>Whether the retry budget is spent.</summary>
    /// <param name="maxAttempts">Total attempts allowed for this session.</param>
    public bool IsExhausted(int maxAttempts) => maxAttempts >= 1 && Failed >= maxAttempts;
}
