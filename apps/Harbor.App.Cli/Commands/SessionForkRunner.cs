using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
namespace Harbor.App.Cli.Commands;

/// <summary>
///     CLI face of <c>harbor sessions fork</c>: validation guards plus argument
///     mapping over <see cref="SessionForkService" /> — load-and-cut semantics,
///     lineage stamping and re-stamping of copied messages all live in one place
///     so every backend shares identical branching behavior.
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
    public async Task<Result<ForkOutcome>> ForkAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<ForkOutcome>("Session id must not be empty.");
        if (string.IsNullOrWhiteSpace(messageId))
            return Result.Failure<ForkOutcome>("Message id must not be empty.");

        Result<SessionFork> forked = await new SessionForkService()
            .ForkAsync(_store, sessionId, messageId, ct: ct).ConfigureAwait(false);
        return forked.Match(
            outcome => Result.Success(new ForkOutcome(outcome.Session.Id, outcome.Copied)),
            error => Result.Failure<ForkOutcome>(error));
    }

    /// <summary>Result of a successful fork.</summary>
    /// <param name="ForkId">Newly created session id.</param>
    /// <param name="Copied">Messages copied into the fork (inclusive of the cut point).</param>
    public sealed record ForkOutcome(string ForkId, int Copied);
}
