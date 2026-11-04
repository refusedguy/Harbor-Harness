// RoslynProjectCompilation.cs — PILOT harness for issue #1729: one Roslyn
// compilation per product project, built once and shared.
//
// WHY THIS FILE EXISTS
// --------------------
// The analyzers in `src/Harbor.Arch.Analyzers/` need a COMPILATION, not a
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
// harness: the analyzers grade the symbols they care about, and a
// project whose generated code is missing (the Avalonia app's XAML-generated
// members, for instance) still resolves every BCL symbol.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Harbor.Architecture.Tests;

/// <summary>Builds one Roslyn compilation per product project, for the architecture analyzers.</summary>
internal static class RoslynProjectCompilation
{
    /// <summary>One product project: where it lives, what it is called, what it compiles.</summary>
    /// <param name="Tree">The top-level tree the project lives in (<c>src</c> or <c>apps</c>).</param>
    /// <param name="Dir">The project directory name.</param>
    /// <param name="AssemblyName">The assembly the project produces.</param>
    internal readonly record struct Project(string Tree, string Dir, string AssemblyName);

    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences = new(BuildReferences);

    /// <summary>The references every compilation is built against.</summary>
    internal static ImmutableArray<MetadataReference> References => LazyReferences.Value;

    // =====================================================================
    // THE PARSED TREE OF EVERY PRODUCT FILE, ONCE FOR THE WHOLE PROCESS
    // =====================================================================
    //
    // WHY THIS EXISTS
    // --------------
    // The measured cost of parsing the whole product tree is 843.9 ms
    // (`[arch-cost] roslyn parse files=1329 chars=10132286 ms=843.9`, run
    // 39724342525). That is not the expensive part — the SEMANTIC pass over
    // the tree is — but it was paid more than once: the analyzer sweep built
    // its own 64 compilations, the HTTP-client census built its own 64, and
    // the unobserved-task rule built its own again. Each set re-read every
    // file and re-parsed it.
    //
    // The TREE is what the parse produces, and the tree is a pure function of
    // (text, parse options, path). It does not depend on the compilation's
    // usings, so the SAME tree can serve the compilation with the implicit
    // global usings and the one without them. Caching it is therefore not a
    // cache of a verdict — it is a cache of a parse.
    //
    // THE CONTRACT
    // ------------
    //   1. ONE tree per file path, for the whole process. The key is the
    //      ABSOLUTE path, and the parse options are ONE shared immutable
    //      instance, so the tree is the tree every caller used to build.
    //   2. The tree is never mutated: a `SyntaxTree` is immutable, and the
    //      compilation that holds it holds a reference, not a copy.
    //   3. A file that cannot be read yields no tree — the same answer
    //      `SourceScan.TryReadAllText` gave, so the file set of every
    //      compilation is unchanged.

    /// <summary>
    ///     The parse options every product tree is parsed with — ONE instance,
    ///     shared, because the options are immutable and a tree carries the
    ///     instance it was parsed with.
    /// </summary>
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>
    ///     The parsed tree of every file, once for the whole process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<SyntaxTree?>> TreeCache =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     The parsed tree of one file, or <c>null</c> when the file cannot be
    ///     read.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    internal static SyntaxTree? TreeFor(string path)
    {
        Lazy<SyntaxTree?> lazy = TreeCache.GetOrAdd(
            path,
            static p => new Lazy<SyntaxTree?>(
                () => ParseFile(p),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>Reads and parses one file. Runs at most once per path.</summary>
    private static SyntaxTree? ParseFile(string path)
    {
        string? text = SourceScan.TryReadAllText(path);
        return text is null ? null : CSharpSyntaxTree.ParseText(text, ParseOptions, path: path);
    }

    // =====================================================================
    // THE PRODUCT COMPILATIONS, ONCE FOR THE WHOLE PROCESS
    // =====================================================================
    //
    // WHY THIS EXISTS
    // --------------
    // Two consumers each wanted their own set of 64 product compilations:
    // the analyzer sweep (`ArchAnalyzerPromotionTests`) and the HTTP-client
    // census (`HttpClientReuseRules`). They differ in ONE thing — whether the
    // compilation carries the project's implicit global usings — and they were
    // each building their own set from scratch, in their own way: the sweep's
    // set was built concurrently, the census's set SEQUENTIALLY, and the
    // census's analyzer pass ran SEQUENTIALLY too. The census was therefore
    // one core busy for forty seconds while three cores had nothing to do,
    // and six tests sat waiting for it.
    //
    // THE VARIANTS ARE THE KEY
    // ------------------------
    // The key is the usings-variant, so the two consumers keep their OWN
    // compilation — the verdicts of the sweep are computed over compilations
    // without the implicit usings, and the HTTP-client rule's are computed
    // over compilations WITH them, exactly as before. What they now share is
    // the PARSE, and the build's concurrency.
    //
    // The ORDER of the result is the order of the projects, not the order the
    // threads finished in: the result array is indexed, so a consumer that
    // merges the projects in order gets the same list it got before.

    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<(Project Project, CSharpCompilation Compilation)>>>
        CompilationSets = new(StringComparer.Ordinal);

    /// <summary>
    ///     The product compilations for one usings-variant: built once for the
    ///     whole process, concurrently, and shared by every consumer.
    /// </summary>
    /// <param name="globalUsings">
    ///     The implicit global usings the compilation carries. Null means the
    ///     compilation carries none.
    /// </param>
    internal static IReadOnlyList<(Project Project, CSharpCompilation Compilation)> Compilations(
        IReadOnlyList<string>? globalUsings)
    {
        string key = globalUsings is null ? string.Empty : string.Join(";", globalUsings);

        Lazy<IReadOnlyList<(Project Project, CSharpCompilation Compilation)>> lazy =
            CompilationSets.GetOrAdd(
                key,
                _ => new Lazy<IReadOnlyList<(Project Project, CSharpCompilation Compilation)>>(
                    () => BuildCompilations(globalUsings),
                    LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>
    ///     Compiles every product project once, CONCURRENTLY: a runner with
    ///     four cores should not parse 64 projects one after another while
    ///     three of its cores are idle.
    /// </summary>
    private static IReadOnlyList<(Project Project, CSharpCompilation Compilation)> BuildCompilations(
        IReadOnlyList<string>? globalUsings)
    {
        IReadOnlyList<Project> projects = ProductProjects();
        var result = new (Project Project, CSharpCompilation Compilation)[projects.Count];

        Parallel.For(
            0,
            projects.Count,
            index =>
            {
                Project project = projects[index];
                result[index] = (project, ForProject(project, globalUsings));
            });

        return result;
    }

    /// <summary>
    ///     The implicit global usings the SDK gives every project whose
    ///     <c>ImplicitUsings</c> is enabled — and every project in this
    ///     repository has it (the root <c>Directory.Build.props</c> sets it).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A compilation WITHOUT them is not the project's compilation. A
    ///         file that writes <c>HttpClient</c> and never says
    ///         <c>using System.Net.Http;</c> relies on the SDK's implicit
    ///         usings, and in a compilation that lacks them the name does not
    ///         resolve: the semantic model answers "error type", and every rule
    ///         that asks about the TYPE is blind to that file.
    ///     </para>
    ///     <para>
    ///         This was MEASURED, not reasoned: the HTTP-client rule
    ///         (<c>HttpClientReuseRules</c>) found the two creations in
    ///         <c>McpHttpClientFactory.cs</c> — the one file that says
    ///         <c>using System.Net.Http;</c> — and none of the five in the
    ///         files that rely on the implicit using. The set is the SDK's
    ///         default for <c>Microsoft.NET.Sdk</c>; no project in this
    ///         repository declares a <c>&lt;Using Include&gt;</c> item of its
    ///         own.
    ///     </para>
    ///     <para>
    ///         It is a PARAMETER and not a default, on purpose: the rules that
    ///         already run over these compilations were measured against a
    ///         compilation without the implicit usings, and moving them onto
    ///         the full one is a separate piece of work with its own findings
    ///         (see <c>docs/SEMANTIC_GUARDS.md</c>).
    ///     </para>
    /// </remarks>
    internal static ImmutableArray<string> ImplicitGlobalUsings { get; } =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    ];

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
    /// <param name="project">The project to compile.</param>
    /// <param name="globalUsings">
    ///     The implicit global usings the project inherits. Null means the
    ///     compilation carries none — which is what the rules that were
    ///     measured against this harness expect; see
    ///     <see cref="ImplicitGlobalUsings" /> for why that is a finding and
    ///     not a default.
    /// </param>
    internal static CSharpCompilation ForProject(Project project, IEnumerable<string>? globalUsings = null)
    {
        var trees = new List<SyntaxTree>();

        foreach (string path in SourceFilesOf(project))
        {
            // The tree is parsed ONCE for the whole process and shared by every
            // compilation that holds this file — the tree does not depend on the
            // compilation's usings, so the two variants of a product project
            // parse it once between them.
            SyntaxTree? tree = TreeFor(path);
            if (tree is null)
            {
                continue;
            }

            trees.Add(tree);
        }

        return CSharpCompilation.Create(
            project.AssemblyName,
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithUsings(globalUsings));
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
