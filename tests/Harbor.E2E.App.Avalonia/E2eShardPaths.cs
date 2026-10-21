namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     The directories one shard process writes into.
/// </summary>
/// <remarks>
///     <para>
///         This project runs as SEVERAL PROCESSES (see
///         <c>e2e-shards.json</c> and <c>harbor-gates run-e2e-avalonia-shards</c>):
///         the Avalonia <c>Application</c>, the <c>$HOME</c> the driver sets and
///         the Harbor home <c>CommonConfig.OverrideHarborHomeForTests</c> pins
///         are all process-wide, so a class cannot share a process with another
///         class that is driving the same window.
///     </para>
///     <para>
///         <b>Why the screenshots need their own directory.</b> The screenshot
///         directory used to be shared by the whole project, and
///         <c>AvaloniaUiTests</c> DELETES the stale <c>??-*.png</c> files in it
///         when it initialises. With two processes running side by side, one
///         process's initialisation deletes the frames the other one had just
///         written - a silent loss of evidence, and the kind that only shows up
///         as "the screenshot is missing" weeks later.
///     </para>
///     <para>
///         So the runner hands each process a directory of its own through
///         <see cref="ShardDirectoryVariable" />, and every screenshot path in
///         this project is resolved THROUGH it. The fallback (no variable set)
///         is the historical single-process location, so a developer's local
///         run still writes where it always did.
///     </para>
/// </remarks>
internal static class E2eShardPaths
{
    /// <summary>
    ///     The environment variable the runner sets: the directory this process
    ///     may write screenshots into. Absent when the suite runs as ONE
    ///     process (a local run), in which case the historical paths are used.
    /// </summary>
    internal const string ShardDirectoryVariable = "HARBOR_E2E_SHARD_DIR";

    /// <summary>
    ///     The environment variable the runner sets: this process's shard id.
    ///     Used to keep per-class paths distinct between processes.
    /// </summary>
    internal const string ShardIdVariable = "HARBOR_E2E_SHARD_ID";

    /// <summary>This process's shard id, or <c>null</c> outside a sharded run.</summary>
    internal static string? ShardId { get; } = Read(ShardIdVariable);

    /// <summary>This process's private screenshot root, or <c>null</c> outside a sharded run.</summary>
    internal static string? ShardDirectory { get; } = Read(ShardDirectoryVariable);

    /// <summary>Whether this process is one shard of a multi-process run.</summary>
    internal static bool IsSharded => ShardDirectory is not null;

    /// <summary>
    ///     A suffix that makes a per-class path unique to this process.
    ///     Empty outside a sharded run, so local paths do not change.
    /// </summary>
    internal static string ProcessSuffix
        => ShardId is { Length: > 0 } id ? "-" + id : string.Empty;

    /// <summary>
    ///     Where this process writes the screenshots of <paramref name="leaf" />.
    /// </summary>
    /// <param name="leaf">The sub-directory of the shard root, e.g. <c>avalonia-ui</c>.</param>
    /// <param name="fallback">
    ///     The directory to use when the suite runs as ONE process - the
    ///     historical location, unchanged.
    /// </param>
    internal static string Screenshots(string leaf, string fallback)
        => ShardDirectory is { } root ? Path.Combine(root, leaf) : fallback;

    private static string? Read(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
