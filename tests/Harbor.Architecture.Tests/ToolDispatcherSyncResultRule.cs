// ToolDispatcherSyncResultRule — the AwaitOne abandon path stays async.
//
// THE DEFECT (#1099, sync-over-async on abandon paths)
// -----------------------------------------------------
// `ToolDispatcher.AwaitOneWithAbandonAsync` read the finished task with a
// blocking `pending.Result` behind `IsCompletedSuccessfully` (two sites: the
// boundary fast-path and the post-grace ternary). The guard makes the block a
// no-wait today, but the spelling is still sync-over-async: it throws
// AggregateException instead of the task's exception if the guard ever drifts,
// and it is the shape a copy-paste carries into a path where the task is NOT
// complete. The fix is the plain `await pending.ConfigureAwait(false)` —
// identical on the guarded path, honest on every other.
//
// SCOPE — ONE METHOD, ON PURPOSE
// ------------------------------
// Only `AwaitOneWithAbandonAsync` in
// `src/Harbor.Application/Agents/ToolDispatcher.cs` is graded. The sibling
// `AwaitAllWithAbandonAsync` carries the same smell (`all.Result`,
// `tasks[i].Result`) and is a real remainder of the same audit, but widening
// this rule to it without fixing it would ship a red gate. It is left for the
// follow-up that fixes it, not graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no sync read" and "the walk found nothing"
//      are the same report.
//   2. `AwaitOne_CarriesNoSyncResult` fails on the pre-fix lines
//      (`return pending.Result;`, `? pending.Result`) and passes on the fixed
//      shape, so a revert re-opens the hole AND reddens this gate.
//   3. `AwaitOne_PinsAsyncRead` requires the `await pending` spelling inside
//      the guarded branches, so deleting the branches (or swapping in a
//      different sink) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the AwaitOne abandon path reads the finished task
///     with <c>await</c>, never a blocking <c>.Result</c>.
/// </summary>
public sealed class ToolDispatcherSyncResultRule
{
    private static string DispatcherSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Application", "Agents", "ToolDispatcher.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 30)
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

    private static int FindMethod(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("AwaitOneWithAbandonAsync", StringComparison.Ordinal)
                && lines[i].Contains("Task<ToolResultEntry>", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     The graded file is readable and contains the graded method. A gate
    ///     that cannot see its subject reports a healthy zero.
    /// </summary>
    [Test]
    public async Task GradedFile_IsReadable()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string source = DispatcherSource();
        await Assert.That(source).IsNotEmpty()
            .Because("ToolDispatcher.cs must be readable from the checkout");

        await Assert.That(source).Contains("AwaitOneWithAbandonAsync")
            .Because("AwaitOneWithAbandonAsync is the one abandon path this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no blocking <c>.Result</c> read survives in the
    ///     AwaitOne abandon path. Every guarded branch awaits instead, so
    ///     re-introducing either pre-fix line fails here.
    /// </summary>
    [Test]
    public async Task AwaitOne_CarriesNoSyncResult()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitOneWithAbandonAsync must exist in ToolDispatcher.cs; if it "
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
            .Because("a blocking .Result read in AwaitOneWithAbandonAsync is sync-over-async "
                   + "(#1099): the guarded branches must await the finished task instead. "
                   + "Offending 1-based lines: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The guarded branches carry the async read: dropping the branches
    ///     (or swapping in a different sink) must not silently satisfy the rule.
    /// </summary>
    [Test]
    public async Task AwaitOne_PinsAsyncRead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("AwaitOneWithAbandonAsync must exist in ToolDispatcher.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("await pending.ConfigureAwait(false)")
            .Because("the guarded branches must read the finished task with "
                   + "await pending.ConfigureAwait(false), not a blocking take (#1099)");
    }
}
