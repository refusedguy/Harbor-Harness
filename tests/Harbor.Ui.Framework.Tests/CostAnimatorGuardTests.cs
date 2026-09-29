// CostAnimatorGuardTests.cs — guard for #676.
//
// CostAnimator used to be a second source of cost:
//
//     DisplayCost = _baseCost + (decimal)(elapsed.TotalSeconds * 0.0001);
//
// `$0.0001/сек` is a rate that exists nowhere in the core. Pricing.CalculateCost
// (src/Harbor.Abstractions.Contracts/Models/Session.cs) is the only authority for
// a dollar amount, and it is a function of a Usage snapshot — it has no notion
// of elapsed time, so there is no core quantity for a per-second rate to have
// come from. The number on screen was therefore a UI invention, growing
// smoothly, and it looked exactly like a live bill.
//
// The fix does not move the rate somewhere else in the UI; it removes the
// category. A cost is a fact the core reports, so the animator is now only a
// slide between two reported facts, and the invariants below say so:
//
//   1. the displayed value never leaves the hull [min, max] of what the core
//      reported (the source-level rule below enforces the same for the whole
//      UI framework, so the shape cannot reappear anywhere else);
//   2. a reported value does not drift while time passes — the direct #676
//      regression;
//   3. with nothing reported yet the readout is the em-dash placeholder, in
//      the same spirit as StatusBarText.CostCell's #457 rule ("no data ⇒ no
//      cell, never a bare $0.0000").
//
// The tests assert the display behaviour AND scan the source, because either
// half alone is satisfiable by a rewrite in the same layer: behaviour alone
// passes if the rate is spelled differently, and the scan alone passes if the
// fabrication moved behind a helper method. The source rule is pinned to the
// exact offending line by RuleStillCatchesTheOffendingShape, so it cannot rot
// into a pattern that matches nothing.

using System.Text.RegularExpressions;
using Harbor.Ui.Framework.Animation;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #676: the cost readout may only ever show a number the core reported.
/// </summary>
public class CostAnimatorGuardTests
{
    /// <summary>
    ///     An elapsed-time reading. A cost readout has no business consuming one:
    ///     the core reports what was spent, never how fast.
    /// </summary>
    private static readonly Regex ElapsedTimeRead =
        new(@"\bTotal(Milli)?Seconds\b", RegexOptions.Compiled);

    /// <summary>
    ///     Money vocabulary, so a line that also reads elapsed time is a line
    ///     deriving money from time.
    /// </summary>
    private static readonly Regex MoneyToken =
        new(@"cost|usd|price|spend|billing|budget", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The verbatim #676 line, kept so the rule below cannot drift off it.</summary>
    private const string OffendingLine =
        "DisplayCost = _baseCost + (decimal)(elapsed.TotalSeconds * 0.0001);";

    // ── behaviour: the readout is a function of reported values only ─────────

    [Test]
    public async Task A_Reported_Value_Does_Not_Drift_While_Time_Passes()
    {
        using var animator = new CostAnimator();
        animator.Start(0.0123m);

        // ~1 s of ticking. The old rate added $0.0001 over that window — four
        // orders of magnitude more than decimal rounding, so the margin does not
        // depend on how coarse the runner's clock is. The assertion is on the
        // maximum ever displayed, so a mid-window spike cannot hide behind an
        // equal final value.
        decimal highest = 0m;
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(25);
            animator.Advance();
            highest = Math.Max(highest, animator.DisplayCost);
        }

        await Assert.That(animator.DisplayCost)
            .IsEqualTo(0.0123m)
            .Because("elapsed wall time must never contribute a dollar amount (#676)");
        await Assert.That(highest)
            .IsEqualTo(0.0123m)
            .Because("the readout grew above the last value the core reported");
    }

    [Test]
    public async Task Displayed_Cost_Never_Leaves_The_Hull_Of_Reported_Values()
    {
        using var animator = new CostAnimator();
        animator.Start(0.0123m);

        var reported = new List<decimal> { 0.0123m };
        var observed = new List<decimal>();

        // Deliberately non-monotonic: a session switch or a re-priced context can
        // move the reported total down, and a slide has to survive that too.
        foreach (decimal value in new[] { 0.0300m, 0.0301m, 0.0075m, 0.0100m })
        {
            animator.BaseCost = value;
            reported.Add(value);
            await Task.Delay(30);
            animator.Advance();
            observed.Add(animator.DisplayCost);
        }

        // observed[i] is a frame drawn after reported[i + 1] was handed over
        // (reported[0] is the Start value), so the hull it must stay inside
        // spans reported[0..i + 1] — that is, the frame may not show more than
        // the core had said by then, nor less than the smallest thing it had
        // ever said in this run.
        for (int i = 0; i < observed.Count; i++)
        {
            decimal lowest = reported.Take(i + 2).Min();
            decimal highest = reported.Take(i + 2).Max();

            await Assert.That(observed[i])
                .IsGreaterThanOrEqualTo(lowest)
                .Because($"frame {i} dipped below the smallest value the core ever reported");
            await Assert.That(observed[i])
                .IsLessThanOrEqualTo(highest)
                .Because($"frame {i} showed more than the core had reported so far (#676)");
        }
    }

    [Test]
    public async Task A_Slide_Moves_Between_The_Two_Reported_Values_And_Lands_Exactly()
    {
        using var animator = new CostAnimator();
        animator.Start(0.0123m);
        animator.BaseCost = 0.0300m;

        // Handing over a new figure arms a slide; it does not apply it. A
        // snapping implementation is already on 0.0300 here, so this single
        // assertion carries the whole "it travels, it does not jump" claim
        // without depending on catching a lucky mid-slide frame — which a
        // loaded CI runner may never offer. Time-free by construction: no await
        // separates the assignment from the reading.
        await Assert.That(animator.DisplayCost).IsEqualTo(0.0123m)
            .Because("the readout jumped to the new value instead of starting a slide toward it");

        // Every frame of the slide stays between the two reported numbers, and
        // the slide ends ON the second one — exactly, not near it. Polled rather
        // than slept for a fixed span, so this states the requirement instead of
        // pinning the slide's duration.
        decimal lowest = 0.0300m;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (animator.DisplayCost != 0.0300m && DateTime.UtcNow < deadline)
        {
            lowest = Math.Min(lowest, animator.DisplayCost);
            await Task.Delay(20);
            animator.Advance();
        }

        await Assert.That(animator.DisplayCost).IsEqualTo(0.0300m)
            .Because("a finished slide must land on the reported value, not near it");
        await Assert.That(lowest).IsGreaterThanOrEqualTo(0.0123m)
            .Because("a frame dipped below the value the core last reported");
        await Assert.That(animator.DisplayCost).IsLessThanOrEqualTo(0.0300m)
            .Because("the slide overshot the value the core reported");
    }

    [Test]
    public async Task With_Nothing_Reported_The_Readout_Is_Not_A_Number()
    {
        using var animator = new CostAnimator();

        await Assert.That(animator.AnimatedText).IsEqualTo("—")
            .Because("an unreported cost is absent, not $0.0000 (#457: no data ⇒ no cell)");
        await Assert.That(animator.DisplayCost).IsEqualTo(0m);

        // Ticking before anything arrived must not conjure a value either.
        await Task.Delay(20);
        animator.Advance();

        await Assert.That(animator.AnimatedText).IsEqualTo("—");
        await Assert.That(animator.DisplayCost).IsEqualTo(0m);
    }

    [Test]
    public async Task A_Reported_Zero_Renders_As_Zero_Rather_Than_As_A_Dash()
    {
        // The dash means "the core has not said", not "the cost is zero". The
        // zero-is-not-a-cell decision belongs to StatusBarText.CostCell, which
        // the view models bind to; the animator reports what it was told.
        using var animator = new CostAnimator();
        animator.Start(0m);

        await Assert.That(animator.AnimatedText).IsEqualTo("$0.0000");
    }

    [Test]
    public async Task Stop_Freezes_The_Readout_And_Advance_Becomes_A_No_Op()
    {
        using var animator = new CostAnimator();
        animator.Start(0.0123m);
        animator.Stop();

        await Task.Delay(30);
        animator.Advance();

        await Assert.That(animator.IsRunning).IsFalse();
        await Assert.That(animator.DisplayCost).IsEqualTo(0.0123m);
    }

    [Test]
    public async Task Tick_Fires_While_Running_And_Not_After_Stop()
    {
        using var animator = new CostAnimator();
        int ticks = 0;
        animator.Tick += () => ticks++;

        animator.Start(0.0123m);
        animator.Advance();
        await Assert.That(ticks).IsEqualTo(1)
            .Because("the host repaints from Tick, so a running slide must raise it");

        animator.Stop();
        animator.Advance();
        await Assert.That(ticks).IsEqualTo(1)
            .Because("a stopped animator told the host to stop ticking");
    }

    // ── source: the shape is gone from the whole UI framework ───────────────

    [Test]
    public async Task No_Line_In_The_Ui_Framework_Derives_A_Cost_From_Elapsed_Time()
    {
        string? root = FindRepoRoot();
        await Assert.That(root).IsNotNull().Because("the perimeter must be reachable for this gate to mean anything");

        List<string> offenders = [];
        int scanned = 0;

        foreach (string project in Directory.GetDirectories(Path.Combine(root!, "src"), "Harbor.Ui.Framework*"))
        {
            foreach (string file in Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (DerivesCostFromElapsedTime(lines[i]))
                    {
                        offenders.Add(
                            $"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(0)
            .Because("an empty scan makes the rule vacuously green");
        await Assert.That(offenders).IsEmpty()
            .Because("money must come from the core, not from elapsed time (#676)");
    }

    [Test]
    public async Task CostAnimator_Is_Still_Inside_The_Scanned_Perimeter()
    {
        // The rule above is only as good as its reach. Naming the file it was
        // written for keeps a rename or a move from silently disarming it.
        string? root = FindRepoRoot();
        await Assert.That(root).IsNotNull();

        string animator = Path.Combine(
            root!, "src", "Harbor.Ui.Framework.ViewModels", "Animation", "CostAnimator.cs");

        await Assert.That(File.Exists(animator)).IsTrue()
            .Because("CostAnimator is the type #676 is about; if it moved, re-point this gate");
    }

    [Test]
    public async Task Rule_Still_Catches_The_Offending_Shape_From_676()
    {
        await Assert.That(DerivesCostFromElapsedTime(OffendingLine)).IsTrue()
            .Because("the rule must still match the line it was written against");

        // The same fabrication with the constant hoisted into a field — the way
        // it would come back if someone "tidied" the original.
        await Assert.That(DerivesCostFromElapsedTime("DisplayCost = _baseCost + (decimal)(elapsed.TotalSeconds * _usdPerSecond);"))
            .IsTrue();

        // A negative control: a duration formatter is not a cost leak, and a
        // rule that flagged it would be turned off rather than obeyed.
        await Assert.That(DerivesCostFromElapsedTime("public string DurationText => Duration.TotalSeconds < 1"))
            .IsFalse();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether one source line reads elapsed time AND talks about money —
    ///     the shape of "this dollar amount is a function of how long we waited".
    /// </summary>
    private static bool DerivesCostFromElapsedTime(string line)
        => ElapsedTimeRead.IsMatch(line) && MoneyToken.IsMatch(line);

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
