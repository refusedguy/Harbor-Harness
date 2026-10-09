// AotBlockDemotionRules.cs — issue #747, "the AOT block claims analysis passes
// cleanly while demoting IL2026 to a warning".
//
// WHAT THE BLOCK ACTUALLY SAYS
// ---------------------------
// apps/Harbor.App.Cli/Harbor.App.Cli.csproj, the PropertyGroup conditioned on
// '$(HarborWithAot)' == 'true', set ten properties. Three of them decide
// whether a trim/AOT diagnostic fails the build:
//
//   <ILLinkTreatWarningsAsErrors>false</ILLinkTreatWarningsAsErrors>
//   <NativeAOTErrorOnWarningsAsErrors>false</NativeAOTErrorOnWarningsAsErrors>
//   <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026;IL3050;IL2104;IL3053</WarningsNotAsErrors>
//
// and the comment above it read, in full:
//
//   "… so AOT analysis passes cleanly. Only third-party assemblies that emit
//    their own trimming warnings (MemoryPack, MessagePack) are suppressed."
//
// WHY THAT IS A LIE ABOUT THE BLOCK, NOT A LIE ABOUT SOMETHING ELSE
// ----------------------------------------------------------------
// The available defence is that the claim was scoped to something narrower —
// "clean" for first-party code, "suppressed" only for third-party — and that
// only the neighbouring sentence made it look false. Reading the block kills
// that defence:
//
//   * The claim has no narrower scope to retreat to. "AOT analysis passes
//     cleanly" is a claim about the whole project, and the block's own
//     property demotes four of the diagnostics that would falsify it.
//     TreatWarningsAsErrors is true repo-wide (Directory.Build.props), so
//     each of those four entries is load-bearing: without it this
//     configuration would fail on code that ships today. The block was
//     configured NOT to require a clean analysis while claiming one.
//
//   * `WarningsNotAsErrors` is not suppression and cannot express a
//     third-party scope. It is a per-project COMPILER property: it demotes
//     diagnostics reported in this compilation. `NoWarn` is the property that
//     suppresses, and this csproj already uses it for the S6668 Sonar
//     exemption above the block — so the file distinguished the two and this
//     sentence used the wrong word. And IL2026/IL3050 are exactly the
//     first-party "this call site will not survive trimming" diagnostics; no
//     property here can say "third-party only". The two properties that DO
//     govern ILLink/ILC-time warnings about referenced assemblies are the two
//     set to `false`, i.e. explicitly "do not fail".
//
// The substance of the old claim was not fiction — fourteen `.cs` files use
// JsonSerializerContext and the provider payloads are hand-written over
// Utf8JsonWriter — which is exactly why it survived: it read true at a glance
// and false where it counted. The fix is to make the text true, not to delete
// the list; #413 owns the list and that issue's own "no blanket NoWarn" rules
// out the quiet repair.
//
// THE RULE THAT WAS WRITTEN, FAILED, AND WAS DELETED
// --------------------------------------------------
// This file first shipped a seventh rule: the block's comment may not contain
// the words "passes cleanly" (nor five near-synonyms) while it demotes a
// diagnostic. It was deleted before this file reached CI, and the reason is
// the most useful thing here.
//
// The rule FAILED AGAINST THE HONEST COMMENT. Saying "this block does not mean
// AOT analysis passes cleanly" — the first sentence of the replacement text —
// contains the banned phrase, so the guard punished the fix and permitted the
// lie. A check that a truthful sentence fails and a false one can be made to
// pass by rewording is not enforcing a state; it is enforcing a wording. That
// is the trap the brief for this issue named, in its other half: a guard that
// cannot be satisfied by changing the thing it is about is a guard on text.
//
// So there is deliberately NO rule here about what the comment may claim. The
// contradiction between "analysis passes cleanly" and four demoted diagnostics
// is a fact about the XML, and the two rules that read the XML
// (Every_Demoted_Diagnostic_Has_A_Row_With_A_Reason, and the non-emptiness
// rule) are the whole enforcement of it: while the list is non-empty, every
// entry in it must be individually argued for in this file, which is a
// stronger requirement than any sentence could be. When #413 empties the list
// these rules go quiet and the claim becomes true, with nothing deleted.
//
// WHAT IS NOT CLAIMED HERE, AND WHERE IT WENT
// ------------------------------------------
// This file does NOT decide whether the code is AOT-clean, and is not allowed to
// pretend to. That needs an actual `dotnet publish -p:HarborWithAot=true` plus a
// run of the artifact it produces.
//
// UNTIL #413 that publish did not exist. No workflow in .github/workflows/ set
// HarborWithAot or HARBOR_MINIMAL, so the block was never evaluated by any build
// in the repo and "analysis passes cleanly" was not merely unverified — it was
// UNFALSIFIABLE, because no process existed whose output could contradict it.
// The guard below used to enforce that absence (`No_Workflow_Enables_The_Aot_Block`),
// which is why it had to change rather than merely be deleted: a guard that
// asserts a hole exists goes RED on the day the hole is filled, so it made
// fixing the problem a regression. `A_Workflow_Enables_The_Aot_Block` asserts
// the opposite now — the publish happens, and the demotion list is reconciled
// against what the publish actually emitted.
//
// The publish gate itself is the `aot-publish` job in .github/workflows/ci.yml,
// and the inventory it compares against is .github/aot-warning-baseline.txt.
// Neither is reachable from this project (it would have to shell out to a
// 10-20 minute ILC pass, and this file runs on every build), so what this file
// CAN do is close the cheap half honestly: prove a workflow turns the block on,
// and prove the two tables that describe the concessions — the csproj's demotion
// list and the committed inventory — name the same set of diagnostics.
//
// What remains unproven by construction, and is stated rather than papered over:
// whether that inventory still matches a real publish. Only the job can say, and
// it says it on every PR.
//
// THE TABLE IS IN THE HOUSE SHAPE
// -------------------------------
// The IDs are the permissions, so the table is keyed per ID and every row
// states why through ExemptionReason — the one place in this project that
// decides what counts as a reason. That is #747's acceptance criterion 2:
// "triaged item-by-item (each ID named, each either fixed or justified in its
// own row) rather than carried as a group". Table_Rows_Are_Not_Stale is the
// liveness half, copied from ProviderPayloadSerializationRules: fix the code,
// delete the row, or the row becomes a permission for a problem that no longer
// exists. The_Block_Comment_Names_Every_Demoted_Diagnostic is the tie back to
// the file the issue is about — the sentence that outlived four edits to the
// list could not have named what the list gave up, and now it has to.
//
// NON-VACUITY
// -----------
// A rule whose subject is empty is green for no reason: the trap
// EnforcerIntegrityTests and ProviderPayloadSerializationRules both document,
// and ProviderPayloadSerializationRules' planted-offender control earned its
// place on its first CI run because the matcher had been matching nothing.
// Closed in four places:
//   * The_Demoted_Diagnostic_Set_Is_NonEmpty — the subject exists.
//   * The_Real_Block_Is_Found_And_Parses — the reader is pointed at the real
//     file, and the premise that makes the demotion load-bearing
//     (TreatWarningsAsErrors=true repo-wide) is read, not assumed.
//   * A_Workflow_Enables_The_Aot_Block — the publish the "not triaged" rows
//     were waiting for is READ from .github/workflows rather than assumed, in
//     the positive direction: it now fails when no workflow turns the block on,
//     which is the state the rows' premise describes.
//   * NonVacuity_The_Reader_Catches_An_Unlisted_Diagnostic_And_Ignores_A
//     _Property_Reference — synthetic csproj text. The `$(…)` case matters
//     most: a reader that does not filter it out demands a table row for a
//     literal string that is not a diagnostic, and the guard becomes
//     unfalsifiable in the other direction.
//
// SCOPE
// -----
// apps/Harbor.App.Cli only — the one project with a HarborWithAot block. Not
// a repo-wide AOT sweep: IsAotCompatible is set on four src projects
// (Harbor.DesignSystem, Harbor.Ui.Framework.Rendering, Harbor.Tui.CellForge,
// Harbor.Tui.CellForge.Engine) and those already take IL2026/IL3050 as ERRORS
// on every build, so they are the enforced set and this rule has nothing to add
// to them. Enumerating every csproj here would duplicate the architecture
// tests' project inventory for no additional subject.

using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #747: the CLI's NativeAOT block may not carry diagnostic IDs as an
///     unexplained group. Each one it demotes is named in a row that states why,
///     and the block's own comment has to name them back.
/// </summary>
public sealed class AotBlockDemotionRules
{
    /// <summary>Repo-relative path of the project carrying the AOT block.</summary>
    internal const string CliProjectPath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    /// <summary>Repo-relative directory holding the workflows that would enable the block.</summary>
    internal const string WorkflowDirectoryPath = ".github/workflows";

    /// <summary>
    ///     The MSBuild property whose value the block uses to make a diagnostic
    ///     non-fatal — <c>WarningsNotAsErrors</c>, and specifically NOT
    ///     <c>NoWarn</c>. This file reads the demotion set, and the two are
    ///     different claims about a build (warning kept vs. warning gone).
    /// </summary>
    internal const string DemotionProperty = "WarningsNotAsErrors";

    /// <summary>
    ///     The tokens that switch the AOT block on. A workflow that names either
    ///     one evaluates the block, which is what makes the trim/AOT diagnostics
    ///     it demotes observable at all.
    /// </summary>
    internal static readonly string[] AotEnableTokens = ["HarborWithAot", "HARBOR_MINIMAL"];

    /// <summary>
    ///     Every diagnostic the block demotes, keyed by ID, each with the argument
    ///     for tolerating that one specifically.
    /// </summary>
    /// <remarks>
    ///     The "not triaged" wording is STALE as of #413 and is kept only until the
    ///     first <c>aot-publish</c> run reports what the publish actually emits. It
    ///     was true when written (no workflow evaluated the block) and became false
    ///     when the publish landed, which is exactly what
    ///     <see cref="A_Workflow_Enables_The_Aot_Block" /> now detects.
    ///     The fourth is <c>IL3053</c>, which could not be matched to any documented
    ///     diagnostic from this checkout; its row records an unexplained entry
    ///     honestly instead of dressing it up. Removing a row is the correct response
    ///     to removing the ID from the csproj, and
    ///     <see cref="Table_Rows_Are_Not_Stale" /> enforces that direction.
    /// </remarks>
    internal static readonly Dictionary<string, ExemptionReason.Row> KnownDemotions = new(StringComparer.Ordinal)
        {
            ["IL2026"] = new(
                "RequiresUnreferencedCode. Not triaged: no workflow in .github/workflows/ sets "
                + "HarborWithAot, so no build in this repo has ever emitted it for this project and "
                + "there is nothing to triage against. #413 owns the first full publish that will "
                + "produce real counts.",
                "#413"),
            ["IL3050"] = new(
                "RequiresDynamicCode. Not triaged for the same reason as IL2026, and it is the one "
                + "with a known live source: -p:HarborWithAot=true does not set HarborWithPlugins, so "
                + "Harbor.Plugins.Compilation (Roslyn) stays in the compile. #413 owns the triage.",
                "#413"),
            ["IL2104"] = new(
                "ILLink per-assembly trim-warning rollup, so a warning inside a referenced assembly "
                + "surfaces against that assembly rather than against the call site. Not triaged, and "
                + "not separable from IL2026/IL3050 until a publish has produced a rollup to read.",
                "#413"),
            ["IL3053"] = new(
                "UNIDENTIFIED. Carried since before #747 and not matched to any documented trim or AOT "
                + "diagnostic reachable from this checkout, so this row records an unexplained entry "
                + "rather than a justification. Either #413 names it with a publish in hand or the "
                + "list loses it; a row that cannot explain its own ID is the group carriage this "
                + "issue was opened about.",
                "#413"),
        };

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     Non-vacuity: the subject exists. If the demotion list is emptied — #413's
    ///     direction — this fails and forces the table to be revisited rather than
    ///     leaving four rows arguing for concessions that are no longer made.
    /// </summary>
    [Test]
    public async Task The_Demoted_Diagnostic_Set_Is_NonEmpty()
    {
        IReadOnlyList<string> demoted = DemotedDiagnostics(CliCsprojXml());

        await Assert.That(demoted).IsNotEmpty()
            .Because("a guard over an empty demotion set is green for no reason. If the set is now "
                   + "empty, delete KnownDemotions with it — that is #413 landing, and it should "
                   + "arrive as a deliberate edit here rather than as four stale rows.");
    }

    /// <summary>
    ///     Every ID the block demotes has its own row. This is the rule that stops
    ///     the list from being extended as a group: appending a fifth ID to the
    ///     csproj without a fifth argument fails here.
    /// </summary>
    [Test]
    public async Task Every_Demoted_Diagnostic_Has_A_Row_With_A_Reason()
    {
        IReadOnlyList<string> demoted = DemotedDiagnostics(CliCsprojXml());

        var failures = new List<string>();
        foreach (string id in demoted)
        {
            if (KnownDemotions.ContainsKey(id))
            {
                continue;
            }

            failures.Add(
                $"{CliProjectPath}: {DemotionProperty} now demotes {id} and "
                + "AotBlockDemotionRules.KnownDemotions has no row for it. Add a row saying what makes "
                + "THIS diagnostic tolerable, or take the ID out of the csproj. A fifth ID appended to "
                + "the list is the group carriage #747 was opened about.");
        }

        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every row argues its case, through the one check in this project that
    ///     decides what counts as a reason. Keeps a URL, or a five-word shrug, from
    ///     passing as triage.
    /// </summary>
    [Test]
    public async Task Every_Demoted_Diagnostic_Row_States_A_Reason()
    {
        var failures = ExemptionReason.RowsWithoutAReason(
            "AotBlockDemotionRules.KnownDemotions",
            KnownDemotions.Select(kv => (Key: kv.Key, Row: kv.Value)));

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Liveness: every row still corresponds to a real demotion. Take the ID out
    ///     of the csproj and this fails, so the table cannot rot into a permission
    ///     for a concession that is not being made.
    /// </summary>
    [Test]
    public async Task Table_Rows_Are_Not_Stale()
    {
        HashSet<string> real = DemotedDiagnostics(CliCsprojXml()).ToHashSet(StringComparer.Ordinal);

        var stale = KnownDemotions.Keys
            .Where(k => !real.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        await Assert.That(stale).IsEmpty()
            .Because("a row for a diagnostic the block no longer demotes is an argument for a "
                   + "concession that is not being made: " + string.Join(", ", stale));
    }

    /// <summary>
    ///     The block's own comment names every diagnostic the block demotes. A
    ///     comment that describes what the block does without saying what it gives up
    ///     is the shape #747 is: the old sentence survived four unrelated edits to
    ///     the list, and nothing in the file connected the two.
    /// </summary>
    /// <remarks>
    ///     This is the one rule here that reads the comment, and it is a coverage
    ///     requirement keyed on state rather than a claim ban: it is satisfied only by
    ///     naming the specific IDs the XML carries, so a new ID in the property forces
    ///     a new sentence. It cannot be satisfied by rewording, and it does not
    ///     constrain which words may be used — see the header on the rule that was
    ///     deleted for trying.
    /// </remarks>
    [Test]
    public async Task The_Block_Comment_Names_Every_Demoted_Diagnostic()
    {
        string? csproj = CliCsprojXml();
        string? comment = BlockComment(csproj);
        IReadOnlyList<string> demoted = DemotedDiagnostics(csproj);

        await Assert.That(comment).IsNotNull()
            .Because($"{CliProjectPath} has no comment directly above the AOT PropertyGroup, so the "
                   + "block states nothing about what it concedes at all");

        var missing = demoted
            .Where(id => !comment!.Contains(id, StringComparison.Ordinal))
            .ToList();

        await Assert.That(missing).IsEmpty()
            .Because("the comment above the AOT block must name each diagnostic the block demotes: "
                   + string.Join(", ", missing) + ". A comment that says what the block does without "
                   + "saying what it gives up is the shape of claim #747 rejected.");
    }

    /// <summary>
    ///     Some workflow enables the AOT block — read, not assumed, and in the
    ///     POSITIVE direction.
    /// </summary>
    /// <remarks>
    ///     This rule used to assert the opposite: <c>No_Workflow_Enables_The_Aot_Block</c>
    ///     required that NO workflow turn the block on, and its own comment said that
    ///     landing an AOT publish job "must also turn this file red, because at that
    ///     point the rows stop being honest and start being stale". That is a guard on
    ///     a hole. It made the correct fix a red build, and it would have kept
    ///     failing for as long as the fix existed — the failure mode this repo's own
    ///     #747 header describes as "a check that a truthful sentence fails and a false
    ///     one can be made to pass by rewording is not enforcing a state".
    ///
    ///     The demotion list is load-bearing either way, so what this now enforces is
    ///     that the list is OBSERVABLE: something has to evaluate it, or the four
    ///     concessions in the csproj are assertions about a configuration no build
    ///     ever reads.
    /// </remarks>
    [Test]
    public async Task A_Workflow_Enables_The_Aot_Block()
    {
        IReadOnlyList<string> workflows = EnumerateWorkflows();

        await Assert.That(workflows).IsNotEmpty()
            .Because("no workflow files were found to scan, so this rule would be green for the "
                   + "wrong reason — the same vacuous pass every other rule in this project guards "
                   + "against");

        var enablers = new List<string>();
        foreach (string workflow in workflows)
        {
            string text = SourceScan.TryReadAllText(workflow) ?? string.Empty;
            foreach (string token in AotEnableTokens)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    enablers.Add($"{Path.GetFileName(workflow)}:{token}");
                }
            }
        }

        await Assert.That(enablers).IsNotEmpty()
            .Because("no workflow enables the AOT block, so this PropertyGroup is never evaluated by "
                   + "any build in the repo and every id in "
                   + $"{DemotionProperty} is an assertion about a configuration nothing reads. "
                   + "The aot-publish job in .github/workflows/ci.yml publishes with "
                   + "-p:HarborWithAot=true; if it was removed, say so here and re-triage "
                   + "KnownDemotions against a manual publish — do not leave this file green on a "
                   + "block that is never built.");
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The reader is pointed at the real file, the real file still parses into a
    ///     block and a list, and the premise that makes the demotion load-bearing is
    ///     read from Directory.Build.props. Without these, every rule above passes on
    ///     a project file that was renamed or restructured, or on a list that no longer
    ///     relaxes anything.
    /// </summary>
    [Test]
    public async Task The_Real_Block_Is_Found_And_Parses()
    {
        string? csproj = CliCsprojXml();

        await Assert.That(csproj).IsNotNull()
            .Because($"{CliProjectPath} is missing — every rule in this file would degrade to "
                   + "\"nothing to check\" and pass for no reason");

        await Assert.That(AotBlock(csproj)).IsNotNull()
            .Because($"no PropertyGroup in {CliProjectPath} is conditioned on HarborWithAot and sets "
                   + $"{DemotionProperty}. Either the block was renamed or the demotion moved, and "
                   + "this guard has to be pointed at the new shape rather than deleted.");

        await Assert.That(BlockComment(csproj)).IsNotNull()
            .Because("the AOT block has no XML comment directly above it");

        string root = RepoPaths.RepoRoot!;
        string props = Path.Combine(root, "Directory.Build.props");
        string propsText = SourceScan.TryReadAllText(props) ?? string.Empty;

        await Assert.That(propsText).Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>")
            .Because($"{props} no longer sets TreatWarningsAsErrors=true. If that flipped, the "
                   + $"{DemotionProperty} list in the AOT block would be an inert line rather than a "
                   + "concession, and this whole file would be guarding a no-op.");
    }

    /// <summary>
    ///     Sensitivity control over synthetic csproj text: the reader must report every
    ///     ID the property carries, must ignore an MSBuild property reference sitting
    ///     in the same value, and must not invent IDs that are not there. The
    ///     <c>$(…)</c> case is the one that matters — a reader that treated
    ///     <c>$(WarningsNotAsErrors)</c> as a diagnostic would demand a table row for a
    ///     literal string nobody demoted, and the guard would be unfalsifiable in the
    ///     other direction.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Reader_Catches_An_Unlisted_Diagnostic_And_Ignores_A_Property_Reference()
    {
        const string TwoIdsPlusAPlantedOne = """
            <Project>
              <!-- IL2026 IL3050 IL9999 -->
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026;IL3050;IL9999</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        const string PropertyReferenceOnly = """
            <Project>
              <!-- no diagnostic is demoted here -->
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors)</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        const string OneCarriedId = """
            <Project>
              <!-- IL2026 -->
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        const string Unconditioned = """
            <Project>
              <PropertyGroup>
                <WarningsNotAsErrors>IL2026</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        await Assert.That(DemotedDiagnostics(TwoIdsPlusAPlantedOne))
            .IsEquivalentTo(new[] { "IL2026", "IL3050", "IL9999" })
            .Because("all three IDs must be read, the leading $(WarningsNotAsErrors) property "
                   + "reference must be dropped rather than reported as a diagnostic, and the planted "
                   + "IL9999 — which has no row — is the exact failure "
                   + "Every_Demoted_Diagnostic_Has_A_Row_With_A_Reason exists to report");

        await Assert.That(DemotedDiagnostics(PropertyReferenceOnly)).IsEmpty()
            .Because("a bare $(WarningsNotAsErrors) carries no diagnostic ID, and reporting one would "
                   + "make the guard demand a table row for a string that is not a warning");

        await Assert.That(DemotedDiagnostics(OneCarriedId)).IsEquivalentTo(new[] { "IL2026" })
            .Because("a carried ID must not be reported twice or as extra, or the control above would "
                   + "prove nothing about the reader's discrimination");

        await Assert.That(DemotedDiagnostics(Unconditioned)).IsEmpty()
            .Because("only a PropertyGroup conditioned on HarborWithAot is in scope — an "
                   + "unconditioned WarningsNotAsErrors elsewhere in the file is a different property "
                   + "and must not be attributed to the AOT block");
    }

    /// <summary>
    ///     The comment reader is as load-bearing as the property reader: a comment two
    ///     nodes up, or separated by real content, is not the block's description and
    ///     reporting it would let the naming rule pass on a comment that describes
    ///     something else.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Comment_Reader_Takes_The_Adjacent_Comment_And_Not_An_Earlier_One()
    {
        const string Adjacent = """
            <Project>
              <!-- the block's own comment, naming IL2026 -->
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        const string SeparatedByContent = """
            <Project>
              <!-- an older comment about the file, naming IL2026 and IL3050 -->
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        const string NoComment = """
            <Project>
              <PropertyGroup Condition="'$(HarborWithAot)' == 'true'">
                <WarningsNotAsErrors>$(WarningsNotAsErrors);IL2026</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """;

        string? adjacent = BlockComment(Adjacent);
        await Assert.That(adjacent).IsNotNull()
            .Because("a comment immediately above the block, separated only by the newline that "
                   + "terminates it, is the block's description — whitespace is not content");
        await Assert.That(adjacent).Contains("IL2026")
            .Because("the reader must return the adjacent comment itself, not merely a non-null "
                   + "string, or it could be returning some other comment and still pass");

        await Assert.That(BlockComment(SeparatedByContent)).IsNull()
            .Because("a PropertyGroup stands between the comment and the AOT block, so that comment "
                   + "describes something else; reading it would let the naming rule pass on a "
                   + "comment that never described the AOT block at all");

        await Assert.That(BlockComment(NoComment)).IsNull()
            .Because("no comment means no description, and the naming rule must say so rather than "
                   + "silently pass on an empty string");
    }

    // =====================================================================
    // 3. Helpers.
    // =====================================================================

    /// <summary>Absolute path of the CLI project, or <see langword="null" /> outside a checkout.</summary>
    /// <returns>The path, or <see langword="null" /> when not running from a checkout.</returns>
    private static string? CliCsproj() =>
        RepoPaths.RepoRoot is { } root ? Path.Combine(root, CliProjectPath) : null;

    /// <summary>
    ///     The CLI csproj's XML text, or <see langword="null" /> outside a checkout.
    ///     The readers below take XML rather than a path so the synthetic controls can
    ///     hand them a document without the path/content ambiguity that would come
    ///     from sniffing which one they were given.
    /// </summary>
    /// <returns>The csproj text, or <see langword="null" /> when it cannot be read.</returns>
    private static string? CliCsprojXml() =>
        CliCsproj() is { } path ? SourceScan.TryReadAllText(path) : null;

    /// <summary>Every workflow definition file, sorted for deterministic failure messages.</summary>
    /// <returns>The workflow paths, empty when not running from a checkout.</returns>
    private static IReadOnlyList<string> EnumerateWorkflows()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, WorkflowDirectoryPath);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return [.. Directory.GetFiles(dir, "*.yml", SearchOption.TopDirectoryOnly)
                   .Concat(Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly))
                   .OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     The <c>PropertyGroup</c> conditioned on <c>HarborWithAot</c> that sets the
    ///     demotion property.
    /// </summary>
    /// <param name="xml">The csproj's XML text.</param>
    /// <returns>The block, or <see langword="null" /> when no such group exists.</returns>
    private static XElement? AotBlock(string? xml)
    {
        if (xml is null)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        return document.Descendants()
            .Where(e => e.Name.LocalName == "PropertyGroup")
            .FirstOrDefault(g =>
                (g.Attribute("Condition")?.Value ?? string.Empty)
                    .Contains("HarborWithAot", StringComparison.Ordinal)
                && g.Elements().Any(e => e.Name.LocalName == DemotionProperty));
    }

    /// <summary>
    ///     The diagnostic IDs a csproj's AOT block demotes: the semicolon-separated
    ///     value of the demotion property, minus MSBuild property references such as
    ///     <c>$(WarningsNotAsErrors)</c>, minus blanks.
    /// </summary>
    /// <param name="xml">The csproj's XML text.</param>
    /// <returns>The IDs, in the order the property lists them.</returns>
    private static IReadOnlyList<string> DemotedDiagnostics(string? xml)
    {
        string? value = AotBlock(xml)?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == DemotionProperty)?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (string token in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsDiagnosticId(token))
            {
                ids.Add(token);
            }
        }

        return ids;
    }

    /// <summary>
    ///     The XML comment directly above the AOT block.
    /// </summary>
    /// <remarks>
    ///     Walking backwards, whitespace-only text is skipped — the newline that
    ///     terminates a comment is not evidence that the comment is absent. Anything
    ///     else stops the search, including an ELEMENT: if another PropertyGroup
    ///     stands between the comment and the AOT block, that comment describes
    ///     something else, and returning it would let the naming rule pass on a
    ///     comment that never described the AOT block. Returning null and failing is
    ///     the honest answer; guessing is not.
    /// </remarks>
    /// <param name="xml">The csproj's XML text.</param>
    /// <returns>The comment text, or <see langword="null" /> when there is none.</returns>
    private static string? BlockComment(string? xml)
    {
        XElement? block = AotBlock(xml);
        if (block is null)
        {
            return null;
        }

        for (XNode? node = block.PreviousNode; node is not null; node = node.PreviousNode)
        {
            if (node is XComment comment)
            {
                return comment.Value;
            }

            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value))
                {
                    return null;
                }

                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    ///     A bare diagnostic ID: letters then digits, nothing else. This keeps
    ///     <c>$(WarningsNotAsErrors)</c> out, and picks up a <c>CS…</c> or <c>IL3xxx</c>
    ///     a future contributor appends just as readily as the four that are there now
    ///     — the guard is about the shape of the concession, not about these names.
    /// </summary>
    /// <param name="token">One <c>;</c>-separated token from the property value.</param>
    /// <returns><see langword="true" /> when the token names a diagnostic.</returns>
    private static bool IsDiagnosticId(string token)
    {
        int firstDigit = -1;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsAsciiDigit(token[i]))
            {
                firstDigit = i;
                break;
            }
        }

        // A diagnostic ID has a letter prefix and a numeric suffix: no leading digit,
        // and at least one character after the first digit.
        if (firstDigit <= 0 || firstDigit == token.Length - 1)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
