// HttpClientCreationAnalyzerTests.cs — the controls for HARCH005 and HARCH006,
// the two rules that answer "where is an HTTP client created?" by resolving
// the TYPE instead of matching the spelling.
//
// WHAT THESE TESTS ARE
// --------------------
// The rule they serve is `tests/Harbor.Architecture.Tests/HttpClientReuseRules.cs`
// (#1854). That rule used to answer its two questions with a regex over the
// comment-stripped source:
//
//   1. "Is a client created HERE?" — the token `new HttpClient`.
//   2. "Is the creation inside a DELEGATE?" — the token `=> new HttpClient`.
//
// The analyzer in `src/Harbor.Arch.Analyzers/HttpClientCreationAnalyzer.cs`
// answers both by symbol. Three things have to be true before the rule may
// throw its text matcher away, and each of them is a test below:
//
//   1. The analyzer CAN fail — it reports every planted creation, in every
//      spelling, and it reports the per-call shape.
//   2. The analyzer stays SILENT where the text rule is silent — the factory
//      client, the delegate that RETURNS a shared client, the member access
//      that is not a construction, and the prose and the string literal that
//      merely NAME the API.
//   3. The analyzer and the text rule AGREE on the live tree, in both
//      directions. The comparison uses the text rule's own detector, never a
//      copy of its patterns: a copy would agree with itself and prove nothing.
//
// THE ONE PLACE THE TWO DISAGREE, AND WHY IT IS A FINDING
// -------------------------------------------------------
// The text rule's delegate question is "does this LINE start with `=> new
// HttpClient`". That is the shape of an EXPRESSION-BODIED lambda and of
// nothing else, so a lambda with a BLOCK body, and an anonymous method, were
// both invisible to it. The analyzer asks whether the creation sits under an
// anonymous function, and the difference is a finding, not a defect: the
// planted source below carries all three shapes, and the test asserts that the
// analyzer sees the two the text rule cannot.
//
// THE PLANTED CONTROLS ARE THE POINT
// ---------------------------------
// A guard that matches nothing is indistinguishable from a guard that is
// broken, and a broken guard is worse than no guard because it is believed.

using System.Collections.Immutable;
using Harbor.Arch.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The controls of the HTTP-client creation rules: the planted offenders,
///     the planted near-misses, and the differential against the text rule they
///     replace.
/// </summary>
public sealed class HttpClientCreationAnalyzerTests
{
    /// <summary>
    ///     The planted OFFENDERS. Seven creations, in five spellings, three of
    ///     them inside a delegate.
    /// </summary>
    /// <remarks>
    ///     Every spelling the text rule needed a separate pattern for is here
    ///     once: the plain construction, the fully qualified one, the
    ///     target-typed one, the handler (one layer below the client), and the
    ///     per-call shape in its three forms.
    /// </remarks>
    private const string OffendersSource = """
        using System;
        using System.Net.Http;

        namespace Promotion
        {
            public sealed class Offenders
            {
                public HttpClient Plain() => new HttpClient();

                public HttpClient Qualified() => new System.Net.Http.HttpClient();

                public HttpClient TargetTyped()
                {
                    HttpClient client = new();
                    return client;
                }

                public SocketsHttpHandler Handler() => new SocketsHttpHandler();

                public Func<HttpClient> PerCallExpression() => () => new HttpClient();

                public Func<HttpClient> PerCallBlock()
                {
                    return () =>
                    {
                        return new HttpClient();
                    };
                }

                public HttpClient PerCallAnonymousMethod()
                {
                    Func<HttpClient> factory = delegate { return new HttpClient(); };
                    return factory();
                }
            }
        }
        """;

    /// <summary>
    ///     The planted NEAR-MISSES. Nothing here creates a client.
    /// </summary>
    /// <remarks>
    ///     The factory client is the RIGHT way to get one. A delegate that
    ///     RETURNS a shared client is not a creation. A member access whose
    ///     NAME is the type is not a construction. And prose — a doc comment
    ///     and a string literal — that names the API is data, not a call.
    /// </remarks>
    private const string NearMissesSource = """
        using System;
        using System.Net.Http;

        namespace Promotion
        {
            public sealed class Benign
            {
                /// <summary>Prose that names the API: `new HttpClient()` is not a construction.</summary>
                public string Note() => "new HttpClient()";

                public HttpClient FromFactory(IHttpClientFactory factory) => factory.CreateClient("provider:x");

                public Func<HttpClient> SharedDelegate(HttpClient shared) => () => shared;
            }

            public sealed class Renews
            {
                public HttpClient HttpClient { get; } = null!;
            }
        }
        """;

    /// <summary>
    ///     The analyzer reports every planted creation, and the per-call shape
    ///     in all three of its spellings.
    /// </summary>
    [Test]
    public async Task The_Analyzer_Reports_Every_Planted_Creation()
    {
        CSharpCompilation compilation = Compile(OffendersSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new HttpClientCreationAnalyzer());

        List<Diagnostic> creations =
        [
            .. diagnostics.Where(static d => d.Id == HttpClientCreationAnalyzer.CreationDiagnosticId)
        ];
        List<Diagnostic> perCall =
        [
            .. diagnostics.Where(static d => d.Id == HttpClientCreationAnalyzer.DelegateDiagnosticId)
        ];

        await Assert.That(creations.Count).IsEqualTo(7)
            .Because(
                "the planted source carries seven creations: the plain one, the fully qualified one, the "
                + "target-typed one, the handler, and the per-call shape in its three forms. A creation the "
                + "analyzer does not see is a creation the rule would not catch. Reported: "
                + string.Join(" | ", creations.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));

        await Assert.That(perCall.Count).IsEqualTo(3)
            .Because(
                "the per-call shape has three spellings in the planted source: an expression-bodied lambda, a "
                + "lambda with a block body, and an anonymous method. The text rule saw only the first one, "
                + "because it matched the token `=> new`. Reported: "
                + string.Join(" | ", perCall.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     The analyzer stays silent on every near-miss.
    /// </summary>
    [Test]
    public async Task The_Analyzer_Stays_Silent_On_The_Planted_Near_Misses()
    {
        CSharpCompilation compilation = Compile(NearMissesSource, "Harbor.Application");
        List<Diagnostic> diagnostics = await RunAsync(compilation, new HttpClientCreationAnalyzer());

        await Assert.That(diagnostics.Count).IsEqualTo(0)
            .Because(
                "a rule that reports the factory client, a delegate that returns a shared client, a member "
                + "access whose name is the type, or prose and a string literal that merely NAME the API is "
                + "a noise machine, and a noise machine is switched off within a week. Reported: "
                + string.Join(" | ", diagnostics.Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>
    ///     The differential: on the live tree, the analyzer and the text rule
    ///     name the same sites.
    /// </summary>
    /// <remarks>
    ///     The direction that matters first is the one that says the analyzer
    ///     SEES everything the text rule saw. The second direction is the
    ///     precision claim: the analyzer invents no site of its own.
    /// </remarks>
    [Test]
    public async Task The_Analyzer_And_The_Text_Rule_Name_The_Same_Sites_On_The_Live_Tree()
    {
        List<string> textSites = HttpClientReuseRules.TextRuleSites();
        List<string> analyzerSites = await HttpClientReuseRules.AnalyzerSitesAsync();

        var missed = textSites.Where(site => !analyzerSites.Contains(site, StringComparer.Ordinal)).ToList();
        var extra = analyzerSites.Where(site => !textSites.Contains(site, StringComparer.Ordinal)).ToList();

        await Assert.That(missed).IsEmpty()
            .Because(
                "the text rule reads the whole source text and the analyzer reads the SYMBOLS, so the "
                + "analyzer must see a SUPERSET of the text rule's sites. A site the text rule names and the "
                + "analyzer does not is a shape the analyzer cannot reach. Sites: " + string.Join(" | ", missed));

        await Assert.That(extra).IsEmpty()
            .Because(
                "the analyzer resolves the created TYPE, so a site it names is a real construction. A site it "
                + "names and the text rule does not is either a shape the text rule cannot see (the ground "
                + "the flip to the semantic rule stands on) or a false positive. Sites: "
                + string.Join(" | ", extra));

        await Assert.That(textSites.Count).IsGreaterThan(0)
            .Because(
                "a differential over two empty sets agrees with itself and proves nothing. The live tree has "
                + "seven client creations, and the scope must see them.");
    }

    // =====================================================================
    // Harness.
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
}
