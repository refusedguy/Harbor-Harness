using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
namespace Harbor.App.Cli.Commands;

/// <summary>
///     Branching core for <c>harbor sessions fork</c>: load the source session,
///     cut its history at the given message (inclusive), and materialize a NEW
///     session bound to the same directory/agent/provider/model with lineage
///     recorded via <see cref="Harbor.Abstractions.Contracts.Models.Session.ParentSessionId" />.
///     The source session is never modified.
/// </summary>
public sealed class SessionForkRunner
{
    private readonly ISessionStore _store;

    /// <summary>Construct over any backend — branching is store-generic by design.</summary>
    public SessionForkRunner(ISessionStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    ///     Fork <paramref name="sessionId" /> at <paramref name="messageId" />.
    ///     Returns the new session id and how many messages were copied.
    /// </summary>
    /// <param name="sessionId">Source session to branch from.</param>
    /// <param name="messageId">Cut point; this message is INCLUDED in the fork.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    ///     Failure modes: source missing / history unreadable / unknown message id /
    ///     create or persist failures — all surfaced as <see cref="Result" /> errors;
    ///     partial copy failures report what was already written in the error text.
    /// </remarks>
    public async Task<Result<ForkOutcome>> ForkAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<ForkOutcome>("Session id must not be empty.");
        if (string.IsNullOrWhiteSpace(messageId))
            return Result.Failure<ForkOutcome>("Message id must not be empty.");

        var loaded = await _store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
            return Result.Failure<ForkOutcome>($"Cannot load session '{sessionId}': {loaded.Error}");

        var history = await _store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (history.IsFailure)
            return Result.Failure<ForkOutcome>($"Cannot read '{sessionId}' history: {history.Error}");

        int cutIndex = -1;
        for (int i = 0; i < history.Value.Count; i++)
        {
            if (history.Value[i].Id.ToString().Equals(messageId, StringComparison.Ordinal))
            {
                cutIndex = i;
                break;
            }
        }

        if (cutIndex < 0)
            return Result.Failure<ForkOutcome>(
                $"Message '{messageId}' not found among {history.Value.Count} message(s) of session '{sessionId}'.");

        // Late C# collection expressions over an index range: prefix [0..cutIndex] inclusive.
        var prefix = history.Value.Take(cutIndex + 1).ToList();

        var created = await _store.CreateAsync(
            loaded.Value.Directory,
            loaded.Value.Agent,
            loaded.Value.ProviderId,
            loaded.Value.Model,
            ct).ConfigureAwait(false);
        if (created.IsFailure)
            return Result.Failure<ForkOutcome>($"Cannot create forked session: {created.Error}");

        var fork = created.Value with
        {
            Title = $"{loaded.Value.Title} (fork)",
            ParentSessionId = loaded.Value.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var headerSaved = await _store.UpdateAsync(fork, ct).ConfigureAwait(false);
        if (headerSaved.IsFailure)
            return Result.Failure<ForkOutcome>($"Cannot persist forked session header: {headerSaved.Error}");

        for (int i = 0; i < prefix.Count; i++)
        {
            var appended = await _store.AppendMessageAsync(fork.Id, prefix[i], ct).ConfigureAwait(false);
            if (appended.IsFailure)
                return Result.Failure<ForkOutcome>(
                    $"Copied {i} of {prefix.Count} message(s) before failing: {appended.Error}");
        }

        return Result.Success(new ForkOutcome(fork.Id, prefix.Count));
    }

    /// <summary>Result of a successful fork.</summary>
    /// <param name="ForkId">Newly created session id.</param>
    /// <param name="Copied">Messages copied into the fork (inclusive of the cut point).</param>
    public sealed record ForkOutcome(string ForkId, int Copied);
}
