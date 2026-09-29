// McpOAuthTokenShapeRules.cs — the guard for issue #566.
//
// WHY THIS FILE EXISTS
// --------------------
// `McpOAuthHandler` surfaced ONE lookup — "give me an access token for this
// server" — through THREE conventions at once:
//
//   1. `GetAccessTokenAsync`      -> `Task<string>`, throws
//                                   `McpOAuthLoginRequiredException`
//   2. `TryGetAccessTokenAsync`   -> `Task<string?>`, null
//   3. `TryGetAccessTokenResultAsync` -> `Task<Result<string>>`
//
// Only #2 was ever wired (`McpRegistry.GetTransport` at :548), so #1's callers
// were zero and #3's only consumer was #2 itself. But #1 was the one left in
// the PUBLIC surface, which makes it the one new code reaches for — and it is
// the shape that crashes a transport on a server the user simply has not
// logged into yet. "Not logged in" is an expected state, not an exception.
//
// The three-way split also forced a fourth, uglier convention. #3 reported
// "there is no token" as `Failure("LoginRequired: ...")` — the same Failure
// channel as "the server rejected our refresh grant" — and then had to tell
// them apart by re-parsing its own error string:
//
//     if (refreshed.Error.StartsWith("RefreshFailed:", StringComparison.Ordinal))
//
// An outcome encoded in a string that the same file pattern-matches is the
// signature of a missing type, and it is what the fix removes: the two cases
// are genuinely different, so they get genuinely different rails —
// `Maybe.None` for "no token exists" and `Result.Failure` for "the grant was
// rejected". `Result<Maybe<string>>` says all three things at once, and it is
// the shape the two transports and `McpLoopbackListener.ParseQueryResult`
// already produce locally (#590).
//
// WHAT IS ENFORCED
// ----------------
//   1. SHAPE (reflection): `McpOAuthHandler` exposes exactly ONE public
//      access-token method, and it returns `Task<Result<Maybe<string>>>`.
//      Reflection, not a text scan, because the shape is a property of the
//      compiled type — and because "exactly one" is the actual invariant; a
//      guard that only banned the throwing name would happily accept a fourth.
//   2. SHAPE (reflection): the transport seam carries `Result<Maybe<string>>`
//      on `McpTransportRequest` and on both transport constructors, so absence
//      cannot be re-introduced as a bare `null` at the boundary.
//   3. NO EXCEPTION CONVENTION (reflection): the bespoke login-required
//      exception type must not exist. While it existed, both transports carried
//      a `catch (McpOAuthLoginRequiredException)` arm that could not fire,
//      because the wired provider returns `null` rather than throwing.
//   4. NO STRING PROTOCOL (source): no `Failure` whose text opens with the
//      `LoginRequired:` / `RefreshFailed:` markers, and no `StartsWith` that
//      re-parses them. A source rule because the protocol is a statement, not
//      a signature — reflection cannot see it.
//
// NON-VACUITY
// -----------
// Rule 4's matcher is run against a synthetic positive control, because a
// source scan that matches nothing is indistinguishable from a source scan
// that is broken, and a broken guard is believed. See `Matcher_...` below.
// Rule 1 carries its own: zero found methods is a failure, not a pass.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Enforces the single <c>Result&lt;Maybe&lt;string&gt;&gt;</c> access-token
///     contract for the MCP OAuth path. See the file header for the incident.
/// </summary>
public sealed class McpOAuthTokenShapeRules
{
    /// <summary>
    ///     The one public token lookup. Named explicitly rather than matched by
    ///     shape so that renaming it is a deliberate, reviewable act instead of
    ///     a silent way to satisfy rule 1.
    /// </summary>
    private const string SingleShapeName = "TryGetAccessTokenResultAsync";

    /// <summary>
    ///     The single accepted return type. Absence is <c>Maybe.None</c>; a
    ///     rejected grant is a <c>Failure</c>.
    /// </summary>
    private static readonly Type AcceptedShape = typeof(Task<Result<Maybe<string>>>);

    /// <summary>
    ///     The bearer-token supplier signature, as the transports must declare it.
    /// </summary>
    private static readonly Type AcceptedProvider = typeof(Func<CancellationToken, Task<Result<Maybe<string>>>>);

    // Assembled from fragments so this file's own source does not contain the
    // banned literals contiguously — the scan below is src/-scoped today, but a
    // guard that breaks the day someone widens the scan is a guard that gets
    // narrowed back instead of fixed.
    private static readonly string LoginRequiredExceptionName = "McpOAuth" + "LoginRequiredException";

    private static readonly string LoginMarker = "Login" + "Required:";
    private static readonly string RefreshMarker = "Refresh" + "Failed:";

    /// <summary>
    ///     An outcome encoded as a prefixed string literal — either the failure
    ///     itself or the <c>StartsWith</c> that re-parses one. Matches the quote
    ///     that opens the literal, so a bare word in a comment cannot trip it
    ///     and a literal with some other prefix is not flagged.
    /// </summary>
    private static readonly Regex OutcomePrefixProtocol =
        new("\"" + LoginMarker + "|\"" + RefreshMarker, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Transport constructors whose token-supplier parameter is pinned.</summary>
    private static readonly Type[] TransportTypes = [typeof(McpHttpTransport), typeof(McpSseTransport)];

    // ── Rule 1: exactly one public shape ─────────────────────────────────────

    [Test]
    public async Task TokenLookup_HasExactlyOnePublicShape()
    {
        MethodInfo[] lookups =
        [
            .. typeof(McpOAuthHandler)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("AccessToken", StringComparison.Ordinal)),
        ];

        await Assert.That(lookups.Length).IsEqualTo(1)
            .Because(
                "One lookup must have one shape. McpOAuthHandler used to expose the same " +
                "'give me an access token' operation as a throwing Task<string>, a null-returning " +
                "Task<string?> and a Result<string> (#566). Found: " +
                Describe(lookups) + ". Delete the extras; do not add a fourth.");

        MethodInfo method = lookups[0];

        await Assert.That(method.Name).IsEqualTo(SingleShapeName)
            .Because(
                "The surviving name is part of the contract: a token lookup is a Result-railway " +
                "operation, and the sibling '…Result' names (ParseQueryResult, LoadResult, " +
                "RegisterClientResultAsync) mark that rail in this assembly. A plain name invites " +
                "the nullable overload back as a 'convenience'.");

        await Assert.That(method.ReturnType).IsEqualTo(AcceptedShape)
            .Because(
                "'No usable token exists' is absence, not failure: there is no error to report, the " +
                "user has simply not run 'harbor mcp login'. 'The grant was rejected' IS a failure. " +
                "Result<Maybe<string>> states both; Task<string> can only throw or lie.");
    }

    // ── Rule 2: the transport seam carries the same shape ────────────────────

    [Test]
    public async Task TransportRequest_CarriesTheMaybeProvider()
    {
        PropertyInfo? provider = typeof(McpTransportRequest).GetProperty("OAuthTokenProvider");

        await Assert.That(provider).IsNotNull()
            .Because("McpTransportRequest is a positional record; the property is its public face.");

        await Assert.That(provider!.PropertyType).IsEqualTo(AcceptedProvider)
            .Because(
                "A Func<…, Task<string?>> on this seam is what forced the transports to convert " +
                "absence into null and then re-invent it as Maybe<string>.From(…), losing the " +
                "absent-vs-rejected distinction on the way (#566).");
    }

    [Test]
    public async Task EveryTransport_AcceptsTheMaybeProvider()
    {
        List<string> offenders = [];

        foreach (Type transport in TransportTypes)
        {
            ParameterInfo[] parameters =
            [
                .. transport
                    .GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Where(p => string.Equals(p.Name, "oauthTokenProvider", StringComparison.Ordinal)),
            ];

            if (parameters.Length == 0)
            {
                offenders.Add($"{transport.Name}: no 'oauthTokenProvider' constructor parameter found");
                continue;
            }

            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType != AcceptedProvider)
                {
                    offenders.Add($"{transport.Name}.{parameter.Name}: {parameter.ParameterType}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because(
                "A transport that accepts a nullable token re-opens the null convention on the " +
                "public seam even though McpOAuthHandler stopped speaking it (#566).");
    }

    // ── Rule 3: the exception convention is gone ─────────────────────────────

    [Test]
    public async Task NoBespokeLoginRequiredExceptionTypeExists()
    {
        Type[] types = typeof(McpOAuthHandler).Assembly.GetTypes();

        Type[] offenders = [.. types.Where(t => string.Equals(t.Name, LoginRequiredExceptionName, StringComparison.Ordinal))];

        await Assert.That(offenders).IsEmpty()
            .Because(
                "The type existed only so 'the user has not logged in' could travel as an exception. " +
                "It had no production thrower — McpRegistry wires the null-returning overload — yet " +
                "both remote transports still carried a 'catch (McpOAuthLoginRequiredException)' arm " +
                "that could never fire, and the throwing overload stayed public for the next caller " +
                "to reach for (#566). Absence is Maybe.None.");

        // Non-vacuity: the type enumeration must actually see the OAuth types,
        // or "no offenders" is an artefact of a broken lookup.
        await Assert.That(types.Any(t => t == typeof(McpOAuthHandler))).IsTrue()
            .Because("Harbor.Tools.Builtin is a direct project reference; its types must be reachable.");
    }

    // ── Rule 4: no outcome-in-an-error-string protocol ───────────────────────

    [Test]
    public async Task TokenLookup_DoesNotEncodeOutcomesInPrefixedErrorStrings()
    {
        (string File, IReadOnlyList<string> Hits)[] scanned = ScanMcpSources();
        string[] files = [.. scanned.Select(s => s.File)];

        await Assert.That(files.Length).IsGreaterThanOrEqualTo(10)
            .Because(
                $"Non-vacuity: the scan must reach the MCP sources. It saw {files.Length} — the " +
                "path or the glob is wrong, and every assertion below is vacuous.");

        List<string> violations = [];
        foreach ((string file, IReadOnlyList<string> hits) in scanned)
        {
            foreach (string hit in hits)
            {
                violations.Add($"{file}: {hit}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "An outcome smuggled through an error string has to be re-parsed by the same file " +
                "('refreshed.Error.StartsWith(\"RefreshFailed:\")'), which is what the Maybe is for. " +
                "No token exists -> Success(Maybe.None). Grant rejected -> Result.Failure. " +
                "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Non-vacuity of the source matcher ────────────────────────────────────

    [Test]
    public async Task Matcher_FlagsBothHalvesOfTheStringProtocol()
    {
        const string Producer = """
            return Result.Failure<string>($"LoginRequired: MCP server '{_server}' needs OAuth login.");
            """;
        const string Consumer = """
            if (refreshed.Error.StartsWith("RefreshFailed:", StringComparison.Ordinal))
            """;

        await Assert.That(MatchOutcomePrefix(Producer).Count).IsGreaterThan(0)
            .Because("Producing the prefixed failure is half the #566 protocol; a miss means the rule is inert.");

        await Assert.That(MatchOutcomePrefix(Consumer).Count).IsGreaterThan(0)
            .Because(
                "Re-parsing the prefixed failure is the other half, and the half that actually shipped. " +
                "A guard that only watched the producer would have stayed green through the whole bug.");
    }

    [Test]
    public async Task Matcher_AcceptsTheFixedSpelling()
    {
        const string Fixed = """
            return Result.Success(Maybe<string>.None);
            """;

        await Assert.That(MatchOutcomePrefix(Fixed)).IsEmpty()
            .Because("The fixed spelling must not be flagged, or the guard cannot be adopted.");
    }

    [Test]
    public async Task Matcher_IgnoresTheMarkersOutsideAStringLiteral()
    {
        // A doc comment that merely *mentions* the old convention, in a shape the
        // regex must not read as a re-introduction of it.
        const string Mention = """
            ///     The old Failure("LoginRequired: …") is gone.
            // LoginRequired: not here.
            var reasoning = nameof(LoginRequired);
            """;

        await Assert.That(MatchOutcomePrefix(Mention)).IsEmpty()
            .Because(
                "Only a quote that OPENS a literal marks the protocol. Without this, the first honest " +
                "explanatory comment in this file family would cry wolf and the rule would be deleted.");
    }

    // ── Discovery helpers ────────────────────────────────────────────────────

    /// <summary>Repo-relative MCP source files, with the outcome-marker hits in each.</summary>
    private static (string File, IReadOnlyList<string> Hits)[] ScanMcpSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, "src", "Harbor.Tools.Builtin", "Tools", "Mcp");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<(string, IReadOnlyList<string>)> scanned = [];
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            scanned.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), MatchOutcomePrefix(File.ReadAllText(file))));
        }

        return [.. scanned.OrderBy(s => s.File, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     One hit per line that carries an outcome marker inside a string
    ///     literal, with the line text so a CI failure is actionable.
    ///     <para>
    ///     Line comments are stripped first, exactly as
    ///     <c>ResultMaybeSignatureTests</c> does. The fixed code is *supposed* to
    ///     be able to say what it used to do — a doc comment reading
    ///     <c>/// was Result.Failure("LoginRequired: …")</c> is documentation, not a
    ///     reintroduction — and a guard that cannot tell those apart gets deleted
    ///     the first time somebody writes an honest sentence about the bug.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> MatchOutcomePrefix(string text)
    {
        List<string> hits = [];
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Safe to split naively for this pattern: a "//" inside a string
            // literal cannot produce a match, because the match must open with a
            // quote AND the marker text is unreachable from one.
            string code = lines[i];
            int comment = code.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                code = code[..comment];
            }

            if (OutcomePrefixProtocol.IsMatch(code))
            {
                hits.Add($"L{i + 1}: {lines[i].Trim()}");
            }
        }

        return hits;
    }

    /// <summary>Readable method list for failure messages.</summary>
    private static string Describe(IEnumerable<MethodInfo> methods)
        => string.Join(", ", methods.Select(m => $"{m.Name} -> {m.ReturnType.Name}"));
}
