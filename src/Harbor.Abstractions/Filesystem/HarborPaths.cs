namespace Harbor.Abstractions.Filesystem;

/// <summary>
///     Single resolution point for the Harbor home directory
///     (<c>~/.harbor</c>): config, sessions, plugins, MCP overlays.
/// </summary>
/// <remarks>
///     Moved verbatim from <c>Harbor.Hosting</c> (#1570): Harbor.Application
///     and Harbor.Tools.Builtin must resolve the same root without
///     referencing the Hosting composition root; Harbor.Hosting keeps a
///     forwarder over this type.
///     <para>
///         <c>HARBOR_HOME</c> wins when set (absolute path expected) — this is
///         how test hosts get a hermetic config root: on Windows
///         <see cref="Environment.SpecialFolder.UserProfile" /> resolves from
///         the user token and ignores a swapped <c>USERPROFILE</c> process
///         variable, so HOME/USERPROFILE isolation alone leaks the dev-box
///         <c>~/.harbor/config.json</c> into tests. It also enables portable
///         installs (e.g. <c>HARBOR_HOME=D:\harbor-home</c>).
///     </para>
/// </remarks>
public static class HarborPaths
{
    /// <summary>Environment variable overriding the Harbor home directory.</summary>
    public const string HarborHomeVariable = "HARBOR_HOME";

    /// <summary>
    ///     Resolve the Harbor home directory: <c>HARBOR_HOME</c> when set to a
    ///     non-empty value, else <c>~/.harbor</c> under the user profile.
    /// </summary>
    public static string GetHarborHome()
    {
        if (Environment.GetEnvironmentVariable(HarborHomeVariable) is { Length: > 0 } custom)
            return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
    }
}
