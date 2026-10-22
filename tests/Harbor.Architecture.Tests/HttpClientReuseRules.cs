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
    internal readonly record struct ClientCreationSite(string File, int Line);

    /// <summary>
    ///     What the analyzer found in the product tree: the creations, the ones
    ///     that sit inside a delegate, and the files its scope covers.
    /// </summary>
    /// <param name="Creations">Every client creation the analyzer found.</param>
    /// <param name="InDelegate">The creations that sit inside a delegate.</param>
    /// <param name="ScopeFiles">The repo-relative files the analyzer's scope covers.</param>
    internal sealed record ClientCreationCensus(
        IReadOnlyList<ClientCreationSite> Creations,
        IReadOnlyList<ClientCreationSite> InDelegate,
        IReadOnlyList<string> ScopeFiles);

    /// <summary>
    ///     The census, computed once. The analyzer runs over every product
    ///     compilation, and that is the most expensive thing this rule does,
    ///     so it is done ONCE and shared by every test below.
    /// </summary>
    private static readonly Lazy<Task<ClientCreationCensus>> LazyCensus = new(BuildCensus);

    /// <summary>
    ///     The product compilations THIS rule grades, one per product project.
    /// </summary>
    /// <remarks>
    ///     They carry the project's implicit global usings, because a
    ///     compilation without them is not the project's compilation: four of
    ///     the five files that create an HTTP client never say
    ///     <c>using System.Net.Http;</c> — they rely on the SDK's implicit
    ///     usings — and in a compilation that lacks them the name does not
    ///     resolve, so the semantic model answers "error type" and the rule
    ///     sees nothing in those files. That was measured, not reasoned: the
    ///     first run of this rule found the two creations in the one file that
    ///     DOES say <c>using System.Net.Http;</c>, and none of the other five.
    ///     <para>
    ///         The set is the PROCESS-WIDE one, keyed by the usings-variant: the
    ///         analyzer sweep grades the same 64 projects over compilations
    ///         WITHOUT the implicit usings, and the two sets used to be built
    ///         from scratch — the census's SEQUENTIALLY, which is why six tests
    ///         sat waiting for forty seconds while one core was busy.
    ///     </para>
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<(RoslynProjectCompilation.Project Project, CSharpCompilation Compilation)>>
        LazyCompilations = new(() => RoslynProjectCompilation.Compilations(RoslynProjectCompilation.ImplicitGlobalUsings));

    /// <summary>
    ///     The files the analyzer's scope covers, read off the product
    ///     compilations — WITHOUT running the analyzer over them.
    /// </summary>
    /// <remarks>
    ///     The scope is a fact about the COMPILATIONS, not about the analyzer's
    ///     verdict, and it used to be read out of the census — so the test that
    ///     checks the scope waited for the whole analyzer pass to finish. The
    ///     scope is now its own lazy, and the scope test no longer waits for
    ///     the pass it does not need.
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<string>> LazyScopeFiles = new(BuildScopeFiles);

    /// <summary>
    ///     One product project's contribution to the census.
    /// </summary>
    /// <param name="Creations">Every client creation the analyzer found in this project.</param>
    /// <param name="InDelegate">The creations that sit inside a delegate.</param>
    /// <param name="ScopeFiles">The repo-relative files this project contributes to the scope.</param>
    internal sealed record ProjectCensus(
        IReadOnlyList<ClientCreationSite> Creations,
        IReadOnlyList<ClientCreationSite> InDelegate,
        IReadOnlyList<string> ScopeFiles);

    /// <summary>
    ///     The per-project contributions of the ONE census pass, in project
    ///     order.
    /// </summary>
    /// <remarks>
    ///     Kept so a reader can compare ONE of them against a fresh
    ///     computation over the same compilation — the parity proof that the
    ///     concurrent pass says what the sequential one said.
    /// </remarks>
    internal static IReadOnlyList<ProjectCensus> CensusProjects => Volatile.Read(ref _censusProjects);

    private static IReadOnlyList<ProjectCensus> _censusProjects = [];

    /// <summary>
    ///     The files the analyzer's scope covers: the syntax trees of the
    ///     product compilations, deduped and sorted.
    /// </summary>
    private static IReadOnlyList<string> BuildScopeFiles()
    {
        var scopeFiles = new List<string>();

        foreach ((_, CSharpCompilation compilation) in LazyCompilations.Value)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                string? path = tree.FilePath;
                if (path is not null)
                {
                    scopeFiles.Add(SourceScan.Relative(path));
                }
            }
        }

        return [.. scopeFiles.Distinct(StringComparer.Ordinal).OrderBy(static f => f, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Runs the analyzer over the product tree and reads its verdicts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The pass is SEQUENTIAL, and that is a MEASURED decision, not a
    ///         default. The census used to be a four-way fan-out over the 64
    ///         product compilations, and the measurement said the fan-out
    ///         LOSES: the census's own family of tests fell from 239.8 s to
    ///         118.5 s, but the other 1 135 tests rose by 146 s, for a net
    ///         +27 s over the whole shard (runs 39724342525 and 39734618143,
    ///         both on d0242260).
    ///     </para>
    ///     <para>
    ///         The reason is that the runner's four cores are already busy.
    ///         The shard runs fourteen lanes, and the summed test time is
    ///         about 3.2x the CPU the shard actually spends — so a fan-out
    ///         does not create capacity, it only moves the same work onto four
    ///         threads at once. And four threads binding the tree at the same
    ///         time allocate four times as fast, which on a workstation GC
    ///         costs MORE CPU than the one-threaded pass it replaced: the
    ///         census's wall fell from 40.2 s to 24.0 s, the shard's CPU rose
    ///         by about 45 core-seconds, and 45 core-seconds is 146 s of
    ///         summed test time.
    ///     </para>
    ///     <para>
    ///         The per-project contributions are still recorded, so one
    ///         project's share of the census can be recomputed and compared
    ///         with what this pass recorded.
    ///     </para>
    /// </remarks>
    private static async Task<ClientCreationCensus> BuildCensus()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<(RoslynProjectCompilation.Project Project, CSharpCompilation Compilation)> compilations =
            LazyCompilations.Value;

        var results = new ProjectCensus[compilations.Count];

        for (int index = 0; index < compilations.Count; index++)
        {
            results[index] = await CensusFor(compilations[index].Compilation).ConfigureAwait(false);
        }

        var creations = new List<ClientCreationSite>();
        var inDelegate = new List<ClientCreationSite>();

        foreach (ProjectCensus result in results)
        {
            creations.AddRange(result.Creations);
            inDelegate.AddRange(result.InDelegate);
        }

        Volatile.Write(ref _censusProjects, results);

        watch.Stop();
        Volatile.Write(ref _censusMilliseconds, watch.ElapsedMilliseconds);

        // The cost, printed rather than asserted: a measurement that fails a
        // build on a slow runner is a gate that gets switched off. The number
        // is the CENSUS's own — the wait a test sees is the wait, and the wait
        // is what this number measures.
        Console.WriteLine(
            $"[arch-cost] http-client-census compilations={compilations.Count} "
            + $"ms={watch.ElapsedMilliseconds} creations={creations.Count} "
            + $"in-delegate={inDelegate.Count} scope-files={LazyScopeFiles.Value.Count}");

        return new ClientCreationCensus(creations, inDelegate, LazyScopeFiles.Value);
    }

    /// <summary>
    ///     How long the ONE census pass took, in milliseconds.
    /// </summary>
    /// <remarks>
    ///     Recorded BY the pass, not measured by the reader: a reader that
    ///     times its own wait on a memoised lazy measures the wait, and the
    ///     wait is zero when the pass already ran.
    /// </remarks>
    internal static long CensusMilliseconds => Volatile.Read(ref _censusMilliseconds);

    private static long _censusMilliseconds;

    /// <summary>
    ///     The census of ONE product project: the analyzer's verdicts over that
    ///     project's compilation, and the files it contributes to the scope.
    /// </summary>
    /// <remarks>
    ///     This is the OLD implementation, kept as the ORACLE of the concurrent
    ///     pass: the fan-out above calls exactly this method, one project at a
    ///     time, so a reader can recompute one project's share of the census
    ///     on this thread and compare it with what the fan-out recorded.
    /// </remarks>
    private static async Task<ProjectCensus> CensusFor(CSharpCompilation compilation)
    {
        var creations = new List<ClientCreationSite>();
        var inDelegate = new List<ClientCreationSite>();
        var scopeFiles = new List<string>();

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            string? path = tree.FilePath;
            if (path is not null)
            {
                scopeFiles.Add(SourceScan.Relative(path));
            }
        }

        List<Diagnostic> diagnostics = await RunAnalyzerAsync(compilation).ConfigureAwait(false);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            string? path = diagnostic.Location.SourceTree?.FilePath;
            if (path is null)
            {
                continue;
            }

            var creation = new ClientCreationSite(
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

        return new ProjectCensus(creations, inDelegate, scopeFiles);
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
        List<ScanHit> hits = ScanRunner.CollectStripped(
            CreationOracle,
            ScanRunner.ReadStrippedSources(
                CreationOracle,
                ScanRunner.ScopeFiles(CreationOracle)));

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
    ///     Rule 1 as a FUNCTION of the census: the creations the table does not
    ///     name.
    /// </summary>
    /// <remarks>
    ///     The rule's verdict is a function, not a paragraph inside a test, so
    ///     that the same function can be handed a PLANTED census and be seen to
    ///     go red — see
    ///     <c>The_Rule_Goes_Red_On_A_Client_Created_Outside_The_Table</c>. A
    ///     verdict that only ever runs over the live tree cannot be shown to
    ///     fail, and a guard that cannot be shown to fail is a comment.
    /// </remarks>
    /// <param name="census">The census to grade.</param>
    internal static List<ClientCreationSite> CreationViolations(ClientCreationCensus census) =>
        [.. census.Creations.Where(static c => AllowedCreators.All(row => row.Path != c.File))];

    /// <summary>
    ///     Rule 1: a client is created only where the table says it may be.
    /// </summary>
    [Test]
    public async Task A_Client_Is_Created_Only_Where_The_Table_Names()
    {
        ClientCreationCensus census = await LazyCensus.Value;

        List<ClientCreationSite> violations = CreationViolations(census);

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
    ///     The rule goes RED on a client created outside the table.
    /// </summary>
    /// <remarks>
    ///     The control for the rule above. It is planted on purpose: the census
    ///     is handed one creation in a file the table does not name, and the
    ///     rule's own verdict function has to report it. Without this, the
    ///     green above says nothing — a rule that reports nothing is green, and
    ///     a census that never saw a creation would also be green.
    /// </remarks>
    [Test]
    public async Task The_Rule_Goes_Red_On_A_Client_Created_Outside_The_Table()
    {
        const string Offender = "src/Harbor.Example/NotInTheTable.cs";

        var planted = new ClientCreationCensus(
            [new ClientCreationSite(Offender, 42)],
            [],
            [Offender]);

        List<ClientCreationSite> violations = CreationViolations(planted);

        await Assert.That(violations.Count).IsEqualTo(1).Because(
            "the planted census holds one creation in a file the allowance table does not name, and "
            + "the rule's verdict has to report exactly that one site. A rule that reports nothing "
            + "here is a rule that cannot fail, and the green over the live tree says nothing about "
            + "a guard that cannot be shown to fail.");

        await Assert.That(violations[0].File).IsEqualTo(Offender).Because(
            "the violation has to name the file it is about; a verdict that reported an unidentifiable "
            + "site would be a verdict nobody could act on.");

        await Assert.That(violations[0].Line).IsEqualTo(42).Because(
            "the violation has to name the LINE it is about, so that the report points at the "
            + "construction rather than at the file.");
    }

    /// <summary>
    ///     The recorded census says what a fresh computation says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The census is a memoised answer about a tree that does not
    ///         change while the suite runs, so a memoised answer is only worth
    ///         having if it is the SAME answer a fresh computation gives. This
    ///         test recomputes ONE project's share of the census — the project
    ///         that owns the OAuth handler, the file #1854's defect lived in, so
    ///         the comparison is over a project that actually has a verdict to
    ///         get wrong — and compares it with what the census recorded.
    ///     </para>
    ///     <para>
    ///         The per-project records are also compared with the merged
    ///         census, because a merge that lost a project or reordered the
    ///         list would be a census that reports a different set of sites
    ///         than the pass found.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Recorded_Census_Says_What_A_Fresh_Computation_Says()
    {
        const string OwnedFile = "src/Harbor.Tools.Builtin/Tools/Mcp/McpOAuthHandler.cs";

        ClientCreationCensus census = await LazyCensus.Value;
        IReadOnlyList<ProjectCensus> projects = CensusProjects;

        await Assert.That(projects.Count).IsGreaterThan(0).Because(
            "the census recorded nothing per project, so there is no share to compare against, and "
            + "the comparison below would be comparing two empty sets and proving nothing.");

        int index = -1;
        for (int i = 0; i < projects.Count; i++)
        {
            if (projects[i].ScopeFiles.Contains(OwnedFile, StringComparer.Ordinal))
            {
                index = i;
                break;
            }
        }

        await Assert.That(index).IsGreaterThanOrEqualTo(0).Because(
            $"the project that owns {OwnedFile} is not in the census's per-project record, so the "
            + "comparison below has nothing to compare.");

        // The ORACLE: the same computation, over the same compilation, on this
        // thread, with no memo to read from.
        IReadOnlyList<(RoslynProjectCompilation.Project Project, CSharpCompilation Compilation)> compilations =
            LazyCompilations.Value;
        ProjectCensus fresh = await CensusFor(compilations[index].Compilation);

        ProjectCensus recorded = projects[index];

        await Assert.That(string.Join(" | ", fresh.Creations.Select(static c => c.File + ":" + c.Line)))
            .IsEqualTo(string.Join(" | ", recorded.Creations.Select(static c => c.File + ":" + c.Line)))
            .Because(
                "the concurrent pass and the sequential pass run the same analyzer over the same "
                + "compilation, so they must name the same creations in the same order. A difference "
                + "means the fan-out changed a verdict, which is the one thing it must not do.");

        await Assert.That(string.Join(" | ", fresh.InDelegate.Select(static c => c.File + ":" + c.Line)))
            .IsEqualTo(string.Join(" | ", recorded.InDelegate.Select(static c => c.File + ":" + c.Line)))
            .Because(
                "the per-call shape is a SECOND verdict of the same pass, and a fan-out that kept "
                + "the first one while losing the second would be a pass that reports half of what "
                + "it saw.");

        // The MERGE: the census is the projects' contributions, in project order.
        string merged = string.Join(" | ", census.Creations.Select(static c => c.File + ":" + c.Line));
        string perProject = string.Join(
            " | ",
            projects.SelectMany(static p => p.Creations).Select(static c => c.File + ":" + c.Line));

        await Assert.That(merged).IsEqualTo(perProject).Because(
            "the census is the projects' contributions concatenated in PROJECT order, and a merge "
            + "that lost a project or reordered the list would be a census that reports a different "
            + "set of sites than the pass found.");

        await Assert.That(recorded.Creations.Count).IsGreaterThan(0).Because(
            "the project compared above is the one that owns the OAuth handler, and it creates a "
            + "client. A comparison over a project with no creations proves that two empty lists "
            + "agree with each other.");
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
    /// <remarks>
    ///     The scope is read off the product COMPILATIONS, not out of the
    ///     census: it is a fact about which files the analyzer grades, and
    ///     reading it out of the verdict meant this test waited for the whole
    ///     analyzer pass to finish before it could say anything about the
    ///     scope.
    /// </remarks>
    [Test]
    public async Task The_Scan_Finds_The_Product_Tree_And_Every_Named_File()
    {
        IReadOnlyList<string> scopeFiles = LazyScopeFiles.Value;
        var failures = new List<string>();

        if (scopeFiles.Count < 500)
        {
            failures.Add(
                $"the analyzer's scope yields {scopeFiles.Count} file(s); the floor is 500 — the "
                + "scope is broken rather than the code clean");
        }

        foreach (var row in AllowedCreators)
        {
            if (!scopeFiles.Contains(row.Path, StringComparer.Ordinal))
            {
                failures.Add($"{row.Path} is in this rule's table and the scope stopped seeing it");
            }
        }

        if (!scopeFiles.Contains("src/Harbor.Hosting/Modules/JsonProviderDiscovery.cs", StringComparer.Ordinal))
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
