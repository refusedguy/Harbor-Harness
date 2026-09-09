using System.Text;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl.Commands;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Auto-title service (opencode-style, SRP extraction from the REPL runner):
///     sessions born with the default <c>Session yyyy-MM-dd HH:mm</c> stamp get
///     the first user prompt as an instant heuristic title, then a background
///     micro-request asks the model for a real 2-5 word title. The loop is never
///     blocked and never hijacked — the title call bypasses the agent entirely.
///     All collaboration flows through <see cref="IReplHost"/>; no service locator.
/// </summary>
internal sealed class SessionTitleService(IReplHost host, ILogger logger)
{
    private readonly HashSet<string> _autoTitledSessions = new();

    public async Task MaybeAutoTitleAsync(CancellationToken ct)
    {
        if (!IsDefaultTitle(host.SessionModel.Title) || !_autoTitledSessions.Add(host.SessionModel.Id))
        {
            return;
        }

        if (host.SessionStore is not { } store)
        {
            return;
        }

        var messages = await store.GetMessagesAsync(host.SessionModel.Id, ct).ConfigureAwait(false);
        if (messages.IsFailure)
        {
            _autoTitledSessions.Remove(host.SessionModel.Id);
            return;
        }

        string? first = null;
        foreach (var m in messages.Value)
        {
            if (m is UserMessage u && !string.IsNullOrWhiteSpace(u.Content))
            {
                first = u.Content;
                break;
            }
        }

        if (string.IsNullOrEmpty(first))
        {
            _autoTitledSessions.Remove(host.SessionModel.Id);
            return;
        }

        await ApplySessionTitleAsync(store, host.SessionModel, HeuristicTitle(first), ct).ConfigureAwait(false);

        // AI upgrade on the pool: full observation inside, the frame loop never waits.
        Session captured = host.SessionModel;
        string prompt = first;
        _ = Task.Run(() => UpgradeTitleWithAiAsync(captured, prompt));
    }

    /// <summary>First line of the first prompt, 48 chars max.</summary>
    private static string HeuristicTitle(string firstUserMessage)
    {
        string title = firstUserMessage.Split('\n')[0].Trim();
        return title.Length > 48 ? title[..47] + "…" : title;
    }

    /// <summary>Background title summarization: one micro-request, timeout 30 s.</summary>
    private async Task UpgradeTitleWithAiAsync(Session session, string firstUserMessage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (host.SessionStore is not { } store)
            {
                return;
            }

            var clientResult = host.ProviderRegistry.GetClient(ProviderId.Create(session.ProviderId));
            if (clientResult.IsFailure)
            {
                return;
            }

            string excerpt = firstUserMessage.Length > 500 ? firstUserMessage[..500] : firstUserMessage;
            var request = new LlmRequest(
                Model: session.Model,
                Messages: [LlmUserMessage.Text(
                    "Generate a short chat title (2-5 words) for a conversation that started " +
                    "with this user message. Reply with the title only — no quotes, no punctuation " +
                    "around it, same language as the message:\n" + excerpt)],
                SystemPrompt: "You name chat sessions. Reply with a 2-5 word title only.",
                Tools: []);
            var sb = new StringBuilder();
            await foreach (var evt in clientResult.Value.StreamAsync(request, timeout.Token).ConfigureAwait(false))
            {
                if (evt is TextDeltaEvent td)
                {
                    sb.Append(td.Delta);
                }
            }

            string? title = SanitizeAiTitle(sb.ToString());
            if (title is null)
            {
                return;
            }

            await ApplySessionTitleAsync(store, session, title, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Heuristic title already applied — AI upgrade is best-effort.
            logger.LogDebug(ex, "AI title upgrade failed");
        }
    }

    /// <summary>Sanitize a model-produced title; null when unusable.</summary>
    private static string? SanitizeAiTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string title = raw.Split('\n')[0].Trim().Trim('"', '\'', '«', '»', '.', '!', ':');
        if (title.Length == 0 || IsDefaultTitle(title))
        {
            return null;
        }

        return title.Length > 48 ? title[..47] + "…" : title;
    }

    /// <summary>Persist a title; refresh the live session + sidebar when current.</summary>
    private async Task ApplySessionTitleAsync(
        ISessionStore store, Session session, string title, CancellationToken ct)
    {
        var updated = session with { Title = title };
        var saved = await store.UpdateAsync(updated, ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return;
        }

        if (session.Id == host.SessionModel.Id)
        {
            host.SessionModel = updated;
            if (host.Screen.Sidebar is { } sidebar)
            {
                sidebar.State = sidebar.State with { SessionTitle = title };
            }

            host.WakeUp();
        }
    }

    /// <summary>Default stamp from <c>Session.Create</c> (<c>Session yyyy-MM-dd HH:mm</c>).</summary>
    private static bool IsDefaultTitle(string title) =>
        title.Length == 24 && title.StartsWith("Session 2", StringComparison.Ordinal);
}
