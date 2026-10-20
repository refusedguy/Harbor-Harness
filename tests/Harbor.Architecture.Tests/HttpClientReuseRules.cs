// HttpClientReuseRules.cs — the guard for issue #1854.
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
// KNOWN LIMITATION — stated, not hidden
// -------------------------------------
// This is a text rule over comment-stripped source, not a C# parser, and it
// cannot see a call frequency. It proves WHERE a client is created and HOW
// MANY times, not HOW OFTEN the creating code runs. A client created inside a
// method that is itself called per request would pass rules 1 and 3 if it sat
// in an allowed file; the shape #1854 actually had is caught by rule 2, and a
// per-request creation in a NEW file is caught by rule 1. What the rules
// cannot see is stated here rather than left for a reader to discover.
//
// NON-VACUITY
// -----------
// Every rule here can pass for the wrong reason — a scan that matches nothing,
// a scope that finds no files, a table whose rows no longer mean anything —
// and a guard that passes for the wrong reason is worse than no guard, because
// it is believed. So: the scope must really yield the product tree and
// contain every file the table names; the matcher must fire on planted
// per-call shapes and stay silent on the shared-client shapes that are
// correct; the table's reasons must be stated; and a row that no longer has a
// creation behind it must fail, so the table cannot rot into a permission
// for a problem that stopped existing.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1854: an HTTP client is created once, by a named owner, and
///     never per call. See the file header for the incident and the limits of
///     what a text rule can see.
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
    ///     Rule 1 + rule 3 as data: the named creators, their budgets, and the
    ///     controls that prove the matcher can both fire and stay quiet.
    /// </summary>
    private static readonly ScanRule CreationRule = new()
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
    ///     Rule 2 as data: no file may create a client inside a delegate. There
    ///     is deliberately NO baseline here — the allowance table of rule 1
    ///     cannot buy a delegate back.
    /// </summary>
    private static readonly ScanRule DelegateRule = new()
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

    /// <summary>
    ///     Rule 1: a client is created only where the table says it may be.
    /// </summary>
    [Test]
    public async Task A_Client_Is_Created_Only_Where_The_Table_Names()
    {
        List<string> violations = ScanRunner.Evaluate(CreationRule);

        await Assert.That(violations).IsEmpty().Because(
            "An HTTP client may be built only by a named owner, in the files this rule's table names, "
            + "with the lifetime the row states. A client built anywhere else is a new site, and a new "
            + "site is how #1854's defect comes back: a private connection pool per client, with its "
            + "own DNS lifetime and nobody to dispose it. Use IHttpClientFactory.CreateClient(name) "
            + "for a provider, the shared McpHttpClientFactory for a remote MCP transport, or the "
            + "process-wide client the table names. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     Rule 2: no client is created inside a delegate.
    /// </summary>
    [Test]
    public async Task No_Client_Is_Created_Inside_A_Delegate()
    {
        List<string> violations = ScanRunner.Evaluate(DelegateRule);

        await Assert.That(violations).IsEmpty().Because(
            "A delegate that creates a client is invoked per call, so it mints a client per call — the "
            + "exact shape #1854 found in McpOAuthHandler, where `() => new HttpClient()` was called at "
            + "five call sites (discovery, token endpoint, registration, refresh, probe). Every one of "
            + "those clients carried its own SocketsHttpHandler and its own connection pool, and none of "
            + "them was ever disposed. Build the client where its owner lives and keep it. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     Rule 3: the table is a budget — an allowed file may not grow a
    ///     second creation beside the one its row declares.
    /// </summary>
    [Test]
    public async Task The_Table_Is_A_Budget_And_Not_A_Permission()
    {
        var sources = new List<(string DisplayPath, string Source)>();
        foreach (var row in AllowedCreators)
        {
            string full = Path.Combine(
                RepoPaths.RepoRoot ?? ".",
                row.Path.Replace('/', Path.DirectorySeparatorChar));
            string? text = SourceScan.TryReadAllText(full);
            await Assert.That(text).IsNotNull().Because(
                $"the table names {row.Path}, and that file has to exist — a table row for a file that "
                + "is gone excuses nothing and hides the fact that the table drifted from the tree.");
            sources.Add((row.Path, text!));
        }

        List<ScanHit> hits = ScanRunner.Collect(CreationRule, sources);
        var failures = new List<string>();

        foreach (var row in AllowedCreators)
        {
            int actual = hits.Count(hit => string.Equals(hit.File, row.Path, StringComparison.Ordinal));
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
        List<string> discovery = ScanRunner.CheckDiscovery(CreationRule);
        discovery.AddRange(ScanRunner.CheckDiscovery(DelegateRule));

        await Assert.That(discovery).IsEmpty().Because(
            "The rule needs a repository root, must really find the product tree (well over 500 source "
            + "files under src/ and apps/), and must still contain every file the table names — a scope "
            + "that quietly stopped seeing them is broken, not clean. "
            + string.Join("; ", discovery));
    }

    /// <summary>
    ///     The matcher fires on the planted per-call shapes and stays quiet on
    ///     the shared-client shapes that are correct. Both directions, because
    ///     "reports everything" and "reports nothing" are the same broken
    ///     detector.
    /// </summary>
    [Test]
    public async Task The_Matcher_Separates_A_PerCall_Client_From_A_Shared_One()
    {
        List<string> failures = ScanRunner.CheckControls(CreationRule);
        failures.AddRange(ScanRunner.CheckControls(DelegateRule));

        await Assert.That(failures).IsEmpty().Because(
            "the per-call shapes must be detected, or the rule guards nothing; the factory client, the "
            + "shared-client delegate and the prose that merely names the API must stay quiet, or the "
            + "rule forbids the fix and is permanently red. "
            + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every row of the table states why it is allowed, in the row itself.
    /// </summary>
    [Test]
    public async Task Every_Row_States_Its_Reason()
    {
        List<string> failures = ScanRunner.CheckReasons(CreationRule);

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
        List<string> stale = ScanRunner.StaleBaselineKeys(
            CreationRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(CreationRule)));

        await Assert.That(stale).IsEmpty().Because(
            "A row that excuses a creation which no longer exists is a permission for a problem that "
            + "stopped existing — and it hides the fact that the file grew a DIFFERENT creation, which "
            + "the budget rule would then accept as the one the row always meant. Delete the row and "
            + "say what the file does now. Stale rows: " + string.Join("; ", stale));
    }
}
