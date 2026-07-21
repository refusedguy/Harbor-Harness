namespace Harbor.E2E.Framework;
/// <summary>
///     Helpers for resolving the path to a built Harbor app executable and
///     locating the .NET host. Centralised so all drivers agree on the build
///     layout — the alternative (each driver hardcoding paths) breaks when
///     `dotnet test` runs from different working directories.
/// </summary>
internal static class HarborAppLocator
{
    /// <summary>
    ///     Locate the repository root by walking up from the test bin directory
    ///     until <c>Harbor.slnx</c> is found. Falls back to the current working
    ///     directory if the slnx is never seen (typically only happens when
    ///     running tests against an installed package, not from a source checkout).
    /// </summary>
    public static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    ///     Resolve the path to the project file (<c>.csproj</c>) for a Harbor
    ///     app, relative to the repo root. Throws if the file does not exist —
    ///     the test should be skipped (via <c>[Skip]</c>) rather than fail with
    ///     a confusing FileNotFoundException.
    /// </summary>
    public static string ResolveProjectPath(string relativePath)
    {
        string root = FindRepoRoot();
        string full = Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            throw new FileNotFoundException(
                $"Harbor app project not found at '{full}'. " +
                "Ensure the test is running from a source checkout with the project built.", full);
        return full;
    }

    /// <summary>
    ///     Returns the path to <c>dotnet</c> on the current system. Prefers the
    ///     <c>DOTNET_HOST_PATH</c> env var (set by the .NET SDK when invoking
    ///     test host), then <c>PATH</c> lookup, then a fallback to the well-known
    ///     <c>$HOME/.dotnet/dotnet</c> install location used in CI containers.
    /// </summary>
    public static string ResolveDotnetHost()
    {
        string? env = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        string? home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string fallback = Path.Combine(home, ".dotnet", "dotnet");
            if (File.Exists(fallback))
                return fallback;
        }

        // Trust PATH — `dotnet` should be on it on any developer machine.
        return "dotnet";
    }
}
