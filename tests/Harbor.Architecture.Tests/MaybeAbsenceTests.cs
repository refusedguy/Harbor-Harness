// MaybeAbsenceTests.cs — guard for the "absence is Maybe<T>" wave.
//
// #589 established the rule in the renderer: a value that can be absent must
// not be modelled as a Result that always succeeds. This test extends the same
// rule to the two projects this wave converted, so the conversion cannot be
// silently undone one PR later:
//
//   * a LOOKUP declares absence with `Maybe<T>` — not with a nullable
//     out-parameter (`out T? x` + `bool`), which makes the caller write the
//     same null check the signature already promised was impossible;
//   * a SEARCH over a sequence uses `MaybeExtensions.TryFirst`/`TryFind` — not
//     `Enumerable.FirstOrDefault` followed by a null test;
//   * a SINGLE-VALUE accessor declares absence with `Maybe<T>` — not with a
//     `Try`-prefixed method returning `T?` (#591), which is the same
//     convention-driven shape wearing a different hat: the `Try` prefix is a
//     *promise to the caller* ("this cannot fail") carried by nothing but the
//     spelling of the method name, and a `null` return is indistinguishable
//     from "there was nothing to give" at the call site.
//
// The third rule (#591) extends the scope to `src/Harbor.Tui.CellForge.Engine`
// and is scoped NARROWER on purpose. That project is mid-flight towards an
// empty reference list (#33/T2 #435, #33/T3 #436) and has two outstanding
// absence findings of its own; adopting all of #589's rules there in one wave
// would drag in unrelated conversions. The scope lists below say exactly which
// rule applies where, so widening is a one-line decision rather than an audit.
//
// The checks are source-text based on purpose, matching BenchmarkContractTests:
// they need no reference to the converted assemblies, so they run in the
// lightweight architecture-test project instead of forcing apps/Harbor.App.Cli
// and src/Harbor.Hosting to be built by this test.
//
// ACCEPTED EXEMPTIONS below are not debt swept under the rug — each carries the
// reason it cannot be a Maybe today, so the next reader knows which wave owns
// it. Two conventions were established while converting and are recorded here
// because they decide whether a site is convertible at all:
//
//   1. `Maybe<T>` cannot express None for a non-nullable value type.
//      `Maybe<T>.From(T? value)` branches on `value == null`, which is always
//      false for a struct, so `Maybe<(string, string, T)>.From(default)` is
//      Some, not None. A value-tuple element type therefore cannot carry
//      absence — the tuple has to be named (a record) before Maybe applies.
//   2. "there is no element" and "the element is empty" are DIFFERENT absences.
//      `args.FirstOrDefault(a => !a.StartsWith('-'))` followed by
//      `string.IsNullOrWhiteSpace(...)` merges them; collapsing that into a
//      single `HasNoValue` check changes which inputs are rejected.
//
// Both exemptions below cite one of these.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: absence is <c>Maybe&lt;T&gt;</c> in the projects that
///     adopted it, and the exemptions are documented rather than silent.
/// </summary>
public class MaybeAbsenceTests
{
    /// <summary>The two projects the Maybe-absence wave converted.</summary>
    private static readonly string[] GuardedProjects =
    [
        "apps/Harbor.App.Cli",
        "src/Harbor.Hosting"
    ];

    /// <summary>
    /// Projects additionally covered by the single-value rule
    /// (<c>NullableTryReturn</c>). Deliberately a separate list from
    /// <see cref="GuardedProjects"/>: the Engine is in scope for the one shape
    /// #591 named, not yet for <c>out T?</c> / <c>FirstOrDefault</c>, which have
    /// their own open conversions in that project.
    /// </summary>
    private static readonly string[] SingleValueAbsenceProjects =
    [
        ..GuardedProjects,
        "src/Harbor.Tui.CellForge.Engine"
    ];

    /// <summary>
    ///     A nullable out-parameter on a signature — the shape a lookup used when
    ///     it reported absence by convention (<c>out T? x</c> + <c>bool</c>)
    ///     instead of by value.
    /// </summary>
    private static readonly Regex NullableOutParameter = new(
        @"\bout\s+[A-Za-z_][A-Za-z0-9_<>,\.]*\?\s+[A-Za-z_][A-Za-z0-9_]*\s*[,)]",
        RegexOptions.Compiled);

    /// <summary>
    /// A single-value accessor that reports absence by convention: a
    /// <c>Try</c>-prefixed member returning <c>Nullable&lt;T&gt;</c>.
    /// <list type="bullet">
    ///     <item><c>public BufferPair? TryTake()</c> — matches (#591).</item>
    ///     <item><c>public bool TryTakeEvent(out InputEvent e)</c> — no match;
    ///     <c>bool</c> carries no <c>?</c>, so the absence is a return VALUE,
    ///     not a nullable.</item>
    ///     <item><c>public bool SupportsSyncWrites</c> — no match. A predicate is
    ///     a defined boolean answer, not an absence (#591's "honest
    ///     rejections").</item>
    ///     <item><c>public ISyncTerminalBackend SyncBackend =&gt; ...</c> — no
    ///     match. That is the throw-next-to-a-bool finding, a different rule.</item>
    ///     <item><c>_focusedId = ...FirstOrDefault(...)?.Id</c> — no match; no
    ///     access modifier and no <c>Try</c> name. That is
    ///     <see cref="FirstOrDefault"/>'s business.</item>
    /// </list>
    /// The <c>(?:public|protected|internal)</c> anchor is what keeps a bare
    /// call site such as <c>while (Parser.TryTakeEvent(out var evt))</c> out of
    /// the net: the rule is about a declared signature, not a use of one.
    /// </summary>
    private static readonly Regex NullableTryReturn = new(
        @"\b(?:public|protected|internal)\b[^;{]*\b[A-Za-z_][A-Za-z0-9_.]*(?:<[^;{}]*>)?\?[ \t]+Try[A-Za-z0-9_]*[ \t]*[(<]",
        RegexOptions.Compiled);

    /// <summary>
    ///     The engine file whose <c>TryTake</c> this exemption exists for, and the
    ///     pin it has to agree with.
    /// </summary>
    private const string BufferSwapChainPath =
        "src/Harbor.Tui.CellForge.Engine/Rendering/BufferSwapChain.cs";

    /// <summary>
    ///     A project name appearing inside the <see cref="NullableTryReturnExemptions"/>
    ///     reason for <see cref="BufferSwapChainPath"/>.
    /// </summary>
    /// <remarks>
    ///     Scoped to <c>Harbor\.</c>-prefixed dotted names. The reason is prose, so the
    ///     matcher cannot be "any capitalised word" — that would fire on <c>#435</c>,
    ///     <c>CSharpFunctionalExtensions</c> and <c>BufferPair</c>. A dotted name starting
    ///     with the repo's own assembly prefix is the only shape in this text that means
    ///     "a project in the reference closure".
    /// </remarks>
    private static readonly Regex ProjectNameInProse = new(
        @"\bHarbor\.[A-Za-z0-9_.]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Files allowed to keep a <c>Try</c>-prefixed nullable return, each with
    /// the reason it is not converted yet. Adding an entry is a decision, not
    /// an oversight — the reason is printed in the failure message and
    /// <see cref="Exemptions_AreStillUsed"/> deletes the entry once it stops
    /// matching anything.
    /// </summary>
    private static readonly Dictionary<string, string> NullableTryReturnExemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Tui.CellForge.Engine/Rendering/BufferSwapChain.cs"] =
            "BLOCKED ON A DEPENDENCY DECISION #435/#436 OWN, not a Maybe decision this wave can "
            + "make (#591). `TryTake` is a pure absence — one state, 'nothing pending' — so "
            + "Maybe<BufferPair> is the correct target and there is no Result axis here at all. "
            + "But the engine reached `Maybe<T>` only TRANSITIVELY: its csproj declares zero "
            + "PackageReference entries, and CSharpFunctionalExtensions arrived through THREE "
            + "projects — Harbor.Abstractions and Harbor.Ui.Framework.State by direct edge, plus "
            + "Harbor.Abstractions.Contracts via Harbor.Ui.Framework.Rendering. #789 and #591 "
            + "recorded two carriers: true of the two direct edges, wrong about the closure, which "
            + "#809 measured. #435 removed the two direct edges and #436 the last one, so ZERO carriers are left and the "
            + "ladder this text used to predict (3 -> 1 after #435 -> 0 after #436) is fully "
            + "taken: the engine is a standalone leaf. The last carrier was "
            + nameof(HarborAbstractionsContractsCarrierNote)
            + " (Harbor.Abstractions.Contracts, reached via Harbor.Ui.Framework.Rendering), which "
            + "#436 removed, so a PackageReference added from today on re-opens a path no slice "
            + "accounts for, and would answer #789's owner question without a decision behind it. "
            + "Converting today picks a dependency owner "
            + "without answering 'direct PackageReference on CSE, or a vendored Maybe<T>?'. "
            + "Converted the moment that decision lands; the recipe is in #591. "
            + "Conversion note for whoever does it: CSE's Maybe<T> is a STRUCT, so `?.` and "
            + "`is not { }` do not bind to its value. The single caller, "
            + "src/Harbor.Tui.CellForge/Chat/Streaming/ScreenSession.cs:142, becomes "
            + "`offer.HasValue ? offer.Value : <re-check next frame>`."
    };

    /// <summary>
    ///     Files allowed to keep a nullable out-parameter, each with the reason
    ///     it is not part of this wave. Adding an entry is a decision, not an
    ///     oversight — the reason is printed in the failure message.
    /// </summary>
    private static readonly Dictionary<string, string> NullableOutParameterExemptions = new(StringComparer.Ordinal)
    {
        ["apps/Harbor.App.Cli/Repl/PromptPipeline.cs"] =
            "ChannelReader.TryDequeue is a BCL signature — cannot take Maybe.",
        ["apps/Harbor.App.Cli/Repl/Commands/ReplCommandCatalog.cs"] =
            "ReplCommandCatalog.TryResolve is a pure Maybe candidate, but 7 existing "
            + "assertions in tests/Harbor.App.Cli.Tests/SlashPanelsCatalogTests.cs call it; "
            + "rewriting another project's tests is out of scope for this wave.",
        ["apps/Harbor.App.Cli/Commands/TuiAttachRunner.cs"] =
            "TuiAttachOptions.TryParse carries a parse ERROR out-param, not just absence — "
            + "that is a Result<T>, i.e. the MapError/Result wave, not this one.",
        ["apps/Harbor.App.Cli/Commands/TuiAttachVerb.cs"] =
            "Caller of TuiAttachOptions.TryParse — same parse-error reason.",
        ["apps/Harbor.App.Cli/Commands/EventsWatchRunner.cs"] =
            "EventsWatchOptions.TryParse carries a parse ERROR out-param — a Result<T>, "
            + "not a Maybe.",
        ["apps/Harbor.App.Cli/Commands/EventsVerb.cs"] =
            "Caller of EventsWatchOptions.TryParse — same parse-error reason.",
        ["src/Harbor.Hosting/RuntimeRendererSwapMiddleware.cs"] =
            "TryResolve returns out backendId AND out error — the error is a reason the "
            + "user must see, so this is a Result<T> candidate (Result wave).",
        ["src/Harbor.Hosting/Modules/TuiBackendRegistry.cs"] =
            "TuiBackendRegistry.Resolve never returns nothing — an unknown id falls back "
            + "to FallbackBackendId by contract. Absence is unrepresentable here, so a "
            + "Maybe would be a lie in the other direction.",
        ["src/Harbor.Hosting/Modules/PluginReloadService.cs"] =
            "versions.TryGetValue(..., out string? version) is a LOCAL dictionary lookup "
            + "inside ListInstalled, not a signature. Same conversion, deferred to avoid "
            + "widening this wave."
    };

    /// <summary>
    ///     Files allowed to keep <c>FirstOrDefault</c>, each with the reason the
    ///     sequence's absence cannot be a Maybe without changing behaviour.
    /// </summary>
    private static readonly Dictionary<string, string> FirstOrDefaultExemptions = new(StringComparer.Ordinal)
    {
        ["apps/Harbor.App.Cli/Program.cs"] =
            "args.Skip(1).FirstOrDefault() is handed to RunListModelsAsync as a provider "
            + "name; the absence of an argument is the SAME absence as an empty one there.",
        ["apps/Harbor.App.Cli/Commands/SkillsCommand.cs"] =
            "Convention 2: `FirstOrDefault(a => !a.StartsWith('-'))` + IsNullOrWhiteSpace "
            + "distinguishes \"no such argument\" from \"empty argument\". Collapsing to "
            + "HasNoValue would accept inputs the command rejects today.",
        ["apps/Harbor.App.Cli/Commands/PluginsCommand.cs"] =
            "Same as SkillsCommand — convention 2 (no argument vs empty argument).",
        ["apps/Harbor.App.Cli/Commands/McpLoginRunner.cs"] =
            "Convention 1 for LoadRemoteEntries(): the element type is a value tuple, so "
            + "Maybe<T>.From(default) is Some, never None. The `args.FirstOrDefault(a => "
            + "!a.StartsWith('-'))` sites are convention 2. Naming the tuple (McpRemoteEntry) "
            + "is a type-extraction change, not a Maybe change."
    };

    [Test]
    public async Task GuardedProjects_DeclareNoAbsenceViaNullableOutParameter()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles(GuardedProjects))
        {
            if (!NullableOutParameter.IsMatch(text))
            {
                continue;
            }

            string relative = Relative(file);
            if (NullableOutParameterExemptions.TryGetValue(relative, out string? reason))
            {
                _ = reason;
                continue;
            }

            violations.Add($"{relative}:{line} — nullable out-parameter declares absence by convention: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A lookup in apps/Harbor.App.Cli or src/Harbor.Hosting must return Maybe<T> (#589's rule). "
                + "An `out T? x` + `bool` signature makes the caller re-check the null the signature already "
                + "promised could not happen. Return Maybe<T> instead, or add the file to "
                + "NullableOutParameterExemptions with the reason it is not a Maybe.");
    }

    [Test]
    public async Task GuardedProjects_DeclareNoAbsenceViaFirstOrDefault()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles(GuardedProjects))
        {
            if (!text.Contains("FirstOrDefault(", StringComparison.Ordinal))
            {
                continue;
            }

            string relative = Relative(file);
            if (FirstOrDefaultExemptions.TryGetValue(relative, out string? reason))
            {
                _ = reason;
                continue;
            }

            violations.Add($"{relative}:{line} — FirstOrDefault + null test is TryFirst: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "`Enumerable.FirstOrDefault` returns null to mean 'absent', which is exactly Maybe<T>.None. "
                + "Use MaybeExtensions.TryFirst / TryFind so the absence is in the type. Two cases cannot be "
                + "converted without changing behaviour: a value-type element (Maybe<T>.From never yields None) "
                + "and 'no element' vs 'empty element' — add those to FirstOrDefaultExemptions with the reason.");
    }

    [Test]
    public async Task GuardedProjects_DeclareNoAbsenceViaNullableTryReturn()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles(SingleValueAbsenceProjects))
        {
            if (!NullableTryReturn.IsMatch(text))
            {
                continue;
            }

            string relative = Relative(file);
            if (NullableTryReturnExemptions.TryGetValue(relative, out string? reason))
            {
                _ = reason;
                continue;
            }

            violations.Add($"{relative}:{line} — 'Try' + nullable return declares absence by convention: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A single-value accessor must return Maybe<T>, not `T?` under a `Try` prefix (#591). "
                + "The prefix is a promise to the caller that the signature itself does not keep: a `null` "
                + "return is indistinguishable from 'there was nothing to give', and nothing in the type "
                + "makes the caller handle it. `public bool TryX()` is NOT this smell — a predicate has a "
                + "defined boolean answer. Add the file to NullableTryReturnExemptions with the reason it "
                + "is not a Maybe.");
    }

    [Test]
    public async Task Exemptions_AreStillUsed()
    {
        // Guards the guard: an exemption whose pattern has since been fixed is
        // dead weight that hides the rule it was protecting.
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var stale = new List<string>();

        foreach (string relative in NullableOutParameterExemptions.Keys)
        {
            string absolute = Path.Combine(root, relative);
            if (!File.Exists(absolute) || !ScanFile(absolute).Any(hit => NullableOutParameter.IsMatch(hit.Text)))
            {
                stale.Add($"{relative} — nullable out-parameter exemption no longer matches anything");
            }
        }

        foreach (string relative in FirstOrDefaultExemptions.Keys)
        {
            string absolute = Path.Combine(root, relative);
            if (!File.Exists(absolute) || !ScanFile(absolute).Any(hit => hit.Text.Contains("FirstOrDefault(", StringComparison.Ordinal)))
            {
                stale.Add($"{relative} — FirstOrDefault exemption no longer matches anything");
            }
        }

        foreach (string relative in NullableTryReturnExemptions.Keys)
        {
            string absolute = Path.Combine(root, relative);
            if (!File.Exists(absolute) || !ScanFile(absolute).Any(hit => NullableTryReturn.IsMatch(hit.Text)))
            {
                stale.Add($"{relative} — nullable-Try-return exemption no longer matches anything");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exemption that no longer matches is silently dead: it would let the pattern come back "
                + "with nobody watching. Delete it, or update the reason to describe the new shape.");
    }

    /// <summary>
    ///     The BufferSwapChain exemption reason and
    ///     <see cref="CellForgeEngineCseOwnershipTests.PinnedCseCarriers"/> state the same
    ///     fact — which projects hand CSharpFunctionalExtensions to the engine — and the
    ///     reason is the copy a reader meets FIRST.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #809 measured the closure, found the count was three rather than the two
    ///         #789 recorded, and pinned the three in executable form. It did not correct
    ///         the OTHER copy: this exemption's reason still says CSE arrives "through
    ///         Harbor.Abstractions and Harbor.Ui.Framework.State — the exact two references
    ///         #435 deletes", and that sentence is printed verbatim in
    ///         <see cref="NullableTryReturns_AreAbsent"/>'s failure message. So the stale
    ///         count is not a comment nobody reads — it is the text the next person is shown
    ///         when a scan trips, and it is wrong in the one direction that matters: it tells
    ///         the author of #435 that their slice ends the dependency, when the third carrier
    ///         (<c>Harbor.Abstractions.Contracts</c>, reached via
    ///         <c>Harbor.Ui.Framework.Rendering</c>) survives until #436.
    ///     </para>
    ///     <para>
    ///         Nothing cross-checked them: <c>Exemptions_AreStillUsed</c> asks whether the
    ///         row still MATCHES the pattern, never whether its reason is accurate, and the
    ///         two files never referenced each other. This is the drift the
    ///         <c>ExemptionReason</c> header names as deliberately out of its scope — "whether
    ///         the reason is TRUE" — so it is not a gap in that helper, it is the one claim
    ///         here that is mechanically checkable.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task BufferSwapChainExemptionReason_NamesEveryMeasuredCseCarrier()
    {
        string? reason = NullableTryReturnExemptions.GetValueOrDefault(BufferSwapChainPath);
        await Assert.That(reason).IsNotNull()
            .Because(
                $"{BufferSwapChainPath} must stay in NullableTryReturnExemptions while it keeps a "
                + "Try-prefixed nullable return. Without the row this rule has nothing to grade.");

        if (reason is null)
        {
            return;
        }

        var named = ProjectNameInProse.Matches(reason)
            .Select(m => m.Value.TrimEnd('.'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = CellForgeEngineCseOwnershipTests.PinnedCseCarriers
            .Where(carrier => !named.Contains(carrier))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(missing).IsEmpty()
            .Because(
                "This reason and CellForgeEngineCseOwnershipTests.PinnedCseCarriers are the same claim "
                + "about which projects hand CSharpFunctionalExtensions to the engine, and the reason is "
                + "the copy printed into the failure message. #809 measured the closure as THREE and "
                + "pinned it; this text was left saying \"the exact two references #435 deletes\", which "
                + "omitted "
                + nameof(HarborAbstractionsContractsCarrierNote)
                + " — the last carrier, reached via Harbor.Ui.Framework.Rendering, which #436 removed. "
                + "Both slices have now landed, so this reason's carrier count (zero) matches the pin by "
                + "re-measurement, not by subtraction. The ladder was 3 -> 1 after #435 -> 0 after #436 "
                + "and is fully taken; a PackageReference added from today on re-opens a path no slice "
                + "accounts for — answering #789's open owner question without a decision behind it. "
                + "Name every carrier here, or fix the pin, but the two must not disagree.");
    }

    /// <summary>
    ///     Anchors the last carrier in a compile-checked symbol so the sentence
    ///     above cannot drift into naming a project that does not exist. #435
    ///     left it the LAST carrier and #436 closed it; the symbol now anchors
    ///     the history the reason retells, not a live edge.
    /// </summary>
    private const string HarborAbstractionsContractsCarrierNote = "Harbor.Abstractions.Contracts";

    /// <summary>
    ///     The matcher and the reason both have to be real, or the rule above is green
    ///     because it compared an empty set against an empty expectation.
    /// </summary>
    [Test]
    public async Task CarrierProseMatcher_IsNotVacuous()
    {
        // The matcher finds project names in the reason as it stands today, and stays
        // silent on the words a looser matcher would catch.
        string? reason = NullableTryReturnExemptions.GetValueOrDefault(BufferSwapChainPath);
        await Assert.That(reason).IsNotNull();

        if (reason is null)
        {
            return;
        }

        var hits = ProjectNameInProse.Matches(reason).Select(m => m.Value).ToArray();
        await Assert.That(hits.Length).IsGreaterThan(0)
            .Because(
                "ProjectNameInProse matched no Harbor.* name in the BufferSwapChain reason, so "
                + "BufferSwapChainExemptionReason_NamesEveryMeasuredCseCarrier compared an empty "
                + "named-set against the pinned carriers and would have failed for the wrong reason — or, if "
                + "the carrier list ever emptied, passed for none.");

        // It must not fire on the non-project tokens the reason also contains.
        foreach (string notAProject in new[] { "#435", "#436", "#591", "CSharpFunctionalExtensions", "Maybe", "BufferPair" })
        {
            await Assert.That(ProjectNameInProse.IsMatch(notAProject)).IsFalse()
                .Because($"'{notAProject}' is not a project name; a matcher that reported it would be "
                    + "matching capitalised words rather than the reference closure.");
        }

        // And the pin it grades is the one the CSE guard actually enforces.
        await Assert.That(CellForgeEngineCseOwnershipTests.PinnedCseCarriers.Length).IsEqualTo(0)
            .Because(
                "The ladder this rule documents was 3 carriers -> 1 after #435 -> 0 after #436, and #436 "
                + "has landed, so zero is correct and the engine is a standalone leaf. At 0 the "
                + "missing-set comparison above is vacuously true, so the reason must state the landed "
                + "count out loud — asserted next — or the rule is green because it compared nothing "
                + "against nothing.");
        await Assert.That(reason.Contains("ZERO carriers", StringComparison.Ordinal)).IsTrue()
            .Because(
                "At a zero-length carrier list the missing-set comparison above passes no matter what "
                + "the reason says. The reason must therefore carry the landed count in words, so a "
                + "regression that re-opens the path cannot hide behind a stale sentence.");
    }

    [Test]
    public async Task Scanner_ReadsTheGuardedProjectsAndIgnoresComments()
    {
        // Guards the scanner itself: if RepoRoot cannot be found the two tests
        // above would pass vacuously, and a vacuous guard is worse than none.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "The absence guard walks the working tree; without a repository root (no Harbor.slnx above "
                + "AppContext.BaseDirectory) every source-text rule below would silently pass. That would make "
                + "the guard untestable in exactly the environment where it matters.");

        if (root is null)
        {
            return;
        }

        int files = GuardedProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        await Assert.That(files).IsGreaterThan(50)
            .Because($"The two guarded projects should hold well over 50 source files; found {files}.");

        // The single-value scope adds CellForge.Engine, so a typo in that path
        // would silently shrink the #591 rule back to two projects. The
        // difference is the Engine's own file count: it must actually be there.
        int engineFiles = CountProjectFiles(root, "src/Harbor.Tui.CellForge.Engine");
        await Assert.That(engineFiles).IsGreaterThan(40)
            .Because(
                $"The #591 single-value rule also scans src/Harbor.Tui.CellForge.Engine, which holds ~60 "
                + $"source files; found {engineFiles}. A path that does not resolve makes the rule vacuous.");

        // Comment lines must not count — the converted sites explain themselves in prose.
        int commentHits = GuardedProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(ScanFile)
            .Count(hit => hit.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));
        await Assert.That(commentHits).IsEqualTo(0)
            .Because("Comment lines are excluded from both rules, so a comment mentioning the pattern cannot fail the build.");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Source files of one project, build output excluded.</summary>
    private static int CountProjectFiles(string root, string project)
    {
        string dir = Path.Combine(root, project);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>Every non-comment, non-blank source line of the given projects.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanGuardedFiles(string[] projects)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            yield break;
        }

        foreach (string project in projects)
        {
            string dir = Path.Combine(root, project);
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
    ///     One file, one-based line numbers, comment and blank lines dropped.
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
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
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
