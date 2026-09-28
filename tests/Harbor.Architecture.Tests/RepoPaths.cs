// RepoPaths.cs — filesystem layout helpers for the architecture tests that walk
// the repository instead of loading assemblies.
//
// Repo-root discovery walks up from AppContext.BaseDirectory looking for
// Harbor.slnx, the same convention used by the Avalonia/CLI/IPc test projects.
// If the marker is not found the helpers below return empty/false rather than
// throwing, so a published or trimmed test host degrades to "nothing to check"
// instead of failing the Release build with an unrelated error.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Locates repository files from a test host that may be running out of a
///     nested <c>bin/</c> directory.
/// </summary>
internal static class RepoPaths
{
    /// <summary>The solution file that marks the repository root.</summary>
    private const string RootMarker = "Harbor.slnx";

    /// <summary>Repo-relative path of the README template, quoted in failure messages.</summary>
    internal const string ReadmeTemplateRelativePath = "docs/standards/README-template.md";

    /// <summary>Absolute path of the repository root, or <c>null</c> when not running from a checkout.</summary>
    internal static string? RepoRoot { get; } = FindRepoRoot();

    /// <summary>Every <c>src/**/*.csproj</c>, sorted for deterministic failure messages.</summary>
    internal static IReadOnlyList<string> EnumerateSrcProjects()
    {
        if (RepoRoot is null)
        {
            return [];
        }

        string src = Path.Combine(RepoRoot, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        string[] found = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories);

        // Never descend into build output — a stale obj/ copy would be counted
        // as a second project.
        return [.. found.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                  .OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>The <c>src/</c> csproj whose directory name is <paramref name="projectName" />.</summary>
    internal static string? FindSrcProject(string projectName)
    {
        foreach (string csproj in EnumerateSrcProjects())
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(csproj)!), projectName, StringComparison.Ordinal))
            {
                return csproj;
            }
        }

        return null;
    }

    /// <summary>Whether a csproj opts into NuGet packing.</summary>
    internal static bool IsPackable(string csprojPath)
    {
        try
        {
            return File.ReadAllText(csprojPath)
                       .Contains("<IsPackable>true</IsPackable>", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Absolute path of the README that must accompany a project directory.</summary>
    internal static string ReadmeFor(string projectDir) => Path.Combine(projectDir, "README.md");

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
