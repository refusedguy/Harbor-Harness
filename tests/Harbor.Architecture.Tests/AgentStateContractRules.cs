// AgentStateContractRules.cs — the guard for issue #559.
//
// WHY THIS FILE EXISTS
// --------------------
// `IAgent.State` was declared `AgentState State { get; }` — non-nullable — while
// `DefaultAgent` initialised the backing field with `null!` and first assigned it
// inside `Initialize`. The annotation was a lie the compiler could not check, and it
// actively discouraged the one check the class needed: `DefaultAgent` null-checked its
// OWN property in four places (`State is null`, `State?.SessionId`, `State?.IsRunning`)
// and dereferenced it unguarded a handful of lines later in the same methods. Roughly
// twenty external call sites trusted the annotation instead — `PromptPipeline.IsBusy`
// is evaluated on every frame — and three of them sat in *constructors*, where the
// caller has no sequencing available and a bridge built before `Initialize` NREs at
// construction time.
//
// WHY `Maybe<AgentState>` AND NOT A FABRICATED `AgentState.Uninitialized`
// -----------------------------------------------------------------------
// The issue's suggested one-liner was an `AgentState.Uninitialized` factory. It cannot
// work: `AgentState` carries a `SessionId` AND an `AgentDefinition`, so "unbound" would
// have to invent both. The invented `SessionId` reaches
// `_sessionStore.AppendMessageAsync("")` on the very next prompt, and the invented
// `AgentDefinition` reaches the status bar as `Model == ""`. That trades one lie (null)
// for a strictly worse one (fabricated data that flows into storage and onto the
// screen). The third option — refusing to hand out an unbound agent at all — would mean
// moving agent construction behind a factory that knows the session, i.e. rewiring the
// DI root plus the eleven production `Initialize` call sites. The honest middle is
// absence: `Maybe<AgentState>`, with the "is a turn in flight?" decision made ONCE in
// `AgentStateProbe.IsRunning` instead of re-guessed at every consumer.
//
// TWO RULES, TWO KINDS OF PROOF
// -----------------------------
//   1. SHAPE (reflection): `IAgent.State` is `Maybe<AgentState>`. Reflection, because
//      the shape is a property of the compiled type — the text that produced the bug
//      can be retyped without ever saying `null!` again.
//   2. FABRICATION (source): no file under `src/` or `apps/` pairs an `AgentState` with
//      the null-forgiving operator. A source rule, because the fabrication is a
//      statement, not a signature.
//
// SCOPE. `src/` + `apps/` only — this is a production-code ratchet. Test doubles
// (`StubAgent`, `FakeAgent`, `RecordingAgent`) implement `IAgent` too and are migrated
// in the same PR, but re-gating nine test fixtures on every future signature change
// buys nothing: the shape rule above already fails the whole build when the contract
// moves, and a test double cannot ship a runtime lie.
//
// REUSE, NOT A SECOND SCANNER. The matcher and the comment stripper belong to
// SourceNullabilityScan, which already backs the two other `null!` gates — that file's
// header is explicit that a gate which copies the scanner is free to drift from the
// rule it claims to enforce. This file keeps only its own FILE WALK, because the slice
// genuinely differs: `apps/` holds the production consumers of `IAgent.State` and is
// not in `EnumerateSources`' `src/` glob.
//
// NON-VACUITY — the part that makes the guard worth having
// --------------------------------------------------------
// A source scan that matches nothing is indistinguishable from one that is broken, and a
// broken guard is worse than no guard because it is believed. Four tests below close
// that: the shape predicate runs against a control carrying the ORIGINAL non-nullable
// declaration; the scan runs against the original `= null!` spelling, the fixed
// spelling, and a doc comment that mentions BOTH words (so the "comments are ignored"
// claim is proved rather than accidentally satisfied); and discovery must reach a
// non-trivial number of `AgentState` declarations.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Enforces the <c>Maybe&lt;AgentState&gt;</c> contract on <see cref="IAgent.State" />
///     and the absence of a null-forgiving initialiser anywhere that declares one. See the
///     file header for the incident and the non-vacuity argument.
/// </summary>
public sealed class AgentStateContractRules
{
    /// <summary>Repo-relative trees that may declare an <c>IAgent</c> implementation.</summary>
    private static readonly string[] ProductionTrees = ["src", "apps"];

    // ── Rule 1: shape ────────────────────────────────────────────────────────

    [Test]
    public async Task State_OnIAgent_IsMaybeOfAgentState()
    {
        await Assert.That(HasHonestStateShape(typeof(IAgent))).IsTrue()
            .Because(
                "Before Initialize an agent has no state, and there is no AgentState to hand out: its "
                + "SessionId and Agent fields would both have to be invented, and the invented session "
                + "id flows straight into ISessionStore.AppendMessageAsync while the invented definition "
                + "flows onto the status bar as Model == \"\". Absence is Maybe<AgentState> (#559). "
                + "The non-nullable annotation the contract used to carry was assigned `null!` in "
                + "DefaultAgent and dereferenced blind at ~20 call sites, three of them in constructors.");
    }

    [Test]
    public async Task ShapeCheck_RejectsTheDeclarationThatShipped()
    {
        await Assert.That(HasHonestStateShape(typeof(ILegacyShape))).IsFalse()
            .Because(
                "Positive control. ILegacyShape below declares `AgentState State { get; }` — the exact "
                + "shape DefaultAgent could not honour. If the predicate accepts it, rule 1 is inert and "
                + "its passing on the real interface means nothing.");
    }

    // ── Rule 2: fabrication ──────────────────────────────────────────────────

    [Test]
    public async Task NoProductionFile_PairsAgentStateWithNullForgivingNull()
    {
        var violations = new List<string>();
        string? root = RepoPaths.RepoRoot;

        foreach (string file in EnumerateProductionSources())
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            foreach ((int line, string text) in ScanSource(source))
            {
                string rel = root is null ? file : Path.GetRelativePath(root, file);
                violations.Add($"{rel}({line}): {text}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "An `AgentState` initialised with `null!` is the lie #559 is about: the signature promises "
                + "a value the constructor has not produced yet, and the only defences are the ad-hoc "
                + "`State is null` guards that then get bypassed a few lines later. Use Maybe<AgentState>. "
                + $"Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    [Test]
    public async Task FabricationScan_FlagsTheSpellingThatShipped()
    {
        // Verbatim on purpose: this is DefaultAgent.cs:119 exactly as it shipped,
        // rewritten only into the form the scanner takes. A miss means rule 2 is
        // inert and its passing on the real tree means nothing.
        const string Shipped = """
            /// <summary>
            ///     Current agent state snapshot. null until Initialize is called.
            /// </summary>
            public AgentState State { get; private set; } = null!;
            """;

        // `.Count`, not IsNotEmpty(): TUnit routes IsEmpty/IsNotEmpty by the STATIC
        // collection type, and IReadOnlyList is not one of the shapes it names.
        await Assert.That(ScanSource(Shipped).Count).IsGreaterThan(0)
            .Because("The #559 spelling is a real positive control; a miss means the rule is inert.");
    }

    [Test]
    public async Task FabricationScan_AcceptsTheFixedSpelling()
    {
        const string Fixed = """
            private AgentState? _state;
            public Maybe<AgentState> State => _state is { } bound ? Maybe.From(bound) : Maybe<AgentState>.None;
            """;

        await Assert.That(ScanSource(Fixed).Count).IsEqualTo(0)
            .Because("The fixed spelling must not be flagged, or the guard cannot be adopted.");
    }

    [Test]
    public async Task FabricationScan_IgnoresTheCommentThatDocumentsTheOldSpelling()
    {
        // BOTH words sit on ONE comment line, and nothing else in the snippet is a
        // hit — so the zero below is earned by the comment stripper, not by the
        // accidental absence of "AgentState". The second assertion is what makes
        // that claim checkable: it proves the raw line WOULD have been flagged.
        // DocLine and the first line of Documented are deliberately the same text
        // rather than one interpolated into the other — deriving it would mean a
        // `$"""` literal here, whose brace-escaping around `_state is { } bound`
        // is exactly the kind of near-miss a control should not contain.
        const string DocLine =
            "/// Current AgentState snapshot: the field was `null!` until Initialize is called (#559).";
        const string Documented = """
            /// Current AgentState snapshot: the field was `null!` until Initialize is called (#559).
            private AgentState? _state;
            public Maybe<AgentState> State => _state is { } bound ? Maybe.From(bound) : Maybe<AgentState>.None;
            """;

        await Assert.That(ScanSource(Documented).Count).IsEqualTo(0)
            .Because(
                "StripComments runs before this matcher, so a contributor documenting WHY the field is no "
                + "longer null-forgiving must not fail the gate.");

        await Assert.That(IsFabrication(DocLine)).IsTrue()
            .Because(
                "Positive control for the assertion above: with the comment stripper out of the path this "
                + "line is a violation, which is exactly the false positive the stripper exists to remove. "
                + "If this ever goes false the assertion above stops proving anything.");
    }

    [Test]
    public async Task AgentStateDeclarations_AreDiscoverable()
    {
        int declarations = EnumerateProductionSources()
            .Count(static f => ReadCommentStripped(f).Contains("AgentState", StringComparison.Ordinal));

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the fabrication gate checks nothing.");
        await Assert.That(declarations).IsGreaterThan(3)
            .Because(
                "Non-vacuity for the source rule: the scan must actually reach the IAgent implementations. "
                + "Expected at least DefaultAgent, TracingAgentProxy and the interface itself; found "
                + $"{declarations}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The #559 rule as a predicate: <paramref name="contract" /> must declare
    ///     <c>State</c> as <c>Maybe&lt;AgentState&gt;</c>. Exposed so the positive control runs
    ///     the SAME check against the shape that shipped.
    /// </summary>
    internal static bool HasHonestStateShape(Type contract)
    {
        PropertyInfo? state = contract.GetProperty("State", BindingFlags.Public | BindingFlags.Instance);
        return state is not null && state.PropertyType == typeof(Maybe<AgentState>);
    }

    /// <summary>
    ///     One source line declares an <c>AgentState</c> AND suppresses its nullability with the
    ///     null-forgiving operator — the pair that made <c>State</c> null in production while the
    ///     signature said otherwise. Reuses <see cref="SourceNullabilityScan.NullForgivingOnNull" />
    ///     so this rule cannot drift from the two gates already built on it.
    /// </summary>
    internal static bool IsFabrication(string line) =>
        line.Contains("AgentState", StringComparison.Ordinal)
        && SourceNullabilityScan.NullForgivingOnNull.IsMatch(line);

    /// <summary>
    ///     One <c>(line, offending text)</c> entry per violation in
    ///     <paramref name="source" />, comment-stripped first. Shared by the production
    ///     walk and by the controls below, so a control proves the code path the
    ///     production rule actually runs — not a re-implementation of it.
    /// </summary>
    internal static IReadOnlyList<(int Line, string Text)> ScanSource(string source)
    {
        // Split after stripping, so a line number indexes the real source.
        string[] lines = SourceNullabilityScan.StripComments(source).Split('\n');
        var hits = new List<(int, string)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsFabrication(lines[i]))
            {
                hits.Add((i + 1, lines[i].Trim()));
            }
        }

        return hits;
    }

    /// <summary>
    ///     The comment-stripped text of one file. Delegates to
    ///     <see cref="SourceNullabilityScan.StripComments" /> for the same non-drift reason.
    /// </summary>
    private static string ReadCommentStripped(string path)
    {
        try
        {
            return SourceNullabilityScan.StripComments(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Every production <c>.cs</c> file under <see cref="ProductionTrees" />, sorted for a
    ///     stable failure message. Not <c>SourceNullabilityScan.EnumerateSources</c>: that
    ///     globs <c>src/</c> only, and the CLI composition root in <c>apps/</c> is exactly
    ///     where the blind dereferences this issue is about lived.
    /// </summary>
    private static IReadOnlyList<string> EnumerateProductionSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var found = new List<string>();
        foreach (string tree in ProductionTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Never descend into build output — a stale obj/ copy would be counted as a
            // second occurrence of every violation.
            found.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     The declaration <c>IAgent.State</c> used to carry — reproduced so rule 1 has a positive
    ///     control that fails for the right reason instead of vacuously passing.
    /// </summary>
    private interface ILegacyShape
    {
        AgentState State { get; }
    }
}
