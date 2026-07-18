using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Registration;

/// <summary>
///     <see cref="IPluginRegistrar" /> decorator that isolates failures per plugin.
///     <see cref="Register" /> never throws — every exception from the inner registrar
/// is caught, logged at <c>LogLevel.Error</c>, and returned as a failure result. This
/// lets the host continue loading subsequent plugins even when one throws during
/// <c>Initialize</c> or <c>Register*</c>.
/// </summary>
public sealed class SafePluginRegistrar : IPluginRegistrar
{
    private readonly IPluginRegistrar _inner;
    private readonly ILogger _logger;

    /// <summary>
    ///     Construct a new safe-registrar decorator.
    /// </summary>
    /// <param name="inner">Underlying registrar.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SafePluginRegistrar(IPluginRegistrar inner, ILogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Result Register(LoadedPlugin plugin, IPluginLoadHost host)
    {
        if (plugin is null)
            throw new ArgumentNullException(nameof(plugin));
        if (host is null)
            throw new ArgumentNullException(nameof(host));

        try
        {
            return _inner.Register(plugin, host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin registration threw for {DisplayName}", plugin.DisplayName);
            return Result.Failure($"Plugin registration threw for '{plugin.DisplayName}': {ex.Message}");
        }
    }
}
