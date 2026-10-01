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
///         the port as a REQUIRED parameter (#670), which is the point — a host that constructs
///         the factory by hand must name the one implementation.
///     </para>
///     <para>
///         #882 corrected the claim this paragraph used to make, which was that a host "fails to
///         compile" if it wires nothing. That is true of a <c>new</c> site and false of a DI
///         registration, and the shipped host registers rather than constructs. It also said
///         nothing about a SECOND implementer, which is the failure the port exists to prevent.
///         <c>SessionForkPortSeamRules</c> is what counts the implementers, and it excludes test
///         assemblies — this one included — on the reason <c>ThemeStoreSeamRules</c> gives: a
///         fixture that implements the port proves the port is injectable rather than
///         duplicating the fork, and a rule that fails on the right kind of code is a rule that
///         gets deleted.
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
            : forked.ConvertFailure<SessionForked>();
    }
}