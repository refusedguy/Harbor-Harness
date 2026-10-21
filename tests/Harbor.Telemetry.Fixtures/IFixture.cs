using CSharpFunctionalExtensions;
using Harbor.Abstractions.Contracts;

namespace Harbor.Telemetry.Fixtures;

/// <summary>
///     The observed seam of the fixture. The marker sits on the TYPE, so it
///     covers every member of the interface except the ones
///     <see cref="ObservedAttribute.Exclude" /> names.
/// </summary>
/// <remarks>
///     <para>
///         The names the generator derives from this seam are the contract the
///         guard asserts: the span <c>fixture.do_work</c>, the counter
///         <c>fixture.calls</c>, the duration histogram
///         <c>fixture.duration.ms</c> and the status tag
///         <c>fixture.status</c>.
///     </para>
///     <para>
///         <c>Compute</c> is EXCLUDED on purpose: it is the member the guard
///         proves is forwarded without a span. An excluded member is an
///         explicit decision by the author, so the generator stays quiet about
///         it — silence is the contract there, not an omission.
///     </para>
/// </remarks>
[Observed(Exclude = new[] { "Compute" })]
public interface IFixture
{
    /// <summary>The call that succeeds.</summary>
    Task<Result> DoWorkAsync(CancellationToken ct = default);

    /// <summary>
    ///     The call that FAILS. A failure is not an exception in Harbor, and
    ///     the generated decorator has to say so: the span is marked, and the
    ///     status is <c>failure</c>, not <c>exception</c>.
    /// </summary>
    Task<Result> FailAsync(CancellationToken ct = default);

    /// <summary>A synchronous call, and the member the seam excludes.</summary>
    Result<int> Compute(int seed);

    /// <summary>A property: a read, not a call, so it carries no span.</summary>
    string Name { get; }
}
