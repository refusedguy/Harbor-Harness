namespace Harbor.Plugins.Hosting;

/// <summary>
///     Probe for the out-of-process plugin host binary
///     (<c>harbor-plugins-host</c>, see <c>Harbor.Plugins.Host</c> AssemblyName).
/// </summary>
/// <remarks>
///     <para>
///         #1055 slice 1 (graceful absence): plugins are optional, so startup
///         must distinguish "nothing to load" from "nowhere to load it". When
///         no plugin scripts exist anywhere and this probe is negative, the CLI
///         skips the whole compile pipeline — including the Roslyn side of the
///         composition — and reports one honest line instead of an error.
///     </para>
///     <para>
///         Slice 3 turns the present-branch into the default compile+execute
///         route; until then the probe only decides which absence line is
///         logged. It spawns nothing and touches no network.
///     </para>
/// </remarks>
public static class PluginHostLocator
{
    /// <summary>Out-of-process host binary name (matches the host AssemblyName).</summary>
    public const string HostBinaryName = "harbor-plugins-host";

    /// <summary>
    ///     True when the out-of-process plugin host ships next to the current binary.
    /// </summary>
    /// <param name="baseDirectory">
    ///     Directory to probe. Defaults to <see cref="AppContext.BaseDirectory" /> —
    ///     the layout a published CLI produces (host next to the CLI binary).
    ///     Exposed for tests; production callers leave it null.
    /// </param>
    public static bool IsHostAvailable(string? baseDirectory = null)
    {
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        return File.Exists(Path.Combine(dir, HostBinaryName))
            || File.Exists(Path.Combine(dir, HostBinaryName + ".exe"));
    }
}
