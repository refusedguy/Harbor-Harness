// MapErrorFailureShapeTests.cs — guard for the "hand-rolled error re-wrapping →
// ResultExtensions.MapError" wave, plus the behaviour tests that pin the converted
// error text.
//
// THE WAVE
// --------
// When a step on a Result railway fails and the caller adds its own context to the
// reason, the reason must travel through the library's error-mapping member, spelled
// `created.MapError(e => $"Failed to create session: {e}")`, and not through a
// hand-assembled failure spelled `Result.Failure<Session>(...)` around a string that
// interpolates `created.Error`.
//
// The second form is where the original cause dies: the string is built once, at the
// call site, from a value the caller had to remember to interpolate, and a future
// edit that adds a context prefix without the `{…Error}` hole silently drops the
// cause. The first form cannot be written wrong that way — the error is a function
// of `e`, so it is structurally impossible to lose it.
//
// THE MEASUREMENT, AND WHY THE ALLOW-LIST IS AS LONG AS IT IS
// ----------------------------------------------------------
// Verified against the pinned CSharpFunctionalExtensions 3.7.0 source
// (Result/Methods/Extensions/MapError.cs), NOT against the API inventory doc:
//
//     line 10: MapError(this Result     result, Func<string,string>) → Result
//     line 56: MapError<T>(this Result<T> result, Func<string,string>) → Result<T>
//
// Both return the SAME success type they were given. No overload anywhere in 3.7.0
// changes `T`: the typed-error overloads (`Result<T,E> → Result<T,E2>`) keep `T` and
// change the error type instead. That single fact decides this wave:
//
//   * `Result.Failure<HandBuilt>("ctx: " + x.Error)` where `x` is a `Result<S>` and
//     the method returns `Result<T>` is a RE-TYPE. `MapError` cannot express it
//     (it would hand back `Result<S>`), and `ConvertFailure<T>()` — which can —
//     THROWS `InvalidOperationException` on success (ConvertFailure.cs:10-16), so it
// check-doc-cites: record-drift ConvertFailure.cs:10 now="unresolved" [no tracked file, and none in git history: the citation names a DEPENDENCY's source (TUnit / CSharpFunctionalExtensions), which no rule in this repo can resolve] -->
//     is only safe already inside a failure branch.
//   * Only a site whose success type is unchanged is a `MapError` site.
//
// Counted over `src/`, `apps/` and `contrib/` (not tests) with a balanced-paren scan
// for `Failure(...)` calls whose argument mentions `<ident>.Error`:
//
//     72  hand-rolled error re-wrapping sites in total
//     17  of them hand-BUILD the message (a string interpolating or concatenating
//         another result's `.Error`) — this is the family this guard governs
//      4  type-preserving, therefore the only `MapError`-expressible ones (this wave)
//     13  re-type the success value, so `MapError` cannot express them (allow-listed)
//
// docs/ROP-API-INVENTORY.md row 11 counts "40" sites for the bare
// `Result.Failure(<x>.Error)` spelling and routes them to `MapError`. That count was
// taken against the bare re-type form, every member of which changes `T`, so none of
// them is a `MapError` candidate. The row is corrected in the same PR.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that is
// broken, and a broken guard is worse than none because it is believed. Two tests
// close that. `Scanner_FlagsTheOldSpellingAndAcceptsTheNewOne` requires a repository
// root and a non-trivial file count, and runs the SAME matcher against a synthetic
// positive control (the old spelling, which must be flagged) and two synthetic
// negative controls (the converted spelling and its legitimate neighbours, which
// must not be). `AllowList_EveryEntryStillMatchesSomething` then fails when an
// exemption stops matching anything, so a stale exemption cannot sit there silently
// letting the pattern back in.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Agents;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a <c>Result</c> failure message that carries a cause is
///     produced by <c>ResultExtensions.MapError</c>, never by hand. Behaviour tests:
///     the converted sites still name their cause.
/// </summary>
public sealed class MapErrorFailureShapeTests
{
    /// <summary>Repo-relative trees this wave governs, composition roots included.</summary>
    private static readonly string[] GuardedTrees = ["src", "apps", "contrib"];

    /// <summary>
    ///     A hand-built failure message: a <c>Failure(…)</c> call whose argument assembles a
    ///     string from another result's <c>.Error</c>. The window may not cross a <c>;</c>, so
    ///     a match is always confined to one statement — that is what stops an unrelated
    ///     <c>Failure(…)</c> earlier in a method from borrowing an interpolated string that
    ///     belongs to a later one.
    ///     <list type="bullet">
    ///         <item><c>Result.Failure($"Cannot export '{id}': {s.Error}")</c> — matches.</item>
    ///         <item>
    ///             <c>if (r.IsFailure)</c> — no match: <c>IsFailure</c> is followed by <c>)</c>
    ///             and the pattern requires <c>(</c>.
    ///         </item>
    ///         <item>
    ///             <c>Result.Failure&lt;Session&gt;(resolved.Error)</c> — no match: no string is
    ///             built, so this is a plain re-type — a different wave.
    ///         </item>
    ///         <item>
    ///             <c>created.MapError(e =&gt; $"…: {e}")</c> — no match: the callee is not
    ///             <c>Failure</c>, which is the whole point of the conversion.
    ///         </item>
    ///     </list>
    /// </summary>
    // Raw string literal, deliberately: a verbatim @"…" cannot hold the regex's own double
    // quote without doubling it, and this pattern leans on `"` as a delimiter. The pattern
    // IS the specification here, so it has to be readable as written.
    private static readonly Regex HandBuiltFailureError = new(
        """
        Failure\s*(?:<[^<>()]*>)?\s*\((?:[^;]){0,400}?(?:\$"[^"\n]*\{\s*[A-Za-z_][A-Za-z0-9_]*(?:\(\))?\.Error\s*\}|\+\s*[A-Za-z_][A-Za-z0-9_]*(?:\(\))?\.Error\b)
        """,
        RegexOptions.Compiled);

    /// <summary>
    ///     Files allowed to keep a hand-built failure message, each with the reason it is not
    ///     part of this wave. Every reason is the same fact stated against concrete type
    ///     pairs, because the fact is mechanical: <c>MapError</c> returns <c>Result&lt;T&gt;</c>
    ///     for a <c>Result&lt;T&gt;</c> and no other overload exists. These sites own a
    ///     DIFFERENT <c>T</c> on the way out, so they are re-types, not error-mappings —
    ///     filed for the re-type wave.
    ///     <para>
    ///         An exemption is granted per file, not per site, so a NEW hand-built site inside
    ///         an exempt file would slip past. That is the same trade-off
    ///         <c>MaybeAbsenceTests</c> makes, and it is the right one here: five narrow files
    ///         versus a per-site allow-list that rots the moment a line moves.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> HandBuiltMessageExemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Storage.Jsonl/SessionPorter.cs"] =
            "Four sites, all re-types: `store.GetAsync` is Result<Session> → Result (line 50), "
            + "`GetMessagesAsync` is Result<IReadOnlyList<AgentMessage>> → Result (63), "
            + "`TryReadNonEmptyLineAsync` is Result<Maybe<string>> → Result<string> (95), and "
            + "`store.CreateAsync` is Result<Session> → Result<string> (124). MapError would hand "
            + "back the store's own Result<T>; the porter's Result / Result<string> is a different "
            + "contract. A message that keeps both the session id and the store's reason is right "
            + "here — the hand-built string is the honest shape until the signature moves.",
        ["src/Harbor.Tools.Builtin/Tools/Mcp/McpOAuthHandler.cs"] =
            "Two re-types: `McpOAuthFlow.RefreshAsync` is Result<TokenResponse> → Result<string> "
            + "(138) and `ExchangeCodeAsync` is Result<TokenResponse> → Result<string> (204). The "
            + "public surface is a token STRING; the OAuth flow returns a token RESPONSE. MapError "
            + "cannot cross that boundary, and flattening to `ex => ex` would throw the server name "
            + "away — the very loss this wave exists to prevent.",
        ["src/Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs"] =
            "One re-type: `entry.GetTransport` is Result<IMcpRemoteTransport> → Result<string> "
            + "(409). The registry's contract is a JSON-RPC string; the transport factory returns a "
            + "transport. The hand-built prefix keeps `server.method`, which is the only thing that "
            + "identifies WHICH call failed.",
        ["src/Harbor.Application/Agents/SubAgentRunner.cs"] =
            "Four re-types: Result<Session> → Result<SubAgentRunResult> (109), "
            + "Result<IReadOnlyList<AgentMessage>> → Result<SubAgentRunResult> (132), "
            + "Result<AgentRunResult> → Result<SubAgentRunResult> (178) and the same at 189. The "
            + "sub-run rail has its own payload type; the three trailer sites also wrap the text in "
            + "SubAgentFailureFormat.WithResumeTrailer, which is still a function of `e` and would "
            + "compose with MapError the moment the types line up.",
        // src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs is deliberately
        // NOT here any more (#600). Its two re-type sites are now
        // `ConvertFailure<Session>().MapError(…)` — a re-type the library CAN express,
        // so the exemption had nothing left to exempt. See
        // SessionsSlice_HasNoHandBuiltFailureMessage and
        // SessionsSlice_ExemptionIsGoneOnceItsLastSiteWasConverted.
    };

    // ── Rule: no hand-built failure message ──────────────────────────────────

    [Test]
    public async Task ResultFailureMessage_IsMapped_NotHandBuilt()
    {
        int scanned = 0;
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTrees())
        {
            scanned++;

            if (HandBuiltMessageExemptions.ContainsKey(relative))
            {
                continue;
            }

            HandBuiltHit hit = FindHandBuilt(text);
            if (hit.Found)
            {
                violations.Add($"{relative}:{hit.Line}: {hit.Snippet}");
            }
        }

        await Assert.That(scanned).IsGreaterThan(500)
            .Because(
                "Non-vacuity: src/, apps/ and contrib/ hold well over 500 source files (1118 at the "
                + "time of writing). Fewer means the walk stopped matching — a renamed tree, a moved "
                + "marker — and every assertion below became vacuous. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(violations).IsEmpty()
            .Because(
                "A failure message that carries another result's cause is the shape where the cause "
                + "dies: it is assembled at the call site, so an edit can add a prefix and drop the "
                + "cause with nothing noticing. Write `x.MapError(e => \"context: \" + e)` instead — "
                + "the message becomes a function of `e` and cannot lose it. If the site's success "
                + "type CHANGES across the failure, MapError genuinely cannot apply (it is "
                + "Result<T> → Result<T>); add the file to HandBuiltMessageExemptions with the "
                + "concrete type pair. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Issue #600: the Sessions slice has no hand-built failure left ─────────

    /// <summary>
    ///     <b>#600.</b> The three type-preserving sites in <c>SessionFactory</c> were
    ///     converted by the earlier wave; the remaining two are re-types
    ///     (<c>Result&lt;IReadOnlyList&lt;AgentMessage&gt;&gt;</c> and <c>Result</c> both
    ///     have to arrive as <c>Result&lt;Session&gt;</c>). The inventory's own verdict
    ///     on a re-type is a <c>ConvertFailure</c> wave, not an exemption — see
    ///     <c>docs/ROP-API-INVENTORY.md</c> §3 row 11 and §4 item 12 — and
    ///     <c>ConvertFailure&lt;T&gt;()</c> composes with <c>MapError</c>, so the prefix
    ///     can still be a function of <c>e</c> rather than a string that merely
    ///     interpolates <c>x.Error</c>.
    /// </summary>
    [Test]
    public async Task SessionsSlice_HasNoHandBuiltFailureMessage()
    {
        const string slice = "src/Harbor.Ui.Framework.Sessions/";

        int scanned = 0;
        var hits = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTrees())
        {
            if (!relative.StartsWith(slice, StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            HandBuiltHit hit = FindHandBuilt(text);
            if (hit.Found)
            {
                hits.Add($"{relative}:{hit.Line}: {hit.Snippet}");
            }
        }

        await Assert.That(scanned).IsGreaterThan(3)
            .Because(
                "Non-vacuity: the slice holds well over three source files. Fewer means the walk stopped "
                + "matching and the assertion below became vacuous — a guard that cannot see the file it "
                + "was written for is worse than no guard, because it reads as a permanent green light.");

        await Assert.That(hits).IsEmpty()
            .Because(
                "A failure message that carries a cause is a function of `e`, never a string assembled at "
                + "the call site. `GetMessagesAsync` is Result<IReadOnlyList<AgentMessage>> and "
                + "`AppendMessageAsync` is Result, but both must arrive as Result<Session> — so re-type "
                + "with ConvertFailure<Session>() (inside the IsFailure branch, which is what keeps it from "
                + "throwing on a success) and then rewrite the context with MapError. The branch path's "
                + "copy failure additionally reports how far it got (`{copied} of {total} copied`) while "
                + "leaving a half-written branch in the store, so that number is exactly the kind of "
                + "context that must not be droppable. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    /// <summary>
    ///     The two-sided close: once the slice is clean, its exemption must be GONE.
    ///     A stale entry would let the hand-built shape back in with nobody watching,
    ///     and <see cref="AllowList_EveryEntryStillMatchesSomething" /> cannot see
    ///     that — it only checks that an entry still matches, not that a converted
    ///     site no longer needs one.
    /// </summary>
    [Test]
    public async Task SessionsSlice_ExemptionIsGoneOnceItsLastSiteWasConverted()
    {
        const string converted = "src/Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs";

        await Assert.That(HandBuiltMessageExemptions.ContainsKey(converted)).IsFalse()
            .Because(
                converted + " is exempt for its two RE-TYPE sites, but both are now expressed as "
                + "ConvertFailure<Session>().MapError(…) — a re-type the library member CAN perform, inside "
                + "a failure branch. An exemption with nothing left to exempt is dead width: it silently "
                + "re-widens the rule the next time someone writes the hand-built shape. Remove the entry "
                + "(#600).");
    }

    // ── Self-check 1: the scanner must not run vacuously ─────────────────────

    [Test]
    public async Task Scanner_FlagsTheOldSpellingAndAcceptsTheNewOne()
    {
        // Positive control: the exact shape this wave removed. If the matcher ever stops
        // recognising it, the rule above is inert and reads as a permanent green light.
        const string Old = """
            if (createResult.IsFailure)
                return Result.Failure<Session>($"Failed to create default session: {createResult.Error}");
            """;

        // Negative control: the converted spelling plus the legitimate neighbour on the
        // next line — a log call that mentions `.Error`, and a bare re-type.
        const string Converted = """
            if (createResult.IsFailure)
            {
                _logger.LogError("Failed to create default session: {Error}", createResult.Error);
                return createResult.MapError(static e => $"Failed to create default session: {e}");
            }

            return Result.Failure<Session>(resolved.Error);
            """;

        // Second negative control, wider net: an IsFailure guard, a literal failure, a
        // MapError chain and a re-type in one method — the shapes a false positive would
        // hit first, and the ones a future contributor is most likely to write.
        const string Neighbours = """
            if (resolved.IsFailure)
                return Result.Failure("Import failed: payload is empty.");

            Result<List<Hunk>> parsed = HunkParser.TryParse(patch)
                .MapError(static e => $"Failed to parse patch: {e}");

            if (parsed.IsFailure)
                return Result.Failure<PatchInput>(parsed.Error);
            """;

        await Assert.That(FindHandBuilt(Old).Found).IsTrue()
            .Because(
                "The positive control for this wave. A hand-built \"context: {x.Error}\" inside a "
                + "Failure call is the banned shape; a miss means the matcher degraded and the rule "
                + "is decorative.");

        await Assert.That(FindHandBuilt(Converted).Found).IsFalse()
            .Because(
                "The converted spelling must stay clean. The log call, the MapError chain and the "
                + "bare re-type are all legal neighbours — flag them and the guard cries wolf on "
                + "the first honest hit, after which nobody reads it.");

        await Assert.That(FindHandBuilt(Neighbours).Found).IsFalse()
            .Because(
                "Same control, wider net. The window is bounded to one statement because it cannot "
                + "cross a `;`, so the interpolated string belonging to the MapError chain cannot be "
                + "borrowed by an earlier Failure call — this is the assertion that proves it.");
    }

    // ── Self-check 2: no allow-list entry may go stale ───────────────────────

    [Test]
    public async Task AllowList_EveryEntryStillMatchesSomething()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        var stale = new List<string>();

        foreach (string relative in HandBuiltMessageExemptions.Keys)
        {
            if (!File.Exists(Path.Combine(RepoPaths.RepoRoot, relative)))
            {
                stale.Add($"{relative} — exempt file no longer exists");
                continue;
            }

            if (!FindHandBuilt(File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, relative))).Found)
            {
                stale.Add($"{relative} — exemption no longer matches a hand-built failure message");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exemption whose pattern has been fixed is silently dead: it would let the shape "
                + "come back with nobody watching. Delete the entry, or update its reason to describe "
                + "the new shape. Stale entries:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    // ── Behaviour: the converted sites still name their cause ────────────────

    [Test]
    public async Task SessionFactory_CreateNewAsync_Error_StillCarriesTheStoreCause()
    {
        const string Cause = "disk full (ENOSPC) while writing the session header";

        Result<Session> result = await NewFactory(new FailingSessionStore(Cause))
            .CreateNewAsync(agentName: "code");

        await Assert.That(result.IsFailure).IsTrue()
            .Because("The stub store fails CreateAsync, so the branch under test is the one taken.");

        await Assert.That(result.Error).IsEqualTo($"Failed to create session: {Cause}")
            .Because(
                "This is the wave's reason for existing. The store's own reason must survive the "
                + "context prefix verbatim: that reason is the only thing that tells an operator the "
                + "disk filled up, and it is exactly what a later 'simplification' to "
                + "`.MapError(_ => \"Failed to create session\")` would throw away.");

        await Assert.That(result.Error).Contains(Cause)
            .Because(
                "Restated independently of the exact prefix: whatever the context text becomes, the "
                + "cause must still be in the message. A rewrite that keeps the prefix and drops the "
                + "cause fails this even when the equality check is loosened.");
    }

    [Test]
    public async Task SessionFactory_CreateDefaultAsync_Error_StillCarriesTheStoreCause()
    {
        const string Cause = "root is not writable";

        Result<Session> result = await NewFactory(new FailingSessionStore(Cause))
            .CreateDefaultAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo($"Failed to create default session: {Cause}")
            .Because(
                "The second converted site, same contract: MapError carries the cause through, it "
                + "does not replace it. A regression that swapped MapError for a literal "
                + "Result.Failure(\"Failed to create default session.\") would still fail the happy "
                + "path and would lose the diagnosis only here.");
    }

    [Test]
    public async Task SessionFactory_CreateBranchAsync_Error_StillCarriesTheCauseAndTheSessionId()
    {
        const string Cause = "session index is corrupt";
        Session source = Session.Create("/home/user/project", "code", "test-provider", "test-model");

        Result<Session> result = await NewFactory(new FailingSessionStore(Cause))
            .CreateBranchAsync(source);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo($"Failed to branch session '{source.Id}': {Cause}")
            .Because(
                "The third converted site, and the only one whose MapError lambda CAPTURES state "
                + "(`source.Id`) rather than being `static`. A capture is easy to break — dropping it, "
                + "or shadowing `e` — and this assertion is what notices. Domain context (WHICH "
                + "session) and cause (WHY) must both survive. Since #670 the cause arrives from the "
                + "core fork unchanged (ResultConversionBehaviourTests), so this is exactly the two "
                + "halves and nothing else: a session-id prefix, and the store's text.");
    }

    /// <summary>
    ///     The fourth site: the parent exists, the history read does not. Since #670 this goes
    ///     through the core fork, so it also pins that the chain stays composition rather than
    ///     substitution — the factory's session id, then the store's own text.
    /// </summary>
    [Test]
    public async Task SessionFactory_CreateBranchAsync_HistoryReadFailure_StillCarriesTheCause()
    {
        const string Cause = "transcript is not readable";
        Session source = Session.Create("/home/user/project", "code", "test-provider", "test-model");

        Result<Session> result = await NewFactory(new BranchStore(causeOnHistoryRead: Cause, parent: source))
            .CreateBranchAsync(source);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo($"Failed to branch session '{source.Id}': {Cause}")
            .Because(
                "The fork reads the parent before it creates the child, so this is reachable only "
                + "with a parent row to hand back — BranchStore gained one for exactly that. The "
                + "point of the assertion is unchanged from the pre-#670 shape: the cause arrives "
                + "byte-identical, with the session id in front of it and nothing dropped.");
    }

    /// <summary>
    ///     The fifth site, and the one the issue calls load-bearing: the child already exists in
    ///     the store, the transcript is truncated, and the message is the only record of how far
    ///     the copy got.
    /// </summary>
    [Test]
    public async Task SessionFactory_CreateBranchAsync_CopyFailure_StillReportsProgressAndCause()
    {
        const string Cause = "write-ahead log is full";
        Session source = Session.Create("/home/user/project", "code", "test-provider", "test-model");
        // Three messages, so the copy loop fails on the FIRST append and the reported
        // progress is "0 of 3" — a number that could only come from a real counter.
        var messages = new List<AgentMessage>
        {
            NewUserMessage("m1", "start the port"),
            NewUserMessage("m2", "on it"),
            NewUserMessage("m3", "now the tests")
        };

        Result<Session> result = await NewFactory(
                new BranchStore(causeOnAppend: Cause, history: messages, parent: source))
            .CreateBranchAsync(source);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo(
                $"Failed to branch session '{source.Id}': Failed to copy message history (0 of 3 copied): {Cause}")
            .Because(
                "Three layers of context, each owned by the layer that knows it: the session id by "
                + "the Presentation layer, the copy progress by the core loop (only it knows where "
                + "it stopped), the cause by the store. #670 deleted the duplicate fork and brought "
                + "the progress along with it; dropping it would leave a half-written child in the "
                + "store with nothing recording how far it reached.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     A <see cref="SessionFactory" /> wired to a store that always fails, to the real core
    ///     fork over that same store (#670), and to nothing else. <c>IAgent</c> is
    ///     <c>null</c> because the three create paths never read it, and
    ///     <c>ICommonConfigModelRefReader</c> is <c>null</c> because it is an optional
    ///     dependency (#63) — passed as a declared constructor argument since #470,
    ///     where it used to be dug out of a service provider on every call.
    /// </summary>
    private static SessionFactory NewFactory(ISessionStore store)
    {
        AgentRegistry agents = new();
        _ = agents.Register(AgentDefinition.CodeDefault("test-model", "test-provider"));

        return new SessionFactory(
            agents,
            null!,
            store,
            new CoreSessionForker(store),
            NullLogger<SessionFactory>.Instance,
            configReader: null);
    }

    /// <summary>A user message with the full positional shape <c>UserMessage</c> declares.</summary>
    private static UserMessage NewUserMessage(string id, string content)
        => new(id, "source-session", DateTimeOffset.UnixEpoch, content, "code", "test-model");

    /// <summary>One hand-built failure message, located for a human-readable report.</summary>
    private readonly record struct HandBuiltHit(bool Found, int Line, string Snippet);

    /// <summary>
    ///     The first hand-built failure message in <paramref name="source" />.
    ///     <para>
    ///         A match that starts after a <c>//</c> on its own line is skipped: a doc comment
    ///         that MENTIONS the old shape ("was Result.Failure($\"…{x.Error}\")") is prose about
    ///         the rule, not a violation of it.
    ///     </para>
    /// </summary>
    private static HandBuiltHit FindHandBuilt(string source)
    {
        foreach (Match match in HandBuiltFailureError.Matches(source))
        {
            // LastIndexOf(char, startIndex) throws when startIndex is out of range, and returns
            // match.Index itself when the match sits on a newline — hence the clamp, so the span
            // length can never go negative.
            int lineStart = Math.Min(source.LastIndexOf('\n', match.Index) + 1, match.Index);
            if (source.AsSpan(lineStart, match.Index - lineStart).IndexOf("//", StringComparison.Ordinal) >= 0)
            {
                continue;
            }

            int line = CountNewLines(source, match.Index);
            int lineEnd = source.IndexOf('\n', match.Index);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            return new HandBuiltHit(true, line + 1, source[lineStart..lineEnd].Trim());
        }

        return new HandBuiltHit(false, -1, string.Empty);
    }

    private static int CountNewLines(string source, int upTo)
    {
        int count = 0;
        for (int i = 0; i < upTo; i++)
        {
            if (source[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Every <c>.cs</c> file of the guarded trees, excluding build output.</summary>
    private static IEnumerable<(string Relative, string Text)> ScanGuardedTrees()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        foreach (string tree in GuardedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file));
            }
        }
    }
}

/// <summary>A session store whose every call fails with one fixed reason.</summary>
internal sealed class FailingSessionStore(string cause) : ISessionStore
{
    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Session>(cause));

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<Session>(cause));

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<Session>>(cause));

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(cause));

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(cause));

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<AgentMessage>>(cause));

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(cause));

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(cause));

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<SessionMetadata>(cause));

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(cause));

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<int>(cause));
}

/// <summary>
///     A store that SUCCEEDS at creating the branch and then fails at one named step, so
///     the branch-copy path — the one that re-types and reports progress — is reachable.
///     <see cref="FailingSessionStore" /> cannot get there: it fails
///     <c>CreateAsync</c> too, and the branch path returns before it ever reads history.
/// </summary>
internal sealed class BranchStore(
    string? causeOnHistoryRead = null,
    string? causeOnAppend = null,
    IReadOnlyList<AgentMessage>? history = null,
    Session? parent = null) : ISessionStore
{
    public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Session.Create(directory, agentName, providerId, modelId)));

    public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(causeOnHistoryRead is { } cause
            ? Result.Failure<IReadOnlyList<AgentMessage>>(cause)
            : Result.Success<IReadOnlyList<AgentMessage>>(history ?? []));

    public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(causeOnAppend is { } cause
            ? Result.Failure(cause)
            : Result.Success());

    public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        // #670: the core fork reads the PARENT before it creates anything, so a fixture that
        // wants to reach the history or append step has to hand one back. `null` keeps the old
        // "not found" answer, which now fails one step earlier than it used to.
        => Task.FromResult(parent is { } p
            ? Result.Success(p)
            : Result.Failure<Session>($"Session '{sessionId}' not found."));

    public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Session>>([]));

    public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(SessionMetadata.Empty));

    public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
}
