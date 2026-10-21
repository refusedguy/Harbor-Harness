using CSharpFunctionalExtensions;

namespace Harbor.Telemetry.Fixtures;

/// <summary>
///     The seam that carries NO marker. It is the negative control of the
///     fixture: the generator must emit nothing for it, and the guard proves
///     that it does not decorate everything it sees.
/// </summary>
public interface IPlainFixture
{
    /// <summary>A call that looks exactly like the observed one.</summary>
    Task<Result> DoWorkAsync(CancellationToken ct = default);
}
