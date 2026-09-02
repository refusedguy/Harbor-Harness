using Microsoft.Extensions.Logging;

namespace Harbor.Registries.Events;

/// <summary>
///     Type-allowlist middleware. Only events whose runtime type matches one
///     of the configured allowed types pass through.
/// </summary>
/// <remarks>
///     <para>
///         When constructed with no allowed types, all events pass through
///         (<c>_allowAll = true</c>). This makes the middleware a no-op by
///         default — useful as a base for configuration-driven filtering.
///     </para>
///     <para>
///         Uses a plain <c>for</c> loop with <c>Type</c> equality (not
///         <c>IsAssignableFrom</c>) for O(n) matching with zero reflection.
///         <see cref="AgentEvent.GetType()" /> is a built-in CLR method, not
///         reflection.
///     </para>
/// </remarks>
public sealed class TypeFilterMiddleware : IEventBusMiddleware
{
    public string Name => "type-filter";

    private readonly ILogger _logger;
    private readonly Type[] _allowedTypes;
    private readonly bool _allowAll;

    public TypeFilterMiddleware(ILogger<TypeFilterMiddleware> logger, params Type[] allowedTypes)
    {
        _logger = logger;
        if (allowedTypes.Length == 0)
        {
            _allowAll = true;
            _allowedTypes = Array.Empty<Type>();
        }
        else
        {
            _allowAll = false;
            _allowedTypes = allowedTypes;
        }
    }

    public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default)
    {
        if (_allowAll)
            return ValueTask.FromResult(true);

        int count = _allowedTypes.Length;
        for (int i = 0; i < count; i++)
        {
            if (_allowedTypes[i] == @event.GetType())
                return ValueTask.FromResult(true);
        }

        _logger.LogTrace("Filtered out event type {Type}", @event.GetType().Name);
        return ValueTask.FromResult(false);
    }
}
