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
///         #1055 slice 3: the present-branch is the default compile+execute
///         route — the CLI registers the located binary as the
///         <c>harbor-csharp-plugins</c> stdio MCP server and never compiles
///         CS plugins in-process. The probe itself spawns nothing and
///         touches no network.
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
        => LocateHost(baseDirectory) is not null;

    /// <summary>
    ///     Full path of the out-of-process plugin host binary, or null when it
    ///     does not ship next to the current binary.
    /// </summary>
    /// <remarks>
    ///     #1055 slice 3: the out-of-process host is the default CS-plugin
    ///     route. The CLI registers this path as the
    ///     <c>harbor-csharp-plugins</c> stdio MCP server
    ///     (<c>ToolsCatalog.CreateMcpRegistry</c>), so CS plugins execute in
    ///     the host process and the CLI never compiles them in-process.
    /// </remarks>
    /// <param name="baseDirectory">
    ///     Directory to probe. Defaults to <see cref="AppContext.BaseDirectory" />.
    ///     Exposed for tests; production callers leave it null.
    /// </param>
    public static string? LocateHost(string? baseDirectory = null)
    {
        string dir = baseDirectory ?? AppContext.BaseDirectory;
        string candidate = Path.Combine(dir, HostBinaryName);
        if (File.Exists(candidate))
            return candidate;
        string exeCandidate = Path.Combine(dir, HostBinaryName + ".exe");
        return File.Exists(exeCandidate) ? exeCandidate : null;
    }
}
