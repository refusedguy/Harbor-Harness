using CSharpFunctionalExtensions;
using Harbor.Abstractions.Contracts;

namespace Harbor.Telemetry.Fixtures;

/// <summary>
///     The seam whose MEMBER carries the marker. The member's own statement
///     wins over every coarser level, so the span name is the one stated here
///     and the arguments are captured — the only way to ask for arguments,
///     because they are potentially user data.
/// </summary>
public interface IFixtureWithArgs
{
    /// <summary>
    ///     The observed call. Its arguments land in the span as
    ///     <c>arg.label</c> and <c>arg.count</c>.
    /// </summary>
    [Observed("fixture.with_args", CaptureArguments = true)]
    Task<Result> RunAsync(string label, int count, CancellationToken ct = default);
}
