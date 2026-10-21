namespace Harbor.Plugins.Abstractions;

/// <summary>
///     A live RUN of a plugin definition (#1737).
/// </summary>
/// <remarks>
///     <para>
///         The run is the handle that makes a registration reversible. It knows
///         exactly which registrations the plugin made, and stopping it takes
///         precisely those back — not one more, not one less — and then shuts
///         the plugin down. The definition survives the stop: what is left is a
///         definition with no run, which is a state the host can report and a
///         caller can run again.
///     </para>
///     <para>
///         A run is created by the registration itself. There is no way to have a
///         run without registrations and no way to register without a run: the
///         two are the same fact, seen from two sides.
///     </para>
/// </remarks>
public interface IPluginRun
{
    /// <summary>The plugin's stable id.</summary>
    public string Name { get; }

    /// <summary>The plugin's own version.</summary>
    public Version Version { get; }

    /// <summary>
    ///     How this run came to be: the definition was started, or it replaced
    ///     the one that was running.
    /// </summary>
    public PluginRunMode Mode { get; }

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    ///     Stop the run: take back what it registered, and shut the plugin down.
    /// </summary>
    /// <remarks>
    ///     Stopping twice is a failure, not a silent success: the second stop has
    ///     nothing to take back, and a caller that believes it did something when
    ///     it did nothing is exactly the kind of lie this model exists to remove.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or a failure saying what could not be taken back.</returns>
    public Task<Result> StopAsync(CancellationToken ct = default);
}
