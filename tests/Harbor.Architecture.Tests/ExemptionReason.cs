// ExemptionReason.cs — the ONE place that answers "does this tolerated row
// state a reason?".
//
// WHY THIS FILE EXISTS
// --------------------
// This project has five tables whose rows are *permissions* — a baseline
// violation, a permanent capability, a documented layer exception, a
// declared-but-unbound reference, a plugin allowance. Before this file each one
// asked the same question in its own words, and only some of them asked it at
// all:
//
//   * `PresentationCapabilityRules.RuleTable_And_Baseline_Are_WellFormed`
//     blank-checks the reason on `PermanentCapabilities` only. A
//     `KnownViolations` row had no reason field AT ALL — its value was a
//     tracking-issue URL, and the prose explaining why the violation is
//     tolerated lived in a `//` comment above the row, which no compiler and no
//     runtime can see.
//   * `EnforcerIntegrityTests.DocumentedExceptions_AllHaveReasons` blank-checks
//     `FullLayerMatrixTests.DocumentedException.Reason`.
//   * `DeclaredButUnboundProjectReferences` carried a `Reason` that nothing
//     checked at all.
//
// Three different answers, written three times, and the row shape that most needs
// one — the tracked-violation baseline — is the one that had none. That is the
// form an exception takes when nobody has to justify it: add the row, paste the
// issue URL, move on. Six months later the row is stale, the liveness test fires,
// and the cheap repair is to re-add it, because the reason it was tolerated is
// not anywhere the tool can read.
//
// So the reason is promoted from a comment to a VALUE, and this file is the single
// check that a value is a reason. A row without one does not pass.
//
// WHAT COUNTS AS A REASON
// -----------------------
// Three rules, and the third is the one that matters:
//
//   1. Not blank. Trivial, but it was the whole check before this file, and
//      "not blank" alone is satisfied by a space.
//   2. Not the tracking issue again. A URL in the reason slot is the defect this
//      file exists to catch: it names WHERE the debt is tracked, not WHY it is
//      tolerated, and it reads as a reason precisely because it is not one.
//   3. At least `MinimumLength` characters. A sentence has to fit in it.
//      `later`, `TODO`, `see above` and `n/a` all pass a blank-check and all say
//      nothing; a floor turns them into build failures without trying to judge the
//      prose, which is not a thing a test can do honestly.
//
// Deliberately NOT checked: whether the reason is TRUE, or whether the debt it
// describes still exists. That is a different question, and the tables that can
// answer it already do it by liveness — every baseline row is re-probed against
// reality and fails when the violation it grandfathers is gone. This file only
// asks the narrower question: did someone write down the argument?

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The single check that an exemption row states why it is exempt. Shared by
///     every table in this project that grants a permission, so a fourth copy of
///     "is this blank?" cannot appear beside the third.
/// </summary>
internal static class ExemptionReason
{
    /// <summary>
    ///     The floor for a reason, in characters. A reason that fits in fewer
    ///     characters than this sentence has not stated an argument.
    /// </summary>
    internal const int MinimumLength = 40;

    /// <summary>Matches a bare URL standing alone in a reason slot.</summary>
    private static readonly Regex UrlOnly = new(
        @"^\W*https?://\S+[.,;]?\W*$",
        RegexOptions.Compiled);

    /// <summary>
    ///     One row of an exemption table, reduced to the two fields this check
    ///     reads. The table's own key is deliberately NOT a field here and is
    ///     passed alongside instead: a key is how the failure message finds the
    ///     row, not a property of the exemption, and putting it on the record
    ///     made it look optional — the first version of this file did exactly
    ///     that and did not compile.
    /// </summary>
    /// <param name="Reason">The reason, or <see langword="null" /> if there is none.</param>
    /// <param name="TrackedBy">The tracking issue URL, when the table has one.</param>
    internal readonly record struct Row(string? Reason, string? TrackedBy);

    /// <summary>
    ///     Every row of <paramref name="table" /> whose reason would not survive
    ///     review, described in terms of what to write instead. Empty means the
    ///     table is well formed.
    /// </summary>
    /// <param name="table">
    ///     The table's name, used verbatim in each message so the failure points at
    ///     the declaration rather than at this helper.
    /// </param>
    /// <param name="rows">The rows to check, each with the key its table names it by.</param>
    internal static IReadOnlyList<string> RowsWithoutAReason(
        string table,
        IEnumerable<(string Key, Row Row)> rows)
    {
        var failures = new List<string>();

        foreach ((string key, Row row) in rows)
        {
            string reason = row.Reason?.Trim() ?? string.Empty;

            if (reason.Length == 0)
            {
                failures.Add(
                    $"{table}['{key}'] states no reason — a row that grants a permission and does not "
                    + "say why is indistinguishable from an accident. Write the argument in the row "
                    + "itself, not in a comment above it: a comment is invisible to every tool that "
                    + "reads this table, so the reason is lost the moment the next person reformats "
                    + "the file.");
                continue;
            }

            // A URL is where the debt is tracked, not why it is tolerated. The
            // TrackedBy column already carries it; a reason that is only that URL
            // has been written in the wrong column.
            if (UrlOnly.IsMatch(reason)
                || (!string.IsNullOrWhiteSpace(row.TrackedBy) && reason == row.TrackedBy.Trim()))
            {
                failures.Add(
                    $"{table}['{key}'] has a tracking issue where its reason should be "
                    + $"('{TrimForMessage(reason)}') — a URL says where the debt is tracked, not why "
                    + "it is tolerated here. Add the argument, and keep the URL in its own column.");
                continue;
            }

            if (reason.Length < MinimumLength)
            {
                failures.Add(
                    $"{table}['{key}'] states a {reason.Length}-character reason "
                    + $"('{TrimForMessage(reason)}') — fewer than {MinimumLength} characters cannot be "
                    + "an argument. Say what makes this row necessary and what would remove it.");
            }
        }

        return failures;
    }

    /// <summary>Shortens a value so a failure message stays readable.</summary>
    private static string TrimForMessage(string value) =>
        value.Length <= 80 ? value : value[..77] + "...";
}
