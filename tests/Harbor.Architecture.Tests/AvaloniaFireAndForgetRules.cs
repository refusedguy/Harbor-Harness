// AvaloniaFireAndForgetRules.cs — source-level guard for §FP-003 in the Avalonia
// desktop shell (issue #569).
//
// THE DEFECT THIS GUARDS
// ----------------------
// `_ = SomeAsync()` throws away a Task with nobody watching it. .NET Core does
// NOT surface an unobserved fault at the throw site: the exception is parked on
// the Task and the process-wide `TaskScheduler.UnobservedTaskException` event
// fires only at finalization — which for a desktop app that lives for hours is
// effectively never. The result is a save/open/diagnostic that failed silently:
// no log, no toast, no red surface.
//
// `Harbor.Abstractions.Tools.TaskFireAndForget.Forget` exists for exactly this
// case. Its own XML doc states the rule this file enforces:
//
//     "The `Run` contract of effect hosts stays synchronous, so awaiting is
//      not an option — but every fault is now observable."
//
// WHY A SOURCE-TEXT RULE AND NOT AN ANALYZER
// ------------------------------------------
// Two reasons, both deliberate.
//
//   1. The interesting sites are *expression-bodied* discards
//      (`private void OpenFile() => _ = _workspaceCommands.OpenFileAsync();`)
//      and *statement-position discards inside a void member*
//      (`public void BranchSession() => _sessions.BranchCommand.ExecuteAsync(null);`).
//      The second shape has no `_ =` at all — a grep for `_ = ` cannot find it,
//      which is exactly how it survived the original audit. A discard rule has
//      to look at the enclosing member's return type, which is what
//      `IsUnobservedTaskDiscard` does below.
//   2. The rule spans `apps/Harbor.App.Avalonia`, which this test project does
//      not reference (and must not: it is an app, a composition root). A
//      source-text scan needs no reference edge, exactly as MaybeAbsenceTests
//      documents for its own app-scoped rules.
//
// SCOPE — AND WHY IT IS THIS NARROW
// ---------------------------------
// The guard covers `apps/Harbor.App.Avalonia` ONLY. That is the perimeter the
// #569 fix actually reworked; the Blazor shell was deliberately left out of
// scope by the owner, so policing it here would be a guard that cries wolf on
// code nobody agreed to change. Guarding a perimeter you did not convert is
// not "stricter", it is a lie about what was reviewed.
//
// If a later wave converts another shell, add that path to the rule's `Trees`
// in the same commit that converts it. Widening this list without converting
// is the one way to make this file worthless.
//
// THE RULE, IN FULL
// -----------------
// Inside a guarded project, a statement that evaluates a Task-shaped call and
// discards the result is a build failure. There is exactly one accepted form:
// a `TaskFireAndForget.Forget(...)` call whose second argument is a fault sink.
// (Spelled without an indented code block on purpose — S125 reads an indented
// line under a `//` banner as commented-out code and warns on it.)
//
// There are ZERO exemptions, and that is on purpose. docs/ANTIPATTERNS.md §9
// also blesses a hand-rolled
// `.ContinueWith(t => log(t.Exception), TaskContinuationOptions.OnlyOnFaulted)`,
// and two such sites existed in this app before the fix — but "does this
// multi-line statement happen to mention OnlyOnFaulted on its fourth line" is a
// rule a future contributor can defeat by reformatting. The helper is one call
// with a mandatory sink, so it is what the guard demands. Both hand-rolled
// sites were converted, so nothing needed an exemption to land.
//
// NON-VACUITY
// -----------
// A source-text guard that silently matches nothing is worse than no guard: it
// buys confidence it has not earned, and this repository has already been
// bitten by exactly that shape (see PresentationCapabilityRules.cs, "MECHANISM
// — AND THE TRAP THIS FILE IS BUILT AROUND"). Three things defend against it:
//
//   * `Scanner_FindsTheGuardedProject` — the file walk really found the app.
//   * `Detector_FiresOnKnownBadAndStaysQuietOnKnownGood` — the detector, in
//     isolation, fires on a hand-written violation and stays quiet on the
//     conforming shape AND on the ~200 non-Task `_ =` discards that legitimately
//     exist in this codebase (`_ = sb.Append(...)`, `_ = store.Dispatch(...)`).
//   * `Detector_FiresOnTheVoidMemberThatDropsTheTaskEntirely` — pins the shape
//     that has no `_ =` and therefore no grep for it.
//
// A rule that cannot fail is not a rule. These tests are what make this one
// failable.
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The rule below is a ScanRule with a CustomParse: the verdict needs the
// per-line filter the old file walk applied (blank and `//` lines dropped,
// everything else graded raw — block comments included, exactly as before) plus
// the accepted-helper carve-out, neither of which a shared line scan expresses.
// So the rule plugs `ParseFireAndForget` through the Func-overload, over the
// same predicates. The `Forbidden` row documents the three discard shapes as
// one alternation composed from the same fields — not a copy — so the row
// cannot drift from the parser. Enumeration, baseline and the
// control/discovery verdicts are ScanRunner's; this file keeps the issue prose
// and the test names.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §FP-003 guard: no unobserved Task discard in the Avalonia desktop shell.
/// </summary>
public class AvaloniaFireAndForgetRules
{
    private const string SubId = "UNOBSERVED-TASK-DISCARD";

    /// <summary>
    ///     A call whose result is a Task (or a Task-shaped awaitable). Used as
    ///     the RIGHT-hand side test for a discard, so that the hundreds of
    ///     legitimate non-Task discards in this codebase — <c>_ = sb.Append(x)</c>,
    ///     <c>_ = store.Dispatch(msg)</c>, <c>_ = Interlocked.Increment(ref n)</c> —
    ///     are not swept up with the Task discards.
    /// </summary>
    /// <remarks>
    ///     Deliberately conservative: a Task-returning call this fails to
    ///     recognise produces a false negative (the guard misses a real site),
    ///     never a false positive that would block unrelated work. Every shape
    ///     actually present in the guarded tree is covered — see
    ///     <see cref="Detector_FiresOnEveryTaskShapePresentInTheGuardedTree" />.
    /// </remarks>
    /// <remarks>
    ///     <b>The outer <c>(?:…)</c> is load-bearing.</b> This pattern is an
    ///     alternation, and both rules below concatenate it after a prefix. An
    ///     ungrouped alternation splices at the TOP level, so
    ///     <c>prefix + A|B|C</c> parses as <c>(prefix+A) | B | C</c> — and the
    ///     rule then fires on <em>any</em> line containing <c>.AsTask(</c> or
    ///     <c>.ContinueWith(</c>, discarding lines that have nothing to do with
    ///     fire-and-forget. That is a false-positive machine, and it was caught
    ///     by running the rule over the real tree after the conversion, not by
    ///     inspection. <see cref="Detector_IgnoresTaskShapedCallsThatAreObserved" />
    ///     pins it.
    /// </remarks>
    private static readonly Regex TaskShapedCall = new(
        @"(?:"
        + @"(?:\w*Async|Task\.Run|Task\.Delay|Task\.WhenAll|Task\.Factory\.StartNew)\s*\("
        + @"|\.AsTask\s*\(\)"
        + @"|\.ContinueWith\s*\("
        + @")",
        RegexOptions.Compiled);

    /// <summary>
    ///     The explicit discard form: <c>_ = </c> immediately followed by a
    ///     Task-shaped call. Matches both statement position
    ///     (<c>_ = RefreshAsync();</c>) and expression-bodied member position
    ///     (<c>private void SaveFile() => _ = _workspaceCommands.SaveFileAsync();</c>).
    /// </summary>
    private static readonly Regex ExplicitDiscard = new(
        @"(?<![A-Za-z0-9_.])_ = \s*(?<rhs>[^\n]*" + TaskShapedCall + @")",
        RegexOptions.Compiled);

    /// <summary>
    ///     The implicit discard form: a <c>void</c> member whose body IS a
    ///     Task-shaped call. There is no <c>_ = </c> to grep for — the compiler
    ///     accepts <c>void M() => _sessions.BranchCommand.ExecuteAsync(null);</c>
    ///     without a word, and the Task is dropped on the floor. This is the
    ///     shape that made <c>AvaloniaWorkspaceCommands</c> the single worst site
    ///     in the app while remaining invisible to the original audit.
    /// </summary>
    private static readonly Regex VoidMemberDroppingTask = new(
        @"\bvoid\s+[\w<>,\[\]?\. ]*\s+\w+\s*\([^)]*\)\s*=>\s*[^;{]*" + TaskShapedCall,
        RegexOptions.Compiled);

    /// <summary>
    ///     The same implicit-discard shape for a <c>void</c> member with an EMPTY
    ///     parameter list (<c>public void BranchSession() =&gt; …</c>). Kept as a
    ///     separate pattern rather than loosening the one above, because
    ///     <c>\([^)]*\)</c> already matches <c>()</c> — but only if the
    ///     return-type/name separator below it is also allowed to be empty, and
    ///     tightening that would start matching unrelated <c>void</c> members.
    /// </summary>
    private static readonly Regex VoidParameterlessMemberDroppingTask = new(
        @"\bvoid\s+\w+\s*\(\s*\)\s*=>\s*[^;{]*" + TaskShapedCall,
        RegexOptions.Compiled);

    /// <summary>
    ///     The one accepted shape. A Task may be started from a synchronous
    ///     contract (a constructor, an Avalonia lifecycle override, a
    ///     <c>[RelayCommand] void</c>, a key handler returning <c>bool</c>) — that
    ///     is not the defect. Dropping it unobserved is.
    /// </summary>
    private static readonly Regex AcceptedHelperCall = new(
        @"TaskFireAndForget\.Forget\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The three discard shapes as one alternation, composed from the same
    ///     fields the parser grades — not a copy — so this row cannot drift from
    ///     them. The runner does not execute it (the rule grades through
    ///     <see cref="ParseFireAndForget" />); it documents the shape the rule
    ///     bans and carries the failure text.
    /// </summary>
    private static readonly Regex ForbiddenShape = new(
        ExplicitDiscard.ToString()
        + "|" + VoidMemberDroppingTask.ToString()
        + "|" + VoidParameterlessMemberDroppingTask.ToString(),
        RegexOptions.Compiled);

    /// <summary>The rule as data: one documented shape, a custom parser, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "AvaloniaFireAndForget",
        Trees = ["apps/Harbor.App.Avalonia"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                ForbiddenShape,
                "route it through TaskFireAndForget.Forget(task, ex => _logger.LogError(ex, ...)) — "
                + "the surrounding member is synchronous and cannot await, which is not the defect; "
                + "losing the fault is."),
        ],
        Controls =
        [
            // Every Task-discard shape this app actually used, before the fix.
            new ScanControl("Bad/Open.cs", "            _ = _workspaceCommands.OpenFileAsync();", SubId),
            new ScanControl("Bad/Save.cs", "    private void SaveFile() => _ = _workspaceCommands.SaveFileAsync();", SubId),
            new ScanControl("Bad/Command.cs", "            _ = OpenCommand.ExecuteAsync(value);", SubId),
            new ScanControl("Bad/Own.cs", "            _ = RefreshFileTreeAsync();", SubId),
            new ScanControl("Bad/Run.cs", "            _ = Task.Run(async () =>", SubId),
            new ScanControl("Bad/Delay.cs", "            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>", SubId),
            new ScanControl("Bad/Dispose.cs", "            _ = _pty.DisposeAsync().AsTask().ContinueWith(", SubId),
            // The shape with no `_ =` at all: a void member whose body is a
            // Task-shaped call. No grep for `_ = ` can find it.
            new ScanControl("Bad/Implicit.cs", "    public void BranchSession() => _sessions.BranchCommand.ExecuteAsync(null);", SubId),
            // The conforming shape, and the ordinary non-Task discards that are
            // not defects and must not be swept up.
            new ScanControl("Good/Helper.cs", "        TaskFireAndForget.Forget(RefreshAsync(), ex => _logger.LogError(ex, \"x\"));", null),
            new ScanControl("Good/Append.cs", "            _ = sb.Append(text);", null),
            new ScanControl("Good/Dispatch.cs", "            _ = store.Dispatch(new AppMsg.TogglePanel(Id));", null),
            new ScanControl("Good/Increment.cs", "            _ = Interlocked.Increment(ref _revision);", null),
            new ScanControl("Good/Remove.cs", "            _ = state.Selected.Remove(cursor);", null),
            new ScanControl("Good/Move.cs", "            _ = buffer.MoveRight();", null),
            new ScanControl("Good/Sync.cs", "        public void ClearChat() => _chat.ClearCommand.Execute(null);", null),
            // Observed Task-shaped calls: the argument to the helper, a stored
            // continuation, and a returned AsTask. A detector that fires on mere
            // PRESENCE of a Task-shaped call blocks correct code and gets deleted.
            new ScanControl("Good/Observed1.cs", "            _pty.DisposeAsync().AsTask(),", null),
            new ScanControl("Good/Observed2.cs", "            ex => _logger.LogWarning(ex, \"PTY dispose failed\"));", null),
            new ScanControl("Good/Observed3.cs", "        var observed = source.ContinueWith(ApplyResult, TaskScheduler.Default);", null),
            new ScanControl("Good/Observed4.cs", "    public Task<Completion> FlushAsync() => _pipe.FlushAsync().AsTask();", null),
            // Prose about a discard stays prose: comment lines never reach the rule.
            new ScanControl("Good/Prose.cs", "            // _ = RefreshAsync(); was the old shape.", null),
        ],
        MinHits = 50,
        CustomParse = ParseFireAndForget,
    };

    /// <summary>
    ///     The custom parser: the discard verdict over one file's raw source. The
    ///     line filter is the old file walk's own — blank and <c>//</c> lines
    ///     dropped, everything else graded raw — so a caller cannot reach the
    ///     predicates with a line the walk would have skipped, and the planted
    ///     controls grade identically to product files.
    /// </summary>
    private static IEnumerable<ScanHit> ParseFireAndForget(string displayPath, string rawSource)
    {
        string[] lines = rawSource
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (AcceptedHelperCall.IsMatch(text))
            {
                continue;
            }

            if (DiscardedTaskExpression(text) is not null)
            {
                yield return new ScanHit(SubId, displayPath, i + 1, text.Trim());
            }
        }
    }

    // ── the rule ──────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaShell_StartsNoTaskWithoutObservingItsFault()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "§FP-003. Every one of these is a Task whose exception nobody observes. "
                + "In the desktop shell that means a failed save / open / diagnostics read goes silently "
                + "unreported. `TaskFireAndForget.Forget` exists for exactly this contract — the surrounding "
                + "member is synchronous and cannot await, which is not the defect; losing the fault is. "
                + string.Join("\n", violations));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        // Without a repository root every rule in this file passes vacuously.
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing. "
                + "The guarded Avalonia shell holds well over 50 source files. "
                + string.Join("; ", discovery));
    }

    [Test]
    public async Task Detector_FiresOnKnownBadAndStaysQuietOnKnownGood()
    {
        // The Task-discard shapes this app actually used must fire; the conforming
        // helper shape and the ordinary non-Task discards must stay quiet. The
        // snippets live on Rule.Controls, so the control drives the REAL parser
        // rather than a second implementation of it.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the detector must recognise every Task discard, otherwise the rule has a hole — and "
                + "flagging a non-Task discard would make the guard cry wolf. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_FiresOnTheVoidMemberThatDropsTheTaskEntirely()
    {
        // No `_ =` anywhere in the planted line. A grep for `_ = ` cannot find it,
        // which is exactly why `AvaloniaWorkspaceCommands.BranchSession` survived
        // the original audit while being the worst site in the app. Proved by the
        // Bad/Implicit.cs control on the rule, driven here through the same verdict.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "a void member whose body is a Task-shaped call drops that Task unobserved, and no `_ =` "
                + "marks it. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IgnoresTaskShapedCallsThatAreObserved()
    {
        // The Task-shape alternation must stay wrapped in its non-capturing group:
        // an ungrouped alternation fires on the mere PRESENCE of `.AsTask(` or
        // `.ContinueWith(`, observed or not. Proved by the Good/Observed* controls
        // on the rule — the argument to the helper, a stored continuation, and a
        // returned AsTask — driven here through the same verdict.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "an observed Task is not a discard. If the detector fires on one, the Task-shape "
                + "alternation has lost its non-capturing group and the rule matches on presence "
                + "rather than on a discard — which blocks correct code and gets deleted. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_FiresOnEveryTaskShapePresentInTheGuardedTree()
    {
        // Ties the detector to reality: every distinct Task-discard shape that
        // existed in the Avalonia shell before #569 is planted on the rule, so a
        // future edit to the patterns that silently drops one of them fails here
        // instead of leaking a hole in the rule.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "every shape that came out of the real Avalonia tree must still be caught. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: it is wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    ///     The discarded Task-shaped expression on this line, or <c>null</c> when
    ///     the line does not discard a Task. Handles both the explicit
    ///     <c>_ =</c> form and the implicit void-member form.
    /// </summary>
    private static string? DiscardedTaskExpression(string line)
    {
        Match explicitDiscard = ExplicitDiscard.Match(line);
        if (explicitDiscard.Success)
        {
            return explicitDiscard.Groups["rhs"].Value.Trim();
        }

        if (VoidMemberDroppingTask.IsMatch(line) || VoidParameterlessMemberDroppingTask.IsMatch(line))
        {
            return line.Trim();
        }

        return null;
    }
}
