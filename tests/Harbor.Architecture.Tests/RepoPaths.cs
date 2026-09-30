// RepoPaths.cs — filesystem layout helpers for the architecture tests that walk
// the repository instead of loading assemblies.
//
// Repo-root discovery walks up from AppContext.BaseDirectory looking for
// Harbor.slnx, the same convention used by the Avalonia/CLI/IPc test projects.
// If the marker is not found the helpers below return empty/false rather than
// throwing, so a published or trimmed test host degrades to "nothing to check"
// instead of failing the Release build with an unrelated error.

using System.Collections.Concurrent;
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

    /// <summary>
    ///     Every <c>src/**/*.csproj</c>, sorted for deterministic failure messages.
    /// </summary>
    /// <remarks>
    ///     Memoised because the per-project walkers call this once per project: without the
    ///     cache a rule that walks 52 projects re-walked the whole <c>src/</c> tree 52 times,
    ///     which is what #456's link resolution would otherwise have added on top of an
    ///     already-per-project walk. The tree does not change while the gate runs.
    /// </remarks>
    internal static IReadOnlyList<string> EnumerateSrcProjects() => CachedSrcProjects.Value;

    private static readonly Lazy<IReadOnlyList<string>> CachedSrcProjects = new(DiscoverSrcProjects);

    private static IReadOnlyList<string> DiscoverSrcProjects()
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

    /// <summary>
    ///     Every <c>*.cs</c> file a project directory COMPILES: the files under it, plus the
    ///     shared-source files it pulls in with <c>&lt;Compile Include&gt;</c> link items.
    ///     Build output excluded.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #456: the linked part is the point, not a convenience. A csproj-less folder such
    ///         as <c>src/Harbor.Storage.Shared</c> is compiled INTO <c>Harbor.Storage.Jsonl</c>
    ///         and <c>Harbor.Storage.Sqlite</c>, so the code is in those assemblies and the
    ///         IL-level rules judge it. But a rule that walks <c>src/&lt;projectDir&gt;/**</c>
    ///         cannot see the file, because the file is not under the project directory — so
    ///         "which files of this project bind the forbidden target?" came back without it,
    ///         and a violation written in shared source reported as no violation at all.
    ///     </para>
    ///     <para>
    ///         The set is read from the csproj XML, so it is the compiler's own input rather
    ///         than a second hand-maintained list that could drift from the build. A project
    ///         that links nothing extra is unaffected: <c>Harbor.Storage.Memory</c> still
    ///         returns its own files only, which is what
    ///         <c>SharedSourceLinkRules.The_Per_Project_File_Walk_Does_Not_Invent_Links</c>
    ///         pins.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> EnumerateCsFiles(string projectDir)
    {
        if (RepoRoot is null)
        {
            return [];
        }

        string dir = Path.Combine(RepoRoot, "src", projectDir);
        var found = new List<string>();

        if (Directory.Exists(dir))
        {
            found.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }

        found.AddRange(EnumerateLinkedSourceFiles(projectDir));

        return
        [
            .. found
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The shared-source files <paramref name="projectDir" /> compiles via
    ///     <c>&lt;Compile Include&gt;</c> link items, as absolute paths.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only items resolving OUTSIDE the project directory count: a link item naming a
    ///         file the project already owns is redundant, and counting it would let a rule
    ///         pass on a file that is really the project's own.
    ///     </para>
    ///     <para>
    ///         A resolved-but-absent path is skipped here. This method answers "which files does
    ///         this project compile", and MSBuild already rejects a missing include, so a path
    ///         with nothing behind it is not evidence about the file set.
    ///         <c>SharedSourceLinkRules.Every_Link_Item_Resolves_Into_A_Declared_Folder</c>
    ///         checks the manifest side, where an entry with no file behind it IS a defect.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> EnumerateLinkedSourceFiles(string projectDir)
    {
        if (RepoRoot is null)
        {
            return [];
        }

        string? csproj = FindSrcProject(projectDir);
        if (csproj is null)
        {
            return [];
        }

        string projectRoot = Path.GetFullPath(Path.Combine(RepoRoot, "src", projectDir));
        var result = new List<string>();

        foreach (string include in ReadCompileIncludes(csproj))
        {
            string resolved = Path.GetFullPath(
                Path.Combine(projectRoot, include.Replace('\\', Path.DirectorySeparatorChar)));

            bool outside = !resolved.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            if (outside && File.Exists(resolved))
            {
                result.Add(resolved);
            }
        }

        return result;
    }

    /// <summary>
    ///     The <c>Include</c> paths of every <c>&lt;Compile&gt;</c> item a csproj declares,
    ///     verbatim (project-relative, un-normalised) and in document order.
    /// </summary>
    /// <remarks>
    ///     Memoised per csproj path: the shared-source rules and the per-project walkers all ask
    ///     the same question of the same 52 files, and re-parsing each one on every call made the
    ///     architecture gate measurably slower for no new information.
    /// </remarks>
    internal static IReadOnlyList<string> ReadCompileIncludes(string csprojPath) =>
        CompileIncludeCache.GetOrAdd(csprojPath, ParseCompileIncludes);

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> CompileIncludeCache =
        new(StringComparer.Ordinal);

    private static IReadOnlyList<string> ParseCompileIncludes(string csprojPath)
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
