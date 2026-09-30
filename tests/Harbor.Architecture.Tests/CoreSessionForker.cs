using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using Harbor.Ui.Framework.Forking;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The #670 fork port (<c>ISessionForker</c>), backed by the real
///     <see cref="SessionForkService" /> over the store under test.
/// </summary>
/// <remarks>
///     <para>
///         Backed by the core rather than stubbed, so an architecture fixture that reaches the
///         fork exercises the same stack the product does. The projection is deliberately the
///         only thing here — it is the identical unwrap the Avalonia adapter performs, so a
///         fixture cannot pass against a fake that is kinder than production.
///     </para>
///     <para>
///         Fixtures that never fork still need one: the <c>SessionFactory</c> constructor takes
///         the port as a REQUIRED parameter (#670), which is the point — a host that wants to
///         fork must name the one implementation, and one that wires nothing fails to compile
///         rather than silently getting a second, different fork.
///     </para>
/// </remarks>
internal sealed class CoreSessionForker(ISessionStore store) : ISessionForker
{
    public async Task<Result<SessionForked>> ForkAsync(
        string sessionId,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken ct = default)
    {
        Result<SessionFork> forked = await new SessionForkService()
            .ForkAsync(store, sessionId, upToMessageId, title, ct).ConfigureAwait(false);

        return forked.IsSuccess
            ? Result.Success(new SessionForked(forked.Value.Session, forked.Value.Copied))
            : Result.Failure<SessionForked>(forked.Error);
    }
}