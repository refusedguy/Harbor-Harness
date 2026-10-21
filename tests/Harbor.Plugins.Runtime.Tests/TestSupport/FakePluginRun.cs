using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     A run the test drives by hand: it answers a stop without touching a host
///     (#1737).
/// </summary>
public sealed class FakePluginRun : IPluginRun
{
    /// <summary>
    ///     Construct a run over the given plugin.
    /// </summary>
    /// <param name="plugin">The plugin the run belongs to.</param>
    /// <param name="mode">How the run came to be.</param>
    public FakePluginRun(LoadedPlugin plugin, PluginRunMode mode = PluginRunMode.Run)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        Name = plugin.Name;
        Version = plugin.Version;
        Mode = mode;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Version Version { get; }

    /// <inheritdoc />
    public PluginRunMode Mode { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>How many times the run was stopped.</summary>
    public int StopCount { get; private set; }

    /// <inheritdoc />
    public Task<Result> StopAsync(CancellationToken ct = default)
    {
        StopCount++;
        return Task.FromResult(Result.Success());
    }
}
