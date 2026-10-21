// OnboardingFireAndForgetRule — the onboarding wizard's background work stays observed.
//
// THE DEFECT (#1099, the #948 class — async void swallows everything after await)
// --------------------------------------------------------------------------------
// `OnboardingViewModel.Next` launched its async work with a bare discard:
// `_ = FinishAsync()` on the final step and `_ = LoadModelsAsync()` when
// entering step 4. Both callees are catch-all inside, but a fault on the
// catch path itself (a throwing toast, a torn-down message bus) still parks
// on the discarded task where only finalization could ever see it: no log,
// no surface. The surrounding member is a synchronous relay command and
// cannot await — which is not the defect; losing the fault is.
//
// The fix routes both detaches through the shared fire-and-forget helper
// with a fault sink logging through the view-model logger (the same shape
// the desktop shell guard blesses for synchronous contracts).
// This rule pins that shape.
//
// SCOPE — ONE METHOD, ON PURPOSE
// -------------------------------
// Only `Next` in
// `src/Harbor.Desktop.ViewModels/ViewModels/OnboardingViewModel.cs` is
// graded. The sibling sites from the same audit (the abandon-path sync
// reads) are real remainders, but widening this rule to them without fixing
// them would ship a red gate. They are left for the follow-ups that fix
// them, not graded here.
//
// NON-VACUITY
// -----------
//   1. `GradedFile_IsReadable` — the graded file was found and contains the
//      graded method. Without it, "no bare discard" and "the walk found
//      nothing" are the same report.
//   2. `FinishDetach_IsObserved` fails on the pre-fix line
//      (`_ = FinishAsync()` with no observer in the method) and passes on
//      the fixed shape, so a revert re-opens the hole AND reddens this gate.
//   3. `LoadModelsDetach_IsObserved` does the same for the catalog fetch:
//      `_ = LoadModelsAsync()` with no observer fails, the observed shape
//      passes. Each site carries its own test, so dropping either
//      continuation fails exactly one gate.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #1099 fix: both background detaches in the onboarding
///     wizard carry a fault observer instead of a bare task discard.
/// </summary>
public sealed class OnboardingFireAndForgetRule
{
    private static string WizardSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Desktop.ViewModels", "ViewModels", "OnboardingViewModel.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string[] MethodWindow(string[] lines, int start, int maxLines = 60)
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
            if (lines[i].Contains("void Next()", StringComparison.Ordinal))
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
            .Because("the rule reads OnboardingViewModel.cs from the checkout");

        string source = WizardSource();
        await Assert.That(source).IsNotEmpty()
            .Because("OnboardingViewModel.cs must be readable from the checkout");

        await Assert.That(source).Contains("void Next()")
            .Because("Next is the one detach site this rule grades; "
                   + "if it moved, this rule must be re-pointed, not silently weakened");

        await Assert.That(source).Contains("FinishAsync")
            .Because("FinishAsync is the save path this rule grades; "
                   + "if it was renamed, this rule must be re-pointed");

        await Assert.That(source).Contains("LoadModelsAsync")
            .Because("LoadModelsAsync is the catalog path this rule grades; "
                   + "if it was renamed, this rule must be re-pointed");
    }

    /// <summary>
    ///     #1099 fix, pinned (site 1 of 2): no bare save discard survives in
    ///     the wizard advance. The detach must carry its fault observer in
    ///     the same method, so dropping the continuation re-introduces the
    ///     exact pre-fix shape — and fails here.
    /// </summary>
    [Test]
    public async Task FinishDetach_IsObserved()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OnboardingViewModel.cs from the checkout");

        string[] lines = WizardSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("Next must exist in OnboardingViewModel.cs; if it "
                   + "was renamed, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("FinishAsync()")
            .Because("the save detach site must exist in Next; if the "
                   + "completion path was reworked, this rule must be re-pointed");

        await Assert.That(window.Contains("_ = FinishAsync()")).IsFalse()
            .Because("a bare save discard parks a wizard fault where only "
                   + "finalization can see it (#1099): the detach must stay observed");

        await Assert.That(window).Contains("TaskFireAndForget.Forget(")
            .Because("the save detach must go through the shared observed-detach "
                   + "helper: the surrounding command is synchronous and cannot "
                   + "await, which is not the defect; losing the fault is (#1099)");

        await Assert.That(window).Contains("Onboarding finish failed")
            .Because("the save observer must log through the fault sink: a detach "
                   + "without a sink observes nothing and is the pre-fix defect (#1099)");
    }

    /// <summary>
    ///     #1099 fix, pinned (site 2 of 2): no bare catalog-fetch discard
    ///     survives in the wizard advance. Same contract as the save site —
    ///     dropping this continuation fails exactly this test.
    /// </summary>
    [Test]
    public async Task LoadModelsDetach_IsObserved()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads OnboardingViewModel.cs from the checkout");

        string[] lines = WizardSource().Split('\n');
        int start = FindMethod(lines);
        await Assert.That(start).IsGreaterThanOrEqualTo(0)
            .Because("Next must exist in OnboardingViewModel.cs; if it "
                   + "was reworked, this rule must be re-pointed");

        string window = string.Join("\n", MethodWindow(lines, start).Select(StripLineComment));
        await Assert.That(window).Contains("LoadModelsAsync()")
            .Because("the catalog-fetch detach site must exist in Next; if the "
                   + "prefetch path was reworked, this rule must be re-pointed");

        await Assert.That(window.Contains("_ = LoadModelsAsync()")).IsFalse()
            .Because("a bare catalog-fetch discard parks a wizard fault where only "
                   + "finalization can see it (#1099): the detach must stay observed");

        await Assert.That(window).Contains("TaskFireAndForget.Forget(")
            .Because("the catalog-fetch detach must go through the shared "
                   + "observed-detach helper, same contract as the save site (#1099)");

        await Assert.That(window).Contains("Live model list failed")
            .Because("the catalog-fetch observer must log through the fault sink: "
                   + "a detach without a sink observes nothing (#1099)");
    }
}
