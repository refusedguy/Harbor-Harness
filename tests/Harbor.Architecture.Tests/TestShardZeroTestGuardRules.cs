// TestShardZeroTestGuardRules.cs — #977: the `test` job's shard matrix states a
// zero-test guard per shard, and one of the four entries stated the OPPOSITE
// with nothing recorded next to it.
//
// THE HOLE
// --------
// `.github/workflows/ci.yml` runs every test project as a plain executable
// (`dotnet exec <dll>`), because `dotnet test` is unusable here — AGENTS.md §Quick
// state records that the VSTest bridge cannot reach the MTP runner. The consequence
// is that NOTHING in a job notices a suite which discovered zero tests: the host
// exits 0, the loop records `pass`, the shard is green, and the artifact is a TRX
// with no cases in it. `--minimum-expected-tests 1` is the one thing that turns
// that green into MTP's exit 8.
//
// The matrix declared that flag as a per-shard VALUE:
//
//     matrix:
//       include:
//         - shard: core
//           guard: true
//         - shard: platform
//           guard: true
//         - shard: ui
//           guard: true
//         - shard: e2e
//           guard: false      <-- #977, and no comment saying why
//
// and `Run shard ${{ matrix.shard }}` expands it to `--minimum-expected-tests 1`
// only when the value is the string "true". So `e2e` ran its five projects with
// no guard at all, and `Run shard` exited 0 on an empty run. Three of the four
// shards were protected and one was not — which is the #912 shape exactly: a rule
// written as "every shard" and expressed as a list, where one entry quietly fails
// to comply.
//
// WHY IT MATTERED MORE FOR THIS SHARD THAN FOR THE OTHERS
// -------------------------------------------------------
// Measured 2026-10-01 on `dev` (run 36849021180, job 110328386509): `e2e` is the
// ONLY home of its five projects.
//
//   Harbor.E2E.Cli            total 36    skipped 0
//   Harbor.E2E.App.Avalonia   total 104   skipped 12
//   Harbor.Tui.E2E.Tests      total 39    skipped 1
//   Harbor.App.Cli.Tests      total 272   skipped 0
//   Harbor.LoadTests          total 28    skipped 0
//
// `test-os` runs a fixed list of 9 projects (ci.yml:656) and `coverage` a
// fixed list of 18 (ci.yml:515); neither includes any of the five, and no
// other workflow names them either (`Harbor.Tui.E2E.Tests` also appears in
// demo.yml, which does not run it). So `test (e2e)` is the single place 479 tests
// execute, and it was the single place where zero of them executing would have
// read as green.
//
// The counts above are the reason the fix could be the cheap one. Had any project
// been at zero, `--minimum-expected-tests 1` would have turned today's green run
// red — which is what #977 explicitly declined to do, not knowing the counts. The
// smallest is LoadTests at 28. Zero is not a state any of the five is near, so
// `guard: true` costs nothing and closes the hole.
//
// NON-VACUITY, IN BOTH DIRECTIONS
// -------------------------------
// A guard over a list that matched nothing is indistinguishable from a guard
// that is broken, and a broken guard is worse than none because it is believed.
// Five mechanisms, each going RED rather than passing quietly:
//
//   1. `TheMatrixWasParsedAtAll` requires the include: block to yield AT LEAST the
//      four shards the job declares, per the `shard:` axis list. A parser that
//      stops matching YAML returns an empty matrix and every "all shards are
//      guarded" assertion below is then satisfied by nothing — the #901 "Found 0"
//      failure, where two checks passed on zero subjects.
//   2. `TheGuardFlagIsActuallyReadByTheRunStep` requires the run step to still
//      contain the `matrix.guard` test and the `--minimum-expected-tests 1`
//      assignment. `guard: true` on all four rows is a DECLARATION; without the
//      expansion it is a comment. #899 is the shape where a guard counted itself.
//   3. `TheRuleFiresOnAPlantedUnguardedShard` plants a fifth shard carrying
//      `guard: false` and requires it to be reported. On today's tree the real
//      matrix is clean, so the rule would otherwise be green on the strength of
//      having read nothing that mattered.
//   4. `TheRuleAcceptsAPlantedGuardedShard` is the other end. A rule that reports
//      every row is not a working guard, it is noise nobody reads.
//   5. `EveryShardInTheMatrixIsGuarded` is scoped to the `test` job's OWN include
//      block — the text between `  test:` and the next top-level job key — so a
//      `guard:` line added to some other job cannot make this pass.
//
// Also note `RequireRepoRoot` THROWS rather than returning quietly: "could not find
// the file this guard polices" must never render as "every shard is guarded".
//
// WHAT THIS DELIBERATELY DOES NOT CLAIM
// -------------------------------------
// * It reads the WORKFLOW, not a run. It cannot tell you the e2e shard executed
//   anything, and it is not trying to: by #951's measurement, 43% of `dev` runs
//   ended `cancelled` with zero jobs, which is a separate hole in a separate
//   place (`concurrency` with `cancel-in-progress` false on `push`). Closing the
//   guard does not close that, and nothing here pretends otherwise.
// * It does not count tests. The per-project totals live in the job summary and
//   the TRX artifacts; a floor of 1 is what the guard asserts, because 1 is what
//   distinguishes "ran" from "discovered nothing".
// * `contrib/` is untouched (#555).

namespace Harbor.Architecture.Tests;

/// <summary>
///     Every shard in the <c>test</c> job's matrix must carry the zero-test guard,
///     and the run step must actually expand that flag into the test command.
/// </summary>
public sealed class TestShardZeroTestGuardRules
{
    /// <summary>Repo-relative path of the workflow this guard reads.</summary>
    private const string WorkflowRelativePath = ".github/workflows/ci.yml";

    /// <summary>
    ///     Lowest acceptable number of parsed shard rows. The matrix declares four
    ///     (<c>shard: [core, platform, ui, e2e]</c>). One below that is the signal
    ///     that the parser stopped matching YAML — at which point the "every shard
    ///     is guarded" rule below would be true of nothing at all.
    /// </summary>
    private const int MinimumParsedShards = 4;

    [Test]
    public async Task TheMatrixWasParsedAtAll()
    {
        List<ShardRow> rows = ReadRows(Workflow());

        await Assert.That(rows.Count).IsGreaterThanOrEqualTo(MinimumParsedShards)
            .Because("the `test` job's matrix declares "
                + $"{MinimumParsedShards} shards, and every assertion in this file grades them. "
                + "Fewer rows than that means the reader stopped matching the workflow — YAML was "
                + "reindented, or the include: block moved — and the rule below would then be "
                + "satisfied by an EMPTY set while reading as a clean repository. That is the #901 "
                + "failure: two checks green on zero subjects. Parsed " + rows.Count + " row(s): "
                + Describe(rows));
    }

    [Test]
    public async Task EveryShardInTheMatrixIsGuarded()
    {
        List<ShardRow> rows = ReadRows(Workflow());

        List<string> unguarded =
        [
            .. rows.Where(r => !r.Guarded)
                   .Select(r => $"shard '{r.Name}' declares no working guard"
                                + (r.GuardValue is null
                                    ? " (no `guard:` key on its row)"
                                    : $" (`guard: {r.GuardValue}`)"))
        ];

        await Assert.That(unguarded).IsEmpty()
            .Because("a shard whose row says the guard is off runs its projects with no "
                + "`--minimum-expected-tests`, so a suite that discovered ZERO tests exits 0 and the "
                + "shard reports green. That is #977: `e2e` was the only one of four, it is the ONLY "
                + "home of its five projects (neither `test-os`'s 9 nor `coverage`'s 18 lists contains "
                + "any of them), and a green-on-empty run of it is invisible — the TRX is uploaded with "
                + "no cases and `fail-on-empty: false` never notices. Set `guard: true`. If a shard "
                + "genuinely cannot carry the flag, that is a decision to RECORD here, not a bare "
                + "`false`. Unguarded: " + string.Join(" | ", unguarded));
    }

    /// <summary>
    ///     The declared value is only a decision; the run step has to act on it.
    /// </summary>
    /// <remarks>
    ///     This is the #899 shape — a guard counting itself — one level up. Setting
    ///     <c>guard: true</c> on all four rows and then deleting the expansion in
    ///     <c>Run shard</c> leaves a matrix that reads correctly and a job with no
    ///     protection, and the row-level rule above would stay green the whole time,
    ///     because it never looks at the run step. So the two halves are asserted
    ///     together: a flag that is declared and a command that uses it.
    /// </remarks>
    [Test]
    public async Task TheGuardFlagIsActuallyReadByTheRunStep()
    {
        string step = RunStepBody();

        await Assert.That(step).Contains("matrix.guard")
            .Because("the run step must branch on the matrix value it declared, or `guard: true` is "
                + "a comment. Expected a `matrix.guard` test inside the 'Run shard' step; found: "
                + FirstLine(step));

        await Assert.That(step).Contains("guard=\"--minimum-expected-tests 1\"")
            .Because("the branch must assign `--minimum-expected-tests 1`. Without that assignment a "
                + "`guard: true` row expands to an empty string and the host exits 0 on a zero-test "
                + "run, which is the exact failure #977 reports. Found: " + FirstLine(step));
    }

    /// <summary>
    ///     The rule must be able to report a bad row, proved on planted input.
    /// </summary>
    /// <remarks>
    ///     Today's matrix is clean after the fix, so <see cref="EveryShardInTheMatrixIsGuarded" />
    ///     returns an empty list — which is what a correct workflow looks like, and also what a
    ///     blind rule looks like. This drives the SAME <see cref="ReadRows" /> +
    ///     unguarded-selection with a fifth shard carrying <c>guard: false</c> and requires it to be
    ///     named, so a green run of the real rule means "the matcher fired and found nothing".
    /// </remarks>
    [Test]
    public async Task TheRuleFiresOnAPlantedUnguardedShard()
    {
        const string Planted = """
            jobs:
              test:
                strategy:
                  matrix:
                    shard: [core, e2e]
                    include:
                      - shard: core
                        guard: true
                      - shard: e2e
                        guard: false
            """;

        List<ShardRow> rows = ReadRows(Planted);

        List<string> unguarded = [.. rows.Where(r => !r.Guarded).Select(r => r.Name)];

        await Assert.That(rows.Count).IsEqualTo(2)
            .Because("the planted matrix declares two shards and both rows must parse, or the "
                + "control proves nothing. Parsed: " + Describe(rows));

        await Assert.That(unguarded.ToArray()).IsEquivalentTo(new[] { "e2e" })
            .Because("the planted row carries `guard: false`, so it must be reported by name. "
                + "Reported: " + string.Join(", ", unguarded) + ". A rule that reports nothing here "
                + "is blind in the real workflow too, and the green assertion above was not "
                + "evidence of anything.");

        // A row with NO `guard:` key at all is the same defect wearing different
        // syntax, and a reader that only ever matches `guard: <value>` would miss
        // it — which is how the next version of this hole gets written.
        //
        // Spelled as its own literal rather than produced by deleting a line from
        // the one above: a raw string literal is dedented by the closing
        // delimiter's indentation, so the spaces in an injected replacement string
        // have to match the DEDENTED text, not the text as written here. Getting
        // that wrong makes `Replace` a silent no-op, the "missing key" case never
        // gets exercised, and the assertion below passes on the same input twice.
        const string PlantedNoKey = """
            jobs:
              test:
                strategy:
                  matrix:
                    shard: [core, e2e]
                    include:
                      - shard: core
                        guard: true
                      - shard: e2e
                        projects: >-
                          Harbor.Something.Tests
            """;

        List<ShardRow> missing = ReadRows(PlantedNoKey);

        await Assert.That(missing.Count).IsEqualTo(2)
            .Because("both rows of the keyless matrix must parse, or the case below is not exercised "
                + "at all. Parsed: " + Describe(missing));

        await Assert.That(missing.Where(r => !r.Guarded).Select(r => r.Name).ToArray())
            .IsEquivalentTo(new[] { "e2e" })
            .Because("a shard row with no `guard:` key inherits no guard — the expansion compares "
                + "against the string \"true\", and an absent value is not it. Deleting the key must "
                + "be caught exactly like spelling `false`.");
    }

    /// <summary>
    ///     The other end of the same control: a guarded row must NOT be reported.
    /// </summary>
    /// <remarks>
    ///     A rule that condemns every row is not a guard, it is a wall — and a wall gets disabled,
    ///     which is how the real defect comes back. This is the assertion that keeps the failure
    ///     message worth reading.
    /// </remarks>
    [Test]
    public async Task TheRuleAcceptsAPlantedGuardedShard()
    {
        const string Planted = """
            jobs:
              test:
                strategy:
                  matrix:
                    shard: [core, ui]
                    include:
                      - shard: core
                        guard: true
                      - shard: ui
                        guard: true
            """;

        List<ShardRow> rows = ReadRows(Planted);

        await Assert.That(rows.Count).IsEqualTo(2)
            .Because("both planted rows must parse, or the negative control is vacuous. Parsed: "
                + Describe(rows));

        await Assert.That(rows.Where(r => !r.Guarded).Select(r => r.Name)).IsEmpty()
            .Because("every planted row carries `guard: true`. Reporting them means the rule "
                + "condemns any matrix at all, which is how a real violation ends up muted. Parsed: "
                + Describe(rows));
    }

    /// <summary>One row of the <c>test</c> job's matrix <c>include:</c> block.</summary>
    /// <param name="Name">The shard id, as written after <c>- shard:</c>.</param>
    /// <param name="GuardValue">
    ///     The literal text of the row's <c>guard:</c> value, or <see langword="null" /> when the row
    ///     carries no such key. Kept as the RAW string so the failure message can quote what the file
    ///     actually says rather than a normalised verdict.
    /// </param>
    private sealed record ShardRow(string Name, string? GuardValue)
    {
        /// <summary>Whether this row actually turns the zero-test guard on.</summary>
        /// <remarks>
        ///     The comparison is against the exact string <c>true</c> because the run step's own test
        ///     is `[ "${{ matrix.guard }}" = "true" ]`. YAML would accept <c>True</c>, <c>yes</c> and
        ///     <c>on</c> as booleans, and a value written that way becomes the string <c>"True"</c>
        ///     on the matrix — which the shell comparison rejects. So <c>yes</c> is NOT guarded here,
        ///     and treating it as guarded is precisely the silent hole this file exists to close.
        /// </remarks>
        internal bool Guarded => string.Equals(GuardValue, "true", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every shard row declared in the <c>test</c> job's matrix.
    /// </summary>
    /// <remarks>
    ///     Deliberately a line reader rather than a YAML parser: this repository has no YAML
    ///     dependency, and adding one for four rows of a matrix would be a heavier change than the
    ///     defect. The reader is bounded to the <c>test</c> job's own text so a <c>guard:</c> key
    ///     anywhere else in the file — including in a comment — cannot satisfy it.
    /// </remarks>
    private static List<ShardRow> ReadRows(string workflow)
    {
        var rows = new List<ShardRow>();
        string job = TestJobSection(workflow);

        string? name = null;
        string? guard = null;

        foreach (string raw in job.Split('\n'))
        {
            string line = raw.TrimEnd();

            // A new row: "- shard: e2e". Emits the previous row before starting this one.
            if (line.TrimStart().StartsWith("- shard:", StringComparison.Ordinal))
            {
                Emit();
                name = line[(line.IndexOf("shard:", StringComparison.Ordinal) + "shard:".Length)..].Trim();
                guard = null;
                continue;
            }

            // A guard key anywhere in the row — indented, with a space around the colon.
            // Anchored on the trimmed line so an indented `guard:` under a row is found
            // while a `# guard:` comment (which keeps its leading '#') is not.
            string trimmed = line.Trim();
            if (trimmed.StartsWith("guard:", StringComparison.Ordinal))
            {
                guard = trimmed["guard:".Length..].Trim();
            }
        }

        Emit();
        return rows;

        void Emit()
        {
            if (name is not null)
            {
                rows.Add(new ShardRow(name, guard));
            }
        }
    }

    /// <summary>
    ///     The body of the <c>test:</c> job, up to the next top-level job key.
    /// </summary>
    /// <remarks>
    ///     Job keys sit at two-space indentation. Cutting there is what keeps this file's verdict
    ///     about the <c>test</c> job alone: <c>coverage</c> and <c>test-os</c> have no matrix and no
    ///     guard key today, and if they grew one it would be a different job's decision to grade.
    /// </remarks>
    private static string TestJobSection(string workflow)
    {
        string[] lines = workflow.Replace("\r\n", "\n").Split('\n');

        int start = Array.FindIndex(lines, l => l.StartsWith("  test:", StringComparison.Ordinal));
        if (start < 0)
        {
            return string.Empty;
        }

        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
        {
            // A new job: two spaces, a key, then end-of-line — not `  test-os:`'s nested body,
            // which is indented deeper than this.
            string line = lines[i];
            if (line.Length > 2
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.Contains(':', StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>The <c>Run shard ${{ matrix.shard }}</c> step's script body.</summary>
    /// <remarks>
    ///     Read as the text between the step's <c>run: |</c> and the next step key, so the assertion
    ///     is about the shell that actually executes rather than about the whole file — the file
    ///     mentions <c>--minimum-expected-tests</c> in three other jobs, and a whole-file
    ///     <c>Contains</c> would pass on any of them.
    /// </remarks>
    private static string RunStepBody()
    {
        string[] lines = Workflow().Replace("\r\n", "\n").Split('\n');

        int start = Array.FindIndex(lines,
            l => l.Contains("- name: Run shard ", StringComparison.Ordinal));

        if (start < 0)
        {
            return string.Empty;
        }

        var body = new List<string>();
        for (int i = start; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();

            // The next step begins with "- name:" or "- uses:" at its own indent. Both
            // arms are parenthesised because `&&` binds tighter than `||` here, and an
            // unparenthesised pair of clauses is exactly how a guard ends up reading the
            // wrong span of the file without anything failing.
            bool nextNamedStep = trimmed.StartsWith("- ", StringComparison.Ordinal)
                                 && lines[i].Contains("name:", StringComparison.Ordinal);
            bool nextActionStep = trimmed.StartsWith("- uses:", StringComparison.Ordinal);

            if (i > start && (nextNamedStep || nextActionStep))
            {
                break;
            }

            body.Add(lines[i]);
        }

        return string.Join('\n', body);
    }

    private static string Workflow() => File.ReadAllText(Path.Combine(RequireRepoRoot(), WorkflowRelativePath));

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory + " — this guard reads "
            + WorkflowRelativePath + " and cannot run from a published test host. Returning quietly "
            + "here would report 'every shard is guarded' without having read the matrix at all.");
    }

    /// <summary>First non-empty line of a script body, for failure messages.</summary>
    private static string FirstLine(string body)
    {
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "(the step body was empty)";
    }

    private static string Describe(IReadOnlyList<ShardRow> rows)
        => rows.Count == 0
            ? "(none)"
            : string.Join(" | ", rows.Select(r => $"{r.Name}=>{r.GuardValue ?? "<no guard key>"}"));
}