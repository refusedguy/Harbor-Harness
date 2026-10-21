// HttpClientReuseRules.cs — the guard for issue #1854, answered by the TYPE.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #1854 collected a candidate analyzer package (IDisposableAnalyzers,
// rule IDISP014 "use a single instance of HttpClient") and REJECTED it: the
// upstream has had no release for years and 24 of its 26 rules describe a
// discipline this codebase already follows. The FINDING the package produced
// was real, and this file is where that finding lives on:
//
//   * McpOAuthHandler held a `Func<HttpClient>` and called it at each of its
//     five call sites, so every OAuth request — discovery, token endpoint,
//     dynamic registration, refresh, probe — minted a fresh HttpClient, and
//     with it a fresh SocketsHttpHandler, a fresh connection pool and a fresh
//     DNS resolution. None of them was ever disposed.
//   * JsonProviderDiscovery's desktop path built one HttpClient per
//     providers/*.json file: a dozen-plus private connection pools alive for
//     the whole process, while the CLI path already took its clients from
//     IHttpClientFactory.
//
// A lesson has to be encoded as a RULE, not as a comment: a comment is
// invisible to every tool that reads the tree, and the next
// `() => new HttpClient()` would land without anybody noticing.
//
// WHAT IS ENFORCED
// ----------------
//   1. CREATION IS NAMED. A client may be created in exactly the files the
//      table below names, and each row says who owns that client and for how
//      long. A client created anywhere else is a new site, and a new site is
//      where the defect comes back.
//   2. NO CLIENT IN A DELEGATE. A delegate that CREATES a client produces a
//      new client per invocation — that is the shape #1854 found, verbatim
//      (`() => new HttpClient()`). A delegate that RETURNS a shared client is
//      fine and stays silent.
//   3. THE TABLE IS A BUDGET. Each named file may create the number of
//      clients its row states, so a second creation inside an allowed file
//      fails as well.
//
// Rule 2 is deliberately NOT a baseline: no file is allowed to create a
// client inside a delegate, so the allowance table cannot buy one back.
//
// THE TWO QUESTIONS ARE NOW ASKED OF THE SYMBOL, NOT OF THE TEXT
// --------------------------------------------------------------
// This file used to answer both questions with a REGEX over the
// comment-stripped source: the token `new HttpClient` for the first, and the
// token `=> new HttpClient` for the second. Both were questions about a
// SPELLING, and a spelling has two blind spots that cost the rule real
// coverage:
//
//   * The delegate question could only see an EXPRESSION-BODIED lambda,
//     because that is the shape whose token sequence begins with `=>`. A
//     lambda with a block body and an anonymous method were invisible to it,
//     and both are the defect the rule exists for.
//   * A name in a string literal was a hit, and a comment needed a stripper
//     of its own to keep prose about `new HttpClient()` from being an
//     offense.
//
// The analyzer (`src/Harbor.Arch.Analyzers/HttpClientCreationAnalyzer.cs`,
// `HARCH005` and `HARCH006`) asks both questions by symbol: the created
// object is resolved through the semantic model, and "inside a delegate" is
// read off the syntax tree. The rule below now consumes ITS verdicts.
//
// THE TEXT MATCHER SURVIVES AS THE ORACLE, AND GATES NOTHING
// -----------------------------------------------------------
// The differential in `Roslyn/HttpClientCreationAnalyzerTests.cs` compares the
// two implementations over the live tree, and a comparison needs BOTH of
// them: a copy of the patterns would be a second implementation rather than a
// comparison. So the matcher below stays — as the ORACLE of the migration,
// not as a rule. The two tests at the end of this file keep it honest, because
// a matcher that stopped matching would make the differential vacuous.
//
// KNOWN LIMITATION — stated, not hidden
// -------------------------------------
// The analyzer sees WHERE a client is created and whether the creation sits
// inside a delegate. It does not see HOW OFTEN the creating code runs: a
// client created inside a method that is itself called per request would pass
// rules 1 and 3 if it sat in an allowed file. That limit is the same one the
// text rule had, and it is a limit of the RULE, not of the substrate.
//
// NON-VACUITY
// -----------
// Every rule here can pass for the wrong reason — a scan that matches nothing,
// a scope that finds no files, a table whose rows no longer mean anything —
// and a guard that passes for the wrong reason is worse than no guard, because
// it is believed. So: the scope must really yield the product tree and
// contain every file the table names; the analyzer must fire on planted
// per-call shapes and stay silent on the shared-client shapes that are
// correct; the table's reasons must be stated; and a row that no longer has a
// creation behind it must fail, so the table cannot rot into a permission
// for a problem that stopped existing.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Harbor.Arch.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1854: an HTTP client is created once, by a named owner, and
///     never per call. See the file header for the incident and the limits of
///     what the rule can see.
/// </summary>
public class HttpClientReuseRules
{
    /// <summary>The sub-id a client creation is reported under.</summary>
    private const string CreationSubId = "http-client-creation";

    /// <summary>The sub-id a client creation inside a delegate is reported under.</summary>
    private const string DelegateSubId = "http-client-created-in-a-delegate";

    /// <summary>
    ///     A client being constructed: <c>new HttpClient(…)</c> or
    ///     <c>new SocketsHttpHandler</c>. The lookbehind keeps a member access
    ///     (<c>renew.HttpClient</c>) out of the match; the trailing boundary
    ///     keeps <c>new HttpClientFactory</c> out of it; the optional
    ///     qualification keeps a fully-qualified type in scope.
    /// </summary>
    /// <remarks>
    ///     This pattern belongs to the ORACLE, not to the rule. It is here so
    ///     the differential has something to compare against — see the header.
    /// </remarks>
    private static readonly Regex ClientCreation = new(
        @"(?<![.\w])new\s+(?:[A-Za-z_]\w*\.)*(?:HttpClient|SocketsHttpHandler)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     The same construction, target-typed: <c>HttpClient client = new();</c>.
    ///     A separate pattern because the first one cannot see it, and it is
    ///     the same defect wearing a different hat.
    /// </summary>
    private static readonly Regex TargetTypedCreation = new(
        @"HttpClient\??\s+\w+\s*=\s*new\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     A client created inside a DELEGATE — the shape #1854 found. The
    ///     delegate is invoked per call, so the client is minted per call.
    /// </summary>
    /// <remarks>
    ///     This is the pattern with the blind spot: it sees an
    ///     expression-bodied lambda and nothing else. The analyzer sees all
    ///     three forms of the shape, and the differential over the live tree
    ///     is where the two are compared.
    /// </remarks>
    private static readonly Regex CreationInDelegate = new(
        @"=>\s*new\s+(?:[A-Za-z_]\w*\.)*(?:HttpClient|SocketsHttpHandler)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     The files allowed to create an HTTP client: the path, the number of
    ///     creations the file may hold, and the reason it may hold them.
    /// </summary>
    /// <remarks>
    ///     Every row is a CLAIM about a lifetime, and each claim is checkable
    ///     in the source: the client is built once, by the named owner, and
    ///     lives as long as that owner. The budget is what stops a second
    ///     creation from appearing beside the first one.
    /// </remarks>
    private static readonly (string Path, int Budget, string Reason)[] AllowedCreators =
    [
        (
            "src/Harbor.Tools.Builtin/Tools/Mcp/McpOAuthHandler.cs",
            1,
            "The OAuth handler's own client: ONE per handler, built in the constructor, reused by every "
            + "OAuth call, disposed with the handler (#1854). Before #1854 this file created a client at "
            + "each of five call sites — one connection pool per OAuth request, and nobody disposed any "
            + "of them."
        ),
        (
            "src/Harbor.Tools.Builtin/Tools/Mcp/McpHttpClientFactory.cs",
            2,
            "The remote-MCP transports' client owner: one handler and one client per transport, disposed "
            + "with the transport (#1567/#1835). Two creations because the handler and the client that "
            + "wraps it are two objects, both built once and both owned by the factory."
        ),
        (
            "src/Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs",
            2,
            "The web-fetch tool's process-wide shared client: one handler and one client for the whole "
            + "process, built once in a static initializer. The tool follows redirects MANUALLY so every "
            + "hop can be re-validated against the SSRF deny list, which is why the handler carries its "
            + "own configuration instead of taking a factory client."
        ),
        (
            "src/Harbor.Hosting/Modules/ProviderFactories.cs",
            1,
            "The desktop flavor's Ollama client: ONE for the process, built once at composition. It "
            + "carries OLLAMA_HOST as its base address and a 10s timeout, which is why it is not the "
            + "factory's named client. One client for the whole process is not a per-call client."
        ),
        (
            "apps/Harbor.App.Avalonia/Hosting/ConfigRegistration.cs",
            1,
            "The desktop app's model-catalog client: ONE for the process, built once when the app "
            + "registers its configuration. The catalog fetches and caches /models for every discovered "
            + "provider and lives as long as the app does."
        ),
    ];

    /// <summary>
    ///     The text matcher, as the ORACLE of the migration. It gates NOTHING:
    ///     the analyzer's verdicts are what the rule below enforces. It is here
    ///     because a differential needs both implementations, and a copy of the
    ///     patterns would be a second implementation rather than a comparison.
    /// </summary>
    internal static readonly ScanRule CreationOracle = new()
    {
        Id = "HttpClientReuse",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                CreationSubId,
                ClientCreation,
                "One client, owned by a named owner: IHttpClientFactory.CreateClient(name) for a "
                + "provider, the shared McpHttpClientFactory for a remote MCP transport, or the "
                + "process-wide client the files in this rule's table name. A client built per call "
                + "is a private connection pool per call, with its own DNS lifetime and no owner to "
                + "dispose it (#1854)."
            ),
            new ScanForbidden(
                CreationSubId,
                TargetTypedCreation,
                "One client, owned by a named owner: IHttpClientFactory.CreateClient(name) for a "
                + "provider, the shared McpHttpClientFactory for a remote MCP transport, or the "
                + "process-wide client the files in this rule's table name. A client built per call "
                + "is a private connection pool per call, with its own DNS lifetime and no owner to "
                + "dispose it (#1854)."
            ),
        ],
        Baseline =
        [
            .. AllowedCreators.Select(static row => new ScanBaseline(
                CreationSubId + " " + row.Path,
                IsPrefix: false,
                row.Reason)),
        ],
        Controls =
        [
            // The per-call shape #1854 found, in a file the table does not name.
            new ScanControl(
                "Planted/PerCall.cs",
                "public sealed class PerCall\n{\n    public void Send()\n    {\n"
                + "        var http = new HttpClient();\n        http.GetAsync(\"https://x/\");\n    }\n}",
                CreationSubId),
            // The target-typed form of the same thing.
            new ScanControl(
                "Planted/TargetTyped.cs",
                "public sealed class TargetTyped\n{\n    public void Send()\n    {\n"
                + "        HttpClient http = new();\n        http.GetAsync(\"https://x/\");\n    }\n}",
                CreationSubId),
            // A handler built per call is the same defect one layer down.
            new ScanControl(
                "Planted/PerCallHandler.cs",
                "public sealed class PerCallHandler\n{\n    public void Send()\n    {\n"
                + "        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };\n"
                + "        handler.Dispose();\n    }\n}",
                CreationSubId),
            // The RIGHT way: the factory. A rule that fires on it is a rule
            // whose only fix is deletion.
            new ScanControl(
                "NearMiss/Factory.cs",
                "public sealed class ViaFactory\n{\n    public HttpClient Build(IHttpClientFactory factory)\n    {\n"
                + "        return factory.CreateClient(\"provider:x\");\n    }\n}",
                null),
            // A delegate that RETURNS a shared client is not a creation.
            new ScanControl(
                "NearMiss/SharedDelegate.cs",
                "public sealed class SharedDelegate\n{\n    private static readonly HttpClient Shared = Build();\n"
                + "    public Func<HttpClient> Factory { get; } = () => Shared;\n}",
                null),
            // Prose that names the API is not a construction.
            new ScanControl(
                "NearMiss/Prose.cs",
                "// never write `new HttpClient()` here — take the shared one\n"
                + "/// <summary>Uses the process-wide client.</summary>",
                null),
            // The table's allowance is real: the same shape in an allowed file
            // is not reported.
            new ScanControl(
                "src/Harbor.Tools.Builtin/Tools/Mcp/McpOAuthHandler.cs",
                "        _http = httpFactory is not null ? httpFactory() : new HttpClient();",
                null),
        ],
        MinHits = 500,
        MustContain =
        [
            .. AllowedCreators.Select(static row => row.Path),
            // The file #1854's second finding lived in — the scope must still
            // see it, or the rule stopped covering the defect it was written
            // for.
            "src/Harbor.Hosting/Modules/JsonProviderDiscovery.cs",
        ],
    };

    /// <summary>
    ///     The text matcher for the per-call shape, as the ORACLE of the
    ///     migration. It gates nothing, for the same reason the one above does.
    /// </summary>
    internal static readonly ScanRule DelegateOracle = new()
    {
        Id = "HttpClientInDelegate",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                DelegateSubId,
                CreationInDelegate,
                "Build the client where the owner lives and keep it — a field, a lazy field, or the "
                + "one client the owning component already has. A delegate that CREATES a client is "
                + "invoked per call, so it mints a client per call: a private connection pool per "
                + "call, with its own DNS lifetime and no owner to dispose it (#1854)."
            ),
        ],
        Controls =
        [
            // The exact shape #1854 found, quoted from the defect.
            new ScanControl(
                "Planted/PerCallFactory.cs",
                "public sealed class PerCallFactory\n{\n    public McpOAuthHandler(string server)\n    {\n"
                + "        _httpFactory = httpFactory ?? (() => new HttpClient());\n    }\n}",
                DelegateSubId),
            // A creation that is NOT in a delegate is rule 1's business, not
            // this rule's.
            new ScanControl(
                "NearMiss/Plain.cs",
                "public sealed class Plain\n{\n    public HttpClient Build()\n    {\n"
                + "        return new HttpClient();\n    }\n}",
                null),
            // A delegate that returns a shared client is the correct shape.
            new ScanControl(
                "NearMiss/SharedDelegate.cs",
                "public sealed class SharedDelegate\n{\n    private static readonly HttpClient Shared = Build();\n"
                + "    public Func<HttpClient> Factory { get; } = () => Shared;\n}",
                null),
            // Prose that names the API is not a construction.
            new ScanControl(
                "NearMiss/Prose.cs",
                "// the old code was `() => new HttpClient()` — one pool per call",
                null),
        ],
        MinHits = 500,
    };

    // =====================================================================
    // The census: what the analyzer sees on the live tree.
    // =====================================================================

    /// <summary>One client creation, as the analyzer found it.</summary>
    /// <param name="File">Repo-relative path, forward slashes.</param>
    /// <param name="Line">1-based line of the construction.</param>
    internal readonly record struct ClientCreation(string File, int Line);

    /// <summary>
    ///     What the analyzer found in the product tree: the creations, the ones
    ///     that sit inside a delegate, and the files its scope covers.
    /// </summary>
    /// <param name="Creations">Every client creation the analyzer found.</param>
    /// <param name="InDelegate">The creations that sit inside a delegate.</param>
    /// <param name="ScopeFiles">The repo-relative files the analyzer's scope covers.</param>
    internal sealed record ClientCreationCensus(
        IReadOnlyList<ClientCreation> Creations,
        IReadOnlyList<ClientCreation> InDelegate,
        IReadOnlyList<string> ScopeFiles);

    /// <summary>
    ///     The census, computed once. The analyzer runs over every product
    ///     compilation, and that is the most expensive thing this rule does,
    ///     so it is done ONCE and shared by every test below.
    /// </summary>
    private static readonly Lazy<Task<ClientCreationCensus>> LazyCensus = new(BuildCensus);

    /// <summary>
    ///     Runs the analyzer over the product tree and reads its verdicts.
    /// </summary>
    private static async Task<ClientCreationCensus> BuildCensus()
    {
        var creations = new List<ClientCreation>();
        var inDelegate = new List<ClientCreation>();
        var scopeFiles = new List<string>();

        foreach ((_, CSharpCompilation compilation) in ArchAnalyzerPromotionTests.LiveCompilations.Value)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                string? path = tree.FilePath;
                if (path is not null)
                {
                    scopeFiles.Add(SourceScan.Relative(path));
                }
            }

            List<Diagnostic> diagnostics = await RunAnalyzerAsync(compilation);
            foreach (Diagnostic diagnostic in diagnostics)
            {
                string? path = diagnostic.Location.SourceTree?.FilePath;
                if (path is null)
                {
                    continue;
                }

                var creation = new ClientCreation(
                    SourceScan.Relative(path),
                    diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);

                if (diagnostic.Id == HttpClientCreationAnalyzer.DelegateDiagnosticId)
                {
                    inDelegate.Add(creation);
                    continue;
                }

                if (diagnostic.Id == HttpClientCreationAnalyzer.CreationDiagnosticId)
                {
                    creations.Add(creation);
                }
            }
        }

        return new ClientCreationCensus(
            creations,
            inDelegate,
            [.. scopeFiles.Distinct(StringComparer.Ordinal).OrderBy(static f => f, StringComparer.Ordinal)]);
    }

    /// <summary>Runs the analyzer over one compilation.</summary>
    private static async Task<List<Diagnostic>> RunAnalyzerAsync(CSharpCompilation compilation)
    {
        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [new HttpClientCreationAnalyzer()],
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        ImmutableArray<Diagnostic> diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics];
    }

    /// <summary>
    ///     The sites the TEXT matcher names on the live tree, as
    ///     <c>path:line</c>. This is the oracle's half of the differential.
    /// </summary>
    internal static List<string> TextRuleSites()
    {
        List<ScanHit> hits = ScanRunner.Collect(
            CreationOracle,
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(CreationOracle)));

        return
        [
            .. hits.Select(static h => $"{h.File}:{h.Line}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static s => s, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The sites the ANALYZER names on the live tree, as <c>path:line</c>.
    ///     This is the analyzer's half of the differential.
    /// </summary>
    internal static async Task<List<string>> AnalyzerSitesAsync()
    {
        ClientCreationCensus census = await LazyCensus.Value;

        return
        [
            .. census.Creations
                .Select(static c => $"{c.File}:{c.Line}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static s => s, StringComparer.Ordinal)
        ];
    }

    // =====================================================================
    // The rule.
    // =====================================================================

    /// <summary>
    ///     Rule 1: a client is created only where the table says it may be.
    /// </summary>
    [Test]
    public async Task A_Client_Is_Created_Only_Where_The_Table_Names()
    {
        ClientCreationCensus census = await LazyCensus.Value;

        List<ClientCreation> violations =
        [
            .. census.Creations.Where(static c => AllowedCreators.All(row => row.Path != c.File))
        ];

        await Assert.That(violations.ToArray()).IsEmpty().Because(
            "An HTTP client may be built only by a named owner, in the files this rule's table names, "
            + "with the lifetime the row states. A client built anywhere else is a new site, and a new "
            + "site is how #1854's defect comes back: a private connection pool per client, with its "
            + "own DNS lifetime and nobody to dispose it. Use IHttpClientFactory.CreateClient(name) "
            + "for a provider, the shared McpHttpClientFactory for a remote MCP transport, or the "
            + "process-wide client the table names. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations.Select(static v => v.File + ":" + v.Line))}");
    }

    /// <summary>
    ///     Rule 2: no client is created inside a delegate.
    /// </summary>
    [Test]
    public async Task No_Client_Is_Created_Inside_A_Delegate()
    {
        ClientCreationCensus census = await LazyCensus.Value;

        await Assert.That(census.InDelegate.ToArray()).IsEmpty().Because(
            "A delegate that creates a client is invoked per call, so it mints a client per call — the "
            + "exact shape #1854 found in McpOAuthHandler, where `() => new HttpClient()` was called at "
            + "five call sites (discovery, token endpoint, registration, refresh, probe). Every one of "
            + "those clients carried its own SocketsHttpHandler and its own connection pool, and none of "
            + "them was ever disposed. Build the client where its owner lives and keep it. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, census.InDelegate.Select(static v => v.File + ":" + v.Line))}");
    }

    /// <summary>
    ///     Rule 3: the table is a budget — an allowed file may not grow a
    ///     second creation beside the one its row declares.
    /// </summary>
    [Test]
    public async Task The_Table_Is_A_Budget_And_Not_A_Permission()
    {
        ClientCreationCensus census = await LazyCensus.Value;
        var failures = new List<string>();

        foreach (var row in AllowedCreators)
        {
            int actual = census.Creations.Count(c => string.Equals(c.File, row.Path, StringComparison.Ordinal));
            if (actual != row.Budget)
            {
                failures.Add(
                    $"{row.Path} creates {actual} client(s); its row says {row.Budget}. Either a second "
                    + "client grew here — a per-call client wearing an allowed file's name — or the row "
                    + "is stale and the creation it described is gone.");
            }
        }

        await Assert.That(failures).IsEmpty().Because(
            "Each row of the table states a claim about one file, and the claim is checkable: this many "
            + "creations, and no more. A table that only said 'this file is allowed to create clients' "
            + "would be a permission that grows silently, which is the failure mode every table in this "
            + "project is written against. Failures:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    ///     The scope really is the product tree, and it still contains every
    ///     file the table names. A scope that stopped seeing them enforces
    ///     nothing.
    /// </summary>
    [Test]
    public async Task The_Scan_Finds_The_Product_Tree_And_Every_Named_File()
    {
        ClientCreationCensus census = await LazyCensus.Value;
        var failures = new List<string>();

        if (census.ScopeFiles.Count < 500)
        {
            failures.Add(
                $"the analyzer's scope yields {census.ScopeFiles.Count} file(s); the floor is 500 — the "
                + "scope is broken rather than the code clean");
        }

        foreach (var row in AllowedCreators)
        {
            if (!census.ScopeFiles.Contains(row.Path, StringComparer.Ordinal))
            {
                failures.Add($"{row.Path} is in this rule's table and the scope stopped seeing it");
            }
        }

        if (!census.ScopeFiles.Contains("src/Harbor.Hosting/Modules/JsonProviderDiscovery.cs", StringComparer.Ordinal))
        {
            failures.Add(
                "src/Harbor.Hosting/Modules/JsonProviderDiscovery.cs is the file #1854's second finding "
                + "lived in, and the scope stopped seeing it — the rule no longer covers the defect it "
                + "was written for");
        }

        await Assert.That(failures).IsEmpty().Because(
            "The rule needs a repository root, must really find the product tree (well over 500 source "
            + "files under src/ and apps/), and must still contain every file the table names — a scope "
            + "that quietly stopped seeing them is broken, not clean. "
            + string.Join("; ", failures));
    }

    /// <summary>
    ///     The oracle stays honest: it still fires on the planted per-call
    ///     shapes and still stays quiet on the shared-client shapes that are
    ///     correct.
    /// </summary>
    /// <remarks>
    ///     The oracle gates nothing, but the differential is worthless if the
    ///     oracle stopped matching: two matchers that both match nothing agree
    ///     with each other and prove nothing.
    /// </remarks>
    [Test]
    public async Task The_Text_Matcher_Still_Separates_A_PerCall_Client_From_A_Shared_One()
    {
        List<string> failures = ScanRunner.CheckControls(CreationOracle);
        failures.AddRange(ScanRunner.CheckControls(DelegateOracle));

        await Assert.That(failures).IsEmpty().Because(
            "the per-call shapes must be detected, or the differential compares two detectors that both "
            + "see nothing; the factory client, the shared-client delegate and the prose that merely "
            + "names the API must stay quiet, or the oracle forbids the fix. "
            + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every row of the table states why it is allowed, in the row itself.
    /// </summary>
    [Test]
    public async Task Every_Row_States_Its_Reason()
    {
        List<string> failures = ScanRunner.CheckReasons(CreationOracle);

        await Assert.That(failures).IsEmpty().Because(
            "a row that grants a permission and does not say why is indistinguishable from an accident. "
            + "Write the argument in the row itself, not in a comment above it: a comment is invisible "
            + "to every tool that reads this table, so the reason is lost the moment the next person "
            + "reformats the file. " + string.Join("; ", failures));
    }

    /// <summary>
    ///     A row whose file stopped creating a client is stale: the table has
    ///     drifted from the tree, and it must be fixed rather than kept.
    /// </summary>
    [Test]
    public async Task The_Table_Has_No_Stale_Rows()
    {
        ClientCreationCensus census = await LazyCensus.Value;
        var stale = new List<string>();

        foreach (var row in AllowedCreators)
        {
            bool any = census.Creations.Any(c => string.Equals(c.File, row.Path, StringComparison.Ordinal));
            if (!any)
            {
                stale.Add(row.Path);
            }
        }

        await Assert.That(stale).IsEmpty().Because(
            "A row that excuses a creation which no longer exists is a permission for a problem that "
            + "stopped existing — and it hides the fact that the file grew a DIFFERENT creation, which "
            + "the budget rule would then accept as the one the row always meant. Delete the row and "
            + "say what the file does now. Stale rows: " + string.Join("; ", stale));
    }
}
