namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Granular capability a CS-source plugin may declare in its manifest directive
///     (<c>// harbor:capabilities &lt;name&gt;[,&lt;name&gt;...]</c>) and that the user
///     approves per-plugin at trust time. Fail-closed: anything not declared AND not
///     approved is denied.
/// </summary>
public enum PluginCapability
{
    /// <summary>Read files from disk (File.ReadAllText, File.OpenRead, ...).</summary>
    ReadFiles,

    /// <summary>Create, modify or delete files (File.WriteAllText, File.Delete, ...).</summary>
    WriteFiles,

    /// <summary>Spawn external processes (Process.Start).</summary>
    RunProcesses,

    /// <summary>Make outbound HTTP requests (HttpClient, WebRequest).</summary>
    HttpRequests,

    /// <summary>Spawn or register sub-agents (Task tool usage).</summary>
    SubAgents,

    /// <summary>Read environment variables (Environment.GetEnvironmentVariable).</summary>
    ReadEnv,
}

/// <summary>
///     Static parsing/serialization helpers for <see cref="PluginCapability" />.
///     Parsing is strict — an unrecognized capability name makes the whole manifest
///     invalid (fail-closed), instead of silently ignoring the unknown token.
/// </summary>
public static class PluginCapabilities
{
    /// <summary>All known capabilities, in declaration order.</summary>
    public static readonly IReadOnlyList<PluginCapability> All =
    [
        PluginCapability.ReadFiles,
        PluginCapability.WriteFiles,
        PluginCapability.RunProcesses,
        PluginCapability.HttpRequests,
        PluginCapability.SubAgents,
        PluginCapability.ReadEnv,
    ];

    /// <summary>
    ///     Parse a comma/space-separated capability list. Returns failure on any unknown
    ///     name — unknown capability = deny, never allow-by-default.
    /// </summary>
    public static Result<IReadOnlySet<PluginCapability>> TryParse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Result.Success<IReadOnlySet<PluginCapability>>(FrozenEmpty);

        var result = new HashSet<PluginCapability>();
        foreach (var raw in csv.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSingle(raw, out var capability))
                return Result.Failure<IReadOnlySet<PluginCapability>>($"Unknown plugin capability '{raw}'.");
            result.Add(capability);
        }

        return Result.Success<IReadOnlySet<PluginCapability>>(result);
    }

    /// <summary>
    ///     Serialize capabilities to the canonical lowercase manifest form
    ///     (e.g. <c>read_files,http_requests</c>).
    /// </summary>
    public static string ToManifestString(IReadOnlySet<PluginCapability> capabilities)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var cap in capabilities)
        {
            if (sb.Length > 0)
                sb.Append(',');
            sb.Append(ToName(cap));
        }

        return sb.ToString();
    }

    /// <summary>Canonical lowercase name of a capability (e.g. <c>read_files</c>).</summary>
    public static string ToName(PluginCapability capability) => capability switch
    {
        PluginCapability.ReadFiles => "read_files",
        PluginCapability.WriteFiles => "write_files",
        PluginCapability.RunProcesses => "run_processes",
        PluginCapability.HttpRequests => "http_requests",
        PluginCapability.SubAgents => "sub_agents",
        PluginCapability.ReadEnv => "read_env",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    private static bool TryParseSingle(string name, out PluginCapability capability)
    {
        capability = name switch
        {
            "read_files" => PluginCapability.ReadFiles,
            "write_files" => PluginCapability.WriteFiles,
            "run_processes" => PluginCapability.RunProcesses,
            "http_requests" => PluginCapability.HttpRequests,
            "sub_agents" => PluginCapability.SubAgents,
            "read_env" => PluginCapability.ReadEnv,
            _ => PluginCapability.ReadFiles,
        };
        return name is "read_files" or "write_files" or "run_processes" or "http_requests" or "sub_agents" or "read_env";
    }

    private static readonly IReadOnlySet<PluginCapability> FrozenEmpty =
        new HashSet<PluginCapability>();
}
