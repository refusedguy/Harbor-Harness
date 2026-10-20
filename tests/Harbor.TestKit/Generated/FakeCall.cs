namespace Harbor.TestKit.Generated;

/// <summary>
///     One recorded call on a generated test double (issue #1769).
/// </summary>
/// <param name="Member">The interface member that was called.</param>
/// <param name="Arguments">
///     The call arguments, in declaration order. A
///     <see cref="System.Threading.CancellationToken" /> argument is NOT
///     recorded — it is noise in a call log — and an <c>out</c> argument is
///     recorded as its default value.
/// </param>
public sealed record FakeCall(string Member, IReadOnlyList<object?> Arguments);

/// <summary>
///     A disposable that does nothing — the default return value of a generated
///     member whose type is <see cref="IDisposable" /> (issue #1769).
/// </summary>
public sealed class FakeDisposable : IDisposable
{
    /// <summary>The shared instance.</summary>
    public static FakeDisposable Instance { get; } = new();

    private FakeDisposable()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
