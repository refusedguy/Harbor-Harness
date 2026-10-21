using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Instantiation;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Registration;

/// <summary>
///     The live run of one plugin: the registrations it made, and the way to take
///     them back (#1737).
/// </summary>
/// <remarks>
///     <para>
///         The run is created by the registration and owned by nobody else. It
///         holds the ledger — every registration the host accepted, and the way
///         to undo it — so stopping it undoes EXACTLY that set and nothing else.
///     </para>
///     <para>
///         Stopping does two things, in this order: the registrations are taken
///         back first, then the plugin is shut down. The order matters. Taking
///         the registrations back first means the host's registries are consistent
///         even if the plugin's own shutdown throws — a plugin that dies on the
///         way out does not leave its tool behind. Shutting down afterwards gives
///         the plugin a chance to release what it holds, knowing its
///         registrations are already gone.
///     </para>
/// </remarks>
internal sealed class PluginRun : IPluginRun
{
    private readonly LoadedPlugin _plugin;
    private readonly RecordingPluginLoadHost _ledger;
    private readonly ILogger _logger;
    private readonly PluginRunMode _mode;
    private readonly DateTimeOffset _startedAt;
    private int _stopped;

    /// <summary>
    ///     Start a run over the registrations the ledger recorded.
    /// </summary>
    /// <param name="plugin">The plugin the run belongs to.</param>
    /// <param name="ledger">The ledger of what the host accepted.</param>
    /// <param name="logger">Logger for the diagnostics the stop emits.</param>
    /// <param name="mode">How this run came to be.</param>
    internal PluginRun(
        LoadedPlugin plugin,
        RecordingPluginLoadHost ledger,
        ILogger logger,
        PluginRunMode mode)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mode = mode;
        _startedAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public string Name => _plugin.Name;

    /// <inheritdoc />
    public Version Version => _plugin.Version;

    /// <inheritdoc />
    public PluginRunMode Mode => _mode;

    /// <inheritdoc />
    public DateTimeOffset StartedAt => _startedAt;

    /// <inheritdoc />
    public async Task<Result> StopAsync(CancellationToken ct = default)
    {
        // A second stop has nothing to take back. Saying so is the whole point:
        // a caller that believes it tore something down when it tore nothing
        // down is the lie this model exists to remove.
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return Result.Failure($"The run of plugin '{Name}' was already stopped.");
        }

        Result rollback = _ledger.Rollback();
        Result shutdown = await PluginLifecycle.ShutdownAsync(_plugin.Instance, ct).ConfigureAwait(false);

        if (rollback.IsFailure)
        {
            // The registrations are the half that matters: a tool left behind is
            // worse than a plugin that did not shut down cleanly. The rollback's
            // own reason is the message; the shutdown's verdict goes to the log,
            // so one message carries one cause.
            if (shutdown.IsFailure)
            {
                _logger.LogWarning(
                    "Plugin {Name} was shut down with an error: {Error}",
                    Name,
                    shutdown.Error);
            }

            return rollback.MapError(e =>
                $"Stopping plugin '{Name}' left its registrations behind: {e}");
        }

        return shutdown;
    }
}
