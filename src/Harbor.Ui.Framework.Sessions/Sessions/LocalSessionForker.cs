// TEMPORARY — planted for the first commit of the #882 PR, deleted by the second.
//
// This is the failure #882 names, written out so the guard has something real to
// catch: a second implementer of ISessionForker declared in src/, beside
// SessionFactory — the consumer that is supposed to reach the core fork through
// the port. It is deliberately the *stub* flavour, not a re-implementation: it
// compiles, it wires, and the user's first branch gesture fails at runtime with
// a NullReferenceException from a null _forker.
//
// #882's own words: "a future host would now be 'fixed' by someone reintroducing
// a local fork to silence the compiler error. That is precisely the failure mode
// the required parameter was meant to prevent, and only the compiler stands
// between the two." The compiler does not stand between them — this file
// compiles clean. Only a count does.
//
// SessionForkPortSeamRules.ExactlyOneProductionType_ImplementsTheForkPort
// reports it. Commit two deletes this file and the guard goes green.

using CSharpFunctionalExtensions;
using Harbor.Ui.Framework.Forking;

namespace Harbor.Ui.Framework.Sessions;

/// <summary>
///     A second fork implementation, planted to prove the #882 guard is not vacuous.
/// </summary>
/// <remarks>
///     Delete this file. It exists only for the first commit of the #882 pull request, and
///     <c>SessionForkPortSeamRules</c> fails the build while it is here.
/// </remarks>
public sealed class LocalSessionForker : ISessionForker
{
    /// <summary>Never forks: the stub shape #882 is about, kept honest about what it is.</summary>
    /// <param name="sessionId">Ignored.</param>
    /// <param name="upToMessageId">Ignored.</param>
    /// <param name="title">Ignored.</param>
    /// <param name="ct">Ignored.</param>
    /// <returns>A failure. The core's <c>SessionForkService</c> is what should be called here.</returns>
    public Task<Result<SessionForked>> ForkAsync(
        string sessionId,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SessionForked>(
            "LocalSessionForker is a planted stub and must not survive the #882 guard's first commit."));
}
