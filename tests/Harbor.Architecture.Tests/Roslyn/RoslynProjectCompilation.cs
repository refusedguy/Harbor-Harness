// RoslynProjectCompilation.cs — PILOT harness for issue #1729: one Roslyn
// compilation per product project, built once and shared.
//
// WHY THIS FILE EXISTS
// --------------------
// The pilot analyzers in `HarborArchAnalyzers.cs` need a COMPILATION, not a
// file: a symbol is what tells the analyzer that `Assembly.Load` is a method
// on `System.Reflection.Assembly` and that `CollectiblePluginLoadContext`
// derives from `AssemblyLoadContext`. A compilation also carries the one fact
// no text scan can recover — which ASSEMBLY the source belongs to — because a
// compilation IS one assembly.
//
// The harness therefore does what the text rules cannot: it maps a project to
// its own sources (including the shared-source folders the csproj links into
// it) and compiles them against the references the test host already has.
//
// WHY ONE COMPILATION PER PROJECT, AND NOT ONE FOR THE WHOLE TREE
// ---------------------------------------------------------------
// Because `NamespaceOwnershipAnalyzer` asks "which assembly declares this
// namespace?" — a question with no answer unless each project is compiled
// separately. One compilation for the whole tree would answer it with the
// tree's name, which is worse than no answer.
//
// THE REFERENCES
// --------------
// The framework assemblies come from `TRUSTED_PLATFORM_ASSEMBLIES` (the
// standard way an analyzer test host finds its own runtime) and the product
// assemblies from the test's own output directory, so `Result<T>` and the
// Harbor contracts resolve. Reference resolution failures do NOT fail this
// harness: the pilot analyzers grade the symbols they care about, and a
// project whose generated code is missing (the Avalonia app's XAML-generated
// members, for instance) still resolves every BCL symbol.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Harbor.Architecture.Tests;

/// <summary>Builds one Roslyn compilation per product project, for the pilot analyzers.</summary>
internal static class RoslynProjectCompilation
{
    /// <summary>One product project: where it lives, what it is called, what it compiles.</summary>
    /// <param name="Tree">The top-level tree the project lives in (<c>src</c> or <c>apps</c>).</param>
    /// <param name="Dir">The project directory name.</param>
    /// <param name="AssemblyName">The assembly the project produces.</param>
    internal readonly record struct Project(string Tree, string Dir, string AssemblyName);

    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences = new(BuildReferences);

    /// <summary>The references every pilot compilation is built against.</summary>
    internal static ImmutableArray<MetadataReference> References => LazyReferences.Value;

    /// <summary>
    ///     Every product project: the <c>src/</c> projects and the <c>apps/</c>
    ///     entry points, in a stable order.
    /// </summary>
    internal static IReadOnlyList<Project> ProductProjects()
    {
        var result = new List<Project>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return result;
        }

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string dir = Path.GetFileName(Path.GetDirectoryName(csproj)!)!;
            result.Add(new Project("src", dir, RepoPaths.ReadAssemblyName(csproj, dir)));
        }

        string apps = Path.Combine(root, "apps");
        if (Directory.Exists(apps))
        {
            foreach (string csproj in Directory.GetFiles(apps, "*.csproj", SearchOption.AllDirectories))
            {
                if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string dir = Path.GetFileName(Path.GetDirectoryName(csproj)!)!;
                result.Add(new Project("apps", dir, RepoPaths.ReadAssemblyName(csproj, dir)));
            }
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Dir, b.Dir));
        return result;
    }

    /// <summary>
    ///     The compilation of one product project: its own sources plus the
    ///     shared-source folders its csproj links into it.
    /// </summary>
    internal static CSharpCompilation ForProject(Project project)
    {
        var trees = new List<SyntaxTree>();

        foreach (string path in SourceFilesOf(project))
        {
            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(
                text,
                new CSharpParseOptions(LanguageVersion.Preview),
                path: path));
        }

        return CSharpCompilation.Create(
            project.AssemblyName,
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    /// <summary>
    ///     The files one project compiles. <c>src/</c> projects are read through
    ///     the shared helper, which resolves the <c>&lt;Compile Include&gt;</c>
    ///     link items into the shared-source folders; <c>apps/</c> projects own
    ///     everything under their directory.
    /// </summary>
    private static IReadOnlyList<string> SourceFilesOf(Project project)
    {
        if (project.Tree == "src")
        {
            return RepoPaths.EnumerateCsFiles(project.Dir);
        }

        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, project.Tree, project.Dir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(static p =>
                    !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(static p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The framework assemblies the test host runs on, plus the assemblies
    ///     copied next to it — the product ones and the NuGet ones the product
    ///     uses.
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        var candidates = new List<string>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            candidates.AddRange(trusted.Split(Path.PathSeparator));
        }

        try
        {
            candidates.AddRange(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"));
        }
        catch (IOException)
        {
            // A test host with no readable output directory has no product
            // references; the framework ones are still there.
        }

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            string simple = Path.GetFileNameWithoutExtension(path);
            if (!seen.Add(simple))
            {
                continue;
            }

            try
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                // A native or unreadable file in the directory is not a managed
                // reference, and skipping it is the only correct answer.
                _ = ex;
            }
        }

        return builder.ToImmutable();
    }
}
