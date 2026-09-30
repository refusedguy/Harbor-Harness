// ResultFailureConversionTests.cs — guard for the `ConvertFailure` wave.
//
// WHY THIS FILE EXISTS
// --------------------
// CSharpFunctionalExtensions 3.7.0 ships `ConvertFailure` on `Result`,
// `Result<T>`, `Result<T,E>` and `UnitResult<E>`:
//
//     public Result<K> ConvertFailure<K>()   // Result<T>  -> Result<K>
//     public Result   ConvertFailure()       // Result<T>  -> Result
//
// The repo had ZERO uses of it and 64 hand-rolled copies of the same operation,
// all of the shape `Result.Failure[<K>](<otherResult>.Error)`. That is a second
// implementation of a member the library already provides, written out longhand
// at every call site, and the drift risk is real: `ConvertFailure` THROWS
// `InvalidOperationException` on a success, so the guard that makes it safe
// (`if (x.IsFailure) return x.ConvertFailure<K>();`) is load-bearing. Delete the
// guard by accident and the site stops being a re-type and becomes a crash.
//
// The wave converted all 64. This file stops the 65th from landing.
//
// #593 — THE HOLE BETWEEN THE TWO GUARDS, AND WHAT IS LEFT
// -------------------------------------------------------
// #593 claimed 26 hand-written copies of this operation. Re-measured on
// 2026-09-30 (rg over `src/` + `apps/`, `contrib/` excluded): **0 remain**, and the
// one the wave left is the documented `ThemeParseResult` exemption below. The claim
// was true when the audit ran and is false now, so the headline is stale — but the
// sweep was not useless, because asking "where is it now?" found the shape it
// missed:
//
//     src/Harbor.Application/Providers/ProviderHealthCheck.cs:54
//         return Result.Failure<ProviderHealth>(Classify(result.Error));
//
// Same operation, different spelling: the re-type's error is piped through a
// classifier before it is wrapped. `Classify(` sits between the `(` and the
// receiver, so:
//
//   * the pattern above requires `Failure…(ident.Error` — it stops at `Classify(`;
//   * `MapErrorFailureShapeTests` requires an `$"…{x.Error}"` interpolation or
//     `+ x.Error` — `Classify(result.Error)` is neither.
//
// Two guards, one hole, one live site. That is the only reason the second rule
// below exists, and `ReTypeRules_AreDisjointOnTheLiveSite` is what proves the hole
// was real rather than merely believed: it asserts that Rule 1 does NOT match that
// line. A guard that only ever grows the same pattern cannot notice the shape
// right next to it.
//
// The library spelling is `ConvertFailure<K>().MapError(…)` — `MapError` alone is
// `Result<T> → Result<T>` and cannot cross the type change (inventory §4 item 12),
// which is the same two-member composition the Sessions slice uses (#600).
//
// SCOPE
// -----
// All of `src/` and all of `apps/` — exactly the trees the wave touched.
// `contrib/` is not gated: it holds its own copy of the pattern behind the
// scripting bridge, and widening this gate would go red on a tree this wave
// does not change. Extend it there when a `contrib/` site is converted.
//
// WHY A TEXT SCAN AND NOT A REFLECTIVE TEST
// -----------------------------------------
// `ConvertFailure` is an instance method on a struct; nothing about the compiled
// assembly records which of two spellings produced a given `Result`. The banned
// form is also a single syntactic shape, so the text is the honest place to look
// — same trade-off, and the same conclusion, as ResultMaybeSignatureTests.
//
// COMMENT STRIPPING
// -----------------
// Only the code before the first `//` is matched, so this file — and any doc
// comment — may name the old shape without failing the build. A `//` inside a
// string literal cannot produce a match for this pattern, so the naive split is
// safe here.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a failure is re-typed with <c>ConvertFailure</c>, never
///     rebuilt by hand, and the exemptions are documented rather than silent.
/// </summary>
public class ResultFailureConversionTests
{
    /// <summary>The trees the ConvertFailure wave converted.</summary>
    private static readonly string[] GuardedTrees = ["src", "apps"];

    /// <summary>
    ///     The hand-rolled re-type: <c>Result.Failure(x.Error)</c> and
    ///     <c>Result.Failure&lt;K&gt;(x.Error)</c>.
    ///     <list type="bullet">
    ///     <item>The type argument may nest one level, because
    ///     <c>Result.Failure&lt;IReadOnlyList&lt;AgentMessage&gt;&gt;(x.Error)</c> and
    ///     <c>Result.Failure&lt;Maybe&lt;JsonDocument&gt;&gt;(x.Error)</c> are both real
    ///     sites in this repo — a <c>[^&gt;]+</c> pattern cannot see them.</item>
    ///     <item>The leading <c>\b</c> is load-bearing:
    ///     <c>ThemeParseResult.Failure(result.Error, …)</c> contains the
    ///     substring <c>Result.Failure</c> but has no word boundary before it, and
    ///     is a different type with a different factory.</item>
    ///     <item>Requiring the literal <c>.Error</c> after the receiver's dot is
    ///     what keeps <c>SessionStoreErrors.SessionNotFound(id)</c> out.</item>
    ///     </list>
    /// </summary>
    private static readonly Regex HandRolledFailureConversion = new(
        @"\bResult\.Failure(?:<(?:[^<>]|<[^<>]*>)*>)?\(\s*[A-Za-z_][A-Za-z0-9_]*\??\.Error\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Files allowed to keep the hand-rolled form, each with the reason the
    ///     receiver it still contains is not a <c>Result</c> at all. Adding an
    ///     entry is a decision, not an oversight — the reason is printed in the
    ///     failure message. The list is keyed per file, so a file with a mix of
    ///     convertible and non-<c>Result</c> sites says which is which.
    /// </summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Tui.CellForge/Chat/Widgets/JsonThemeLoader.cs"] =
            "The receiver is ThemeParseResult (src/Harbor.DesignSystem/DesignSystem/ThemeJson.cs:11) — "
            + "a hand-rolled `sealed record` with its own IsSuccess/Error/Theme, not a Result<T>. "
            + "ConvertFailure is an instance member of the Result family and does not exist on it, so "
            + "there is nothing to call. Making ThemeParseResult a Result<HarborTheme> would remove the "
            + "duplication at the source; that is a type change to a design-system contract, not this wave.",
    };

    /// <summary>
    ///     <b>#593.</b> The same re-type, piped through a call: the argument to
    ///     <c>Result.Failure</c> is <c>f(x.Error)</c> rather than <c>x.Error</c>.
    ///     <list type="bullet">
    ///     <item>
    ///         The receiver chain is <c>ident(.ident)*</c> so <c>Classify(result.Error)</c>
    ///         and <c>Errors.Wrap(e.Error)</c> both match, while a bare
    ///         <c>ident.Error</c> does not — that one is Rule 1, above, and the two rules
    ///         are asserted disjoint on the live site rather than assumed to be.
    ///     </item>
    ///     <item>
    ///         The <c>\(</c> immediately after the receiver chain is what keeps the
    ///         <see cref="MapErrorFailureShapeTests" /> family out: those arms begin with
    ///         <c>$"</c> or <c>+</c>, never with a bare identifier followed by a call.
    ///     </item>
    ///     <item>
    ///         The window is bounded and may not cross <c>;</c>, so the match is confined
    ///         to one statement and an earlier <c>Failure(…)</c> in the same method cannot
    ///         borrow a later <c>.Error</c>.
    ///     </item>
    ///     </list>
    /// </summary>
    private static readonly Regex FailureReTypeThroughCall = new(
        @"\bResult\.Failure(?:<(?:[^<>]|<[^<>]*>)*>)?\(\s*[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\([^;]{0,200}?\.Error\b",
        RegexOptions.Compiled);

    // #561 REMOVED the `src/Harbor.Plugins.Hosting/PluginHost.cs` entry that used to
    // sit here. Its one non-Result site was `IPluginCompiler.CompileAsync`, which
    // returned a hand-rolled `CompilationResult` with no `ConvertFailure` to call;
    // the compiler contract now returns a real `Result<CompiledPluginAssembly>`, so
    // that site converts and the exemption is obsolete. `Exemptions_AreStillUsed`
    // would have failed on it, which is the guard working.

    [Test]
    public async Task GuardedTrees_DoNotHandRollFailureConversion()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles())
        {
            if (!HandRolledFailureConversion.IsMatch(text))
            {
                continue;
            }

            string relative = Relative(file);
            if (Exemptions.TryGetValue(relative, out string? reason))
            {
                _ = reason;
                continue;
            }

            violations.Add($"{relative}:{line} — hand-rolled failure re-type: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Re-typing a failure is Result<T>.ConvertFailure<K>() (or ConvertFailure() to drop the value), "
                + "not `Result.Failure<K>(other.Error)`. The member throws InvalidOperationException on a "
                + "success, so the `if (x.IsFailure)` guard in front of it must stay — that guard is also "
                + "what keeps the next line's `x.Value` legal. If the receiver is not a Result at all, add "
                + "the file to Exemptions with the reason. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    ///     <b>#593.</b> Rule 2 — the re-type whose error reaches
    ///     <c>Result.Failure</c> through a call rather than by attribute.
    /// </summary>
    [Test]
    public async Task ReTypeThroughACall_IsNotRebuiltByHand()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles())
        {
            if (!FailureReTypeThroughCall.IsMatch(text))
            {
                continue;
            }

            violations.Add($"{Relative(file)}:{line} — re-type through a call: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Re-typing a failure whose reason is piped through a call is the same two members the "
                + "bare form uses: `x.ConvertFailure<K>().MapError(Transformer)`. Neither guard can see it "
                + "alone — the bare-error pattern stops at the call, and the hand-built-message pattern "
                + "needs an interpolation. `MapError` alone is not the answer either: it is Result<T> → "
                + "Result<T> and this site changes T (the model list becomes a ProviderHealth), so the "
                + "re-type and the rewrite are two links, in that order, inside the IsFailure branch that "
                + "keeps ConvertFailure from throwing on a success. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    ///     The evidence that the hole was real: the one live site is invisible to
    ///     Rule 1 <em>and</em> to the sibling <c>MapError</c> guard. A second rule that
    ///     merely re-matched what the first already caught would be dead width, and one
    ///     that is asserted disjoint on a real line cannot be.
    /// </summary>
    [Test]
    public async Task ReTypeRules_AreDisjointOnTheLiveSite()
    {
        const string liveSite =
            "                return Result.Failure<ProviderHealth>(Classify(result.Error));";

        await Assert.That(FailureReTypeThroughCall.IsMatch(liveSite)).IsTrue()
            .Because(
                "Positive control. This is the exact line the wave left behind; if Rule 2 stops "
                + "recognising it the rule is decorative and the file says so in a comment it does not own.");

        await Assert.That(HandRolledFailureConversion.IsMatch(liveSite)).IsFalse()
            .Because(
                "THE HOLE, stated as an assertion. Rule 1 requires `Failure(ident.Error`; here the "
                + "argument opens with `Classify(`, so Rule 1 cannot see the site. This is why a second "
                + "rule exists instead of an extension of the first — the two are disjoint on the one "
                + "line in the tree that has this shape.");

        await Assert.That(liveSite.Contains("$\"", StringComparison.Ordinal)).IsFalse()
            .Because(
                "The sibling guard's family is an ASSEMBLED message: `MapErrorFailureShapeTests` requires "
                + "either a `$\"…{x.Error}\"` interpolation or a `+ x.Error` concatenation. This argument "
                + "is neither — it opens with `Classify(` — so ProviderHealthCheck.cs:54 sat outside BOTH "
                + "guards. Asserted on the text itself rather than by re-implementing the sibling's regex, "
                + "because a local copy of another file's pattern would rot silently and turn a documented "
                + "gap into a false claim. The gap is closed by Rule 2 above, not by widening Rule 1.");
    }

    /// <summary>
    ///     Specificity: the neighbours of the banned shape must stay clean, including the
    ///     one that looks most like it — <c>Classify(ex.Message)</c>, the sibling call two
    ///     lines below the live site. <c>Exception.Message</c> is not a <c>Result</c>'s error
    ///     channel, and flagging it would be a false positive on a legitimate site.
    /// </summary>
    [Test]
    public async Task ReTypeThroughACall_AcceptsItsNeighbours()
    {
        foreach (string sample in new[]
                 {
                     // The sibling call in the SAME file: an Exception, not a Result.
                     "            return Result.Failure<ProviderHealth>(Classify(ex.Message));",
                     // The MapError family, which has its own guard and its own exemption list.
                     "            return Result.Failure<string>($\"Import failed: {headerLineResult.Error}\");",
                     "            return Result.Failure<string>(\"Import failed: \" + created.Error);",
                     // An interpolated message that happens to end in `.Error`, not a call.
                     "            return Result.Failure<HarborTheme>(\"theme load failed: \" + ex.Error);",
                     // Ordinary failures with no Result receiver in the argument at all.
                     "            return Result.Failure(SessionStoreErrors.MessageNotFound(id));",
                     "            return Result.Failure<Maybe<LspLocation>>(firstFailure);",
                     "            return Result.Failure($\"Tool '{name}' is not registered.\");",
                     // The converted spelling this rule exists to produce, both links.
                     "            return result.ConvertFailure<ProviderHealth>().MapError(Classify);",
                     // The other direction: a delegate name that is not a call.
                     "            return Result.Failure<ProviderHealth>(Classify);"
                 })
        {
            await Assert.That(FailureReTypeThroughCall.IsMatch(sample)).IsFalse()
                .Because(
                    "The converted spelling and its legitimate neighbours must all stay clean. A rule that "
                    + "flagged `Classify(ex.Message)` — the very next call in ProviderHealthCheck — would cry "
                    + "wolf on the first honest hit, after which nobody reads it. Sample: " + sample.Trim());
        }
    }

    [Test]
    public async Task Exemptions_AreStillUsed()
    {
        // Guards the guard: an exemption whose pattern has since been fixed is
        // dead weight that hides the rule it was protecting.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The exemption check walks the working tree; without a repository root it proves nothing.");

        if (root is null)
        {
            return;
        }

        var stale = new List<string>();

        foreach (string relative in Exemptions.Keys)
        {
            string absolute = Path.Combine(root, relative);
            if (!File.Exists(absolute) || !ScanFile(absolute).Any(hit => HandRolledFailureConversion.IsMatch(hit.Text)))
            {
                stale.Add($"{relative} — ConvertFailure exemption no longer matches anything");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exemption that no longer matches is silently dead: it would let the pattern come back "
                + "with nobody watching. Delete it, or update the reason to describe the new shape.");
    }

    [Test]
    public async Task Scanner_ReadsTheGuardedTreesAndIsNotVacuous()
    {
        // Guards the scanner itself. Four independent ways this test could pass
        // while checking nothing, so all four are asserted.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "The failure-conversion guard walks the working tree; without a repository root (no "
                + "Harbor.slnx above AppContext.BaseDirectory) every rule below would silently pass. That "
                + "would make the guard untestable in exactly the environment where it matters.");

        if (root is null)
        {
            return;
        }

        // 1. It reads a plausible number of files.
        int files = GuardedTrees
            .SelectMany(tree => Directory.GetFiles(Path.Combine(root, tree), "*.cs", SearchOption.AllDirectories))
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        await Assert.That(files).IsGreaterThan(900)
            .Because($"src/ + apps/ hold ~950 source files; the scanner saw {files}.");

        // 2. Comment stripping is real. Asserted two-sided on purpose: the whole
        //    comment must match the pattern (so the sample is not vacuous) and the
        //    truncated code the scanner actually sees must not. A one-sided
        //    "commentHits == 0" over the tree would pass for the wrong reason —
        //    ScanFile strips `//` first, so such a count is 0 by construction and
        //    proves nothing at all.
        const string trailingCommentLine =
            "            return resolved; // was: Result.Failure<Session>(resolved.Error);";
        int cut = trailingCommentLine.IndexOf("//", StringComparison.Ordinal);
        await Assert.That(cut).IsGreaterThan(0)
            .Because("The sample must have code in front of its '//', or the truncation below removes nothing.");
        await Assert.That(HandRolledFailureConversion.IsMatch(trailingCommentLine)).IsTrue()
            .Because("The sample must really contain the banned shape, or this check would be vacuous.");
        await Assert.That(HandRolledFailureConversion.IsMatch(trailingCommentLine[..cut])).IsFalse()
            .Because("ScanFile truncates each line at its first '//', so a comment naming the old shape cannot fail the build.");

        // 3. The pattern still matches the pre-wave spelling — a regex that stopped
        //    matching would make test #1 vacuously green forever.
        foreach (string sample in new[]
                 {
                     "            return Result.Failure<Session>(resolved.Error);",
                     "            return Result.Failure(resolved.Error);",
                     "            return Task.FromResult(Result.Failure<int>(resolved.Error));",
                     "            return Result.Failure<IReadOnlyList<CompiledPlugin>>(result.Error);",
                     "            return Result.Failure<Maybe<LspLocation>>(built.Error);"
                 })
        {
            await Assert.That(HandRolledFailureConversion.IsMatch(sample)).IsTrue()
                .Because($"The guard's pattern must still match the pre-wave spelling: {sample.Trim()}");
        }

        // 4. And it does NOT match the converted spelling, or the post-wave line
        //    would fail its own guard.
        foreach (string sample in new[]
                 {
                     "            return resolved.ConvertFailure<Session>();",
                     "            return resolved.ConvertFailure();",
                     "            return Task.FromResult(resolved.ConvertFailure());",
                     "            return result.ConvertFailure<IReadOnlyList<CompiledPlugin>>();",
                     "            return built.ConvertFailure<Maybe<LspLocation>>();",
                     "            return resolved.ConvertFailure<IReadOnlyList<AgentMessage>>();",
                     "        : Result.Failure<HarborTheme>($\"theme load failed: {ex.Message}\");",
                     "            return Result.Failure<Session>(SessionStoreErrors.SessionNotFound(id));"
                 })
        {
            await Assert.That(HandRolledFailureConversion.IsMatch(sample)).IsFalse()
                .Because($"The guard's pattern must not match a converted or unrelated line: {sample.Trim()}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Every code-only line of the guarded trees, with build output skipped.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanGuardedFiles()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
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

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach ((int line, string text) in ScanFile(file))
                {
                    yield return (file, line, text);
                }
            }
        }
    }

    /// <summary>
    ///     One file, one-based line numbers. Each line is truncated at its first
    ///     <c>//</c> so a comment cannot trip the rule; a whole-line comment
    ///     therefore yields an empty string and can never match.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> ScanFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            int comment = text.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                text = text[..comment];
            }

            if (text.Trim().Length == 0)
            {
                continue;
            }

            yield return (i + 1, text);
        }
    }

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string absolutePath) =>
        (RepoPaths.RepoRoot is null ? absolutePath : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath))
        .Replace('\\', '/');
}
