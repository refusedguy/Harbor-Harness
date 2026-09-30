// RepoPaths.cs — filesystem layout helpers for the architecture tests that walk
// the repository instead of loading assemblies.
//
// Repo-root discovery walks up from AppContext.BaseDirectory looking for
// Harbor.slnx, the same convention used by the Avalonia/CLI/IPc test projects.
// If the marker is not found the helpers below return empty/false rather than
// throwing, so a published or trimmed test host degrades to "nothing to check"
// instead of failing the Release build with an unrelated error.

using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Locates repository files from a test host that may be running out of a
///     nested <c>bin/</c> directory.
/// </summary>
internal static class RepoPaths
{
    /// <summary>The solution file that marks the repository root.</summary>
    private const string RootMarker = "Harbor.slnx";

    // MSBuild is case-insensitive on Windows and case-sensitive on Linux; a glob
    // for "Harbor.slnx" therefore fails to find a checkout whose solution file is
    // upper-cased. Probe both spellings when walking up from the test bin directory.
    private static readonly string[] RootMarkers = [RootMarker, "Harbor.SLNX"];

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

    /// <summary>Absolute path of the <c>src/&lt;projectDir&gt;</c> directory, or <c>null</c> outside a checkout.</summary>
    internal static string? FindProjectDir(string projectDir)
        => RepoRoot is null ? null : Path.Combine(RepoRoot, "src", projectDir);

    /// <summary>
    ///     Every project in the repository mapped to its produced assembly simple
    ///     name (the <c>&lt;AssemblyName&gt;</c> property, defaulting to the project
    ///     directory name).
    /// </summary>
    /// <remarks>
    ///     This is the ground truth for "does this assembly name exist?". Several
    ///     architecture rules used to name assemblies that no project produces (e.g.
    ///     <c>Harbor.Scripting</c> — a project family, never an assembly), which
    ///     makes such a constraint vacuous: it can never fail. Checking names
    ///     against the real project inventory turns that class of typo/rename/deletion
    ///     into a hard failure.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> EnumerateRepoAssemblyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RepoRoot is null)
        {
            return result;
        }

        foreach (string csproj in Directory.GetFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || csproj.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
            result[ReadAssemblyName(csproj, projectDir)] = projectDir;
        }

        return result;
    }

    /// <summary>
    ///     Reads a csproj and returns the project directory names it declares as a
    ///     <c>&lt;ProjectReference&gt;</c>, split by whether the reference is
    ///     consumed as an analyzer/source generator.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The IL-level matrix rules (<c>Assembly.GetReferencedAssemblies()</c>)
    ///         cannot see a <c>&lt;ProjectReference&gt;</c> whose types are never
    ///         bound — Roslyn drops the assembly from the consumer's reference list.
    ///         Reading the csproj directly closes that hole: a declared edge must be
    ///         justified in the matrix even when it produces no IL.
    ///     </para>
    ///     <para>
    ///         <c>OutputItemType=Analyzer</c> references are source generators, not
    ///         layer dependencies, and are reported separately so they have to be
    ///         declared explicitly rather than silently ignored.
    ///     </para>
    /// </remarks>
    /// <returns>(reference project dir names, analyzer project dir names)</returns>
    internal static (string[] References, string[] Analyzers) ReadProjectReferences(string csprojPath)
    {
        var references = new List<string>();
        var analyzers = new List<string>();

        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return ([], []);
        }

        string projectDir = Path.GetDirectoryName(csprojPath)!;
        foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            string? include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            string resolved = Path.GetFullPath(
                Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
            string name = Path.GetFileNameWithoutExtension(resolved);

            string? outputItemType = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "OutputItemType")?.Value;
            if (string.Equals(outputItemType, "Analyzer", StringComparison.Ordinal))
            {
                analyzers.Add(name);
            }
            else
            {
                references.Add(name);
            }
        }

        return ([.. references], [.. analyzers]);
    }

    /// <summary>Every <c>*.cs</c> file under a project directory, excluding build output.</summary>
    internal static IReadOnlyList<string> EnumerateCsFiles(string projectDir)
    {
        if (RepoRoot is null)
        {
            return [];
        }

        string dir = Path.Combine(RepoRoot, "src", projectDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The <c>Include</c> paths of every <c>&lt;Compile&gt;</c> item a csproj declares,
    ///     verbatim (project-relative, un-normalised) and in document order.
    /// </summary>
    internal static IReadOnlyList<string> ReadCompileIncludes(string csprojPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(csprojPath, LoadOptions.None);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }

        return
        [
            .. document.Descendants()
                .Where(e => e.Name.LocalName == "Compile")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
        ];
    }

    private static string ReadAssemblyName(string csprojPath, string projectDir)
    {
        try
        {
            XDocument document = XDocument.Load(csprojPath, LoadOptions.None);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == "AssemblyName" && element.Value.Length > 0)
                {
                    return element.Value.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            // Fall through to the directory-name default.
        }

        return projectDir;
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
            foreach (string marker in RootMarkers)
            {
                if (File.Exists(Path.Combine(dir.FullName, marker)))
                {
                    return dir.FullName;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
