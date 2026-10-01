// ExistenceCheckLivenessRule.cs — a MEASUREMENT: how many of this project's
// "this name must exist" checks can actually fail.
//
// THE DEFECT
// ----------
// #877, and the part of it that is NOT the tests/ question. Four checks in
// `EnforcerIntegrityTests` read a table, take a project-directory name out of
// it, and assert that the project exists:
//
//   :247  `OutOfScopeAssemblies`          -> "no such src project"
//   :255  `SharedSourceFolders`           -> "no such src folder"
//   :423  `DeclaredButUnboundProjectReferences.To` -> "no such src project"
//   :467  `DocumentedExceptions`          -> "no src project directory"
//
// Every one of them asks `RepoPaths.FindProjectDir(name) is null`. And
// `FindProjectDir` is not an existence check. Read `RepoPaths.cs:85-86`: it
// returns null when `RepoRoot` is null, and otherwise hands the name straight to
// `Path.Combine(RepoRoot, "src", projectDir)`.
//
// It CONCATENATES. `Path.Combine` returns a string for any input, and the only
// way this returns null is when `RepoRoot` is null — i.e. when the test host is
// not running from a checkout. Inside a checkout the answer is non-null for
// EVERY name, including names that have never existed. So all four checks
// reduce to "is the repository root discoverable", which a different test
// (`RepositoryInventory_IsDiscoverable`, :158) already asserts.
//
// A stale table row therefore cannot be caught, and "the guard is green" is
// indistinguishable from "the guard read nothing". This is the #877 defect
// class one level up from the tests/ question: a measurement that cannot fail.
//
// THE ANSWER IS NOT ZERO, AND THAT IS THE POINT
// ----------------------------------------------
// The four checks are dead, so the only way to learn what they are hiding is to
// ask the question they were written to ask, with a real existence test. This
// file does that, and the answer is not a clean bill of health:
//
// `DeclaredButUnboundProjectReferences` carries SIX entries whose `To` names a
// `src/` project that does not exist. All six are `Harbor.Hosting` ->
// a Spectre/Termina/TerminalGui/RazorConsole renderer, and all six were moved
// out of `src/` into `contrib/tui/` (they are the renderers AGENTS.md calls
// unmaintained and out of CI). `Harbor.Hosting.csproj:139-146` still declares
// all six, inside
//
//     <ItemGroup Condition="'$(HarborWithSpectreTui)' == 'true'">
//
// and `HarborWithSpectreTui` is defined nowhere, so that group never opens. So
// the reference is genuinely dead — which is what the exemption says — but the
// exemption names a `src/` path that has not existed since 2026-08-23, and the
// check that says so cannot fire.
//
// That distinction is the reason this is a measurement and not a deletion. The
// rows are not lying about the edge; they are lying about where it lives. And
// the fix is NOT "delete the six rows": `ReadProjectReferences` does not
// evaluate `Condition`, so it still reports those six as declared references,
// and `DeclaredProjectReferences_AreJustifiedByTheMatrix` (:328) consults this
// same table at :352 to decide whether a declared edge is justified. Delete the
// rows and that rule starts reporting six unjustified ProjectReferences that
// are permitted by nothing. Restoring the rows is what is already there.
//
// So this file MEASURES and PINS. It does not decide what should happen to a
// conditional reference to a project that lives outside the enforced tree —
// that is a judgement about the contrib boundary, and the freeze in #555 plus
// the open question in #950 both say it is not this file's to make.
//
// WHY NOT FIX `FindProjectDir` INSTEAD
// ------------------------------------
// Because its blast radius is not this file's to absorb either, and the
// measurement is what makes the decision possible. `FindProjectDir` has 15 call
// sites, and the fix is not a one-line change to a helper:
//
//   * TEN want a PATH, which is exactly what `Path.Combine` is. SharedSourceLinkRules
//     (:258, :418), SessionStoreFailureTextParityRules:554, ProviderCatalogueBudgetRules
//     (:99, :116), ProviderAuthSingleAnswerRules:90, CellForgeEngineAtomicityRules:439
//     and AbstractionsNamespaceOwnershipRules:478 all resolve it and walk it. Several
//     already pair it with their own `Directory.Exists` — CellForgeEngineCseOwnershipTests
//     (:137, :265) is the pattern this file argues for, written twice, in the same
//     project, while the four sites above skip the second half.
//   * ONE is a FILTER where a real existence test would silently narrow what is
//     scanned: `EnforcerIntegrityTests:602`, the one #949 flagged. It is what builds
//     `SrcAssemblyNames()`, and it decides the input to two matrix guards.
//   * FOUR are the dead checks measured here.
//
// So "make FindProjectDir an existence check" is a behaviour change to a shared
// helper with a dozen call sites, not a bug fix — which is why the measurement
// lands first and the change waits for someone who owns the helper.
//
// The four checks THIS file measures are the ones where the question is
// unambiguous ("does this name exist") and the current answer is unconditionally
// "yes". So the measurement here is scoped to those, and the wider question —
// should `FindProjectDir` become an existence check at all, and what happens to
// its ten other call sites — belongs to #950, which already collects it.
//
// NON-VACUITY
// -----------
//   1. TheRepoRootIsDiscoverable — the whole file measures rows whose liveness
//      depends on a real filesystem, so a null root would make every table read
//      as "everything exists" and this file green for the wrong reason.
//   2. TheExistenceProbeCanSayNo — a control on the PROBE, not on the tables: a
//      name that cannot exist is passed to the same `Directory.Exists` question
//      and must come back false. Without it, "six rows are stale" and "the
//      probe always says yes" are the same report, and the six would be
//      indistinguishable from a probe that cannot fail — which is the very
//      defect this file is about, reproduced in its own instrument.
//   3. TheDeadChecksAreDead — the positive control on the DEFECT: the four
//      `FindProjectDir` calls are asserted to return non-null for names that do
//      not exist. If someone makes `FindProjectDir` a real existence check, this
//      goes red, and the four checks it guards start working — which is a
//      reviewed decision with its own blast radius, not a drive-by.
//
// Both files in this class shipped with EMPTY baselines, on purpose: there is
// no local dotnet in the authoring environment, so the red run's log is the
// only execution of these matchers there has ever been, and the follow-up
// commit transcribes it.
//
// WHAT THE RED RUN PROVED (run 36822292544, job 110240325437)
// -----------------------------------------------------------
// One test failed and the other two passed, and the two that passed are the ones
// that make the failure mean something:
//
//   * `EveryPermissionRow_NamesAProjectThatExists` FAILED, and the message is
//     the measurement: "Live stale rows (6) of 42 measured", naming all six.
//   * `TheExistenceProbeCanSayNo` PASSED. So the probe is not a constant that
//     happens to say no: it also found 36 of 42 rows present, and it reported a
//     synthetic name as absent. Without this pass, "six rows are stale" would be
//     indistinguishable from "the instrument always says no" — the same defect
//     this file is about, reproduced inside its own probe.
//   * `TheDeadChecksAreDead` PASSED. All four sites accepted a project that does
//     not exist. So the four checks are confirmed dead by execution, not by
//     reading `Path.Combine`, and each witness name was itself confirmed absent
//     first — otherwise the control would have been asserting the defect against
//     a name that might turn out to exist.
//
// The red run also corrected the file. It flagged S125 on the header's quoted
// `=>` expression-bodied member as commented-out code; the header now cites
// `RepoPaths.cs:85-86` in prose instead of quoting the member, since a build
// gate that claims zero warnings is not a gate if the guard's own header
// introduces one.

namespace Harbor.Architecture.Tests;

/// <summary>One row of a permission table, and whether the project it names is real.</summary>
/// <param name="Table">The declaring table, for the failure message.</param>
/// <param name="Key">How the table names the row.</param>
/// <param name="ProjectDir">The project-directory name the row asserts exists.</param>
internal sealed record TableRow(string Table, string Key, string ProjectDir)
{
    /// <summary>
    ///     Whether <c>src/&lt;ProjectDir&gt;</c> is a real directory. This is the
    ///     question the four dead checks were written to ask, asked properly.
    /// </summary>
    internal bool ExistsOnDisk =>
        RepoPaths.RepoRoot is { } root && Directory.Exists(Path.Combine(root, "src", ProjectDir));
}

/// <summary>Reads the four permission tables the dead checks police.</summary>
internal static class ExistenceCheckLivenessProbe
{
    /// <summary>
    ///     Every row of the four tables whose liveness the four dead checks claim
    ///     to enforce.
    /// </summary>
    /// <remarks>
    ///     Read from the tables themselves, not from a copy, so a new table cannot
    ///     escape the measurement by simply not being listed here. The four
    ///     `FindProjectDir` sites are named in <see cref="DeadCheckSites" /> and
    ///     this list is the same four tables — a fifth table added to a guard
    ///     without a liveness check is a new hole, and <see
    ///     cref="TheDeadChecksAreDead" /> is what makes the count checkable.
    /// </remarks>
    internal static IReadOnlyList<TableRow> Rows()
    {
        var rows = new List<TableRow>();

        foreach (string projectDir in FullLayerMatrixTests.OutOfScopeAssemblies.Keys)
        {
            rows.Add(new TableRow("FullLayerMatrixTests.OutOfScopeAssemblies", projectDir, projectDir));
        }

        foreach (string folder in FullLayerMatrixTests.SharedSourceFolders.Keys)
        {
            rows.Add(new TableRow("FullLayerMatrixTests.SharedSourceFolders", folder, folder));
        }

        foreach (EnforcerIntegrityTests.UnboundReference unbound
                     in EnforcerIntegrityTests.DeclaredButUnboundProjectReferences)
        {
            rows.Add(new TableRow(
                "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences",
                unbound.From + " -> " + unbound.To,
                unbound.To));
        }

        foreach (string from in FullLayerMatrixTests.DocumentedExceptions.Keys)
        {
            rows.Add(new TableRow("FullLayerMatrixTests.DocumentedExceptions", from, from));
        }

        return rows;
    }

    /// <summary>
    ///     The four sites whose check is a concatenation rather than a lookup.
    /// </summary>
    /// <remarks>
    ///     Named with file and line so the count in the header is checkable against
    ///     the file it is about. Each entry is the project-directory name that site
    ///     passes to <c>RepoPaths.FindProjectDir</c>; the control below asserts
    ///     that <c>FindProjectDir</c> returns non-null for every one of them, even
    ///     though none of them exists.
    /// </remarks>
    internal static readonly (string Site, string AbsentProject)[] DeadCheckSites =
    [
        ("EnforcerIntegrityTests.cs:247 OutOfScopeAssemblies", "Harbor.NoSuchProjectForTheProbe"),
        ("EnforcerIntegrityTests.cs:255 SharedSourceFolders", "Harbor.NoSuchSharedFolderForTheProbe"),
        ("EnforcerIntegrityTests.cs:423 DeclaredButUnboundProjectReferences.To", "Harbor.Tui.Spectre"),
        ("EnforcerIntegrityTests.cs:467 DocumentedExceptions", "Harbor.NoSuchExceptionHostForTheProbe"),
    ];
}

/// <summary>
///     Pins how many of the "this name must exist" checks in
///     <see cref="EnforcerIntegrityTests" /> can actually fail. The blind spot is
///     the predicate, not the walk — see the file header.
/// </summary>
public sealed class ExistenceCheckLivenessRule
{
    private static readonly Lazy<IReadOnlyList<TableRow>> Rows = new(ExistenceCheckLivenessProbe.Rows);

    /// <summary>
    ///     The rows whose project does not exist under <c>src/</c>. MEASURED, not
    ///     assumed: transcribed verbatim from the red run (run 36822292544, job
    ///     110240325437) — "Live stale rows (6) of 42 measured".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Pinned as a set of KEYS, never as a count. A count is satisfiable by
    ///         one stale row being fixed while another appears: the number holds, the
    ///         hole changes shape, and the diff that did it reads as a wash. Keys
    ///         make that impossible — a row that starts naming a real project is a
    ///         name somebody has to delete from this array on purpose, which is the
    ///         edit that deserves review.
    ///     </para>
    ///     <para>
    ///         Six of forty-two rows, all of them
    ///         <c>Harbor.Hosting -&gt; a renderer</c>, and all six name a project
    ///         that moved from <c>src/</c> to <c>contrib/tui/</c> on 2026-08-23.
    ///         <c>Harbor.Hosting.csproj:139-146</c> still declares the reference,
    ///         inside an <c>ItemGroup</c> conditioned on
    ///         <c>$(HarborWithSpectreTui)</c> — a property defined nowhere, so the
    ///         group never opens and the edge really is dead, which is what the
    ///         exemption claims. The row is wrong about WHERE the project lives, not
    ///         about whether the edge is bound.
    ///     </para>
    ///     <para>
    ///         The other thirty-six rows are sound, and that is the half of the
    ///         measurement worth having. <c>OutOfScopeAssemblies</c> (2) and
    ///         <c>SharedSourceFolders</c> (2) name real <c>src/</c> directories — the
    ///         latter two have no <c>.csproj</c> at all, being linked-source folders,
    ///         which is why an existence probe keyed on a project FILE would have
    ///         condemned two perfectly good rows. <c>DocumentedExceptions</c> (4) and
    ///         the remaining twenty-eight <c>DeclaredButUnboundProjectReferences</c>
    ///         rows name projects that are all still there.
    ///     </para>
    ///     <para>
    ///         Not a deletion, and this file does not make it one:
    ///         <c>RepoPaths.ReadProjectReferences</c> does not evaluate
    ///         <c>Condition</c>, so it still reports those six as declared
    ///         references, and <c>DeclaredProjectReferences_AreJustifiedByTheMatrix</c>
    ///         consults the same table to decide whether a declared edge is
    ///         justified. Removing the rows would make that rule report six
    ///         unjustified references permitted by nothing.
    ///     </para>
    /// </remarks>
    private static readonly string[] MeasuredStaleRows =
    [
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.RazorConsole]",
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.Spectre]",
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.Spectre.Fullscreen]",
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.SpectreTui]",
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.Termina]",
        "EnforcerIntegrityTests.DeclaredButUnboundProjectReferences[Harbor.Hosting -> Harbor.Tui.TerminalGui]",
    ];

    /// <summary>
    ///     Every row the four checks police names a real <c>src/</c> project —
    ///     except the ones measured above.
    /// </summary>
    [Test]
    public async Task EveryPermissionRow_NamesAProjectThatExists()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "this rule asks whether a project directory exists on disk, and without a checkout "
                + "there are no directories to ask about — every row would read as existing and the "
                + "baseline below would hold for the wrong reason. RepoPaths degrades to empty rather "
                + "than throwing, so this is the one assertion standing between a green run and a blind one.");

        var stale = Rows.Value.Where(r => !r.ExistsOnDisk)
            .Select(r => r.Table + "[" + r.Key + "]")
            .ToArray();

        await Assert.That(string.Join(" | ", stale))
            .IsEqualTo(string.Join(" | ", MeasuredStaleRows))
            .Because(
                "EnforcerIntegrityTests carries four checks that read a permission table and assert the "
                + "named project exists — OutOfScopeAssemblies (:247), SharedSourceFolders (:255), "
                + "DeclaredButUnboundProjectReferences.To (:423), DocumentedExceptions (:467). All four "
                + "ask RepoPaths.FindProjectDir(name) is null, and FindProjectDir is a Path.Combine, not an "
                + "existence test: inside a checkout it returns non-null for every name, so none of the four "
                + "can fail. These are the rows a real check would report. This is the #877 defect class — a "
                + "measurement that cannot fail reports a healthy zero. Live stale rows (" + stale.Length + ") "
                + "of " + Rows.Value.Count + " measured: "
                + (stale.Length == 0 ? "(none)" : string.Join(" | ", stale))
                + " | baseline held " + MeasuredStaleRows.Length + " name(s): "
                + (MeasuredStaleRows.Length == 0 ? "(empty)" : string.Join(" | ", MeasuredStaleRows)));
    }

    // =====================================================================
    // 1. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The probe can report a missing directory. Without this, "six rows are
    ///     stale" and "the probe always says yes" are the same report.
    /// </summary>
    [Test]
    public async Task TheExistenceProbeCanSayNo()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("a null root would make every directory read as missing, and the control below would "
                   + "pass for the wrong reason instead of proving the probe discriminates");

        // The same question, asked of a name that cannot exist. If this came back
        // true the probe would be a constant and the measurement above worthless.
        var impossible = new TableRow("(control)", "Harbor.NoSuchProjectForTheProbe", "Harbor.NoSuchProjectForTheProbe");
        await Assert.That(impossible.ExistsOnDisk).IsFalse()
            .Because(
                "this rule measures whether a project directory exists by asking Directory.Exists. If that "
                + "question cannot come back false, then the rows reported as stale are not evidence of "
                + "staleness — they would be evidence that the instrument is broken, which is the same defect "
                + "this file is about reproduced inside its own probe. Control name: " + impossible.ProjectDir);

        // And the positive side, so "always false" is not the failure either.
        string? aRealProject = Rows.Value.FirstOrDefault(r => r.ExistsOnDisk)?.ProjectDir;
        await Assert.That(aRealProject).IsNotNull()
            .Because(
                "the probe has to be able to say yes as well as no. A probe stuck on false would report every "
                + "row stale and the measurement would be a constant in the other direction. Every row: "
                + string.Join(" | ", Rows.Value.Select(r => r.Table + "[" + r.Key + "] -> "
                                                            + (r.ExistsOnDisk ? "exists" : "MISSING"))));
    }

    /// <summary>
    ///     The four checks read a concatenation, so they cannot fail. This is the
    ///     positive control on the defect.
    /// </summary>
    [Test]
    public async Task TheDeadChecksAreDead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("RepoPaths.FindProjectDir returns null only when the root is undiscoverable, so without "
                   + "a checkout this control would pass for a reason that has nothing to do with the defect");

        var answered = ExistenceCheckLivenessProbe.DeadCheckSites
            .Where(site => RepoPaths.FindProjectDir(site.AbsentProject) is not null)
            .Select(site => site.Site + " accepts '" + site.AbsentProject + "'")
            .ToArray();

        await Assert.That(answered.Length)
            .IsEqualTo(ExistenceCheckLivenessProbe.DeadCheckSites.Length)
            .Because(
                "RepoPaths.FindProjectDir is Path.Combine(RepoRoot, \"src\", name) — it returns a string for "
                + "any input and null only when the root is undiscoverable. So in a checkout all four of these "
                + "checks reduce to 'is the root discoverable', which RepositoryInventory_IsDiscoverable already "
                + "asserts, and a stale row in any of the four tables cannot be caught. This control asserts "
                + "the defect is PRESENT: if it starts failing, FindProjectDir became a real existence check and "
                + "the four checks it guards are now load-bearing — a reviewed decision with its own blast "
                + "radius (ten other call sites use the same helper as a PATH and as a FILTER), not a drive-by. "
                + "Sites accepting a non-existent project: " + answered.Length + " of "
                + ExistenceCheckLivenessProbe.DeadCheckSites.Length + ". Accepted: "
                + (answered.Length == 0 ? "(none)" : string.Join(" | ", answered)));

        // The four names above are absent for a reason, not by accident: the probe
        // used to build them must agree that they are missing. Otherwise this
        // control would be asserting the defect against names that might exist.
        foreach ((string site, string absentProject) in ExistenceCheckLivenessProbe.DeadCheckSites)
        {
            var row = new TableRow(site, absentProject, absentProject);
            await Assert.That(row.ExistsOnDisk).IsFalse()
                .Because(
                    "this name is the witness for " + site + ": the control above claims FindProjectDir accepts "
                    + "a project that does not exist, and that claim is only evidence if the project really does "
                    + "not exist. If src/" + absentProject + " has appeared, the witness is wrong and so is the "
                    + "control — pick another name, or record that the check started working.");
        }
    }
}
