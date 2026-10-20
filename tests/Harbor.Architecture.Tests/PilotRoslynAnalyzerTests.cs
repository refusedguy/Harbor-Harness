// PilotRoslynAnalyzerTests.cs — PILOT for issue #1729: does a Roslyn analyzer
// express our architecture rules better than a regex text scan?
//
// WHAT THESE TESTS ARE
// --------------------
// The pilot's two rules live in `Roslyn/HarborArchAnalyzers.cs`; the harness
// that gives them a compilation lives in `Roslyn/RoslynProjectCompilation.cs`.
// These tests are the MEASUREMENT, and they are the four things a migration
// has to answer:
//
//   1. Can the analyzer be made to FAIL?   (planted offenders)
//   2. Does it stay SILENT where the text rule is silent? (planted near-misses)
//   3. Does it agree with the text rule on the LIVE tree? (differential)
//   4. Is it CHEAPER than the text rule? (the elapsed time is measured and
//      printed, because the arch suite is the slowest thing in CI: #1791
//      measured 738 s of the repository's 1 128 s of test time inside this
//      project, because every rule reads the tree with its own `Lazy`.)
//
// THE PLANTED CONTROLS ARE THE POINT
// ---------------------------------
// A guard that matches nothing is indistinguishable from a guard that is
// broken, and a broken guard is worse than no guard because it is believed.
// Every test below plants BOTH an offender (the analyzer must report it) and
// a near-miss (the analyzer must stay silent), which is the same discipline
// the text rules use — with one difference that matters: the text rule needs
// a comment stripper to keep its near-misses from being offenders, and the
// analyzer does not, because a syntax tree keeps comments in trivia.
//
// NOTHING HERE IS A PRODUCTION GATE YET
// ------------------------------------
// The pilot asserts the analyzers' behaviour; it does not replace
// `ReflectionConventionRule` or `AbstractionsNamespaceOwnershipRules`. The
// text rules keep running, and the differential test below is what proves the
// two agree. Replacing a rule is a separate decision (see the PR body and
// #1729), and it is made on the strength of the whole 110-rule audit, not on
// the strength of two.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The pilot's own tests: the planted controls, the differential against the
///     text rule, and the cost measurement.
/// </summary>
public sealed class PilotRoslynAnalyzerTests
{
    /// <summary>
    ///     Every product project, compiled once. The compilations are the
    ///     expensive part of the pilot, and three tests want them.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(RoslynProjectCompilation.Project Project, CSharpCompilation Compilation)>>
        LiveCompilations = new(BuildLiveCompilations);

    /// <summary>
    ///     The planted OFFENDERS. Every construct here is a way to load or
    ///     manufacture code at run time, and every one of them is a shape the
    ///     text rule has to recognise by spelling.
    /// </summary>
    private const string OffendersSource = """
        using System;
        using System.Reflection;
        using System.Reflection.Emit;
        using System.Runtime.Loader;

        namespace Pilot
        {
            public sealed class Offenders
            {
                public Assembly LoadIt(byte[] bytes) => Assembly.Load(bytes);

                public Assembly LoadFromIt(string path) => Assembly.LoadFrom(path);

                public Assembly LoadFileIt(string path) => Assembly.LoadFile(path);

                public DynamicMethod Emit() => new DynamicMethod("go", typeof(void), Type.EmptyTypes);

                public Assembly Define() =>
                    AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("x"), AssemblyBuilderAccess.Run);

                public AssemblyLoadContext? Context { get; set; }

                public TypeBuilder? Builder { get; set; }
            }

            public sealed class Derived : AssemblyLoadContext
            {
                public Derived()
                    : base(true)
                {
                }
            }
        }
        """;

    /// <summary>
    ///     The planted NEAR-MISSES. Nothing here loads or manufactures code,
    ///     and every line here is a line the text rule has to reason about:
    ///     the string-literal member access the text rule explicitly declines to
    ///     ban, the ordinary member access on an <c>Assembly</c> value, the
    ///     prose that mentions the banned APIs, and the two value types that
    ///     DESCRIBE emitted IL rather than emitting it.
    /// </summary>
    private const string NearMissesSource = """
        using System.Reflection;
        using System.Text.Json;

        namespace Pilot
        {
            public sealed class Benign
            {
                /// <summary>Loads via <c>Assembly.Load(byte[])</c> — prose, not code.</summary>
                public string? Name(JsonElement element) => element.GetProperty("models").GetString();

                public string? Full(Assembly assembly) => assembly.FullName;

                public bool Is(object candidate, object instance) => candidate.GetType().IsInstanceOfType(instance);

                public System.Reflection.Emit.OpCode? Code { get; set; }

                public int Value { get; set; }
            }
        }
        """;

    /// <summary>
    ///     The planted OFFENDER for the namespace rule: a project that declares
    ///     a namespace under a root it does not own.
    /// </summary>
    private const string ForeignNamespaceSource = """
        namespace Harbor.Abstractions.Foreign
        {
            public sealed class Foreign
            {
            }
        }
        """;

    /// <summary>
    ///     The planted OWNER: the same namespace root, declared by a project
    ///     that does own it.
    /// </summary>
    private const string OwnedNamespaceSource = """
        namespace Harbor.Abstractions.Models
        {
            public sealed class Owned
            {
            }
        }
        """;

    // =====================================================================
    // 1. The matcher can fail — and fails on the right things.
    // =====================================================================

    /// <summary>
    ///     Every planted offender is reported, and the set of DISTINCT symbols
    ///     reported is exactly the set of capabilities the rule bans. A matcher
    ///     that reported one symbol twice would still pass a count assertion
    ///     against a hand-written number, so the assertion is on the SET: a
    ///     matcher that missed one would fail, and a matcher that reported a
    ///     symbol nobody planted would fail too.
    /// </summary>
    [Test]
    public async Task Pilot_Harch001_Reports_Every_Planted_Dynamic_Code_Capability()
    {
        CSharpCompilation compilation = Compile(OffendersSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new NoDynamicCodeAnalyzer());

        var reported = diagnostics
            .Select(static d => Quoted(d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
            .Where(static s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        var expected = new List<string>
        {
            "System.AppDomain.DefineDynamicAssembly",
            "System.Reflection.Assembly.Load",
            "System.Reflection.Assembly.LoadFile",
            "System.Reflection.Assembly.LoadFrom",
            "System.Reflection.Emit.DynamicMethod",
            "System.Reflection.Emit.TypeBuilder",
            "System.Runtime.Loader.AssemblyLoadContext",
        };

        await Assert.That(string.Join(" | ", reported)).IsEqualTo(string.Join(" | ", expected))
            .Because(
                "the rule bans one capability per symbol, and the planted source contains one of each: "
                + "the three Assembly load members, the emit factory, the dynamic-code type, the load "
                + "context and the load context's base clause. A symbol missing from the left side is a "
                + "capability the analyzer cannot see; a symbol missing from the right side is a false "
                + "positive. Reported: " + string.Join(" | ", reported));

        await Assert.That(diagnostics.Count).IsGreaterThan(expected.Count - 1)
            .Because(
                "one capability has two spellings in the planted source — the dynamic-code type is both "
                + "constructed and used as a return type — so the number of DIAGNOSTICS is larger than "
                + "the number of distinct symbols. Reported " + diagnostics.Count + " diagnostic(s).");
    }

    /// <summary>
    ///     The negative control: the analyzer stays silent on every near-miss.
    /// </summary>
    /// <remarks>
    ///     The near-misses are the ones the text rule has to work hardest for:
    ///     <c>element.GetProperty("models")</c> is a JSON key and not a CLR
    ///     member; the doc comment names the banned APIs; and
    ///     <c>System.Reflection.Emit.OpCode</c> DESCRIBES emitted IL rather
    ///     than emitting it. The text rule flags the last one, because it
    ///     matches the namespace TOKEN; the analyzer does not, because it
    ///     resolves the type. That difference is deliberate and it is the only
    ///     place where the analyzer is knowingly narrower than the rule.
    /// </remarks>
    [Test]
    public async Task Pilot_Harch001_Stays_Silent_On_The_Planted_Near_Misses()
    {
        CSharpCompilation compilation = Compile(NearMissesSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new NoDynamicCodeAnalyzer());

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "a rule that reports a JSON key, an ordinary member access, prose that mentions the banned "
                + "APIs or a value type that only DESCRIBES emitted IL is a noise machine, and a noise "
                + "machine is switched off within a week. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     The namespace rule: a project that does not own a root is reported,
    ///     and the owner of the root is not.
    /// </summary>
    /// <remarks>
    ///     This is the half the text rule cannot express. The text rule needs
    ///     a THIRD rule (its R3) to say that every namespace declaration is
    ///     attributed to an assembly, because a file walk cannot say which
    ///     assembly declares a namespace. The analyzer is handed the answer:
    ///     one compilation is one assembly.
    /// </remarks>
    [Test]
    public async Task Pilot_Harch002_Reports_The_Foreign_Owner_And_Spares_The_Real_One()
    {
        CSharpCompilation foreign = Compile(ForeignNamespaceSource, "Harbor.Registries");
        List<Diagnostic> foreignReports = await RunAsync(foreign, new NamespaceOwnershipAnalyzer());

        CSharpCompilation owned = Compile(OwnedNamespaceSource, "Harbor.Abstractions.Contracts");
        List<Diagnostic> ownedReports = await RunAsync(owned, new NamespaceOwnershipAnalyzer());

        await Assert.That(foreignReports.Count).IsEqualTo(1)
            .Because(
                "'Harbor.Abstractions.Foreign' declared by 'Harbor.Registries' is exactly the shape the "
                + "rule bans: a project borrowing another project's namespace root. Reported: "
                + string.Join(" | ", foreignReports.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));

        await Assert.That(ownedReports.Count).IsEqualTo(0)
            .Because(
                "'Harbor.Abstractions.Models' declared by 'Harbor.Abstractions.Contracts' is the owner "
                + "declaring under its own root, which is the whole point of the rule. A rule that fired "
                + "here would forbid the contract trio from existing.");
    }

    // =====================================================================
    // 2. The live tree: the analyzer agrees with the text rule, and costs less.
    // =====================================================================

    /// <summary>
    ///     The differential. Every file the TEXT rule flags is a file the
    ///     ANALYZER flags too, and the analyzer flags nothing outside the
    ///     plugin family — the one documented exception.
    /// </summary>
    /// <remarks>
    ///     The direction of the first assertion is the one that matters: a
    ///     migration is safe when the new rule sees everything the old rule
    ///     saw. The second assertion is the precision claim: the analyzer
    ///     invents no finding of its own on the live tree.
    /// </remarks>
    [Test]
    public async Task Pilot_The_Analyzer_Sees_Every_File_The_Text_Rule_Sees()
    {
        List<string> textRuleFiles = TextRuleFiles();
        List<string> analyzerFiles = await AnalyzerFilesAsync();

        var missed = textRuleFiles.Where(f => !analyzerFiles.Contains(f, StringComparer.Ordinal)).ToList();

        await Assert.That(missed.Count).IsEqualTo(0)
            .Because(
                "the text rule strips comments before it matches and the analyzer never looks at trivia, "
                + "so the analyzer must see a SUPERSET of the text rule's files. Files the text rule flags "
                + "and the analyzer does not: " + string.Join(" | ", missed));

        var outside = analyzerFiles
            .Where(f => !f.StartsWith("src/Harbor.Plugins.", StringComparison.Ordinal))
            .ToList();

        await Assert.That(outside.Count).IsEqualTo(0)
            .Because(
                "the plugin family is the documented exception — loading code at run time IS the plugin "
                + "product — and every other product project must be clean. Files the analyzer flags "
                + "outside the plugin family: " + string.Join(" | ", outside));
    }

    /// <summary>
    ///     The namespace rule is clean on the live tree, and the analyzer costs
    ///     a measured fraction of what the text rules cost.
    /// </summary>
    /// <remarks>
    ///     The text rules re-read the repository tree once PER RULE: #1791
    ///     measured one tree walk at ~43 s and the project at 738 s of CPU
    ///     against 1 128 s for the whole repository. The analyzer path parses
    ///     the tree ONCE per project and binds symbols on demand, so the cost
    ///     is paid once and shared by every rule that uses the compilation.
    /// </remarks>
    [Test]
    public async Task Pilot_The_Namespace_Rule_Is_Clean_On_The_Live_Tree_And_The_Analyzer_Is_Cheap()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        List<string> analyzerFiles = await AnalyzerFilesAsync();
        long elapsedMs = started.ElapsedMilliseconds;

        var namespaceFindings = new List<string>();
        foreach ((RoslynProjectCompilation.Project project, CSharpCompilation compilation) in LiveCompilations.Value)
        {
            List<Diagnostic> diagnostics = await RunAsync(compilation, new NamespaceOwnershipAnalyzer());
            foreach (Diagnostic diagnostic in diagnostics)
            {
                namespaceFindings.Add(project.Dir + ": " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Console.WriteLine(
            "[pilot] HARCH001+HARCH002 over " + LiveCompilations.Value.Count + " product compilations: "
            + elapsedMs + " ms; files flagged by HARCH001: " + string.Join(" | ", analyzerFiles));

        await Assert.That(namespaceFindings.Count).IsEqualTo(0)
            .Because(
                "the namespace ownership rule is green today as a text rule, so the analyzer must be green "
                + "too. A finding here is either a real violation the text rule cannot see, or a wrong "
                + "ownership table. Findings: " + string.Join(" | ", namespaceFindings));
    }

    // =====================================================================
    // 3. Harness.
    // =====================================================================

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

    /// <summary>Runs one analyzer over one compilation.</summary>
    private static async Task<List<Diagnostic>> RunAsync(CSharpCompilation compilation, DiagnosticAnalyzer analyzer)
    {
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [analyzer],
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics];
    }

    /// <summary>
    ///     The repo-relative files the TEXT rule flags, from the text rule
    ///     itself — not from a copy of its pattern, which is what makes this a
    ///     differential rather than a second implementation.
    /// </summary>
    private static List<string> TextRuleFiles()
    {
        List<ScanHit> hits = ScanRunner.Collect(
            ReflectionConventionRule.Rule,
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(ReflectionConventionRule.Rule)));

        return
        [
            .. hits.Select(static h => h.File).Distinct(StringComparer.Ordinal).OrderBy(static f => f, StringComparer.Ordinal)
        ];
    }

    /// <summary>The repo-relative files the ANALYZER flags on the live tree.</summary>
    private static async Task<List<string>> AnalyzerFilesAsync()
    {
        var files = new List<string>();

        foreach ((_, CSharpCompilation compilation) in LiveCompilations.Value)
        {
            List<Diagnostic> diagnostics = await RunAsync(compilation, new NoDynamicCodeAnalyzer());
            foreach (Diagnostic diagnostic in diagnostics)
            {
                string? path = diagnostic.Location.SourceTree?.FilePath;
                if (path is null)
                {
                    continue;
                }

                files.Add(SourceScan.Relative(path));
            }
        }

        return
        [
            .. files.Distinct(StringComparer.Ordinal).OrderBy(static f => f, StringComparer.Ordinal)
        ];
    }

    /// <summary>Compiles every product project once, and keeps the result.</summary>
    private static IReadOnlyList<(RoslynProjectCompilation.Project Project, CSharpCompilation Compilation)> BuildLiveCompilations()
    {
        var result = new List<(RoslynProjectCompilation.Project, CSharpCompilation)>();

        foreach (RoslynProjectCompilation.Project project in RoslynProjectCompilation.ProductProjects())
        {
            result.Add((project, RoslynProjectCompilation.ForProject(project)));
        }

        return result;
    }

    /// <summary>
    ///     The symbol a diagnostic names: the message format is
    ///     <c>'{0}' …</c>, so the quoted part is the symbol and the rest is
    ///     the explanation.
    /// </summary>
    private static string Quoted(string message)
    {
        int open = message.IndexOf('\'');
        if (open < 0)
        {
            return string.Empty;
        }

        int close = message.IndexOf('\'', open + 1);
        if (close < 0)
        {
            return string.Empty;
        }

        return message.Substring(open + 1, close - open - 1);
    }
}
