// NamespaceOwnershipControlTests.cs — the non-vacuity controls for HARCH002,
// the namespace-ownership analyzer in `src/Harbor.Arch.Analyzers/`.
//
// WHY THIS FILE EXISTS
// --------------------
// The promoted rule's own control (`Roslyn/ArchAnalyzerPromotionTests.cs`) plants a
// block-scoped namespace in a project that does not own the root, and the same
// namespace in a project that does. Three shapes the rule is written around are
// planted nowhere:
//
//   1. The FILE-SCOPED form, `namespace X;`, which is the form the
//      repository's own source uses. A rule that recognised only the
//      block-scoped form would pass the planted control and see nothing in the
//      tree.
//   2. The NESTED declaration, where the outer declaration is the violation
//      and the inner one is part of it. The rule reports the outermost
//      declaration only, so a control that plants the nested form and expects
//      ONE report proves the finding is not counted twice.
//   3. The near-misses. A root that merely STARTS WITH the same letters
//      (`Harbor.AbstractionsExtra` is not under `Harbor.Abstractions`), and a
//      project that declares a nested namespace under a root it DOES own. A
//      rule that reported either would be a false-positive machine.
//
// The diagnostic id is the switch, so it is checked too. `.editorconfig`
// configures severity by id and a pragma silences one id, so a rule that
// shares its id with another rule cannot be demoted without demoting its
// neighbour. Two analyzers in this tree DID declare HARCH003 until
// 2026-10-20, so the check is over every id in the assembly rather than over
// one rule's id.
//
// WHAT THIS FILE IS NOT
// ---------------------
// A gate over the live tree. The differential is that gate, and this
// file does not repeat it.
//
// The rule under test is `src/Harbor.Arch.Analyzers/NamespaceOwnershipAnalyzer.cs`;
// the harness that
// gives it a compilation is `Roslyn/RoslynProjectCompilation.cs`. The controls
// below drive the REAL analyzer over synthetic source, which is the only way
// "it can fail" means anything.

using System.Collections.Immutable;
using System.Globalization;
using Harbor.Arch.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     HARCH002's non-vacuity controls: the file-scoped form, the nested
///     declaration, the two near-misses, and the uniqueness of the diagnostic
///     id.
/// </summary>
public sealed class NamespaceOwnershipControlTests
{
    /// <summary>
    ///     The OFFENDER in the file-scoped form: a project that declares a
    ///     namespace under a root it does not own, written the way the
    ///     repository's own source writes namespaces.
    /// </summary>
    private const string FileScopedForeignSource = """
        namespace Harbor.Registries.Foreign;

        public sealed class FileScopedForeign
        {
        }
        """;

    /// <summary>
    ///     The OFFENDER in the nested form: the outer declaration borrows the
    ///     root, and the inner one is part of that same declaration.
    /// </summary>
    private const string NestedForeignSource = """
        namespace Harbor.Registries
        {
            namespace Foreign
            {
                public sealed class NestedForeign
                {
                }
            }
        }
        """;

    /// <summary>
    ///     The NEAR-MISS on the prefix: <c>Harbor.AbstractionsExtra</c> shares
    ///     its first segment with <c>Harbor.Abstractions</c> and is not under
    ///     it. The root is compared segment by segment, so a rule that
    ///     compared the names as strings would report this.
    /// </summary>
    private const string SiblingRootSource = """
        namespace Harbor.AbstractionsExtra
        {
            public sealed class SiblingRoot
            {
            }
        }
        """;

    /// <summary>
    ///     The NEAR-MISS on ownership: the project that owns the root declares
    ///     a nested namespace under it. Reporting this would forbid the
    ///     contract trio from existing.
    /// </summary>
    private const string OwnedNestedSource = """
        namespace Harbor.Abstractions
        {
            namespace Models
            {
                public sealed class OwnedNested
                {
                }
            }
        }
        """;

    /// <summary>
    ///     The file-scoped form is reported, and the message names the symbol
    ///     the rule is about.
    /// </summary>
    [Test]
    public async Task Harch002_Reports_A_Foreign_Owner_In_The_File_Scoped_Form()
    {
        CSharpCompilation compilation = Compile(FileScopedForeignSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation);

        await Assert.That(diagnostics.Count).IsEqualTo(1)
            .Because(
                "'Harbor.Registries.Foreign' declared by 'Harbor.Application' is the shape the rule bans: "
                + "a project borrowing another project's namespace root, written in the file-scoped form "
                + "the repository's own source uses. A count of zero means the rule does not recognise the "
                + "form the tree actually uses, and it would pass the pilot's block-scoped control while "
                + "seeing nothing. Reported: " + Describe(diagnostics));

        await Assert.That(diagnostics[0].GetMessage(CultureInfo.InvariantCulture))
            .Contains("Harbor.Registries.Foreign")
            .Because(
                "the message names the symbol the rule is about, so a reader who sees a failure knows "
                + "which declaration to look at without opening the file. The message was: "
                + diagnostics[0].GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     A nested declaration is reported once, not once per level.
    /// </summary>
    [Test]
    public async Task Harch002_Reports_The_Outermost_Declaration_Of_A_Nested_Namespace_Only()
    {
        CSharpCompilation compilation = Compile(NestedForeignSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation);

        await Assert.That(diagnostics.Count).IsEqualTo(1)
            .Because(
                "the outer declaration 'Harbor.Registries' is the violation and the inner one is part of "
                + "the same declaration, so a rule that reported both would count one violation twice and "
                + "the finding count would mean nothing. Reported: " + Describe(diagnostics));
    }

    /// <summary>
    ///     A root that merely starts with the same letters is not under the
    ///     owned root.
    /// </summary>
    [Test]
    public async Task Harch002_Stays_Silent_On_A_Root_That_Merely_Starts_With_The_Same_Letters()
    {
        CSharpCompilation compilation = Compile(SiblingRootSource, "Harbor.Registries");
        List<Diagnostic> diagnostics = await RunAsync(compilation);

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "'Harbor.AbstractionsExtra' is not under 'Harbor.Abstractions'. The roots are compared "
                + "segment by segment, so a rule that compared the names as strings would report a family "
                + "convention as a violation. Reported: " + Describe(diagnostics));
    }

    /// <summary>
    ///     The owner of a root may declare under it, nested or not.
    /// </summary>
    [Test]
    public async Task Harch002_Stays_Silent_On_A_Nested_Declaration_Under_A_Root_The_Project_Owns()
    {
        CSharpCompilation compilation = Compile(OwnedNestedSource, "Harbor.Abstractions.Contracts");
        List<Diagnostic> diagnostics = await RunAsync(compilation);

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "'Harbor.Abstractions.Contracts' owns the 'Harbor.Abstractions' root, so the namespace it "
                + "declares under it is the rule's whole point. A rule that fired here would forbid the "
                + "contract trio from existing. Reported: " + Describe(diagnostics));
    }

    /// <summary>
    ///     No diagnostic id is declared by two analyzers.
    /// </summary>
    /// <remarks>
    ///     The check reads the analyzers out of this assembly rather than out of
    ///     a hand-written list, so an analyzer added later is covered without
    ///     this file being edited.
    /// </remarks>
    [Test]
    public async Task Every_Diagnostic_Id_Is_Declared_By_Exactly_One_Analyzer()
    {
        var ids = new List<string>();

        foreach (Type type in typeof(NamespaceOwnershipControlTests).Assembly.GetTypes())
        {
            if (!typeof(DiagnosticAnalyzer).IsAssignableFrom(type) || type.IsAbstract)
            {
                continue;
            }

            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
            foreach (DiagnosticDescriptor descriptor in analyzer.SupportedDiagnostics)
            {
                ids.Add(descriptor.Id);
            }
        }

        await Assert.That(ids.Count).IsGreaterThan(0)
            .Because(
                "the id check reads the analyzers out of this assembly, so an assembly that no longer "
                + "carries one would make the uniqueness check vacuous. Ids found: "
                + string.Join(" | ", ids));

        var duplicated = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        await Assert.That(duplicated).IsEmpty()
            .Because(
                "the id is the switch: '.editorconfig' configures severity by id and a pragma silences one "
                + "id, so a rule that shares its id with another rule cannot be demoted without demoting "
                + "its neighbour, and it will be demoted together with it. Two analyzers in this tree did "
                + "declare HARCH003 until 2026-10-20, and neither could be turned off on its own. "
                + "Duplicated id(s): " + string.Join(" | ", duplicated)
                + ". Ids found: " + string.Join(" | ", ids));
    }

    /// <summary>Compiles one synthetic source file as one assembly.</summary>
    private static CSharpCompilation Compile(string source, string assemblyName)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: assemblyName + ".cs");

        return CSharpCompilation.Create(
            assemblyName,
            [tree],
            RoslynProjectCompilation.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs the rule over one compilation.</summary>
    private static async Task<List<Diagnostic>> RunAsync(CSharpCompilation compilation)
    {
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [new NamespaceOwnershipAnalyzer()],
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics];
    }

    /// <summary>The findings as one printable line, so a failure names them.</summary>
    private static string Describe(List<Diagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "(none)"
            : string.Join(" | ", diagnostics.Select(static d => d.GetMessage(CultureInfo.InvariantCulture)));
}
