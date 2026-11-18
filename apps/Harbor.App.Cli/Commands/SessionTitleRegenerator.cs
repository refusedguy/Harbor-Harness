using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Regenerates a session title on demand (issue #2684 S06). The one-shot
///     auto-title in <c>Repl/SessionTitleService</c> names a session once and
///     never runs again, so a session whose first prompt was vague keeps that
///     vague title forever. This is the explicit second path: one auxiliary
///     LLM request over the first user message, and a title-only store update.
/// </summary>
/// <remarks>
///     <para>
///         The agent loop is not involved. The request carries no tools, is
///         capped at <see cref="MaxOutputTokens" /> tokens and bounded by
///         <see cref="RequestTimeout" />, so a slow or chatty provider cannot
///         hold the command open or run up a large bill.
///     </para>
///     <para>
///         Only the title changes. Messages, provider, model and metadata are
///         copied through unchanged, which is why this writes
///         <c>session with { Title = ... }</c> instead of reconstructing a
///         session from scratch. The four fallible reads and writes travel on
///         one Result rail rather than a ladder of guards (docs/ROP-API-INVENTORY.md §3.1).
///     </para>
/// </remarks>
public static class SessionTitleRegenerator
{
    /// <summary>Maximum characters of the first prompt sent to the naming model.</summary>
    public const int MaxPromptChars = 500;

    /// <summary>Maximum characters of a persisted title.</summary>
    public const int MaxTitleChars = 48;

    /// <summary>Output-token cap for the naming request.</summary>
    public const int MaxOutputTokens = 24;

    /// <summary>Upper bound on the naming request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private const string NamingSystemPrompt = "You name chat sessions. Reply with a 2-5 word title only.";

    /// <summary>
    ///     Ask the session's own provider for a fresh title and persist it.
    /// </summary>
    /// <param name="store">The store holding the session and its messages.</param>
    /// <param name="providerRegistry">Registry used to resolve the session's provider.</param>
    /// <param name="sessionId">The session to retitle.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <returns>The new title, or a failure describing why no title was saved.</returns>
    public static async Task<Result<string>> RegenerateAsync(
        ISessionStore store,
        IProviderRegistry providerRegistry,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Result.Failure<string>("A session id is required.");
        }

        return await Result.Success(sessionId)
            .Bind(async id => await (await store.GetAsync(id).ConfigureAwait(false))
                .MapError(e => $"Cannot read session {id}: {e}"))
            .Bind(async session => await (await LoadFirstPromptAsync(store, session, ct).ConfigureAwait(false))
                .Bind(prompt => NameAndSaveAsync(providerRegistry, store, session, prompt, ct)))
            .ConfigureAwait(false);
    }

    /// <summary>Read the first non-blank user message, or fail with a clear reason.</summary>
    private static async Task<Result<string>> LoadFirstPromptAsync(
        ISessionStore store,
        Session session,
        CancellationToken ct)
    {
        return await (await store.GetMessagesAsync(session.Id, ct).ConfigureAwait(false))
            .MapError(e => $"Cannot read messages for session {session.Id}: {e}")
            .Bind(messages => messages
                .TryFirst(m => m is UserMessage u && !string.IsNullOrWhiteSpace(u.Content))
                .Map(m => ((UserMessage)m).Content)
                .ToResult("The session has no user message to name."))
            .ConfigureAwait(false);
    }

    /// <summary>Resolve the provider, ask for a title, then persist it.</summary>
    private static async Task<Result<string>> NameAndSaveAsync(
        IProviderRegistry providerRegistry,
        ISessionStore store,
        Session session,
        string firstPrompt,
        CancellationToken ct)
    {
        return await providerRegistry.GetClient(ProviderId.Create(session.ProviderId))
            .MapError(e => $"Cannot resolve provider {session.ProviderId}: {e}")
            .Bind(client => AskAsync(client, session, firstPrompt, ct))
            .Bind(async title => await (await store.UpdateAsync(session with { Title = title }, ct)
                    .ConfigureAwait(false))
                .MapError(e => $"Cannot persist the new title: {e}")
                .Map(_ => title))
            .ConfigureAwait(false);
    }

    /// <summary>One bounded naming request. No tools, no agent loop.</summary>
    private static async Task<Result<string>> AskAsync(
        ILlmClient client,
        Session session,
        string firstPrompt,
        CancellationToken ct)
    {
        string excerpt = firstPrompt.Length > MaxPromptChars ? firstPrompt[..MaxPromptChars] : firstPrompt;
        var request = new LlmRequest(
            Model: session.Model,
            Messages: [LlmUserMessage.Text(
                "Generate a short chat title (2-5 words) for a conversation that started " +
                "with this user message. Reply with the title only, no quotes and no " +
                "punctuation around it, in the language of the message:\n" + excerpt)],
            SystemPrompt: NamingSystemPrompt,
            Tools: [],
            MaxOutputTokens: MaxOutputTokens);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        var sb = new StringBuilder();
        await foreach (var evt in client.StreamAsync(request, timeout.Token).ConfigureAwait(false))
        {
            if (evt is TextDeltaEvent delta)
            {
                sb.Append(delta.Delta);
            }
        }

        string? title = Sanitize(sb.ToString());
        return title is null
            ? Result.Failure<string>("The model did not return a usable title.")
            : Result.Success(title);
    }

    /// <summary>
    ///     Trim a model-produced title down to one usable line.
    /// </summary>
    /// <param name="raw">The raw streamed text.</param>
    /// <returns>The sanitized title, or <see langword="null" /> when nothing usable remains.</returns>
    public static string? Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string title = raw.Split('\n')[0].Trim().Trim('"', '\'', '«', '»', '.', '!', ':');
        if (title.Length == 0)
        {
            return null;
        }

        return title.Length > MaxTitleChars ? title[..(MaxTitleChars - 1)] + "…" : title;
    }
}
