// BackgroundTaskSyncResultRule — the background-task drain path stays async.
//
// THE DEFECT (#1099, sync-over-async on abandon/background paths)
// ----------------------------------------------------------------
// `BackgroundTaskRegistry.DrainCompleted` read the finished run with a
// blocking `task.Result` behind `IsCompletedSuccessfully` inside `Observe`.
// The guard makes the block a no-wait today, but the spelling is still
// sync-over-async: it throws AggregateException instead of the run's
// exception if the guard ever drifts, and it is the shape a copy-paste
// carries into a path where the task is NOT complete. The fix is the plain
// `await task.ConfigureAwait(false)` inside `ObserveAsync`, surfaced through
// `DrainCompletedAsync` — identical on the guarded path, honest on every
// other. (`BackgroundDrain` needs no change beyond awaiting the drain: its
// `completion.Result.Match` reads an already-materialized ROP
// `Result<SubAgentRunResult>`, not a `Task`.)
//
// SCOPE — ONE CLASS, ON PURPOSE
// -----------------------------
// Only `BackgroundTaskRegistry` in
// `src/Harbor.Application/Agents/BackgroundTaskRegistry.cs` is graded
// (`DrainCompletedAsync` + `ObserveAsync`). The sibling abandon-path sync
// reads elsewhere in #1099 (OnboardingVM x2, OpenAiCompatible `Task.Run`)
// are real remainders of the same audit, but widening this rule to them
// without fixing them would ship a red gate. They are left for the
// follow-ups that fix them, not graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded methods. Without it, "no sync read" and "the walk found nothing"
//      are the same report.
//   2. `Drain_CarriesNoSyncResult` fails on the pre-fix lines
//      (`return task.Result;` in `Observe`) and passes on the fixed shape,
//      so a revert re-opens the hole AND reddens this gate.
//   3. `Drain_PinsAsyncRead` requires the `await task` spelling inside the
//      observer, so deleting the observer (or swapping in a different sink)
//      does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the background-task drain path reads the finished
///     run with <c>await</c>, never a blocking <c>.Result</c>.
/// </summary>
public sealed class BackgroundTaskSyncResultRule
{
    private static string RegistrySource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Application", "Agents", "BackgroundTaskRegistry.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 40)
    {
        int end = Math.Min(lines.Length, start + maxLines);
        var window = new string[end - start];
        Array.Copy(lines, start, window, 0, end - start);
        return window;
    }

    private static string StripLineComment(string line)
    {
        int at = line.IndexOf("//", StringComparison.Ordinal);
        return at >= 0 ? line[..at] : line;
    }

    private static int FindObserver(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("ObserveAsync", StringComparison.Ordinal)
                && lines[i].Contains("Task<Result<SubAgentRunResult>>", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The graded file is readable and contains the graded methods. A gate
    ///     that cannot see its subject reports a healthy zero.
    /// </summary>
    [Test]
    public async Task GradedFile_IsReadable()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads BackgroundTaskRegistry.cs from the checkout");

        string source = RegistrySource();
        await Assert.That(source).IsNotEmpty()
            .Because("BackgroundTaskRegistry.cs must be readable from the checkout");

        await Assert.That(source).Contains("DrainCompletedAsync")
            .Because("DrainCompletedAsync is the one drain path this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");

        await Assert.That(source).Contains("ObserveAsync")
            .Because("ObserveAsync is the one observer this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no blocking <c>.Result</c> read survives in the
    ///     background-task drain path. The finished run is awaited instead,
    ///     so re-introducing the pre-fix line fails here.
    /// </summary>
    [Test]
    public async Task Drain_CarriesNoSyncResult()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads BackgroundTaskRegistry.cs from the checkout");

        string[] lines = RegistrySource().Split('\n');
        int start = FindObserver(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("ObserveAsync must exist in BackgroundTaskRegistry.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        var offenders = new List<int>();
        string[] window = MethodWindow(lines, start);
        for (int k = 0; k < window.Length; k++)
        {
            string code = StripLineComment(window[k]);
            if (code.Contains(".Result", StringComparison.Ordinal))
            {
                offenders.Add(start + k + 1);
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("a blocking .Result read in ObserveAsync is sync-over-async "
                   + "(#1099): the observer must await the finished run instead. "
                   + "Offending 1-based lines: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The observer carries the async read: dropping the observer
    ///     (or swapping in a different sink) must not silently satisfy the rule.
    /// </summary>
    [Test]
    public async Task Drain_PinsAsyncRead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads BackgroundTaskRegistry.cs from the checkout");

        string[] lines = RegistrySource().Split('\n');
        int start = FindObserver(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("ObserveAsync must exist in BackgroundTaskRegistry.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("await task.ConfigureAwait(false)")
            .Because("the observer must read the finished run with "
                   + "await task.ConfigureAwait(false), not a blocking take (#1099)");
    }
}
