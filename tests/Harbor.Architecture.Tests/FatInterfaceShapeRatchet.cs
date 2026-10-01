// FatInterfaceShapeRatchet.cs — the RATCHET for #471, slice 2: the three
// interfaces the issue names and the fourth it under-counts.
//
// WHAT THIS IS NOT
// ----------------
// It is not a gate demanding the four-way split. A gate that requires a
// refactor nobody has done is a standing build failure, and a standing build
// failure is how a team learns to reach for `--no-verify`. #752 learned that
// on the ITokenTracker half of this same issue and shipped a ratchet instead;
// this file is the same instrument for the other three. The splits are still
// owed. This file does not do them and does not pretend to.
//
// WHAT THE RE-MEASUREMENT FOUND (the reason this is a ratchet and not a gate)
// -------------------------------------------------------------------------
// The issue's own numbers do not survive contact with the source, and two of
// the four slices are not the shape the issue describes. Every figure below is
// re-derived by the tests in this file, so a reader never has to trust it:
//
//   IReplHost          31 declared members (21 properties + 10 methods), the
//                      issue says 24. 18 product consumers, not 24 — and not the
//                      other reading either, since "24 members of which 6 are
//                      used" is also false: no member of it is dead. It inherits
//                      nothing, so nothing here is double-counted.
//   IThemeService      0 declared members. The file is 19 lines and its body is
//                      empty: it is an alias over IThemeReader (2),
//                      IThemeApplier (6) and IThemeWatcher (4, one an event) —
//                      12 distinct names, or 11 if you leave the event out, which
//                      is where the issue's "11" most likely came from. The range
//                      it cites (":9-54") does not exist in a 19-line file.
//                      #469 already did this split — the three role
//                      aliases are registered in ServiceRegistration.cs and
//                      every consumer depends on a role, not on the alias.
//   ITokenTracker      7 declared members. The issue's number is right. Its
//                      split is already ratcheted by #752
//                      (TokenTrackingRatchet.cs) and is blocked on a product
//                      decision, not on missing work: the aggregate declares
//                      EstimateTokens(IReadOnlyList<AgentMessage>) while the
//                      ITokenEstimator that already exists next door declares
//                      EstimateMessages(IEnumerable<AgentMessage>).
//   IApprovalCoordinator  9 distinct SIGNATURES, which is 7 member NAMES —
//                      RegisterGate and DecideApproval are each an overload
//                      pair. The issue's number is right, and so is its central
//                      claim: 6 of the 7 names are used by exactly ONE consumer
//                      and only RequestCancel is shared (5 callers), so the
//                      demand really is non-overlapping. This is the one slice
//                      where "proven by consumers" means what it says.
//
// WHY IReplHost IS NOT THE FOUR-WAY SPLIT THE ISSUE ASKS FOR
// -----------------------------------------------------------
// "Split it because 24 independent consumers each need a different slice" is a
// claim about the CONSUMERS, and the consumers do not have that shape. Demand
// overlaps hard: WakeUp 17/18, Bridge 16/18, Palette 14/18, and 13 of the 18
// need all three of Bridge+Palette+WakeUp together. A four-way split that
// gave every consumer its own interface would still hand 13 of them a shared
// base carrying those three — which narrows 5 consumers and re-imposes a
// three-member base on the other 13. That is IThemeService's own mistake
// repeated: an alias that keeps the wide shape and adds four names to it.
//
// The shape that is actually there is one wide host surface with a
// single-consumer tail, and the tail is SEVEN members — ScrollTimelineToEnd,
// Composer, RendererPipeline, PanelRegistry, PluginReload, HealthCheck, and Git,
// each used by exactly one command. Git is the seventh because #929 added it
// (see the growth table below): the tail grew with the interface, which is the
// trend this ratchet exists to stop. Extracting those is owed work and the
// interface's own TODO at IReplHost.cs:19-20 already names the first step
// (INewSessionHost). It is not what "split into four" means, and it is not
// cheap: IReplHost is `internal` and hand-wired (nothing AddSingleton's it), so
// the cost is touching 19 commands and the implementor, not a DI axis.
//
// IApprovalCoordinator is the opposite case and deserves to be said plainly: it
// is a real, well-founded ISP violation, and the three-interface split in the
// issue is the right target. It is three NEW DI registration axes on a
// singleton, and #964 measured what an unregistered port costs here — the job
// confirmed 6 errors on Result when IContentHost's implementation was missing.
// Owed, priced, and deliberately not smuggled in beside a ratchet.
//
// IReplHost IS GROWING, NOT SHRINKING
// ------------------------------------
// Counting declared members at every commit that touched the file:
//
//   d0e192e7   26   selection cleanup, follow-tail scroll, /new full reset
//   6db64ab9   28   +2  slash output as panes
//   4c6b9d87   29   +1  panels picker
//   58a0ff3e   29       (panels null-registry fallback needs no Bridge)
//   45a3d8fb   30   +1  image attach
//   5d8a73d4   30       (unchanged)
//   787c777d   31   +1  IGitQuery? Git  (#929, for #857)
//
// Six touching commits: five growth, one flat, ZERO shrink. The issue treats
// IReplHost as a static inventory to be divided up; it is a live interface being
// added to, and the most recent change to it made it wider. That direction is
// what the member ratchet freezes, and it is why this file is worth landing even
// though it pays none of the split.
//
// THE ONE LIVE DEFECT THIS FILE WAS WRITTEN AGAINST
// -------------------------------------------------
// AgentLoop held `private readonly ITokenTracker _tokenTracker`, assigned it in
// the constructor and read it NOWHERE — the four behaviours are constructed
// from the constructor PARAMETER, not the field. That is the concrete instance
// of this issue's cost: a hot object naming the wide type, pinning a reference
// to it, and never consulting it. The write-only scan below is what found it
// and what keeps it from coming back; the field is deleted in the same PR.
//
// RATCHET SEMANTICS
// -----------------
// Every axis fires on ROT (an addition) and on SILENT CHANGE (a delta nobody
// re-measured), and both are reported in separate buckets, because "worse" and
// "different" need different words in a failure message. This file is not a
// to-do list: it does not demand the splits, and it will not go red because
// they have not happened.
//
// HOW THE MEASUREMENT IS TAKEN
// ----------------------------
// Comments are stripped first (SourceCommentStripper), so the XML docs that
// NAME an interface to explain it are not graded as users of it — the #947
// shape, where a naive scan reported 245 findings of which 87 were real. Member
// matching is RECEIVER-AGNOSTIC (`<anything>?.<Member>(`) on purpose: a scan
// anchored on a field name goes blind the moment the field is renamed, which is
// exactly when a new call could slip in. The cost is over-attribution
// (TokenTracker.cs's own delegations to the ITokenEstimator it holds are
// counted as calls on the aggregate) and over-attribution can only make this
// STRICTER, never looser.
//
//   IReplHost is deliberately NOT in the receiver-agnostic table. Its members
//   are ordinary properties and methods with generic names — `Store`, `Agent`,
//   `Bridge`, `Toggle`, `Apply` — so a receiver-agnostic scan over them matches
//   unrelated code all over the CLI (a `store.` on a UiStore, an `agent.` on an
//   IAgent). It is measured by NAME-anchored scan instead, over files that
//   actually name the type, and Ratchet_IReplHostConsumers_AreTheMeasuredOnes
//   pins that scan against real call sites so the weaker instrument is at
//   least a proven one.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One measured product file that names <c>IReplHost</c> and touches at least one
///     of its members, with the members it touches, ordinal-sorted.
/// </summary>
/// <param name="File">Repo-relative path.</param>
/// <param name="Members">Members of the host the file calls or property-reads.</param>
internal sealed record ReplHostConsumerBaseline(string File, IReadOnlyList<string> Members);

/// <summary>
///     One interface this file governs: the type name, the namespace-qualified
///     full name, and how many members it declared when measured.
/// </summary>
internal sealed record AggregateShape(string Name, string FullName, int MeasuredMemberCount);

/// <summary>
///     The two halves of a baseline comparison, both asserted empty by the real
///     tests. <see cref="List{T}" /> rather than <see cref="IReadOnlyList{T}" /> so
///     every assertion binds TUnit's collection overload on a concrete list, the
///     form the rest of this project uses.
/// </summary>
/// <param name="Regressions">Present now, absent from the baseline: the shape got worse.</param>
/// <param name="Improvements">In the baseline, absent now: the shape shrank or moved.</param>
internal sealed record ShapeDelta(List<string> Regressions, List<string> Improvements);

/// <summary>
///     Freezes the measured shape of the three <c>#471</c> aggregates that are not
///     already governed by <see cref="TokenTrackingRatchet" />, and fails the build
///     when any of them grows.
/// </summary>
public sealed class FatInterfaceShapeRatchet
{
    /// <summary>
    ///     The three aggregates. <c>ITokenTracker</c> is deliberately absent: #752
    ///     gave it <c>TokenTrackingRatchet.cs</c>, and two ratchets grading one type
    ///     would be two places to forget to update.
    /// </summary>
    /// <remarks>
    ///     <c>IReplHost</c> is NOT in this table and cannot be. It is
    ///     <c>internal</c> to <c>Harbor.App.Cli</c>, and this test project has
    ///     no <c>ProjectReference</c> to the CLI app — an application, not a
    ///     library, and the layering suite deliberately grades <c>src/</c>
    ///     without it. So there is no assembly to reflect over and no way to
    ///     reach the type by name: an earlier draft of this file asserted 31
    ///     members through <c>Resolve</c> and measured 0, every time, which is
    ///     the shape of a guard that is green for the wrong reason. Its members
    ///     are counted from SOURCE instead, by
    ///     <see cref="MeasureMembersFromSource" />, which reads the declaration
    ///     the same way the consumer scan reads its callers.
    /// </remarks>
    private static readonly AggregateShape[] ReflectedAggregates =
    [
        new(ThemeServiceName, "Harbor.Ui.Framework.Services.IThemeService", MeasuredThemeAliasMembers),
        new(ApprovalCoordinatorName, "Harbor.Abstractions.Permissions.IApprovalCoordinator", MeasuredApprovalMembers),
    ];

    // =====================================================================
    // The measured baseline. Re-measure and update in the same commit as
    // whatever changed the shape — the failure messages print the delta, so
    // the new table is a mechanical edit.
    // =====================================================================

    /// <summary>
    ///     <c>IReplHost</c> declared 31 members when measured: 21 properties and
    ///     10 methods. It inherits nothing, so this is the whole surface and
    ///     nothing is double-counted. The issue's "24" is neither this nor the
    ///     consumer count (19) — see the header.
    /// </summary>
    private const int MeasuredReplHostMembers = 31;

    /// <summary>
    ///     <c>IThemeService</c> declares NOTHING of its own. It is a DI
    ///     convenience alias over three already-narrow roles, landed by #469.
    ///     This is the assertion that keeps it one: the day someone adds a
    ///     member here instead of to the role it belongs on, this goes red.
    /// </summary>
    private const int MeasuredThemeAliasMembers = 0;

    /// <summary>Matches the issue's figure for <c>IApprovalCoordinator</c>, which is correct.</summary>
    private const int MeasuredApprovalMembers = 9;

    /// <summary>The governed aggregate's simple type name, as a named constant.</summary>
    private const string ReplHostName = "IReplHost";

    /// <inheritdoc cref="ReplHostName"/>
    private const string ThemeServiceName = "IThemeService";

    /// <inheritdoc cref="ReplHostName"/>
    private const string ApprovalCoordinatorName = "IApprovalCoordinator";

    /// <inheritdoc cref="ReplHostName"/>
    private const string TokenTrackerName = "ITokenTracker";

    /// <summary>
    ///     Every product file that names <c>IReplHost</c> and uses at least one
    ///     member, with the members it uses. The implementor
    ///     (<c>CellForgeReplRunner.cs</c>) is NOT a row: it is the class being
    ///     implemented, so of course it touches all 31, and grading it would
    ///     make the table unfalsifiable. Test doubles are excluded for the same
    ///     reason a composition root is — they must implement the whole surface
    ///     whatever their body calls, which is the cost, not the demand.
    /// </summary>
    private static readonly ReplHostConsumerBaseline[] ReplHostConsumers =
    [
        new("apps/Harbor.App.Cli/Repl/Commands/AgentCommand.cs",
            ["Agent", "AgentRegistry", "Bridge", "ConfigStore", "Palette", "Screen", "Selection", "SessionModel", "Store", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/AttachCommand.cs",
            ["Agent", "Attachments", "Bridge", "Palette", "SessionModel", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/AuthCommand.cs",
            ["AuthStore", "Bridge", "Palette", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/ConfigCommand.cs",
            ["Agent", "Bridge", "ConfigStore", "Palette", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/JumpCommand.cs",
            ["Bridge", "Git", "Palette", "SessionStore", "Status", "SwitchToSessionAsync", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/ModelCommand.cs",
            ["Agent", "AgentRegistry", "Bridge", "ConfigStore", "Palette", "ProviderRegistry", "ResolveContextWindowAsync", "Screen", "Selection", "SessionModel", "Status", "Store", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/PanelsCommand.cs",
            ["Palette", "PanelRegistry", "Store", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/PluginsPanelCommand.cs",
            ["Bridge", "Palette", "PluginReload", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/ProvidersCommand.cs",
            ["AuthStore", "Bridge", "HealthCheck", "Palette", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/RendererCommand.cs",
            ["Bridge", "Palette", "RendererPipeline", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/SessionsCommand.cs",
            ["Bridge", "Palette", "SessionStore", "SwitchToSessionAsync", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/SessionTreeCommand.cs",
            ["Agent", "Bridge", "Palette", "SessionModel", "SessionStore", "SwitchToSessionAsync", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/StorageCommand.cs",
            ["Bridge", "ConfigStore", "Palette", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/Commands/TuiCommand.cs",
            ["Bridge", "ConfigStore", "Palette", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/PromptPipeline.cs",
            ["Agent", "Attachments", "Bridge", "Composer", "RequestQuit", "Screen", "SessionModel", "Status", "Timeline", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/ReplCommandHost.cs",
            ["Agent", "Bridge", "RequestQuit", "SessionModel"]),
        new("apps/Harbor.App.Cli/Repl/SessionSwitchManager.cs",
            ["Agent", "AgentRegistry", "Bridge", "ResolveContextWindowAsync", "Screen", "ScrollTimelineToEnd", "Selection", "SessionModel", "SessionStore", "Store", "Timeline", "WakeUp"]),
        new("apps/Harbor.App.Cli/Repl/SessionTitleService.cs",
            ["ProviderRegistry", "Screen", "SessionModel", "SessionStore", "WakeUp"]),
    ];

    /// <summary>Every member <c>IReplHost</c> declared when measured, ordinal-sorted.</summary>
    private static readonly string[] BaselineReplHostMembers =
    [
        "Agent", "AgentRegistry", "Attachments", "AuthStore", "Bridge", "Composer",
        "ConfigStore", "ExecuteInfoAsync", "ExecutePaletteItemAsync", "Git",
        "HealthCheck", "OpenSlashPalette", "Palette", "PanelRegistry", "PluginReload",
        "ProviderRegistry", "RendererPipeline", "RequestQuit", "ResolveContextWindowAsync",
        "Screen", "ScrollTimelineToEnd", "Selection", "SessionModel", "SessionStore",
        "Status", "Store", "SwitchToSessionAsync", "SyncSessionsToStoreAsync",
        "Timeline", "ToggleVimMode", "WakeUp",
    ];

    /// <summary>
    ///     The DISTINCT MEMBER NAMES of the three theme ROLES, which is where
    ///     <c>IThemeService</c>'s surface actually lives: 12 — six on
    ///     <c>IThemeApplier</c>, two on <c>IThemeReader</c>, and four on
    ///     <c>IThemeWatcher</c> (three methods plus the <c>ThemeJsonApplied</c>
    ///     event; its two <c>Watch</c> overloads are one name).
    ///     <para>
    ///         So the issue's "11 members" is not far off as a count of the roles'
    ///         METHODS — it is 11 if the event is left out — which is the most
    ///         charitable reading. It is still not a property of
    ///         <c>IThemeService</c>, which declares nothing, and the range it
    ///         cites (<c>:9-54</c> of a 19-line file) does not exist.
    ///     </para>
    /// </summary>
    private static readonly string[] ThemeRoleMembers =
    [
        // IThemeApplier
        "Apply", "ApplyDark", "ApplyHds", "ApplyLight", "SetThemeVariant", "Toggle",
        // IThemeReader
        "Current", "IsDark",
        // IThemeWatcher (the event is a member; the forwarder overload is not separate)
        "ApplyJson", "LoadJson", "ThemeJsonApplied", "Watch",
    ];

    /// <summary>Every member <c>IApprovalCoordinator</c> declared when measured.</summary>
    private static readonly string[] BaselineApprovalMembers =
    [
        "BeginApprovalScope", "CompleteInvocation", "DecideApproval", "DecideApproval",
        "RegisterGate", "RegisterGate", "RequestCancel", "TryCommitApproval",
        "WaitForDecisionAsync",
    ];

    /// <summary>
    ///     <c>IApprovalCoordinator</c>'s two overload pairs are listed twice on
    ///     purpose. Counting them once would understate the surface (7, not 9) and
    ///     a de-overload would then look like progress when it is a signature
    ///     change. Distinct signatures are distinct members.
    /// </summary>
    private static readonly string[] ApprovalDistinctSignatures =
    [
        "BeginApprovalScope()",
        "CompleteInvocation(String)",
        "DecideApproval(String, ApprovalResolution)",
        "DecideApproval(String, String, Int32, ApprovalResolution)",
        "RegisterGate(String)",
        "RegisterGate(String, String, Int32)",
        "RequestCancel(IAgentRunner)",
        "TryCommitApproval(Int64, String, Int32)",
        "WaitForDecisionAsync(String, CancellationToken)",
    ];

    // =====================================================================
    // 1. The ratchets. These pass on the state measured above.
    // =====================================================================

    /// <summary>
    ///     None of the three aggregates declares more members than it did. An
    ///     addition is the only direction that makes the violation worse: every
    ///     implementor and every test double must satisfy the new member, and none
    ///     of the current consumers asked for it.
    /// </summary>
    [Test]
    public async Task Ratchet_NoAggregateDeclaresMoreMembersThanMeasured()
    {
        foreach (AggregateShape aggregate in ReflectedAggregates)
        {
            await Assert.That(Resolve(aggregate.Name) is not null)
                .IsTrue()
                .Because($"{aggregate.FullName} must exist for this ratchet to measure anything. "
                       + "If it does not, the type was renamed or deleted and the MeasuredMemberCount "
                       + "next to it is stale");

            IReadOnlyList<string> measured = MeasureDeclaredMembers(aggregate.Name);

            await Assert.That(measured.Count).IsLessThanOrEqualTo(aggregate.MeasuredMemberCount)
                .Because($"{aggregate.Name} declared {aggregate.MeasuredMemberCount} members when this "
                       + "table was measured and may only hold that line. A larger number is a new "
                       + "member that every implementor and every test double must now satisfy, added to "
                       + "an interface that is already the subject of an unpaid ISP violation (#471). "
                       + $"Declared now ({measured.Count}): "
                       + (measured.Count == 0 ? "(none)" : string.Join(", ", measured)));
        }
    }

    /// <summary>
    ///     <c>IThemeService</c> stays an empty alias. This is the assertion that
    ///     keeps #469's split intact: a member added here is a member that could
    ///     have gone on one of the three roles instead, where only the consumers
    ///     that want it would carry it.
    /// </summary>
    [Test]
    public async Task Ratchet_IThemeService_StaysAnEmptyAliasOverItsThreeRoles()
    {
        await Assert.That(Resolve(ThemeServiceName) is not null)
            .IsTrue()
            .Because("IThemeService must exist to be graded; #469 kept it as the DI alias");

        IReadOnlyList<Type> inherited = Resolve(ThemeServiceName)!
            .GetInterfaces()
            .Where(static i => i.Namespace == "Harbor.Ui.Framework.Services")
            .OrderBy(static i => i.Name, StringComparer.Ordinal)
            .ToList();

        await Assert.That(inherited.Select(static i => i.Name).ToList())
            .IsEquivalentTo(new[] { "IThemeApplier", "IThemeReader", "IThemeWatcher" })
            .Because("#469 split this aggregate into exactly three roles and the whole point of keeping "
                   + "the alias is that it adds a name without adding a surface. A fourth role, or a "
                   + "replacement of one of these, is a decision to re-derive the table for");

        IReadOnlyList<string> own = MeasureDeclaredMembers(ThemeServiceName);
        await Assert.That(own).IsEmpty()
            .Because("a member declared on IThemeService itself is a member reachable through every "
                   + "role, which is precisely the coupling the three roles exist to remove. Put it on "
                   + "the one role that owns it. Declared here: " + string.Join(", ", own));
    }

    /// <summary>
    ///     <c>IReplHost</c>'s per-consumer demand is frozen. The holder set can stay
    ///     at seventeen while a consumer quietly starts calling a member it never
    ///     wanted, and that is the coupling any future split has to undo.
    /// </summary>
    [Test]
    public async Task Ratchet_IReplHostConsumers_AreTheMeasuredOnes()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> measured = MeasureReplHostConsumers();

        ShapeDelta fileDelta = CompareSets(
            "product file that consumes IReplHost",
            ReplHostConsumers.Select(static row => row.File),
            measured.Keys);

        await Assert.That(fileDelta.Regressions).IsEmpty()
            .Because("a new product file consumes IReplHost. A new COMMAND is legitimate — it is a "
                   + "feature — but a new file that merely took a dependency on the wide host is the "
                   + "coupling this issue is about. Add the row here in the same commit. Added: "
                   + string.Join(", ", fileDelta.Regressions));

        await Assert.That(fileDelta.Improvements).IsEmpty()
            .Because("a consumer disappeared: it was properly narrowed, moved, or deleted. All three "
                   + "change what the rest of this file means, so re-measure and drop the row in the "
                   + "same commit. Removed: " + string.Join(", ", fileDelta.Improvements));

        ShapeDelta memberDelta = CompareMemberCalls(measured);
        await Assert.That(memberDelta.Regressions).IsEmpty()
            .Because("a consumer now touches a member it did not touch when measured. Every such call "
                   + "is one more reason that file needs the wide host, which is the opposite of the "
                   + "direction #471 wants. Added: " + string.Join(", ", memberDelta.Regressions));

        await Assert.That(memberDelta.Improvements).IsEmpty()
            .Because("a consumer stopped touching a member, or stopped touching the host. Very likely "
                   + "the split landing; also possibly a rename the scan can no longer see. Either way "
                   + "the row is now wrong. Removed: " + string.Join(", ", memberDelta.Improvements));
    }

    /// <summary>
    ///     <c>IApprovalCoordinator</c> declares the nine signatures the six
    ///     consumers were measured against — counted as distinct signatures, so
    ///     the two <c>RegisterGate</c> and two <c>DecideApproval</c> overloads are
    ///     each their own entry.
    /// </summary>
    [Test]
    public async Task Ratchet_IApprovalCoordinator_DeclaresTheNineMeasuredSignatures()
    {
        IReadOnlyList<string> measured = MeasureApprovalSignatures();

        await Assert.That(measured.Count).IsEqualTo(MeasuredApprovalMembers)
            .Because($"IApprovalCoordinator declared {MeasuredApprovalMembers} distinct member signatures "
                   + "when this table was measured. This is the one slice of #471 whose issue figure is "
                   + "correct, and 6 of its 7 member names are used by exactly one consumer (only "
                   + "RequestCancel is shared), so the split it proposes "
                   + "is well founded. A tenth is a new obligation on the implementor and on every test "
                   + "double. Measured now (" + measured.Count + "): "
                   + (measured.Count == 0 ? "(none)" : string.Join(" | ", measured)));

        // Names, as a SET, so a rename is caught with its replacement in the
        // message rather than only as a count change. The two overload pairs
        // appear twice each on purpose; Distinct() below compares like with like.
        ShapeDelta delta = CompareSets(
            "member name declared on IApprovalCoordinator",
            BaselineApprovalMembers,
            MeasureDeclaredMembers(ApprovalCoordinatorName));

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("a member NAME the nine measured ones do not include. CompareSets prints the added "
                   + "name, which is the thing to look up. Added: " + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a member name left the coordinator. That is a split landing or a deletion, and "
                   + "either way every consumer row above is now stale. Removed: "
                   + string.Join(", ", delta.Improvements));
    }

    /// <summary>
    ///     The signatures are pinned individually, not just counted. The count
    ///     assertion above would still pass if a member were renamed into a
    ///     different one of the same arity, and the two overload PAIRS are exactly
    ///     where a rename hides: <c>RegisterGate(string)</c> becoming
    ///     <c>RegisterGate(string, int)</c> keeps the count at nine.
    /// </summary>
    [Test]
    public async Task Claim_IApprovalCoordinator_TheNineSignaturesAreTheOnesMeasured()
    {
        ShapeDelta delta = CompareSets(
            "signature declared on IApprovalCoordinator",
            ApprovalDistinctSignatures,
            MeasureApprovalSignatures());

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("a distinct SIGNATURE the nine measured ones do not include. Signature-level "
                   + "pinning is what catches an overload whose arity changed — the count would not "
                   + "move. Added: " + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a signature left the coordinator; re-measure the table. Removed: "
                   + string.Join(", ", delta.Improvements));
    }

    /// <summary>
    ///     <c>IReplHost</c> still declares no more than the 31 members measured,
    ///     counted from SOURCE because the type is <c>internal</c> to an
    ///     application this assembly does not reference. See
    ///     <see cref="ReflectedAggregates" /> for why reflection cannot reach it.
    /// </summary>
    [Test]
    public async Task Ratchet_IReplHost_DeclaresNoMoreThanTheThirtyOneMeasuredMembers()
    {
        IReadOnlyList<string> measured = MeasureMembersFromSource(ReplHostName);

        await Assert.That(measured.Count).IsGreaterThan(0)
            .Because($"the source scan found NO members of {ReplHostName}. Either the interface moved, "
                   + "was renamed, or the scan stopped matching — and a ratchet over an empty measurement "
                   + "is green forever, which is the one failure mode a ratchet has to avoid. A non-zero "
                   + "count is also what proves this assertion is looking at the real declaration");

        await Assert.That(measured.Count).IsLessThanOrEqualTo(MeasuredReplHostMembers)
            .Because($"{ReplHostName} declared {MeasuredReplHostMembers} members when this table was "
                   + "measured and may only hold that line. A 32nd is a member every implementor and every "
                   + "test double must satisfy, added to an interface that is already the subject of an "
                   + "unpaid ISP violation (#471), and one that has grown in five of its last six commits. "
                   + $"Declared now ({measured.Count}): " + string.Join(", ", measured));
    }

    /// <summary>
    ///     No product file stores one of these aggregates in a field it never
    ///     reads. This is the axis that found <c>AgentLoop._tokenTracker</c>:
    ///     assigned once, read nowhere, on a hot object, while the four behaviours
    ///     that actually use the tracker were handed the constructor parameter.
    /// </summary>
    [Test]
    public async Task Ratchet_NoAggregateIsStoredInAWriteOnlyField()
    {
        var writeOnly = new List<string>();
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(file) is not { } text)
            {
                continue;
            }

            // Strip ONCE per file. StripAll allocates a fresh array per call, and
            // doing it inside the per-line loop re-stripped the whole file for
            // every candidate line — O(candidates x lines) on a scan that already
            // walks every product file.
            string[] stripped = SourceCommentStripper.StripAll(text.Split('\n'));

            foreach (string line in stripped)
            {
                foreach (Match declaration in WriteOnlyFieldCandidate.Matches(line))
                {
                    string type = declaration.Groups["type"].Value;
                    string field = declaration.Groups["field"].Value;
                    if (ReadsField(stripped, field))
                    {
                        continue;
                    }

                    writeOnly.Add($"{SourceScan.Relative(file)}: {type} {field} is assigned and never read");
                }
            }
        }

        await Assert.That(writeOnly.Distinct(StringComparer.Ordinal).ToList()).IsEmpty()
            .Because("a private field of one of these aggregates is written and never read. That is dead "
                   + "state on a hot object, and it is the concrete cost of this issue: the class names the "
                   + "wide type and pins a reference to it that nothing consults. Forward the CONSTRUCTOR "
                   + "PARAMETER to whoever needs it instead, as AgentLoop's four behaviours already do. "
                   + "Found: " + string.Join("; ", writeOnly.Distinct(StringComparer.Ordinal)));
    }

    // =====================================================================
    // 2. The claim, checked. The re-measurement is the reason this file
    //    exists, so the parts of it that are checkable ARE tests.
    // =====================================================================

    /// <summary>
    ///     The theme surface the alias stands for is the union of three narrow
    ///     roles, and that union is 12 distinct member names — 11 methods plus
    ///     one event. Asserted so the number cannot rot: a role gaining or losing
    ///     a member moves it, visibly.
    /// </summary>
    [Test]
    public async Task Claim_IThemeService_StandsForTwelveMembersAcrossThreeRoles()
    {
        var measured = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string role in new[] { "IThemeReader", "IThemeApplier", "IThemeWatcher" })
        {
            await Assert.That(Resolve(role) is not null)
                .IsTrue()
                .Because($"{role} must exist for IThemeService to be an alias over it rather than a "
                       + "re-implementation of the same members");

            foreach (string member in MeasureDeclaredMembers(role))
            {
                measured.Add(member);
            }
        }

        ShapeDelta delta = CompareSets("member on a theme role", ThemeRoleMembers, measured);

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("a theme role gained a member. That is legitimate on its own — the roles are the "
                   + "right place for one — but it changes the surface the alias stands for, so the "
                   + "count in this file's header has to move with it. Added: "
                   + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a theme role lost a member. Re-measure ThemeRoleMembers and the header count in "
                   + "the same commit. Removed: " + string.Join(", ", delta.Improvements));

        await Assert.That(measured.Count).IsEqualTo(12)
            .Because("the three roles together declared 12 distinct member names when measured — six on "
                   + "IThemeApplier, two on IThemeReader, four on IThemeWatcher — which is the real size "
                   + "of the surface IThemeService aliases. The issue's '11 members' is the roles' method "
                   + "count with the event left out, so it is a real number about a real thing; what is "
                   + "wrong is the attribution. IThemeService itself declares none, and the file it "
                   + "cites is 19 lines long");
    }

    /// <summary>
    ///     <c>IReplHost</c> really is 31 members, counted from source because the
    ///     type is <c>internal</c> to an application this assembly does not
    ///     reference — see <see cref="ReflectedAggregates" />. This is the
    ///     number the issue calls 24, stated so a future reader can check it
    ///     rather than trust it.
    /// </summary>
    [Test]
    public async Task Claim_IReplHost_IsThirtyOneMembers()
    {
        IReadOnlyList<string> measured = MeasureMembersFromSource(ReplHostName);

        await Assert.That(measured.Count).IsEqualTo(MeasuredReplHostMembers)
            .Because("the issue's '24 members' is wrong in a way that matters: it is neither the member "
                   + "count (this one) nor the consumer count (18 product consumers). If this number has "
                   + "moved, the member table and the consumer table both need re-deriving. Measured ("
                   + measured.Count + "): " + string.Join(", ", measured));

        // The "nothing inherited" half is a SOURCE claim for the same reason: a
        // reflection-based check would need the type, and there is no type to
        // reach. A `:` clause after the name is the only way a base can enter.
        string declaration = FindInterfaceDeclaration(ReplHostName);
        string header = declaration[..Math.Min(declaration.Length, 400)];
        int brace = header.IndexOf('{');
        string bases = brace < 0 ? header : header[..brace];

        await Assert.That(bases.Contains(':')).IsFalse()
            .Because("IReplHost declared no base interfaces when measured, so all 31 counted members are "
                   + "its own and none is inherited. A ':' after the type name means a base was added, "
                   + "which would mean the count above is no longer the whole surface and needs "
                   + "re-deriving. Header: " + bases.Trim());
    }

    /// <summary>
    ///     The demand overlaps, which is what makes the four-way split the wrong
    ///     shape for <c>IReplHost</c>. 13 of the 18 measured consumers need
    ///     <c>Bridge</c>, <c>Palette</c> and <c>WakeUp</c> together, so a split that
    ///     gave every consumer its own interface would still hand those 13 a
    ///     common base carrying all three.
    /// </summary>
    [Test]
    public async Task Claim_IReplHost_DemandOverlaps_WhichIsWhyTheFourWaySplitIsTheWrongShape()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> measured = MeasureReplHostConsumers();
        string[] core = ["Bridge", "Palette", "WakeUp"];

        var sharingCore = measured.Keys
            .Where(file => core.All(member => measured[file].Contains(member, StringComparer.Ordinal)))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToList();

        await Assert.That(sharingCore.Count).IsGreaterThanOrEqualTo(13)
            .Because("at least 13 of the measured consumers need Bridge+Palette+WakeUp together. That "
                   + "overlap is the whole reason a four-way split would not narrow them: 13 consumers "
                   + "would still need a shared base carrying three members, so the refactor would move "
                   + "5 consumers and re-impose a base on the other 13. If this count has dropped "
                   + "materially, the split's cost case has changed and should be re-derived. Measured: "
                   + $"{sharingCore.Count} of {measured.Count}");

        // The tail is what IS worth extracting: six members, one consumer each.
        var singles = BaselineReplHostMembers
            .Where(member => measured.Values.Count(used => used.Contains(member, StringComparer.Ordinal)) == 1)
            .OrderBy(static m => m, StringComparer.Ordinal)
            .ToList();

        await Assert.That(singles.Count).IsGreaterThanOrEqualTo(7)
            .Because("the single-consumer tail is the part of IReplHost that is genuinely worth "
                   + "extracting, and it is what the interface's own TODO (IReplHost.cs:19-20, "
                   + "INewSessionHost) is aimed at. Seven when measured: ScrollTimelineToEnd, Composer, "
                   + "RendererPipeline, PanelRegistry, PluginReload, HealthCheck, and Git — the last added "
                   + "by #929, so the tail is GROWING with the interface. If this has collapsed, the tail "
                   + "is gone and the remaining width is a different problem. Measured: "
                   + string.Join(", ", singles));
    }

    /// <summary>
    ///     <c>IApprovalCoordinator</c>'s demand really is non-overlapping — 8 of
    ///     the 9 signatures have exactly one consumer — so the issue's claim about
    ///     THIS interface is the one that holds up. Stated as a test so the
    ///     asymmetry with <see cref="Claim_IReplHost_DemandOverlaps_WhichIsWhyTheFourWaySplitIsTheWrongShape" />
    ///     is a measurement rather than an opinion.
    /// </summary>
    [Test]
    public async Task Claim_IApprovalCoordinator_DemandIsNonOverlapping()
    {
        IReadOnlyDictionary<string, int> fanIn = MeasureApprovalFanIn();
        var singles = fanIn.Where(pair => pair.Value == 1).ToList();

        // Measured: 7 distinct NAMES, of which 6 have exactly one consumer, and
        // the seventh (RequestCancel) has 5. The issue says "8 of the 9" — 8
        // counts the two overload pairs separately, so it is counting signatures
        // where this counts names. Both are true of different units; the
        // name-level figure is the one that matters, because a consumer wanting
        // both RegisterGate overloads is ONE consumer, and the consumer count is
        // what has to justify a split.
        await Assert.That(singles.Count).IsGreaterThanOrEqualTo(6)
            .Because("6 of IApprovalCoordinator's 7 distinct member NAMES are used by exactly one "
                   + "consumer, and only RequestCancel is shared (5 callers). That is what 'proven by "
                   + "consumers' is supposed to mean, and it is what makes the three-interface split well "
                   + "founded — the sharpest contrast in #471 with IReplHost, where 13 of 18 consumers "
                   + "share a three-member base. If this has dropped, the split's justification has gone "
                   + "with it. Measured: " + fanIn.Count + " names, " + singles.Count
                   + " with a single consumer");
    }

    // =====================================================================
    // 3. Non-vacuity. Four synthetic worse states through the SAME
    //    comparators the ratchets above grade with.
    // =====================================================================

    /// <summary>
    ///     Control: a 32nd member on <c>IReplHost</c> is reported as a regression.
    /// </summary>
    [Test]
    public async Task Control_ANewMemberOnTheReplHostIsReportedAsARegression()
    {
        var degraded = new List<string>(BaselineReplHostMembers) { "ScreenshotTheScreen" };

        ShapeDelta delta = CompareSets("member declared on IReplHost", BaselineReplHostMembers, degraded);

        await Assert.That(delta.Regressions)
            .IsEquivalentTo(new[] { "member declared on IReplHost: ScreenshotTheScreen" })
            .Because("this is the claim that the member ratchet is load-bearing, made checkable: a member "
                   + "added to the aggregate must come out of the SAME comparator as a regression. If this "
                   + "fails, the comparator no longer reports growth and "
                   + "Ratchet_NoAggregateDeclaresMoreMembersThanMeasured is green for the wrong reason");

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("adding a member removes nothing");
    }

    /// <summary>
    ///     Control: a member added to the <c>IThemeService</c> alias is reported.
    ///     The alias is the one place where the regression is invisible to a
    ///     member-count check alone, because 1 is still "small".
    /// </summary>
    [Test]
    public async Task Control_AMemberOnTheThemeAliasIsReportedAsARegression()
    {
        ShapeDelta delta = CompareSets("member declared on IThemeService", [], ["LoadHdsTheme"]);

        await Assert.That(delta.Regressions)
            .IsEquivalentTo(new[] { "member declared on IThemeService: LoadHdsTheme" })
            .Because("a member on the alias is reachable through all three roles, which is the coupling "
                   + "#469 removed. The control proves the empty-alias assertion can see it");

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("adding a member removes nothing");
    }

    /// <summary>
    ///     Control: a new <c>IReplHost</c> consumer is reported, and one that
    ///     starts calling a member it never called is reported BY THE PER-FILE
    ///     comparator — the file set is unchanged in that scenario, so only the
    ///     second can catch it.
    /// </summary>
    [Test]
    public async Task Control_ANewReplHostConsumerAndAnExtraMemberAreBothReported()
    {
        const string Consumer = "apps/Harbor.App.Cli/Repl/Commands/StorageCommand.cs";

        var newFile = new List<string>(ReplHostConsumers.Select(static row => row.File))
        {
            "apps/Harbor.App.Cli/Repl/Commands/SomeNewCommand.cs",
        };
        ShapeDelta fileDelta = CompareSets("product file that consumes IReplHost", newFile, newFile);
        await Assert.That(fileDelta.Regressions).IsEmpty()
            .Because("comparing the table to itself must be silent; this is the control's own precondition, "
                   + "and it is what makes the next assertion meaningful");

        // The comparator takes the WHOLE measurement, so the degraded state has
        // to be the baseline with ONE row changed — not just that row. Passing a
        // single-entry dictionary makes every other file look removed, which
        // drowns the one regression this control exists to show in a hundred
        // spurious improvements.
        var degraded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Consumer] = ["Bridge", "ConfigStore", "Palette", "SessionStore", "WakeUp"],
        };
        foreach (ReplHostConsumerBaseline row in ReplHostConsumers)
        {
            if (!degraded.ContainsKey(row.File))
            {
                degraded[row.File] = row.Members;
            }
        }

        ShapeDelta memberDelta = CompareMemberCalls(degraded);

        await Assert.That(memberDelta.Regressions)
            .IsEquivalentTo(new[] { $"member called in {Consumer}: SessionStore" })
            .Because("the file set is unchanged here, so only the per-file comparator can catch it. This is "
                   + "the control that proves Ratchet_IReplHostConsumers_AreTheMeasuredOnes is load-bearing: "
                   + "if the per-file comparison were dropped, this state would pass");

        await Assert.That(memberDelta.Improvements).IsEmpty()
            .Because("the scenario only adds a call");
    }

    /// <summary>
    ///     Control, and the live defect: a write-only field of one of these
    ///     aggregates is found by the SAME predicate the ratchet grades with. This
    ///     is the shape <c>AgentLoop._tokenTracker</c> had — declared, assigned
    ///     once, read zero times — and it is why that axis exists.
    /// </summary>
    [Test]
    public async Task Control_AWriteOnlyAggregateFieldIsFound()
    {
        const string Source = """
            namespace Harbor.Application;
            public sealed class Loop
            {
                private readonly Harbor.Abstractions.Sessions.ITokenTracker _tokenTracker;
                public Loop(Harbor.Abstractions.Sessions.ITokenTracker tokenTracker)
                {
                    _tokenTracker = tokenTracker;
                }
            }
            """;

        await Assert.That(IsWriteOnly(Source, "_tokenTracker")).IsTrue()
            .Because("this is the exact shape AgentLoop had, and the exact reason the write-only axis is in "
                   + "this file. If the predicate stops recognising it, the axis is green for the wrong "
                   + "reason and the dead field can come back");

        // The control has to be specific, not just true: the same field WITH a
        // read must not be reported, or the axis would demand the field be
        // deleted from every class that legitimately holds one.
        const string WithRead = """
            namespace Harbor.Application;
            public sealed class Loop
            {
                private readonly Harbor.Abstractions.Sessions.ITokenTracker _tokenTracker;
                public Loop(Harbor.Abstractions.Sessions.ITokenTracker tokenTracker)
                {
                    _tokenTracker = tokenTracker;
                }
                public int Total() => _tokenTracker.GetStats().TotalInputTokens;
            }
            """;

        await Assert.That(IsWriteOnly(WithRead, "_tokenTracker")).IsFalse()
            .Because("a field that is read is not dead state. Without this half the axis would fire on "
                   + "TuiEffectHost's _coordinator and InProcessHarborClient's _coordinator, which are both "
                   + "genuinely read, and a guard that cries wolf is a guard that gets deleted");
    }

    // =====================================================================
    // Measurement.
    // =====================================================================

    /// <summary>
    ///     Resolves a Harbor type by name across every loaded Harbor assembly, or
    ///     <see langword="null" /> when no assembly declares it. By name, not by
    ///     <c>typeof</c>, so a rename produces a readable failure rather than a
    ///     build error.
    /// </summary>
    private static Type? Resolve(string simpleName)
    {
        foreach (var asm in ArchitectureTestHelpers.LoadHarborAssemblies().Values)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type.Name == simpleName)
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The aggregate's OWN declared members, ordinal-sorted.
    ///     <c>DeclaredOnly</c> is the load-bearing flag: it is what makes
    ///     <c>IThemeService</c> measure 0 rather than the 12 it inherits, and it is
    ///     what keeps an inherited member from being counted twice on a type that
    ///     does declare some of its own.
    /// </summary>
    private static IReadOnlyList<string> MeasureDeclaredMembers(string typeName)
    {
        if (Resolve(typeName) is not { } type)
        {
            return [];
        }

        return
        [
            .. type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static m => m.MemberType
                                  is MemberTypes.Method
                                    or MemberTypes.Property
                                    or MemberTypes.Event)
                .Select(static m => m.Name)
                // Reflection over a PROPERTY or an EVENT also surfaces its
                // accessors as methods — `string Current { get; }` yields both
                // the PropertyInfo "Current" and the MethodInfo "get_Current",
                // and `event E` yields "add_E" and "remove_E". Counting those
                // would report IThemeReader as four members instead of two, and
                // would make a property look like it gained a member when it
                // gained nothing. Keeping only the PropertyInfo/EventInfo and
                // dropping accessor-shaped method names is what makes this a
                // count of MEMBERS rather than a count of IL.
                .Where(static n => !IsAccessorName(n))
                .OrderBy(static n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Whether a reflected member name is a property or event ACCESSOR rather
    ///     than the member itself: the compiler's fixed prefixes.
    /// </summary>
    private static bool IsAccessorName(string name) =>
        name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal);

    /// <summary>
    ///     <c>IApprovalCoordinator</c>'s distinct member SIGNATURES. Counting by
    ///     name alone would report 7, not 9, because two members are overload
    ///     pairs; a de-overload would then read as the interface shrinking when
    ///     it is a signature change every implementor has to re-check.
    ///     <para>
    ///         The rendering is <c>Name(ParameterType.Name, ...)</c>, so the table
    ///         reads <c>RegisterGate(String, String, Int32)</c> rather than the
    ///         source's <c>RegisterGate(string, string, int)</c>. That is
    ///         <see cref="ParameterInfo.ParameterType" />'s own vocabulary and it is
    ///         stable across a parameter RENAME, which is the property worth having:
    ///         renaming a parameter is not an interface change, and a guard that
    ///         reddened on one would be a guard people learn to route around.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> MeasureApprovalSignatures()
    {
        if (Resolve(ApprovalCoordinatorName) is not { } type)
        {
            return [];
        }

        var signatures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            string parameters = string.Join(
                ", ",
                member.GetParameters().Select(static p => p.ParameterType.Name));
            signatures.Add($"{member.Name}({parameters})");
        }

        return [.. signatures];
    }

    /// <summary>
    ///     How many measured consumers touch each <c>IApprovalCoordinator</c>
    ///     member, by NAME over the product trees. Overload pairs collapse here on
    ///     purpose — the fan-in question is "how many files want this capability",
    ///     and <c>CellForgePermissionAsker</c> wanting both <c>RegisterGate</c>
    ///     overloads is one consumer, not two.
    /// </summary>
    private static IReadOnlyDictionary<string, int> MeasureApprovalFanIn()
    {
        var fanIn = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in MeasureDeclaredMembers(ApprovalCoordinatorName)
                     .Distinct(StringComparer.Ordinal))
        {
            fanIn[name] = CountProductCallers(name);
        }

        return fanIn;
    }

    /// <summary>
    ///     Product files that name <c>IReplHost</c> and touch at least one member,
    ///     mapped to the members they touch. Name-anchored rather than
    ///     receiver-agnostic, and deliberately so: its members are ordinary names
    ///     (<c>Store</c>, <c>Agent</c>, <c>Bridge</c>, <c>Toggle</c>) that a
    ///     receiver-agnostic scan would match all over the CLI. The implementor and
    ///     the test doubles are excluded — they must satisfy the whole surface
    ///     whatever their bodies call, which is the COST this ratchet measures, not
    ///     the demand a split would serve.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MeasureReplHostConsumers()
    {
        var measured = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var nameAnchored = new Regex(@"\bIReplHost\b", RegexOptions.Compiled);

        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            string relative = SourceScan.Relative(path);
            if (IsIReplHostImplementor(relative)
                || relative.Contains("/tests/", StringComparison.Ordinal)
                || relative.EndsWith("/IReplHost.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var members = new SortedSet<string>(StringComparer.Ordinal);
            bool namesType = false;
            foreach (string line in SourceCommentStripper.StripAll(text.Split('\n')))
            {
                if (nameAnchored.IsMatch(line))
                {
                    namesType = true;
                }

                foreach (string member in BaselineReplHostMembers)
                {
                    if (MemberAccessIsMatch(line, member))
                    {
                        members.Add(member);
                    }
                }
            }

            // A row must EXERCISE the interface, not merely name it. Three files
            // name IReplHost and use none of it: the declaration itself,
            // ReplCommandContext (a record whose Host property is a constructor
            // parameter), and the IReplHost.cs doc prose. Grading them would add
            // permanently-empty rows that could never change, which is the same
            // reason the implementor and the test doubles are excluded.
            if (namesType && members.Count > 0)
            {
                measured[relative] = [.. members];
            }
        }

        return measured;
    }

    /// <summary>
    ///     Whether a file is the one class that IMPLEMENTS <c>IReplHost</c>. Kept
    ///     as a name check on the single known implementor rather than a scan for
    ///     the <c>: IReplHost</c> base list, because the base list is exactly what
    ///     a future narrow-interface refactor would rewrite.
    /// </summary>
    private static bool IsIReplHostImplementor(string relative) =>
        relative.EndsWith("/CellForgeReplRunner.cs", StringComparison.Ordinal);

    /// <summary>
    ///     How many product files call or property-read a member name. Used for
    ///     the coordinator's fan-in, where the member names are distinctive enough
    ///     that a receiver-agnostic scan does not over-match the way it would on
    ///     <c>IReplHost</c>'s.
    /// </summary>
    private static int CountProductCallers(string member)
    {
        var pattern = new Regex(@"\.\s*" + Regex.Escape(member) + @"\b", RegexOptions.Compiled);
        int count = 0;
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            if (SourceCommentStripper.StripAll(text.Split('\n')).Any(pattern.IsMatch))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Compares one named set against its baseline and labels each delta item
    ///     with what it is, so a failure message reads as a sentence. Every
    ///     comparison in this file goes through here, which is what lets the
    ///     <c>Control_</c> tests claim the ratchets are load-bearing rather than
    ///     merely asserting it.
    /// </summary>
    private static ShapeDelta CompareSets(string label, IEnumerable<string> baseline, IEnumerable<string> actual)
    {
        var expected = baseline.ToHashSet(StringComparer.Ordinal);
        var observed = actual.ToHashSet(StringComparer.Ordinal);

        var regressions = new List<string>();
        foreach (string item in observed.Except(expected, StringComparer.Ordinal)
                     .OrderBy(static item => item, StringComparer.Ordinal))
        {
            regressions.Add($"{label}: {item}");
        }

        var improvements = new List<string>();
        foreach (string item in expected.Except(observed, StringComparer.Ordinal)
                     .OrderBy(static item => item, StringComparer.Ordinal))
        {
            improvements.Add($"{label}: {item}");
        }

        return new ShapeDelta(regressions, improvements);
    }

    /// <summary>
    ///     The per-consumer half: compares each measured file's member set against
    ///     its row, leaving the file set to
    ///     <see cref="Ratchet_IReplHostConsumers_AreTheMeasuredOnes" />'s first
    ///     comparison.
    /// </summary>
    private static ShapeDelta CompareMemberCalls(IReadOnlyDictionary<string, IReadOnlyList<string>> actual)
    {
        var baseline = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (ReplHostConsumerBaseline row in ReplHostConsumers)
        {
            baseline[row.File] = row.Members;
        }

        var regressions = new List<string>();
        var improvements = new List<string>();

        IEnumerable<string> files = baseline.Keys
            .Union(actual.Keys, StringComparer.Ordinal)
            .OrderBy(static file => file, StringComparer.Ordinal);

        foreach (string file in files)
        {
            ShapeDelta delta = CompareSets(
                $"member called in {file}",
                baseline.TryGetValue(file, out var expected) ? expected : Array.Empty<string>(),
                actual.TryGetValue(file, out var observed) ? observed : Array.Empty<string>());
            regressions.AddRange(delta.Regressions);
            improvements.AddRange(delta.Improvements);
        }

        return new ShapeDelta(regressions, improvements);
    }

    /// <summary>
    ///     Matches <c>&lt;anything&gt;.Member</c> or <c>&lt;anything&gt;?.Member</c> for
    ///     one member name, cached per name. The name is inserted into the pattern
    ///     with <see cref="Regex.Escape(string)" /> because a member name is source:
    ///     without it a name containing regex metacharacters would change the
    ///     pattern's meaning. Written against the line so a file cannot appear to
    ///     USE an interface merely by DECLARING the member.
    /// </summary>
    private static bool MemberAccessIsMatch(string line, string member) =>
        MemberAccessPatterns.GetOrAdd(member, static name => new Regex(
            @"\??\.\s*" + Regex.Escape(name) + @"\b", RegexOptions.Compiled))
            .IsMatch(line);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> MemberAccessPatterns = new(StringComparer.Ordinal);

    /// <summary>
    ///     Builds the write-only field pattern from the four name constants.
    ///     Built by a method, not inline, so it reads the constants AFTER their
    ///     static initialisers have run — an interpolated-string field initialiser
    ///     would be a silent empty alternation, and an empty alternation here
    ///     means the axis never fires and the guard is green forever.
    /// </summary>
    private static Regex BuildWriteOnlyCandidate()
    {
        string types = string.Join(
            '|',
            new[] { ReplHostName, ThemeServiceName, ApprovalCoordinatorName, TokenTrackerName }
                .Select(static name => Regex.Escape(name)));

        return new Regex(
            @"\bprivate\s+(?:readonly\s+)?(?<type>" + types + @")\??\s+(?<field>_[A-Za-z_]\w*)\s*;",
            RegexOptions.Compiled);
    }

    /// <summary>
    ///     A private field of one of the governed aggregates, capturing the type
    ///     and the field name. Anchored on <c>private</c> so a parameter, a local
    ///     or a public property cannot match — the defect is specifically a FIELD
    ///     that outlives the constructor and is never consulted.
    /// </summary>
    private static readonly Regex WriteOnlyFieldCandidate = BuildWriteOnlyCandidate();

    /// <summary>
    ///     The text from an interface's name to the end of the line it is declared
    ///     on, or <see cref="string.Empty" /> when no product file declares it.
    ///     Used to read the BASE LIST, which is the one part of an internal
    ///     interface's shape that reflection cannot supply.
    /// </summary>
    private static string FindInterfaceDeclaration(string typeName)
    {
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            foreach (string line in SourceCommentStripper.StripAll(text.Split('\n')))
            {
                int at = line.IndexOf("interface " + typeName, StringComparison.Ordinal);
                if (at >= 0)
                {
                    return line[at..];
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///     Counts the members an interface DECLARES, read from source rather than
    ///     reflection. This exists for <c>IReplHost</c>, which is
    ///     <c>internal</c> to an application this test assembly does not
    ///     reference, so there is no loaded type to inspect — see
    ///     <see cref="ReflectedAggregates" />.
    ///     <para>
    ///         Comment-stripped first, then the interface body is taken by brace
    ///         matching and each line classified: a <c>Name(</c> is a method, an
    ///         <c>event ... Name;</c> is an event, a <c>Name { get</c> is a
    ///         property. Line-oriented on purpose — an interface body is one
    ///         declaration per line in this codebase, and a multi-line
    ///         declaration would need a statement parser, which is the wrong
    ///         instrument for a count.
    ///     </para>
    ///     <para>
    ///         The property form is the load-bearing part. A naive scan that only
    ///         looked for <c>(</c> would report IReplHost's 10 methods and miss its
    ///         21 properties — 10 instead of 31 — and that is the direction a
    ///         measurement error hides in.
    ///     </para>
    /// </summary>
    /// <returns>The declared member names, ordinal-sorted. Empty when not found.</returns>
    private static IReadOnlyList<string> MeasureMembersFromSource(string typeName)
    {
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            string stripped = SourceScan.StripComments(text);
            int declaration = stripped.IndexOf(
                "interface " + typeName,
                StringComparison.Ordinal);
            if (declaration < 0)
            {
                continue;
            }

            int open = stripped.IndexOf('{', declaration);
            if (open < 0)
            {
                return [];
            }

            var members = new SortedSet<string>(StringComparer.Ordinal);
            int depth = 0;
            foreach (string raw in stripped[open..].Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // Classify BEFORE tracking depth, and only once inside the body.
                // The order is load-bearing: `IAgent Agent { get; }` both opens
                // and closes a brace on one line, so a scan that tracked depth
                // first and `continue`d on the closing brace would skip every
                // property in the interface and report IReplHost's 10 methods
                // as its whole surface.
                if (depth >= 1)
                {
                    ClassifyMemberLine(line, members);
                }

                depth += line.Count('{') - line.Count('}');

                // depth back to 0 means the closing brace of the interface body.
                if (depth <= 0)
                {
                    break;
                }
            }

            return [.. members];
        }

        return [];
    }

    /// <summary>
    ///     Classifies one comment-stripped line of an interface body into a member
    ///     name, or ignores it. Split out so the three shapes are readable side
    ///     by side — a count this load-bearing should not hide its cases inside a
    ///     loop body.
    /// </summary>
    private static void ClassifyMemberLine(string line, SortedSet<string> members)
    {
        Match eventMatch = EventDeclaration.Match(line);
        if (eventMatch.Success)
        {
            members.Add(eventMatch.Groups["name"].Value);
            return;
        }

        Match methodMatch = MethodDeclaration.Match(line);
        if (methodMatch.Success)
        {
            members.Add(methodMatch.Groups["name"].Value);
            return;
        }

        Match propertyMatch = PropertyDeclaration.Match(line);
        if (propertyMatch.Success)
        {
            members.Add(propertyMatch.Groups["name"].Value);
        }
    }

    /// <summary>
    ///     An interface method: an identifier immediately followed by
    ///     <c>(</c>, with an optional generic argument list between them so a
    ///     generic method cannot hide. The name is the LAST identifier before
    ///     the paren, which is why the return type is consumed first.
    /// </summary>
    private static readonly Regex MethodDeclaration = new(
        @"(?:[A-Za-z_][\w\.<>,\?\[\]]*\s+)?(?<name>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A property: an identifier followed by <c>{ get</c>. Anchored on
    ///     <c>{ get</c> rather than a bare <c>;</c> so a method is not also
    ///     counted as a property.
    /// </summary>
    private static readonly Regex PropertyDeclaration = new(
        @"(?:[A-Za-z_][\w\.<>,\?\[\]]*\s+)?(?<name>[A-Za-z_]\w*)\s*\{\s*get",
        RegexOptions.Compiled);

    /// <summary>An interface event: <c>event T Name;</c>.</summary>
    private static readonly Regex EventDeclaration = new(
        @"\bevent\s+[A-Za-z_][\w\.<>,\?\[\]]*\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    ///     Whether <paramref name="field" /> is assigned and never read, over
    ///     already-stripped source. A read is any
    ///     occurrence that is neither the declaration nor an assignment target, so
    ///     this is a question about the TEXT and nothing else — no compilation, no
    ///     semantic model, which is what lets it run over the whole product tree
    ///     cheaply.
    /// </summary>
    private static bool IsWriteOnly(string strippedSource, string field) =>
        CountReads(strippedSource.Split('\n'), field) <= 0;

    /// <summary>
    ///     The predicate behind both <see cref="IsWriteOnly" /> and
    ///     <see cref="ReadsField" />. They used to be two byte-identical copies;
    ///     two copies is two things to keep in step, and the copy that drifts is
    ///     the one nobody reads.
    /// </summary>
    /// <returns>How many times the field is READ in the stripped source.</returns>
    private static int CountReads(string[] strippedLines, string field)
    {
        var pattern = new Regex(@"\b" + Regex.Escape(field) + @"\b", RegexOptions.Compiled);
        int occurrences = 0;
        int assignments = 0;

        foreach (string raw in strippedLines)
        {
            string line = raw.Trim();
            foreach (Match hit in pattern.Matches(line))
            {
                occurrences++;
                string after = line[(hit.Index + hit.Length)..].TrimStart();

                // `x = y` is a write. `x == y` and `x != y` are READS, which is
                // why the second test is there: a field compared in a guard is
                // being used, and calling that dead would be a false positive
                // that trains people to ignore this axis.
                if (after.StartsWith('=')
                    && !after.StartsWith("==", StringComparison.Ordinal))
                {
                    assignments++;
                }
            }
        }

        // The declaration itself is one occurrence; every assignment target is
        // one more. Anything left over is a read.
        return occurrences - assignments - 1;
    }

    /// <summary>
    ///     <see cref="IsWriteOnly" /> over a whole file's already-stripped lines,
    ///     for a field located by <see cref="WriteOnlyFieldCandidate" />. The type
    ///     is not a parameter: the question is whether THIS field is read, and
    ///     the declaration already established what type it holds.
    /// </summary>
    private static bool ReadsField(string[] strippedLines, string field) =>
        CountReads(strippedLines, field) > 0;

}
