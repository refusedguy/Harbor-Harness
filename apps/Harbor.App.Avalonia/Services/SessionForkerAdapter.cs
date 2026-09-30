using CSharpFunctionalExtensions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Ui.Framework.Forking;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Adapter that projects the Application-layer <see cref="SessionForkService" /> onto the
///     Ui.Framework <see cref="ISessionForker" /> contract — the same bridge shape as
///     <see cref="CommonConfigReaderAdapter" />, for the same reason (#453, ADR-009): the UI
///     framework needs a core capability it may not reference.
/// </summary>
/// <remarks>
///     <para>
///         #670: the UI framework used to fork sessions itself, with a hand-written second copy
///         of this service that had drifted until a UI fork did not stamp
///         <see cref="Harbor.Abstractions.Models.Session.ParentSessionId" />, kept a title it
///         never persisted, and regenerated every copied message id. Both paths are now one call
///         into <see cref="SessionForkService.ForkAsync" />, so the desktop fork and the
///         <c>harbor sessions fork</c> CLI fork cannot disagree again.
///     </para>
///     <para>
///         <b>One projection, no branching logic:</b> the adapter unwraps
///         <see cref="SessionFork" /> into the port's <see cref="SessionForked" /> and forwards
///         every failure verbatim. Which step failed is decided by the caller, not re-decided
///         here — see <c>SessionFactory.CreateBranchAsync</c>, which adds the session id and the
///         copy progress it owns.
///     </para>
///     <para>
///         Registered as a singleton in <c>ServiceRegistration</c>, which hands it the same
///         <see cref="ISessionStore" /> the rest of the session graph uses — branching is
///         store-generic, so there is no backend selection here.
///     </para>
/// </remarks>
public sealed class SessionForkerAdapter : ISessionForker
{
    private readonly ISessionStore _store;

    /// <summary>Construct the adapter.</summary>
    /// <param name="store">The session store both parent and child live in.</param>
    public SessionForkerAdapter(ISessionStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<Result<SessionForked>> ForkAsync(
        string sessionId,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken ct = default)
    {
        Result<SessionFork> forked = await new SessionForkService()
            .ForkAsync(_store, sessionId, upToMessageId, title, ct).ConfigureAwait(false);

        return forked.IsSuccess
            ? Result.Success(new SessionForked(forked.Value.Session, forked.Value.Copied))
            : Result.Failure<SessionForked>(forked.Error);
    }
}