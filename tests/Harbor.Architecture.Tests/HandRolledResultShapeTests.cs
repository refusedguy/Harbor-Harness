// HandRolledResultShapeTests.cs — no hand-rolled Result<T> facades in shipped code.
//
// WHAT THIS FILE IS FOR
// ---------------------
// Issue #561 found the plugin-compilation seam shipping TWO hand-rolled
// `Result<T>` types that disagreed with each other and with
// CSharpFunctionalExtensions (CFE):
//
//   * `Harbor.Plugins.Abstractions.CompilationResult` reimplemented the surface
//     from scratch. Its `Error` was `_error ?? string.Empty`, so on a SUCCESS it
//     returned `""` — a plausible-looking empty error that a caller could not
//     tell apart from a real one, and its `Value` threw a hand-written
//     `InvalidOperationException` rather than CFE's `ResultFailureException`.
//   * `Harbor.Plugins.Runtime.PluginCompilationResult` wrapped a real
//     `Result<CompiledPlugin>` and was therefore *correct* — but it was still a
//     second spelling of the same contract, in a different project, for a
//     method whose two siblings on the same class already returned a CFE
//     `Result`.
//
// The rules below are what closes the class, rather than a pragma catalogue
// per type, per issue.
//
// RULE 1 — no type may DECLARE `IsSuccess`/`IsFailure` without delegating to a
//          CFE `Result`. Delegating (holding a `Result` member and forwarding)
//          is fine; re-deriving the flags from unrelated state is not.
// RULE 2 — no `Error` member may be an `x ?? string.Empty` coalesce. That
//          expression is the defect itself: it manufactures a well-typed,
//          empty, *readable* error for a value that has no error, which is what
//          makes the wrong branch log something plausible instead of nothing.
// RULE 3 — no VALUE-SHAPED type (`struct` / `record struct`) may carry a
//          nullable-string `Error` member unless it delegates to a CFE
//          `Result`. Added by #588, for the shape #561's two rules cannot see.
//
// WHY A TEXT SCAN AND NOT A REFLECTIVE TEST
// -----------------------------------------
// `IsSuccess`/`IsFailure` are plain properties on structs; nothing in the
// compiled metadata records whether a `Value` getter throws
// `InvalidOperationException` or delegates to CFE, and nothing records that an
// `Error` getter returns a placeholder. The banned shapes are also single
// syntactic forms. So the text is the honest place to look — the same
// trade-off, and the same conclusion, as ResultFailureConversionTests and
// CfeValueBaselineTests.
//
// KNOWN LIMITATION, STATED RATHER THAN HIDDEN
// ------------------------------------------
// Brace matching walks the source text, so a raw/verbatim string literal
// containing an unbalanced brace inside a type body could desynchronise the
// depth counter and swallow the type that follows. This is why the scanner caps
// a body at `MaxBodyLines` and why `PositiveControl_…` below asserts the
// behaviour on synthetic input rather than trusting the tree walk. A second
// order-of-magnitude guard, `Scanner_SeesTheGuardedTrees`, fails if the file
// count collapses.
//
// NON-VACUITY
// -----------
// Both rules had a live target when this file landed, and #561 removed both.
// They are therefore preventive from here on — which is the point, but it also
// means "the build is green" now proves nothing on its own. The
// `PositiveControl_*` tests are what keep the rules honest: they feed the
// scanner the exact pre-#561 source and require it to be reported, and a
// legal delegating facade and require it to be left alone. That is sensitivity
// AND specificity — a rule that flagged everything would pass a
// sensitivity-only control while being useless.
//
// RULE 3 — added by #588. #561's pair each DECLARED `IsSuccess`/`IsFailure`
// (Rule 1) or coalesced their error to `string.Empty` (Rule 2), so both rules
// could see them. `ModelBatch` did neither: it had no flags at all, so the
// success/failure test was re-hand-written at every read site as
// `batch.Error is not null` — the inverse polarity of the library's — and a
// failure carrying a null or empty error was representable and would be read
// back as "success with zero models".
//
// A nullable-string `Error` member on a value-shaped type IS that shape: a
// value channel and an error channel, with the error's *nullness* carrying the
// verdict. `Result<T>` makes the same state unrepresentable — `Error` is
// `string`, and `Result.Failure<T>("")` throws rather than producing a value
// whose error says nothing.
//
// Deliberately narrow: the rule asks for BOTH value-shapedness and a
// `string?` named `Error`, so a JSON DTO (`ThemeDto`), an event
// (`CompactionFailedEvent`) and an `Exception`-carrying read outcome
// (`FrameReadResult`) are untouched. A CLASS-based facade that declares neither
// a flag nor a coalesced error is still outside all three rules — that limit is
// stated rather than hidden, and Rule 1 covers the class case that matters in
// practice.
//
// NON-VACUITY NOTE FOR RULE 3
// ---------------------------
// #588's `ModelBatch` was invisible to the scanner as it stood, for a reason
// worth recording because it applies to any nested or parameterised type:
//
//   * a `record struct X(A, B, C);` with no braces never opened a body, so the
//     parameter list was never part of the scanned text, and
//   * a type nested inside another type's body was skipped entirely, because
//     the walk resumes *after* the enclosing type.
//
// Both are fixed below (see `ScanExtent`). Neither changes what Rules 1 and 2
// report: measured over the pre-#588 tree the fixes move the type count from
// 1368 to 1761 and leave Rule 1 at 1 hit and Rule 2 at 0.
//
// WHY THIS AND NOT A BannedSymbols.txt ENTRY
// ------------------------------------------
// #588 proposed banning `T:Harbor.Registries.ModelBatch` in `BannedSymbols.txt`.
// `ModelBatch` is `private`, so after #588 deletes it the ban could never fire
// again — a guard that is green because its subject does not exist, which is the
// vacuity this file exists to prevent. Rule 3 is red against a live type today,
// names the shape rather than the type, and generalises to the next copy
// anywhere in `src/` or `apps/`.
//
// SCOPE
// -----
// `src/` and `apps/` — the trees that ship. `contrib/` is NOT gated: it is
// unmaintained and outside CI (see AGENTS.md), and a guard that goes red on a
// tree no build covers is a guard that gets deleted rather than obeyed.
// `tests/` is not gated: a test-only Result facade is a test's own business,
// and the compile-time CFE0001 suppression for tests/ is separate.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a shipped type may expose <c>IsSuccess</c>/<c>IsFailure</c>
///     only by delegating to a CSharpFunctionalExtensions <c>Result</c>, and may never
///     synthesise an <c>Error</c> by coalescing to <see cref="string.Empty" />.
/// </summary>
public sealed class HandRolledResultShapeTests
{
    /// <summary>The trees that ship to a user.</summary>
    private static readonly string[] GuardedTrees = ["src", "apps"];

    /// <summary>
    ///     A type body longer than this is abandoned rather than tracked. See
    ///     "KNOWN LIMITATION" in the header: a string literal with an unbalanced
    ///     brace can desynchronise depth, and an unbounded walk would then swallow
    ///     every type in the rest of the file.
    /// </summary>
    private const int MaxBodyLines = 500;

    /// <summary>
    ///     A type declaration and its name. <c>record struct</c> and
    ///     <c>record class</c> come first so <c>record struct Foo</c> is not read as a
    ///     bare <c>struct</c> (the alternation is ordered, and the generic-argument
    ///     list stops the name at <c>&lt;</c>).
    /// </summary>
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:record\s+struct|record\s+class|record|struct|class|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     A type DECLARING the success/failure flag, as opposed to reading one. The
    ///     nullable flag is included on purpose: <c>bool? IsSuccess</c> is the same
    ///     three-valued ambiguity this rule exists to catch, and excluding it would
    ///     leave a one-character hole in the guard.
    /// </summary>
    private static readonly Regex DeclaresResultFlag = new(
        @"\bbool\?\s+Is(?:Success|Failure)\b|\bbool\s+Is(?:Success|Failure)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Evidence of delegation: a member whose type is a CFE <c>Result</c> or
    ///     <c>Result&lt;T&gt;</c>. A wrapper that holds one and forwards its members is
    ///     legitimate and is not reported.
    /// </summary>
    private static readonly Regex DelegatesToCfeResult = new(
        @"\bResult\s*(?:<|\s+_[A-Za-z])",
        RegexOptions.Compiled);

    /// <summary>
    ///     An <c>Error</c> accessor that coalesces to <see cref="string.Empty" />.
    ///     Deliberately narrow: the ~140 other <c>?? string.Empty</c> uses in the tree
    ///     are null-guards on UI text, environment variables and JSON reads, where
    ///     an empty string is the correct rendering of "absent". Only an <c>Error</c>
    ///     channel is a lie, because it makes "no failure" and "failure with nothing
    ///     to say" indistinguishable.
    /// </summary>
    private static readonly Regex CoalescedEmptyError = new(
        @"\bError\s*=>[^;]*\?\?\s*string\s*\.\s*Empty\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Types allowed to declare <c>IsSuccess</c> without delegating to a CFE
    ///     <c>Result</c>. Each entry is a decision, not an oversight, and the reason
    ///     is printed in the failure message.
    /// </summary>
    private static readonly Dictionary<string, string> Rule1Exemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.DesignSystem/DesignSystem/ThemeJson.cs::ThemeParseResult"] =
            "A theme-JSON parse outcome, not a fallible operation over a value: it carries "
            + "BOTH fatal errors and non-fatal lint warnings, so it is a richer shape than "
            + "Result<T>. Its `Error` default of string.Empty is the same wart #561 removed, "
            + "and it is deferred for the same reason ResultFailureConversionTests already "
            + "documents: making it Result<HarborTheme> is a change to a design-system "
            + "contract, not a plugin-seam fix. Tracked, not forgotten."
    };

    /// <summary>
    ///     A value-shaped type declaration: <c>struct</c> or <c>record struct</c>.
    ///     Matched against the DECLARATION LINE only — a class body that happens
    ///     to contain a nested <c>record struct</c> must not be reported as one.
    /// </summary>
    private static readonly Regex ValueShapedDeclaration = new(
        @"\b(?:readonly\s+)?(?:record\s+struct|struct)\s+[A-Za-z_]",
        RegexOptions.Compiled);

    /// <summary>
    ///     The #588 shape: a nullable <c>string</c> member named <c>Error</c>.
    ///     The <c>?</c> is the whole defect — it is what lets "no failure" and
    ///     "failure with nothing to say" be the same value, and it is why a
    ///     non-nullable <c>string Error</c> facade is left to Rules 1 and 2.
    /// </summary>
    private static readonly Regex NullableStringErrorMember = new(
        @"\bstring\?\s+Error\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Types allowed to carry a nullable-string <c>Error</c> while being
    ///     value-shaped, without delegating to a CFE <c>Result</c>. Each entry is
    ///     a decision with a reason, checked by
    ///     <see cref="Rule3Exemptions_AreStillNeeded" />.
    /// </summary>
    private static readonly Dictionary<string, string> Rule3Exemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Tools.Builtin/Tools/Edit/EditTool.cs::EditResult"] =
            "A FOURTH instance of the #588 shape, found by Rule 3 while #588 was being fixed — "
            + "and deliberately NOT fixed there. `private readonly record struct EditResult(bool Ok, "
            + "string Text, int Count, string? Error)` is a `Result<T>` spelled by hand: a boolean "
            + "flag next to a nullable error, with the same null-means-failure channel. It lives in "
            + "a builtin tool, not the providers perimeter that #588 covers, so fixing it is its own "
            + "change. Deferred, not forgotten: #721. When it is fixed, delete this entry — "
            + "Rule3Exemptions_AreStillNeeded fails if the entry outlives the type."
    };

    [Test]
    public async Task Rule1_NoTypeReimplementsTheResultSurface()
    {
        var violations = new List<string>();

        foreach (var type in ScanTypes())
        {
            if (!DeclaresResultFlag.IsMatch(type.Body))
            {
                continue;
            }

            // Delegating to a real Result is allowed — that is a wrapper, not a
            // reimplementation.
            if (DelegatesToCfeResult.IsMatch(type.Body))
            {
                continue;
            }

            string key = type.File + "::" + type.TypeName;
            if (Rule1Exemptions.ContainsKey(key))
            {
                continue;
            }

            violations.Add(
                $"{type.File}:{type.Line} — {type.TypeName} declares IsSuccess/IsFailure without a "
                + "CSharpFunctionalExtensions Result to delegate to, so it is a second, "
                + "hand-written implementation of Result<T>. Return Result<T> (or hold one and "
                + "forward to it) instead of re-deriving the flags.");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A hand-rolled Result<T> is the defect #561 was filed for: two spellings of the "
                + "same contract drift apart, and CFE0001 cannot see either of them because it "
                + "matches on the CSharpFunctionalExtensions.Result symbol. CSharpFunctionalExtensions "
                + "already provides the type. If a type legitimately cannot fail, it should not "
                + "expose IsSuccess at all — an honest value has no failure axis. If a wrapper must "
                + "carry extra state, hold a Result<T> member and forward to it. To allow a genuine "
                + "exception, add \"<file>::<Type>\" to Rule1Exemptions with the reason."
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public async Task Rule2_ErrorIsNeverCoalescedToEmptyString()
    {
        var violations = new List<string>();

        foreach ((string File, int Line, string Text) hit in ScanGuardedFiles())
        {
            if (CoalescedEmptyError.IsMatch(hit.Text))
            {
                violations.Add($"{hit.File}:{hit.Line} — {hit.Text.Trim()}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "An Error member written as `x ?? string.Empty` returns a well-typed, empty, "
                + "readable string for a value that has no error. That is the live half of #561: "
                + "`CompilationResult.Error` did exactly this, so on a success it handed the caller "
                + "a plausible empty error and `if (x.IsFailure) log(x.Error)` had nothing left to "
                + "distinguish. CSharpFunctionalExtensions makes the same state unrepresentable — "
                + "Result.Failure<T>(\"\") throws ArgumentNullException, and .Error throws "
                + "ResultSuccessException when read on a success. If a value has no error to give, "
                + "it is a success, and the type should say so."
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // =====================================================================
    // Rule 3 — #588. Added after Rules 1 and 2, for the shape they cannot see.
    // =====================================================================

    /// <summary>
    ///     The declaration line, i.e. the first line of a scanned body.
    ///     <see cref="ValueShapedDeclaration" /> is matched against this and
    ///     nothing else: a class whose body happens to contain a nested
    ///     <c>record struct</c> is not itself value-shaped.
    /// </summary>
    private static string DeclarationLineOf(string body)
    {
        int newline = body.IndexOf('\n');
        return newline < 0 ? body : body[..newline];
    }

    /// <summary>True when a scanned type is the #588 shape.</summary>
    private static bool IsRule3Violation((string File, int Line, string TypeName, string Body) type) =>
        ValueShapedDeclaration.IsMatch(DeclarationLineOf(type.Body))
        && NullableStringErrorMember.IsMatch(type.Body)
        && !DelegatesToCfeResult.IsMatch(type.Body);

    [Test]
    public async Task Rule3_NoValueShapedTypeCarriesANullableStringErrorChannel()
    {
        var violations = new List<string>();

        foreach (var type in ScanTypes())
        {
            string key = type.File + "::" + type.TypeName;
            if (IsRule3Violation(type) && !Rule3Exemptions.ContainsKey(key))
            {
                violations.Add($"{type.File}:{type.Line} — {type.TypeName}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A value-shaped type that carries a nullable string `Error` is a hand-rolled "
                + "Result<T> with the polarity inverted: the verdict is read off the error's "
                + "nullness at each call site, so a failure with a null or empty error is "
                + "representable and comes back as 'success'. `ModelBatch` "
                + "(src/Harbor.Registries/Providers/ProviderRegistry.cs) was exactly that, and it is "
                + "the shape Rules 1 and 2 above are blind to: it declared no IsSuccess/IsFailure at "
                + "all, and never coalesced an error, so both rules reported nothing. CSharpFunctional"
                + "Extensions makes the same state unrepresentable — `Error` is `string`, and "
                + "`Result.Failure<T>(\"\")` throws instead of yielding a value whose error says "
                + "nothing. Use a real `Result<T>` per task and read it with `IsFailure`."
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public async Task Rule3Exemptions_AreStillNeeded()
    {
        // The same rot `Rule1Exemptions_AreStillNeeded` exists to stop, for Rule 3:
        // an exemption whose type no longer violates the rule would let the shape
        // come back with nobody watching.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("this check walks the working tree; without a repository root it proves nothing");

        if (root is null)
        {
            return;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ScanTypes())
        {
            if (IsRule3Violation(type))
            {
                _ = found.Add(type.File + "::" + type.TypeName);
            }
        }

        var stale = Rule3Exemptions.Keys.Where(key => !found.Contains(key)).ToList();

        await Assert.That(stale).IsEmpty()
            .Because(
                "A Rule 3 exemption that no longer matches a violating type is dead weight, and it "
                + "is the one way this rule could rot: the type was fixed and the entry stayed, so "
                + "the shape is allow-listed with a stale reason. If you fixed the type, delete the "
                + "entry. Stale: " + string.Join(", ", stale));
    }

    [Test]
    public async Task PositiveControl_CatchesThePreIssueModelBatchShape()
    {
        // #588's target in its real surroundings: a `private` record struct with
        // no body braces (so the parameter list IS the whole declaration)
        // carrying a nullable `string? Error` next to a value member, NESTED in
        // the class that owns it. The trailing sibling is the witness: if the
        // extent ever swallows the rest of the file again, the sibling is never
        // yielded and this control fails.
        const string preIssue = """
            public sealed class ProviderRegistry
            {
                private readonly record struct ModelBatch(
                    ProviderId ProviderId,
                    IReadOnlyList<ModelInfo> Models,
                    string? Error);
            }

            public sealed class UnrelatedSibling { }
            """;

        IReadOnlyList<(string TypeName, int Line, string Body)> found = [.. ScanText(preIssue)];

        await Assert.That(found.Select(t => t.TypeName).ToList())
            .IsEquivalentTo(new[] { "ProviderRegistry", "ModelBatch", "UnrelatedSibling" })
            .Because(
                "The scanner must see three types: the outer class, the NESTED record, and the "
                + "sibling after it. Two ways this fails, and both were real in the pre-#588 scanner: "
                + "a `record struct X(A, B, C);` with no braces never opens a brace, so a walk that "
                + "stops only on brace balance ran to the end of the file and the parameter list "
                + "carrying the banned `string? Error` was never looked at; and a nested type was "
                + "skipped, because the walk resumed AFTER the enclosing type.");

        IReadOnlyList<(string TypeName, int Line, string Body)> flagged =
        [
            .. found.Where(t => DeclaresResultFlag.IsMatch(t.Body)
                               || NullableStringErrorMember.IsMatch(t.Body)
                               || CoalescedEmptyError.IsMatch(t.Body))
        ];

        await Assert.That(flagged.Select(t => t.TypeName).ToList()).IsEquivalentTo(new[] { "ModelBatch" })
            .Because(
                "Of the three, only ModelBatch carries a result-shaped error channel. If the outer "
                + "class or the sibling is reported, the match has widened beyond the banned shape.");
    }

    [Test]
    public async Task PositiveControl_LeavesTheNonResultStringErrorShapesAlone()
    {
        // Specificity for Rule 3. Every one of these carries a string-typed member
        // called `Error` and must survive: a JSON DTO, an event, an outcome
        // record that carries an Exception, a delegating facade, and a
        // value-shaped type whose error is NOT a nullable string. If Rule 3
        // flagged any of them it would be matching on the word "Error" rather
        // than on the result shape.
        const string legal = """
            internal sealed record ThemeDto(
                string? Name,
                string? Accent,
                string? Error,
                string? Tool);

            public sealed record CompactionFailedEvent(string SessionId, string Error) : AgentEvent;

            internal readonly record struct FrameReadResult(
                FrameReadOutcome Outcome,
                HarborRequest? Request,
                Exception? Error);

            public readonly record struct DelegatingFacade(string? Error)
            {
                private readonly Result<CompiledPlugin> _inner;
            }

            public readonly record struct CountedRows(int Count, string Error);
            """;

        IReadOnlyList<(string TypeName, int Line, string Body)> flagged =
        [
            .. ScanText(legal)
                .Where(t => ValueShapedDeclaration.IsMatch(DeclarationLineOf(t.Body))
                            && NullableStringErrorMember.IsMatch(t.Body)
                            && !DelegatesToCfeResult.IsMatch(t.Body))
        ];

        await Assert.That(flagged.Count).IsEqualTo(0)
            .Because(
                "Rule 3 asks for value-shapedness AND a nullable-string Error, so a DTO record, an "
                + "event record, an Exception-carrying outcome and a delegating facade must all pass. "
                + "If any is reported, the rule has widened to 'a member called Error' and will fire "
                + "on the next protocol DTO added to the tree.");
    }

    [Test]
    public async Task Rule1Exemptions_AreStillNeeded()
    {
        // Guards the guard. An exemption whose type no longer violates Rule 1 is
        // dead weight: it would let the shape come back with nobody watching,
        // which is the same rot ResultFailureConversionTests.Exemptions_AreStillUsed
        // exists to stop.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("this check walks the working tree; without a repository root it proves nothing");

        if (root is null)
        {
            return;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ScanTypes())
        {
            string key = type.File + "::" + type.TypeName;
            if (Rule1Exemptions.ContainsKey(key)
                && DeclaresResultFlag.IsMatch(type.Body)
                && !DelegatesToCfeResult.IsMatch(type.Body))
            {
                _ = found.Add(key);
            }
        }

        var stale = Rule1Exemptions.Keys.Where(key => !found.Contains(key)).ToList();

        await Assert.That(stale).IsEmpty()
            .Because(
                "A Rule 1 exemption that no longer matches anything would let the hand-rolled "
                + "Result<T> come back silently. If the type was fixed, delete the entry and update "
                + "its reason. Stale: " + string.Join(", ", stale));
    }

    // =====================================================================
    // Non-vacuity. Both rules were fixed by #561, so "the tree is clean" is
    // no longer evidence that they work. These tests supply the evidence.
    // =====================================================================

    [Test]
    public async Task PositiveControl_CatchesThePreIssueCompilationResultShape()
    {
        // The exact shape #561 removed, reduced to the members the rules look
        // at. If this stops being reported, Rule 1 is not running.
        const string preIssue = """
            public readonly record struct CompilationResult
            {
                private readonly CompiledPluginAssembly? _assembly;
                private readonly string? _error;

                public bool IsSuccess => _assembly is not null;
                public bool IsFailure => _assembly is null;
                public CompiledPluginAssembly Value => _assembly ?? throw new InvalidOperationException("is failure");
                public string Error => _error ?? string.Empty;
            }
            """;

        IReadOnlyList<(string TypeName, int Line, string Body)> flagged =
            [.. ScanText(preIssue).Where(t => DeclaresResultFlag.IsMatch(t.Body) && !DelegatesToCfeResult.IsMatch(t.Body))];

        await Assert.That(flagged.Count).IsEqualTo(1)
            .Because(
                "Rule 1 must report the type #561 deleted. A guard that cannot recognise the exact "
                + "defect it was written for is not a guard. If this fails, the type-declaration or "
                + "flag-member pattern stopped matching.");

        await Assert.That(flagged[0].TypeName).IsEqualTo("CompilationResult")
            .Because("the reported name must be the type that declares the flag, not the file or the first type in it");
    }

    [Test]
    public async Task PositiveControl_CatchesTheCoalescedErrorEvenOnItsOwn()
    {
        // Rule 2 must fire on the member alone, without Rule 1's help — a type
        // that delegates to a real Result but still manufactures an empty error
        // is exactly as much of a lie as one that re-derives the flags.
        const string coalesced = """
            public sealed record Wrapper
            {
                private readonly Result<int> _inner;
                public string Error => _message ?? string.Empty;
                private readonly string? _message;
            }
            """;

        bool caught = ScanText(coalesced)
            .Select(hit => hit.Body)
            .Any(CoalescedEmptyError.IsMatch);

        await Assert.That(caught).IsTrue()
            .Because(
                "`Error => _x ?? string.Empty` is the #561 defect in one line. If the pattern stopped "
                + "matching it — a widened \\b, a renamed member, a changed regex — Rule 2 would go "
                + "quiet and look like a clean tree.");
    }

    [Test]
    public async Task PositiveControl_LeavesLegalShapesAlone()
    {
        // Specificity. A rule that flagged every `IsSuccess` would pass the two
        // controls above while being useless, so the legal shapes are asserted
        // as NOT reported.
        const string legal = """
            public readonly record struct DelegatingFacade
            {
                private readonly Result<CompiledPlugin> _inner;
                public bool IsSuccess => _inner.IsSuccess;
                public bool IsFailure => _inner.IsFailure;
                public CompiledPlugin Value => _inner.Value;
                public string Error => _inner.Error;
            }

            public sealed record NotAResult
            {
                public bool IsReady { get; init; }
                public int Count { get; init; }
            }

            public sealed class Service
            {
                public bool IsRunning { get; private set; }
                public string Name { get; init; } = string.Empty;
            }
            """;

        IReadOnlyList<(string TypeName, int Line, string Body)> flagged =
        [
            .. ScanText(legal)
                .Where(t => DeclaresResultFlag.IsMatch(t.Body) && !DelegatesToCfeResult.IsMatch(t.Body))
        ];

        await Assert.That(flagged.Count).IsEqualTo(0)
            .Because(
                "Three legal shapes must survive: a wrapper that DELEGATES to a Result<CompiledPlugin> "
                + "(Rule 1's escape hatch), a plain record with an unrelated IsReady, and a service with "
                + "IsRunning whose Name merely defaults to string.Empty. If any of these is reported, Rule 1 "
                + "is matching on a substring rather than on the Result surface.");
    }

    [Test]
    public async Task Scanner_IgnoresCommentsAndStringLiterals()
    {
        // The scanner has to see code, not prose. Both halves are asserted: the
        // sample really does contain the banned text (so this is not vacuous),
        // and the scanner still finds nothing, because it is all in comments.
        const string commented = """
            // public string Error => _error ?? string.Empty;
            /// <summary>IsSuccess was here once.</summary>
            public sealed class Thing
            {
                public string Name { get; init; } = string.Empty;
            }
            """;
        const string literal = """
            public sealed class Thing
            {
                public string Banner => "IsSuccess and Error => _e ?? string.Empty";
            }
            """;

        bool namedTheBannedShape = CoalescedEmptyError.IsMatch("// public string Error => _error ?? string.Empty;");
        await Assert.That(namedTheBannedShape).IsTrue()
            .Because("the sample must really contain the banned shape, or the two checks below prove nothing");

        foreach (string sample in new[] { commented, literal })
        {
            bool caught = ScanText(sample).Select(hit => hit.Body).Any(CoalescedEmptyError.IsMatch);
            await Assert.That(caught).IsFalse()
                .Because("a comment or a string literal naming the old shape must not fail the build");
        }
    }

    [Test]
    public async Task Scanner_SeesTheGuardedTrees()
    {
        // The walk itself. Without a repository root every rule above would
        // silently pass, which is exactly the environment where the guard is
        // worthless.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "these rules walk the working tree; without Harbor.slnx above AppContext.BaseDirectory "
                + "every rule in this file would report 'no violations' and prove nothing");

        if (root is null)
        {
            return;
        }

        int files = 0;
        foreach (string tree in GuardedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            files += Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }

        await Assert.That(files).IsGreaterThan(900)
            .Because($"src/ + apps/ hold ~950 source files; the scanner saw {files}");

        // The tree walk must also see real types, or the type-level body match
        // is doing nothing at all.
        int types = ScanTypes().Count();
        await Assert.That(types).IsGreaterThan(500)
            .Because($"the body scanner should find hundreds of type declarations; it found {types}");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Every code-only line of the guarded trees, with build output skipped,
    ///     as <c>(repo-relative file, one-based line, code-only text)</c>.
    /// </summary>
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

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach ((int line, string text) in CodeLines(file))
                {
                    yield return (relative, line, text);
                }
            }
        }
    }

    /// <summary>
    ///     Every type declaration in the guarded trees with the code of its body,
    ///     as <c>(file, one-based line of the declaration, name, body)</c>.
    /// </summary>
    private static IEnumerable<(string File, int Line, string TypeName, string Body)> ScanTypes()
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

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach ((string TypeName, int Line, string Body) hit in ScanText(File.ReadAllText(file)))
                {
                    yield return (relative, hit.Line, hit.TypeName, hit.Body);
                }
            }
        }
    }

    /// <summary>
    ///     The single entry point the positive controls drive: given source text,
    ///     find every type declaration and the code of its body.
    /// </summary>
    private static IEnumerable<(string TypeName, int Line, string Body)> ScanText(string source)
    {
        return ScanExtent(source, 0, 0);
    }

    /// <summary>
    ///     How deep the scan descends into a type's own body looking for types
    ///     NESTED inside it. #588's <c>ModelBatch</c> was a private nested record,
    ///     so a scan that stopped at the enclosing type could never see it.
    /// </summary>
    private const int MaxNestingDepth = 3;

    /// <summary>
    ///     <see cref="ScanText" /> plus the two offsets the nesting walk needs:
    ///     <paramref name="lineOffset" /> is the 1-based source line that
    ///     <c>source</c>'s line 1 corresponds to, and <paramref name="depth" />
    ///     caps the descent.
    /// </summary>
    private static IEnumerable<(string TypeName, int Line, string Body)> ScanExtent(
        string source,
        int lineOffset,
        int depth)
    {
        string[] raw = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] code = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            code[i] = CodeOnly(raw[i]);
        }

        for (int i = 0; i < code.Length; i++)
        {
            Match declaration = TypeDeclaration.Match(code[i]);
            if (!declaration.Success)
            {
                continue;
            }

            string name = declaration.Groups[1].Value;

            // Walk forward to the end of the type by brace depth. A type whose
            // opening brace is on a later line (a base list, an attribute) is
            // handled by the same loop — the first line that opens without
            // closing is where the body starts.
            //
            // `parenDepth` exists for the one shape brace depth cannot end: a
            // `record struct X(A, B, C);` with NO body braces, where the
            // parameter list is the whole type. Without it the walk never sees
            // the primary constructor at all, and #588's `ModelBatch` — whose
            // banned `string? Error` is a constructor parameter — was invisible.
            int depthBraces = 0;
            bool opened = false;
            int parenDepth = 0;
            bool seenParen = false;
            int closedParenLine = -1;
            var body = new StringBuilder();
            int consumed = 0;

            for (int j = i; j < code.Length && j - i < MaxBodyLines; j++)
            {
                string line = code[j];
                body.AppendLine(line);

                foreach (char c in line)
                {
                    switch (c)
                    {
                        case '{':
                            depthBraces++;
                            opened = true;
                            break;
                        case '}':
                            depthBraces--;
                            break;
                        case '(':
                            parenDepth++;
                            seenParen = true;
                            break;
                        case ')':
                            parenDepth--;
                            if (seenParen && parenDepth <= 0)
                            {
                                closedParenLine = j;
                            }

                            break;
                    }
                }

                consumed = j;
                if (opened && depthBraces <= 0)
                {
                    break;
                }

                // The parameter list has closed and no body brace has opened, so
                // this declaration ends here — unless a `{` (or a `:` base list)
                // follows, in which case the body is still ahead. Only consulted
                // while `opened` is false, so a `new(...)` call inside an
                // already-open body can never end the type early.
                if (!opened && closedParenLine == j && !OpensBodyAfter(code[j], code, j))
                {
                    break;
                }
            }

            string bodyText = body.ToString();
            yield return (name, lineOffset + i + 1, bodyText);

            // Descend, so a type NESTED in this one is found on its own. The
            // first line of `body` IS this declaration, so rescanning it would
            // find this same type again; skip it and shift by one line. The
            // `TypeDeclaration` pre-test is what keeps this affordable: most
            // bodies contain no nested type at all, and rescanning those is
            // pure cost on a walk that runs once per rule.
            if (depth < MaxNestingDepth)
            {
                int firstNewline = bodyText.IndexOf('\n');
                string inner = firstNewline < 0 ? string.Empty : bodyText[(firstNewline + 1)..];

                if (TypeDeclaration.IsMatch(inner))
                {
                    foreach ((string NestedName, int NestedLine, string NestedBody)
                             in ScanExtent(inner, lineOffset + i + 1, depth + 1))
                    {
                        if (!string.Equals(NestedName, name, StringComparison.Ordinal))
                        {
                            yield return (NestedName, NestedLine, NestedBody);
                        }
                    }
                }
            }

            // Resume after the type so a sibling is still found on its own.
            i = consumed;
        }
    }

    /// <summary>
    ///     True when a body or a base list follows the closing paren of a
    ///     primary-constructor list, so the type is not over.
    /// </summary>
    private static bool OpensBodyAfter(string line, string[] code, int at)
    {
        string rest = line[(line.LastIndexOf(')') + 1)..].Trim();
        string next = at + 1 < code.Length ? code[at + 1].Trim() : string.Empty;

        return rest.StartsWith('{') || next.StartsWith('{')
               || rest.StartsWith(':') || next.StartsWith(':');
    }

    /// <summary>
    ///     One file, one-based line numbers, comments and string literals removed.
    ///     A line that is entirely a comment yields an empty string and can never
    ///     match. Literals become <c>""</c> so that a brace or a keyword inside
    ///     one cannot drive the scanner.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> CodeLines(string path)
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
            yield return (i + 1, CodeOnly(lines[i]));
        }
    }

    /// <summary>
    ///     Strip comments and string/char/verbatim/raw literals from one line,
    ///     keeping everything else. A single left-to-right pass, because cutting
    ///     at the first <c>//</c> would truncate a URL inside a string literal and
    ///     leave the scanner reading half a line of prose.
    /// </summary>
    private static string CodeOnly(string line)
    {
        var code = new StringBuilder(line.Length);
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                code.Append(' ');
                i = end + 2;
                continue;
            }

            if ((c is '"' or '\'' or '@' or '$') && TryStartLiteral(line, i, out int quoteAt, out char quote))
            {
                // Raw/interpolated raw: `"""` … `"""`. Harbor uses these heavily,
                // and their bodies are full of braces, so they must be consumed as
                // one unit or the brace counter below desynchronises.
                if (quote == '"' && quoteAt + 2 < line.Length
                    && line[quoteAt + 1] == '"' && line[quoteAt + 2] == '"')
                {
                    int close = line.IndexOf("\"\"\"", quoteAt + 3, StringComparison.Ordinal);
                    i = close < 0 ? line.Length : close + 3;
                    code.Append("\"\"");
                    continue;
                }

                i = SkipLiteral(line, quoteAt, quote);
                code.Append(quote == '"' ? "\"\"" : "''");
                continue;
            }

            code.Append(c);
            i++;
        }

        return code.ToString();
    }

    /// <summary>
    ///     True when a char, string, verbatim or interpolated literal begins at or
    ///     just after <paramref name="at" /> — that is, for <c>@"…"</c>, <c>$"…"</c>
    ///     and <c>$$"""…"""</c> as well as a bare <c>"</c> or <c>'</c>.
    /// </summary>
    private static bool TryStartLiteral(string line, int at, out int quoteAt, out char quote)
    {
        for (int j = at; j < line.Length; j++)
        {
            if (line[j] is '"' or '\'')
            {
                quoteAt = j;
                quote = line[j];
                return true;
            }

            if (line[j] is not ('@' or '$'))
            {
                break;
            }
        }

        quoteAt = -1;
        quote = '\0';
        return false;
    }

    /// <summary>
    ///     Index just past the literal whose opening quote is at
    ///     <paramref name="quoteAt" />. A verbatim or interpolated literal ends at a
    ///     quote not doubled by another quote; a regular one honours backslash
    ///     escapes. An unterminated literal runs to end of line, which under-strips
    ///     rather than over-strips.
    /// </summary>
    private static int SkipLiteral(string line, int quoteAt, char quote)
    {
        bool verbatim = quoteAt > 0 && line[quoteAt - 1] is '@' or '$';

        int j = quoteAt + 1;
        while (j < line.Length)
        {
            char c = line[j];
            if (c == '\\' && !verbatim)
            {
                j += 2;
                continue;
            }

            if (c == quote)
            {
                if (verbatim && j + 1 < line.Length && line[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return line.Length;
    }
}
