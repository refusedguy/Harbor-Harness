namespace Harbor.Diagnostics;

/// <summary>
///     Ambient identifiers attached to every span and metric emitted inside a
///     run (sprint3-C "sessionId/turnId-атрибуты"). Carried across async
///     boundaries via AsyncLocal; the agent boundary pushes a scope at prompt
///     start and pops it at the end.
/// </summary>
public sealed record CorrelationContext(
    string? SessionId,
    string? AgentName,
    int Turn)
{
    public static CorrelationContext None { get; } = new(null, null, 0);
}

/// <summary>
///     Static access to the ambient <see cref="CorrelationContext" />.
///     Telemetry implementations read <see cref="Current" /> to auto-tag
///     spans/metrics; logging call sites may read it for structured fields.
/// </summary>
public static class Correlation
{
    private static readonly AsyncLocal<CorrelationContext?> Ambient = new();

    /// <summary>The context of the current async flow, or <see cref="CorrelationContext.None" />.</summary>
    public static CorrelationContext Current => Ambient.Value ?? CorrelationContext.None;

    /// <summary>
    ///     Push a context for the current async flow until the returned scope is
    ///     disposed. Always dispose in a finally block.
    /// </summary>
    public static IDisposable Push(CorrelationContext context)
        => new Scope(Ambient.Value, context);

    private sealed class Scope : IDisposable
    {
        private readonly CorrelationContext? _previous;
        private bool _disposed;

        public Scope(CorrelationContext? previous, CorrelationContext context)
        {
            _previous = previous;
            Ambient.Value = context;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
