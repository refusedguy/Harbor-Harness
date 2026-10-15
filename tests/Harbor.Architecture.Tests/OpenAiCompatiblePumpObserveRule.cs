// OpenAiCompatiblePumpObserveRule — the OpenAI-compatible SSE pump stays observed.
//
// THE DEFECT (#1099, the #948 class — async void swallows everything after await)
// --------------------------------------------------------------------------------
// `OpenAiCompatibleLlmClient.StreamAsync` parks the SSE pump on a detached
// `Task.Run`. The delegate is catch-all inside, but the catch-path
// `writer.WriteAsync(..., cancellationToken)` can still fault (a cancelled
// reader throws the error report itself), and a bare `_ = Task.Run(...`
// discard parks that fault where only `TaskScheduler.UnobservedTaskException`
// at finalization could ever see it: no log, no surface. (#1104 already
// removed the `cancellationToken` from `Task.Run` so a pre-cancelled token
// cannot skip the delegate and orphan `writer.TryComplete()`; this rule pins
// the remaining half — the fault observer.)
//
// The fix chains an `OnlyOnFaulted` continuation that logs via the existing
// `OpenAiCompatibleLlmClientLog.StreamTaskFailed` sink (same shape as
// `ToolDispatcher.AbandonLeftoverAsync`, blessed by docs/ANTIPATTERNS.md §9).
// This rule pins that shape.
//
// SCOPE — ONE METHOD, ON PURPOSE
// -------------------------------
// Only `StreamAsync` in
// `src/Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs` is
// graded. The sibling fire-and-forget / sync-over-async sites from the same
// audit (OnboardingVM x2, `ToolDispatcher.AwaitAllWithAbandonAsync` `.Result`
// x2) are real remainders, but widening this rule to them without fixing them
// would ship a red gate. They are left for the follow-ups that fix them, not
// graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no bare discard" and "the walk found
//      nothing" are the same report.
//   2. `PumpDetach_IsObserved` fails on the pre-fix line
//      (`_ = Task.Run(async () =>` with no `OnlyOnFaulted` in the method)
//      and passes on the fixed shape, so a revert re-opens the hole AND
//      reddens this gate.
//   3. `PumpDetach_PinsOnlyOnFaulted` requires the faulted-only
//      continuation, so swapping in a different sink (or a bare await that
//      would serialize the pump) does not silently satisfy the rule.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: the OpenAI-compatible SSE pump carries an
///     <c>OnlyOnFaulted</c> fault observer instead of a bare task discard.
/// </summary>
public sealed class OpenAiCompatiblePumpObserveRule
{
    private static string PumpSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Providers.OpenAiCompatible", "OpenAiCompatibleLlmClient.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 130)
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
            if (lines[i].Contains("StreamAsync", StringComparison.Ordinal)
                && lines[i].Contains("IAsyncEnumerable<LlmEvent>", StringComparison.Ordinal))
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
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string source = PumpSource();
        await Assert.That(source).IsNotEmpty()
            .Because("OpenAiCompatibleLlmClient.cs must be readable from the checkout");

        await Assert.That(source).Contains("StreamAsync")
            .Because("StreamAsync is the one pump site this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");
    }

    /// <summary>
    ///     #1099 fix, pinned: no bare <c>_ = Task.Run(</c> discard survives
    ///     in the pump. The detach must carry its fault observer in the same
    ///     statement chain, so dropping the continuation re-introduces the
    ///     exact pre-fix shape — and fails here.
    /// </summary>
    [Test]
    public async Task PumpDetach_IsObserved()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string[] lines = PumpSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("StreamAsync must exist in OpenAiCompatibleLlmClient.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("Task.Run(")
            .Because("the pump detach site must exist in StreamAsync; if the "
                   + "scheduling was reworked, this rule must be re-pointed");

        await Assert.That(window).Contains("TaskContinuationOptions.OnlyOnFaulted")
            .Because("a bare _ = Task.Run( detach parks a pump fault where only "
                   + "finalization can see it (#1099): the detach must chain its "
                   + "OnlyOnFaulted observer in the same method");
    }

    /// <summary>
    ///     The chained observer is fault-only: a continuation without
    ///     <c>OnlyOnFaulted</c> would run on success too and could mask the
    ///     normal path, while no continuation at all is the pre-fix defect.
    /// </summary>
    [Test]
    public async Task PumpDetach_PinsOnlyOnFaulted()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OpenAiCompatibleLlmClient.cs from the checkout");

        string[] lines = PumpSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("StreamAsync must exist in OpenAiCompatibleLlmClient.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains(".ContinueWith(")
            .Because("the detach observer must be a chained ContinueWith on the pump "
                   + "task, not a second completion path (#1099)");

        await Assert.That(window).Contains("StreamTaskFailed")
            .Because("the detach observer must reuse the StreamTaskFailed sink: it is "
                   + "the pump's fault surface, not a new log shape (#1099)");
    }
}
