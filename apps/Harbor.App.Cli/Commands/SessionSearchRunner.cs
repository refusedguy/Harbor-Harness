using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor sessions search</c>: case-insensitive substring scan
///     over persisted messages. Read-only by design — user, assistant and
///     tool_result roles participate; output is capped so one broad query
///     cannot flood the console.
/// </summary>
public static class SessionSearchRunner
{
    /// <summary>Upper bound on printed matches; further hits are counted but not shown.</summary>
    public const int MaxMatches = 50;

    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        string query,
        string? sessionFilter = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await error.WriteLineAsync("Search query must not be empty.").ConfigureAwait(false);
            return 2;
        }

        var listed = await store.ListAsync().ConfigureAwait(false);
        if (listed.IsFailure)
        {
            await error.WriteLineAsync($"Cannot list sessions: {listed.Error}").ConfigureAwait(false);
            return 1;
        }

        int matches = 0;
        bool capped = false;
        foreach (var session in listed.Value)
        {
            if (sessionFilter is not null && !session.Id.Contains(sessionFilter, StringComparison.Ordinal))
                continue;

            var messages = await store.GetMessagesAsync(session.Id).ConfigureAwait(false);
            if (messages.IsFailure)
                continue;

            (matches, bool cappedNow) = await PrintSessionMatchesAsync(output, session, messages.Value, query, matches).ConfigureAwait(false);
            if (cappedNow)
            {
                capped = true;
                break;
            }
        }

        await output.WriteLineAsync(matches == 0
            ? $"No matches for '{query}'."
            : $"{matches} match(es) for '{query}'{(capped ? $" (output capped at {MaxMatches})" : "")}.").ConfigureAwait(false);
        return matches == 0 ? 1 : 0;
    }

    /// <summary>
    ///     Print matching messages of one session starting at <paramref name="matchCount" />.
    ///     Returns the updated count; the bool is true when the global cap was
    ///     reached (caller must stop scanning).
    /// </summary>
    private static async Task<(int MatchCount, bool Capped)> PrintSessionMatchesAsync(
        TextWriter output,
        Harbor.Abstractions.Models.Session session,
        IReadOnlyList<AgentMessage> messages,
        string query,
        int matchCount)
    {
        string title = string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title;
        foreach (var message in messages)
        {
            string? text = MessageText(message);
            if (text is null || !text.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            if (matchCount >= MaxMatches)
            {
                return (matchCount, true);
            }

            int snippetStart = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            int cutFrom = Math.Max(0, snippetStart - 40);
            string snippet = text[cutFrom..Math.Min(text.Length, snippetStart + query.Length + 80)]
                .Replace("\n", "\\n", StringComparison.Ordinal);
            await output.WriteLineAsync(
                $"[{title}] {session.Id} · {message.Role} · {message.Id} @ {message.CreatedAt:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
            await output.WriteLineAsync($"    …{snippet}…").ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            matchCount++;
        }

        return (matchCount, false);
    }

    /// <summary>Flatten a message into its searchable text; unsupported roles yield null.</summary>
    private static string? MessageText(AgentMessage message) => message switch
    {
        UserMessage user => user.Content,
        AssistantMessage assistant => JoinTextParts(assistant.Parts),
        ToolResultMessage toolResult => string.Join("\n", toolResult.Results.Select(r => r.Output)),
        _ => null,
    };

    /// <summary>
    ///     Zero-allocation-minded text-part join mirroring <c>SubAgentRunner</c>:
    ///     single-part fast path, empty result when no prose exists
    ///     (thinking-only / tool-call-only messages are simply not searchable).
    /// </summary>
    private static string JoinTextParts(IReadOnlyList<ContentPart> parts)
    {
        if (parts.Count == 1 && parts[0] is TextPart single)
            return single.Text;

        string? joined = null;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not TextPart part)
                continue;
            joined = joined is null ? part.Text : $"{joined}\n{part.Text}";
        }

        return joined ?? string.Empty;
    }
}
