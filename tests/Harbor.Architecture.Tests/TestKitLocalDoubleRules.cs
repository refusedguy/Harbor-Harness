// TestKitLocalDoubleRules.cs — GUARD for issue #1825.
//
// WHAT #1825 FOUND
// ----------------
// `Harbor.TestKit` is the one place a shared test double is supposed to live,
// and it was not: five test projects each kept their OWN set of interface
// doubles, and the sets did not even agree with each other.
//
//   StubAuthResolver  ↔  FixedAuthResolver        (the same interface, two names)
//   StubModelCatalog  ↔  StaticModelCatalog       (the same interface, two names)
//   SingleProviderRegistry ↔ FakeProviderRegistry  (the same interface, two names)
//   RateLimitedLlmClient    (a whole LLM client, hand-written, in one project)
//
// The doubles were not copies of each other in the harmless sense — they
// ANSWERED differently. `FixedAuthResolver` and `StubAuthResolver` disagreed
// about what "no key" means; a test written against one did not transfer to
// the other. A shared kit with no weight is not a shared kit.
//
// WHAT IS RULED
// -------------
//   R1  A test project OUTSIDE the shared kit must not declare a TOP-LEVEL
//       double for one of the seven interfaces #1825 consolidated. A
//       top-level double is a project keeping a SET — the shape the issue
//       found. A double nested inside one test class is a test's own
//       instrument and is not judged.
//
//   R2  The exceptions are named, with a reason each, and the list may only
//       SHRINK. Every entry is a file that still has to keep its double, and
//       the reason says why the shared kit cannot answer for it.
//
// NON-VACUITY
// -----------
//   * `A_Top_Level_Double_Outside_The_Kit_Is_Reported` — the synthetic
//     control: the SAME matcher, handed a file with a top-level
//     `IAuthResolver` double, must report it.
//   * `A_Nested_Double_Is_Not_Reported` — the negative control: the same
//     matcher, handed the same double nested inside a test class, must be
//     silent. Without it, "reported" would be satisfied by a matcher that
//     reports every `IAuthResolver` in the tree.
//   * `The_Exception_List_Matches_The_Tree` — every named exception still
//     declares its double, and every file that declares one is named. A stale
//     entry is a rule that has stopped ruling.
//
// WHY ROSLYN, AND NOT A REGEX
// ---------------------------
// The question is "is this type at namespace level", and the honest way to
// answer it is to ask the compiler's own parser. A brace counter over a
// source file gets raw string literals wrong, and a raw string literal with a
// brace in it moves every declaration after it by one level — which would
// turn a nested double into a top-level one and report a file that is fine.
// `Microsoft.CodeAnalysis.CSharp` is already a dependency of this project
// (`InnerExceptionRules` parses with it), so the exact answer costs nothing
// extra.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The shared kit is the only place a shared double may live (#1825).
/// </summary>
public sealed class TestKitLocalDoubleRules
{
    /// <summary>
    ///     The interfaces #1825 consolidated. A project outside the shared kit
    ///     that declares a top-level double for one of them is keeping a set.
    /// </summary>
    internal static readonly string[] ConsolidatedInterfaces =
    [
        "IAuthResolver",
        "IModelCatalog",
        "IProviderRegistry",
        "ISessionContext",
        "ILlmClient",
        "ICompactionService",
        "ITokenTracker",
    ];

    /// <summary>
    ///     The files that still declare a top-level double, each with the
    ///     reason the shared kit cannot answer for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This list is a RATCHET: it may only shrink. A double that moves
    ///         into the kit removes an entry; a double that stays keeps one.
    ///         Every entry is a question the reader should be able to answer
    ///         from the reason alone.
    ///     </para>
    /// </remarks>
    internal static readonly (string File, string Reason)[] AllowedLocalDoubles =
    [
        (
            "tests/Harbor.App.Avalonia.Tests/ProviderAuthTestDoubles.cs",
            "The registry answers GetClient with a FAILURE and serves the catalogue "
            + "directly, while the kit's registry wraps a client and answers GetClient "
            + "with it. Two shapes, and only this project needs the second one."
        ),
        (
            "tests/Harbor.Benchmarks/AgentLoopBenchmark.cs",
            "The benchmark doubles are written for the measurement — they allocate "
            + "nothing on the hot path — and a shared double would change what the "
            + "benchmark measures."
        ),
        (
            "tests/Harbor.Benchmarks/CompactionServiceBenchmark.cs",
            "Same reason as the other benchmark doubles: the measurement decides the "
            + "shape, not convenience."
        ),
        (
            "tests/Harbor.Benchmarks/ProviderRegistryBenchmark.cs",
            "Same reason as the other benchmark doubles: the measurement decides the "
            + "shape, not convenience."
        ),
        (
            "tests/Harbor.LoadTests/LoadTestFakes.cs",
            "The load harness needs the DURABLE transcript — its session context "
            + "persists to the store so the corruption assertions read what was "
            + "written, not what is in memory."
        ),
        (
            "tests/Harbor.Providers.Tests/FakeModelCatalog.cs",
            "IModelCatalog is declared in Harbor.Providers.OpenAiCompatible, a "
            + "PRODUCT assembly. The closure-neutral part of the shared kit may not "
            + "reach it: that part is the one every test project can take without "
            + "gaining a product assembly, and a shared double for this interface "
            + "would put the provider assembly into every one of their closures."
        ),
    ];

    // ---- R1 + R2: the live rule ----------------------------------------------

    /// <summary>
    ///     No project outside the shared kit keeps its own set of doubles for
    ///     the interfaces the shared kit covers.
    /// </summary>
    [Test]
    public async Task Every_Top_Level_Double_Outside_The_Kit_Is_Named()
    {
        IReadOnlyList<string> failures = UnnamedLocalDoubles();

        await Assert.That(failures).IsEmpty()
            .Because(
                "A double that more than one project needs lives in the shared kit, and the "
                + "kit's copy is the one every project reaches (#1825). A project that keeps "
                + "its own copy is a second answer to the same question, and the two answers "
                + "drift. Either move the double into the kit or name the file in the "
                + "exception list with the reason it stays. Offending declarations: "
                + (failures.Count == 0 ? "(none)" : string.Join("; ", failures)));
    }

    /// <summary>
    ///     The exception list and the tree agree: every named file still has
    ///     its double, and every file that has one is named.
    /// </summary>
    [Test]
    public async Task The_Exception_List_Matches_The_Tree()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> found = LocalDoublesByFile();

        var stale = new List<string>();
        foreach ((string file, string reason) in AllowedLocalDoubles)
        {
            if (!found.TryGetValue(file, out IReadOnlyList<string>? doubles) || doubles.Count == 0)
            {
                stale.Add(
                    $"{file} is in the exception list but declares no local double any more. "
                    + $"The reason it was excused ({reason}) no longer applies.");
            }
        }

        var unnamed = new List<string>();
        foreach (string file in found.Keys)
        {
            if (!AllowedLocalDoubles.Any(entry => string.Equals(entry.File, file, StringComparison.Ordinal)))
            {
                unnamed.Add(file);
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exception that no longer applies is a hole in the rule: the file it names "
                + "has nothing to be excused for, and the entry hides the fact that the rule "
                + "is no longer being applied there. Stale entries: "
                + string.Join("; ", stale));

        await Assert.That(unnamed).IsEmpty()
            .Because(
                "Every file that declares a local double must be named in the exception list, "
                + "so that a reader can ask WHY it stays. Unnamed: " + string.Join("; ", unnamed));
    }

    // ---- non-vacuity: the synthetic controls --------------------------------

    /// <summary>
    ///     The interface name the fixtures below implement.
    /// </summary>
    /// <remarks>
    ///     It is spliced into the fixture source rather than written out, and
    ///     that is not decoration. The seam-graph generator
    ///     (<c>tools/gen-seam-graph.py</c>) scans source FILES for base lists
    ///     and reports what it finds as real implementers; a fixture that read
    ///     as a declaration would put a <c>SampleResolver</c> into the
    ///     published graph of who implements <c>IAuthResolver</c>.
    /// </remarks>
    private const string AuthResolverInterface = "IAuthResolver";

    /// <summary>
    ///     A file with a TOP-LEVEL double for a consolidated interface.
    /// </summary>
    private static readonly string SampleTopLevelSource = $$"""
        namespace Harbor.Sample.Tests;

        internal sealed class SampleResolver : {{AuthResolverInterface}}
        {
            public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
                Task.FromResult(Result.Success("k"));
        }
        """;

    /// <summary>
    ///     The same double, NESTED inside a test class.
    /// </summary>
    private static readonly string SampleNestedSource = $$"""
        namespace Harbor.Sample.Tests;

        public class SampleTests
        {
            private sealed class SampleResolver : {{AuthResolverInterface}}
            {
                public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) =>
                    Task.FromResult(Result.Success("k"));
            }
        }
        """;

    /// <summary>
    ///     The matcher the live rule uses, handed a file with a top-level
    ///     double, must report it.
    /// </summary>
    [Test]
    public async Task A_Top_Level_Double_Outside_The_Kit_Is_Reported()
    {
        IReadOnlyList<string> reported =
            DoublesIn("tests/Harbor.Sample.Tests/Sample.cs", SampleTopLevelSource);

        await Assert.That(reported).IsNotEmpty()
            .Because(
                "A rule whose matcher cannot report a top-level double enforces nothing, and "
                + "the tree it walks is full of legitimate ones. Reported: "
                + string.Join("; ", reported));
    }

    /// <summary>
    ///     The same matcher, handed the same double nested inside a test class,
    ///     must be silent. A nested double is one test's instrument.
    /// </summary>
    [Test]
    public async Task A_Nested_Double_Is_Not_Reported()
    {
        IReadOnlyList<string> reported =
            DoublesIn("tests/Harbor.Sample.Tests/Sample.cs", SampleNestedSource);

        await Assert.That(reported).IsEmpty()
            .Because(
                "A double nested inside one test class is that test's instrument, not a project "
                + "keeping a set. Reporting it would make the rule unfollowable. Reported: "
                + string.Join("; ", reported));
    }

    // ---- the machinery ------------------------------------------------------

    /// <summary>
    ///     Every top-level double of a consolidated interface, grouped by the
    ///     file that declares it.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> LocalDoublesByFile()
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach ((string path, string source) in TestTreeSources())
        {
            IReadOnlyList<string> doubles = DoublesIn(path, source);
            if (doubles.Count > 0)
            {
                found[path] = doubles;
            }
        }

        return found;
    }

    /// <summary>
    ///     The top-level doubles of a consolidated interface in one file.
    /// </summary>
    /// <param name="path">The repo-relative path, used in the report.</param>
    /// <param name="source">The file's source.</param>
    internal static IReadOnlyList<string> DoublesIn(string path, string source)
    {
        var reported = new List<string>();

        if (!ConsolidatedInterfaces.Any(name => source.Contains(name, StringComparison.Ordinal)))
        {
            return reported;
        }

        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (TypeDeclarationSyntax type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!IsAtNamespaceLevel(type))
            {
                continue;
            }

            SyntaxList<BaseTypeSyntax> bases = type.BaseList?.Types ?? default;

            foreach (BaseTypeSyntax baseType in bases)
            {
                string? name = SimpleName(baseType);
                if (name is not null && ConsolidatedInterfaces.Contains(name, StringComparer.Ordinal))
                {
                    reported.Add($"{path}: {type.Identifier.ValueText} implements {name}");
                }
            }
        }

        return reported;
    }

    /// <summary>
    ///     Whether a type declaration sits at namespace level — that is,
    ///     whether it is a member of a namespace or of the compilation unit,
    ///     and not of another type.
    /// </summary>
    internal static bool IsAtNamespaceLevel(TypeDeclarationSyntax type) =>
        type.Ancestors().All(ancestor =>
            ancestor is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax);

    /// <summary>
    ///     The simple name of a base type: <c>IAuthResolver</c>,
    ///     <c>Harbor.Abstractions.Providers.IAuthResolver</c> and
    ///     <c>IAuthResolver&lt;T&gt;</c> all answer <c>IAuthResolver</c>.
    /// </summary>
    private static string? SimpleName(BaseTypeSyntax baseType)
    {
        SyntaxNode type = baseType.Type;

        return type switch
        {
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => null,
        };
    }

    /// <summary>
    ///     Every source file of every test project that is not part of the
    ///     shared kit, as (repo-relative path, source) pairs.
    /// </summary>
    /// <remarks>
    ///     A directory under <c>tests/</c> without a <c>.csproj</c> is DATA,
    ///     not a project: <c>tests/fixtures</c> and <c>tests/Plugins</c> hold
    ///     plugin sources and fixtures, they are not compiled by anything, and
    ///     a rule that judged them would describe code that cannot break.
    /// </remarks>
    internal static IReadOnlyList<(string Path, string Source)> TestTreeSources()
    {
        var sources = new List<(string Path, string Source)>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return sources;
        }

        string testsRoot = Path.Combine(root, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return sources;
        }

        foreach (string projectDir in Directory.EnumerateDirectories(testsRoot))
        {
            string project = Path.GetFileName(projectDir);

            if (TestKitSharedCopyReasonRules.IsSharedKitProject(project))
            {
                continue;
            }

            bool isProject = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly).Any();
            if (!isProject)
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                string normalised = file.Replace('\\', '/');
                if (normalised.Contains("/obj/", StringComparison.Ordinal)
                    || normalised.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                string? source = SourceScan.TryReadAllText(file);
                if (source is null)
                {
                    continue;
                }

                sources.Add((SourceScan.Relative(file), source));
            }
        }

        return sources;
    }

    /// <summary>
    ///     The local doubles that no exception names.
    /// </summary>
    internal static IReadOnlyList<string> UnnamedLocalDoubles()
    {
        var failures = new List<string>();

        foreach ((string path, IReadOnlyList<string> doubles) in LocalDoublesByFile())
        {
            if (AllowedLocalDoubles.Any(entry => string.Equals(entry.File, path, StringComparison.Ordinal)))
            {
                continue;
            }

            failures.AddRange(doubles);
        }

        return failures;
    }
}
