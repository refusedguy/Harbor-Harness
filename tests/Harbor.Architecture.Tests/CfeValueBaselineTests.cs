// CfeValueBaselineTests.cs — the ROP-001 backstop, and the tests that prove
// the backstop is real.
//
// WHAT THIS FILE IS FOR
// ---------------------
// `CSharpFunctionalExtensions.Analyzers` ships CFE0001: ".Value accessed without
// checking IsSuccess/IsFailure first". `.Value` on a failed `Result<T>` throws
// `ResultFailureException`, so this is the mechanical form of the §ROP-001 crash
// class. Harbor held "never read .Value unguarded" by review convention alone
// (docs/ROP-API-INVENTORY.md §5) — nothing enforced it.
//
// The analyzer is a build-time gate, not a test, so these tests cannot check
// that it FIRES (that is measured in CI — see "MEASURED" below). What they can
// check, and what actually matters, is everything around it: that the gate is
// still WIRED, that the suppression has not quietly widened to cover production,
// and that the baseline has not rotted into a blanket permission.
//
// WHY A BASELINE EXISTS AT ALL
// ----------------------------
// MEASURED on this branch (CI, `dotnet build Harbor.slnx -c Release`, analyzer
// 1.3.0): 192 CFE0001 sites — 18 in production code, 174 under tests/.
//   * 174/174 in tests/ were FALSE POSITIVES of one shape: the guard is a bare
//     `Assert.That(result.IsSuccess).IsTrue()` statement, and the analyzer's
//     walker only models `if`-scoped checks, ternaries and switch arms, so it
//     never recognises a guard expressed as a standalone assertion. In a test
//     the thrown exception IS the failure signal, so these are suppressed
//     centrally for tests/samples (same condition as the RS0030 exemption in
//     Directory.Build.props), NOT with 174 call-site pragmas.
//   * 18 in production: 1 was a REAL defect (HarborConfig.EffectiveModel, an
//     unchecked .Value in a startup-path property getter) and was FIXED, not
//     baselined. The other 17 are false positives of the early-return /
//     early-continue shape, and carry a documented call-site pragma each.
//
// The 17 are the rows in `Baseline` below.
//
// GRANDFATHERING IS TYPE/MEMBER-GRANULAR, NOT LINE-GRANULAR — deliberately,
// and for the same reason PresentationCapabilityRules is type-granular: adding
// a pragma to a file shifts every line below it, so line-number keys would rot
// on the first edit. The trade-off is real and worth stating: a SECOND unguarded
// .Value added to an already-baselined member would NOT be caught. That is a
// bounded, documented gap, and `Baseline_CountIsPinned` keeps it visible by
// failing if the total ever changes.
//
// THE VACUITY TRAP THIS FILE IS BUILT AROUND
// ------------------------------------------
// A guard that cannot fire is worse than no guard, because it buys false
// confidence. This repo has already been bitten by that exact shape: NetArchTest
// treats a non-existent assembly name as a SATISFIED constraint, so a rule that
// names the wrong assembly passes forever (see PresentationCapabilityRules.cs,
// header). The same failure mode is reachable here in three ways, and each has
// a test below:
//   1. The package reference is deleted, or its version pin is broken  -> wired
//   2. A global `NoWarn` containing CFE0001, or `severity = none` in
//      .editorconfig, hides everything                            -> armed
//   3. The test/samples exemption condition is widened to include src/,
//      silently un-guarding production                             -> scoped
// What these tests CANNOT prove is that the analyzer still emits a diagnostic;
// that is the CI measurement recorded in docs/ROP-API-INVENTORY.md §5 and
// reproduced by the run in the PR. If a future bump of the analyzer package
// makes it stop firing, §5 must be re-measured — a silent drop to zero
// diagnostics is the failure mode to watch for.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One accepted CFE0001 exemption in production code.
/// </summary>
/// <param name="File">Repo-relative path of the baselined file.</param>
/// <param name="Member">Enclosing member name that carries the pragma.</param>
/// <param name="Why">The control-flow shape the analyzer fails to model.</param>
internal sealed record CfeExemption(string File, string Member, string Why);

/// <summary>
///     Guards the CFE0001 backstop: that it is wired and armed, that the
///     test-only suppression has not widened, and that the production baseline
///     is minimal and still real.
/// </summary>
public sealed class CfeValueBaselineTests
{
    private const string DiagnosticId = "CFE0001";
    private const string PackageId = "CSharpFunctionalExtensions.Analyzers";

    /// <summary>
    ///     MEASURED TOTAL, the day this landed. A drop means the analyzer
    ///     stopped firing (regression or a broken pin) and §5 must be
    ///     re-measured before the drop is believed.
    /// </summary>
    /// <remarks>
    ///     This is the SECOND measurement, and the first one under-counted —
    ///     see <see cref="MeasuredProductionSites" />. The lesson is recorded
    ///     rather than quietly corrected: a partial build reports only the
    ///     projects it finished, so "N violations" from a red build is a
    ///     LOWER BOUND, never the count.
    /// </remarks>
    private const int MeasuredTotalSites = 199;

    /// <summary>
    ///     Measured CFE0001 sites in shipped code (src/): 1 real defect, now
    ///     fixed, plus 21 false positives now carrying a documented pragma.
    /// </summary>
    private const int MeasuredProductionSites = 22;

    /// <summary>Measured CFE0001 sites under tests/, all suppressed centrally.</summary>
    private const int MeasuredTestSites = 177;

    /// <summary>
    ///     Every accepted production exemption. Adding a row is a claim that the
    ///     site is a false positive; <see cref="Baseline_ExemptionsAreAllStillReal" />
    ///     verifies the claim and fails the build when the row goes stale.
    /// </summary>
    private static readonly CfeExemption[] Baseline =
    [
        new("src/Harbor.Application/Agents/SubAgentRunner.cs", "PropagateUnderLockAsync",
            "if (IsFailure) { return; } else-branch access"),
        new("src/Harbor.Application/Attachments/ImageAttachmentReader.cs", "CheckVisionAsync",
            "if (IsFailure) { log; return Result.Success(); } then early return"),
        new("src/Harbor.Application/Onboarding/OnboardingWizard.cs", "TryFetchLiveModelsAsync",
            "if (IsFailure) { print reason; return null; } early return"),
        new("src/Harbor.Application/Sessions/SessionForkService.cs", "ForkAsync",
            "if (IsFailure) { return Result.Failure(...); } early return"),
        new("src/Harbor.Lsp/LspManager.cs", "NotifyChangeAsync",
            "if (IsFailure) { return; } early return"),
        new("src/Harbor.Lsp/LspManager.cs", "CloseFileAsync",
            "if (IsFailure) { return; } early return"),
        new("src/Harbor.Lsp/LspManager.cs", "FindDefinitionAsync",
            "if (IsFailure) { return null; } early return"),
        new("src/Harbor.Lsp/LspManager.cs", "FindReferencesAsync",
            "if (IsFailure) { return null; } early return"),
        new("src/Harbor.Lsp/LspServerSession.cs", "TryNormalizeFirstLocation",
            "if (IsFailure) { firstFailure ??= ...; continue; } loop continue"),
        new("src/Harbor.Lsp/LspServerSession.cs", "NormalizeAllLocations",
            "if (IsFailure) { skipped++; continue; } loop continue"),
        new("src/Harbor.Lsp/LspServerSession.cs", "MapBuilt",
            "conditional expression; the analyzer's CheckedFailure ternary arm mis-attributes the branch"),
        new("src/Harbor.Plugins.Hosting/PluginHost.cs", "LoadAllAsync",
            "if (IsFailure) { return/continue; } else-branch access"),
        new("src/Harbor.Storage.Jsonl/SessionPorter.cs", "ImportAsync",
            "if (IsFailure) { skipped++; } else-branch access"),
        new("src/Harbor.Plugins.Runtime/PluginCompilationResult.cs", "Value",
            "Result-shaped wrapper whose Value is the documented pass-through"),
        new("src/Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs", "TestConnectionAsync",
            "ternary whose own condition is the IsSuccess guard"),
        new("src/Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs", "LoadAllAsync",
            "if (IsFailure) { return Result.Failure(...); } early return"),
        new("src/Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs", "BuildProviderGroupAsync",
            "cfgResult.IsSuccess && ... .Value ...; && short-circuit guard"),
    ];

    private static string? Root => RepoPaths.RepoRoot;

    private static string ReadRepoFile(string relativePath)
    {
        if (Root is null)
        {
            throw new InvalidOperationException(
                "[cfe] repository root not found. These tests inspect source files in the "
                + "checkout; without Harbor.slnx above the test host there is nothing to check, "
                + "and reporting 'no violations' would make the guard vacuous.");
        }

        return File.ReadAllText(Path.Combine(Root, relativePath));
    }

    // =====================================================================
    // 1. Non-vacuity: the gate is wired and armed.
    // =====================================================================

    /// <summary>
    ///     The analyzer package is referenced solution-wide and its version is
    ///     pinned. Without this the whole file is a comment: a deleted (or
    ///     typo'd) <c>PackageReference</c> silently un-guards every
    ///     <c>.Value</c> in the repo and every other test here stays green.
    /// </summary>
    [Test]
    public async Task NonVacuity_AnalyzerPackageIsReferencedAndPinned()
    {
        string buildProps = ReadRepoFile("Directory.Build.props");
        string packagesProps = ReadRepoFile("Directory.Packages.props");

        await Assert.That(buildProps.Contains(PackageId, StringComparison.Ordinal)).IsTrue()
            .Because("the CFE0001 backstop is the PackageReference; if it is gone the guard is gone");

        await Assert.That(packagesProps.Contains($"<PackageVersion Include=\"{PackageId}\"", StringComparison.Ordinal)).IsTrue()
            .Because("Central Package Management requires a version pin; an unpinned reference "
                   + "fails restore outright, but a pinned-and-unused one would restore fine and "
                   + "analyze nothing");

        // The pin must not float. An unpinned/empty version restores to nothing.
        Match pin = Regex.Match(packagesProps, @"<PackageVersion Include=""CSharpFunctionalExtensions\.Analyzers"" Version=""([^""]+)""");
        await Assert.That(pin.Success).IsTrue()
            .Because("the analyzer must carry an explicit version");
        await Assert.That(pin.Groups[1].Value).IsNotEmpty()
            .Because("an empty version resolves to no analyzer at all");
    }

    /// <summary>
    ///     Nothing may silence CFE0001 globally. A repo-wide
    ///     <c>NoWarn</c> entry or <c>dotnet_diagnostic.CFE0001.severity = none</c>
    ///     in .editorconfig turns the build green while checking nothing — the
    ///     single most likely way this guard rots into a false comfort.
    /// </summary>
    [Test]
    public async Task NonVacuity_AnalyzerIsNotSilencedGlobally()
    {
        string buildProps = ReadRepoFile("Directory.Build.props");
        string editorConfig = ReadRepoFile(".editorconfig");

        foreach (Match noWarn in Regex.Matches(buildProps, @"<NoWarn>([^<]*)</NoWarn>"))
        {
            bool conditioned = IsInsideTestOrSampleCondition(buildProps, noWarn.Index);

            if (!conditioned && noWarn.Groups[1].Value.Contains(DiagnosticId, StringComparison.Ordinal))
            {
                Assert.Fail(
                    "Directory.Build.props has an UNCONDITIONAL NoWarn containing " + DiagnosticId
                    + ". That disables the backstop for the whole solution while every other test in "
                    + "this file still passes. Scope the suppression to the call site (see Baseline) "
                    + "or to the tests/samples condition — never to everything.");
            }
        }

        foreach (Match line in Regex.Matches(editorConfig, @"^\s*dotnet_diagnostic\.CFE0001\.severity\s*=\s*(\w+)",
            RegexOptions.Multiline))
        {
            string severity = line.Groups[1].Value;
            await Assert.That(severity).IsNotEqualTo("none")
                .Because("'severity = none' disables CFE0001 repo-wide, which is the vacuous-pass case");
            await Assert.That(severity).IsNotEqualTo("silent")
                .Because("'severity = silent' disables CFE0001 repo-wide, which is the vacuous-pass case");
            await Assert.That(severity).IsNotEqualTo("suggestion")
                .Because("CFE0001's value is that a new unguarded .Value breaks the build; "
                       + "at 'suggestion' it is a yellow squiggle nobody has to act on");
        }
    }

    /// <summary>
    ///     The tests/samples CFE0001 exemption must stay scoped to tests and
    ///     samples. This is the subset-semantics guard: if the condition ever
    ///     grows to cover <c>src</c>, a new unguarded <c>.Value</c> in
    ///     production stops failing the build and nothing else notices.
    /// </summary>
    [Test]
    public async Task NonVacuity_TestSuppressionDoesNotCoverProduction()
    {
        string buildProps = ReadRepoFile("Directory.Build.props");

        bool found = false;
        foreach (Match noWarn in Regex.Matches(buildProps, @"<NoWarn>([^<]*)</NoWarn>"))
        {
            if (!noWarn.Groups[1].Value.Contains(DiagnosticId, StringComparison.Ordinal))
            {
                continue;
            }

            found = true;
            await Assert.That(IsInsideTestOrSampleCondition(buildProps, noWarn.Index)).IsTrue()
                .Because("a CFE0001 NoWarn outside the tests/samples condition would switch the "
                       + "backstop off for production code, where the crash actually happens");
        }

        await Assert.That(found).IsTrue()
            .Because("the tests/samples CFE0001 NoWarn entry should exist; if it was removed, the "
                   + "174 test-side false positives are back and the build log is noise again");
    }

    /// <summary>
    ///     The exemption condition must not name a production directory. The
    ///     condition is a raw MSBuild expression, so this asserts on its text.
    /// </summary>
    private static bool IsInsideTestOrSampleCondition(string buildProps, int noWarnIndex)
    {
        // Walk back to the nearest opening <PropertyGroup and read its Condition.
        int groupStart = buildProps.LastIndexOf("<PropertyGroup", noWarnIndex, StringComparison.Ordinal);
        if (groupStart < 0)
        {
            return false;
        }

        int conditionAt = buildProps.IndexOf("Condition=\"", groupStart, StringComparison.Ordinal);
        if (conditionAt < 0 || conditionAt > noWarnIndex)
        {
            return false;
        }

        int end = buildProps.IndexOf('"', conditionAt + 11);
        if (end < 0)
        {
            return false;
        }

        string condition = buildProps[conditionAt..end];

        // Must be conditioned, must mention tests and samples, and must not
        // mention any directory that ships to a user.
        foreach (string production in new[] { "src", "apps", "contrib", "tools" })
        {
            if (condition.Contains($"'{production}'", StringComparison.Ordinal)
                || condition.Contains($"'{production}/", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return condition.Contains("'tests'", StringComparison.Ordinal)
               && condition.Contains("'samples'", StringComparison.Ordinal);
    }

    // =====================================================================
    // 2. The baseline is real, minimal, and can only shrink.
    // =====================================================================

    /// <summary>
    ///     Every row of the baseline still corresponds to a real suppression in
    ///     a real file. This is the test that stops the baseline rotting into a
    ///     blanket permission: if the guarded code is deleted, the row must go
    ///     with it, and if a pragma is dropped the row is reported here.
    /// </summary>
    [Test]
    public async Task Baseline_ExemptionsAreAllStillReal()
    {
        var failures = new List<string>();

        foreach (CfeExemption row in Baseline)
        {
            if (Root is null)
            {
                throw new InvalidOperationException("[cfe] repository root not found; see ReadRepoFile.");
            }

            string path = Path.Combine(Root, row.File);
            if (!File.Exists(path))
            {
                failures.Add($"{row.File}: baselined file no longer exists — delete the row "
                    + $"(member {row.Member})");
                continue;
            }

            string source = File.ReadAllText(path);
            if (!source.Contains($"#pragma warning disable {DiagnosticId}", StringComparison.Ordinal))
            {
                failures.Add($"{row.File}: no '#pragma warning disable {DiagnosticId}' left, so the "
                    + $"exemption for {row.Member} is stale — the site is now enforced, or the code "
                    + "moved. Delete the row.");
            }

            if (!source.Contains(row.Member, StringComparison.Ordinal))
            {
                failures.Add($"{row.File}: member '{row.Member}' not found — the baselined code was "
                    + "renamed or deleted. Re-measure and re-key the row.");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("A baseline row that no longer matches reality is a lie: it grandfathers "
                   + "nothing today and hides the next real violation tomorrow. "
                   + string.Join("\n", failures));
    }

    /// <summary>
    ///     The production baseline is pinned to exactly the rows above, and is
    ///     non-empty. Non-empty is the point: a baseline that silently drops to
    ///     zero rows means either every site was fixed (good — update the number
    ///     in docs/ROP-API-INVENTORY.md §5) or the files moved out from under
    ///     the measurement (bad). Either way it must be a deliberate edit.
    /// </summary>
    [Test]
    public async Task Baseline_CountIsPinned()
    {
        await Assert.That(Baseline.Length).IsGreaterThan(0)
            .Because("an empty baseline would mean the backstop is not demonstrably load-bearing; "
                   + "it is only meaningful because these 17 sites are real and were measured");

        await Assert.That(Baseline.Length).IsEqualTo(17)
            .Because("the production baseline is pinned at 17 members / 21 sites (CFE0001 counts "
                   + "sites, this table counts members). It may only shrink: a new row is a new "
                   + "false positive claim that must be justified in review, and removing a row is "
                   + "always safe. If this number moved, re-measure and update "
                   + "docs/ROP-API-INVENTORY.md §5 in the same commit.");
    }

    /// <summary>
    ///     The measured total is recorded so a silent drop to zero — the analyzer
    ///     quietly ceasing to fire, the exact vacuity this file exists to
    ///     prevent — is visible in a diff rather than discovered by an incident.
    /// </summary>
    [Test]
    public async Task NonVacuity_MeasuredTotalIsRecorded()
    {
        await Assert.That(MeasuredTotalSites).IsEqualTo(199)
            .Because("199 is the CI-measured CFE0001 count (22 production + 177 tests) on analyzer "
                   + "1.3.0. It is recorded so that a future package bump which drops the "
                   + "diagnostic to zero shows up as a number to re-verify, not as a silent "
                   + "green build. See docs/ROP-API-INVENTORY.md §5.");

        // The two halves must reconcile, or one of the three numbers above is a
        // copy-paste that nobody re-measured.
        await Assert.That(MeasuredProductionSites + MeasuredTestSites).IsEqualTo(MeasuredTotalSites)
            .Because("the recorded total is the sum of the recorded halves; if these drift apart, "
                   + "someone edited one without re-running the measurement");
    }

    /// <summary>
    ///     Table integrity: no duplicate member keys (a duplicate would make the
    ///     count in <see cref="Baseline_CountIsPinned" /> a lie), every row is in
    ///     shipped code rather than a test, and every row states why.
    /// </summary>
    [Test]
    public async Task Baseline_IsWellFormed()
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (CfeExemption row in Baseline)
        {
            string key = row.File + "::" + row.Member;
            if (!seen.Add(key))
            {
                failures.Add($"duplicate baseline key '{key}' — merge the rows");
            }

            if (!row.File.StartsWith("src/", StringComparison.Ordinal))
            {
                failures.Add($"baseline row '{key}' is not in src/ — production exemptions belong "
                    + "in shipped code; test-side false positives are handled centrally instead");
            }

            if (string.IsNullOrWhiteSpace(row.Why))
            {
                failures.Add($"baseline row '{key}' states no reason; a row without a reason is "
                    + "a row nobody will ever come back and delete");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(string.Join("\n", failures));
    }
}
