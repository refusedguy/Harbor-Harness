namespace Harbor.Hosting;

using Harbor.Abstractions.Env;

/// <summary>
///     Forwarder over <see cref="Harbor.Abstractions.Filesystem.HarborPaths" />.
///     The canonical home resolution moved down to Harbor.Abstractions (#1570)
///     so Harbor.Application (WorkspacePaths) and Harbor.Tools.Builtin
///     (McpOAuthTokenCache) resolve the same root without referencing this
///     composition root; app-layer call sites keep resolving this type.
/// </summary>
public static class HarborPaths
{
    /// <summary>Environment variable overriding the Harbor home directory.</summary>
    public const string HarborHomeVariable = HarborEnv.Home;

    /// <summary>Forwarded from <c>Harbor.Abstractions.Filesystem.HarborPaths</c>.</summary>
    public static string GetHarborHome() =>
        Harbor.Abstractions.Filesystem.HarborPaths.GetHarborHome();
}
