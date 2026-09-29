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
// If a later wave converts another shell, add that path to `GuardedProjects`
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

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §FP-003 guard: no unobserved Task discard in the Avalonia desktop shell.
/// </summary>
public class AvaloniaFireAndForgetRules
{
    /// <summary>
    ///     Projects this rule polices. Each entry must correspond to a perimeter
    ///     that has actually been converted to <c>TaskFireAndForget</c>.
    /// </summary>
    private static readonly string[] GuardedProjects = ["apps/Harbor.App.Avalonia"];

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

    // ── the rule ──────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaShell_StartsNoTaskWithoutObservingItsFault()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles())
        {
            if (AcceptedHelperCall.IsMatch(text))
            {
                continue;
            }

            string? discarded = DiscardedTaskExpression(text);
            if (discarded is null)
            {
                continue;
            }

            violations.Add(
                $"{Relative(file)}:{line} — fire-and-forget drops the Task on the floor; "
                + $"a fault here dies in TaskScheduler.UnobservedTaskException at finalization "
                + $"and never reaches a log. Route it through TaskFireAndForget.Forget(task, ex => _logger.LogError(ex, ...)): "
                + $"{text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§FP-003. Every one of these is a Task whose exception nobody observes. "
                + "In the desktop shell that means a failed save / open / diagnostics read goes silently "
                + "unreported. `TaskFireAndForget.Forget` exists for exactly this contract — the surrounding "
                + "member is synchronous and cannot await, which is not the defect; losing the fault is.");
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        // Without a repository root every rule in this file passes vacuously.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        int files = GuardedProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        await Assert.That(files).IsGreaterThan(50)
            .Because($"The guarded Avalonia shell should hold well over 50 source files; found {files}. "
                     + "A near-zero count means the path is stale and the rule guards nothing.");

        // Comment lines are excluded — the converted sites explain themselves in prose,
        // and a guard that fails on its own documentation is a guard nobody keeps.
        int commentHits = GuardedProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(ScanFile)
            .Count(hit => hit.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));

        await Assert.That(commentHits).IsEqualTo(0)
            .Because("Comment lines never reach the rule, so prose describing a fire-and-forget cannot fail the build.");
    }

    [Test]
    public async Task Detector_FiresOnKnownBadAndStaysQuietOnKnownGood()
    {
        // (a) every Task-discard shape this app actually used, before the fix.
        string[] mustFail =
        [
            "            _ = _workspaceCommands.OpenFileAsync();",
            "    private void SaveFile() => _ = _workspaceCommands.SaveFileAsync();",
            "            _ = OpenCommand.ExecuteAsync(value);",
            "            _ = RefreshFileTreeAsync();",
            "            _ = Task.Run(async () =>",
            "            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>",
            "            _ = _pty.DisposeAsync().AsTask().ContinueWith("
        ];

        foreach (string bad in mustFail)
        {
            await Assert.That(DiscardedTaskExpression(bad)).IsNotNull()
                .Because($"The detector must recognise this Task discard, otherwise the rule has a hole: {bad.Trim()}");
        }

        // (b) the conforming shape, and the ordinary non-Task discards that are
        //     not defects and must not be swept up.
        string[] mustPass =
        [
            "        TaskFireAndForget.Forget(RefreshAsync(), ex => _logger.LogError(ex, \"x\"));",
            "            _ = sb.Append(text);",
            "            _ = store.Dispatch(new AppMsg.TogglePanel(Id));",
            "            _ = Interlocked.Increment(ref _revision);",
            "            _ = state.Selected.Remove(cursor);",
            "            _ = buffer.MoveRight();",
            "        public void ClearChat() => _chat.ClearCommand.Execute(null);"
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(DiscardedTaskExpression(good)).IsNull()
                .Because($"This is not an unobserved Task discard — flagging it would make the guard cry wolf: {good.Trim()}");
        }
    }

    [Test]
    public async Task Detector_FiresOnTheVoidMemberThatDropsTheTaskEntirely()
    {
        // No `_ =` anywhere in this line. A grep for `_ = ` cannot find it, which
        // is exactly why `AvaloniaWorkspaceCommands.BranchSession` survived the
        // original audit while being the worst site in the app: the Task is
        // discarded with no marker at all.
        const string Implicit =
            "    public void BranchSession() => _sessions.BranchCommand.ExecuteAsync(null);";

        await Assert.That(DiscardedTaskExpression(Implicit)).IsNotNull()
            .Because("A void member whose body is a Task-shaped call drops that Task unobserved, and no `_ =` marks it.");

        const string ConformingVoid = "    public void ClearChat() => _chat.ClearCommand.Execute(null);";
        await Assert.That(DiscardedTaskExpression(ConformingVoid)).IsNull()
            .Because("ICommand.Execute returns void — there is no Task to drop, so this must stay green.");
    }

    [Test]
    public async Task Detector_IgnoresTaskShapedCallsThatAreObserved()
    {
        // REGRESSION FIXTURE — see the remark on TaskShapedCall.
        //
        // When the Task-shape alternation was first written it was NOT wrapped in
        // a non-capturing group, so `prefix + A|B|C` parsed as `(prefix+A)|B|C`.
        // The consequence: any line mentioning `.AsTask(` or `.ContinueWith(` was
        // reported as an unobserved fire-and-forget, whether or not a Task was
        // being discarded at all. This fixture is the line that exposed it —
        // it is the argument to a helper, fully observed, and must stay green.
        string[] alreadyObserved =
        [
            // The exact shape that failed: a Task handed to the helper, one line
            // below the `Forget(` that observes it.
            "            _pty.DisposeAsync().AsTask(),",
            "            ex => _logger.LogWarning(ex, \"PTY dispose failed\"));",
            // A continuation attached to a Task that is itself stored, not dropped.
            "        var observed = source.ContinueWith(ApplyResult, TaskScheduler.Default);",
            // An AsTask() whose Task is returned, not discarded.
            "    public Task<Completion> FlushAsync() => _pipe.FlushAsync().AsTask();"
        ];

        foreach (string line in alreadyObserved)
        {
            await Assert.That(DiscardedTaskExpression(line)).IsNull()
                .Because(
                    "This line does not discard a Task. If the detector fires on it, the Task-shape alternation "
                    + "has lost its non-capturing group and the rule matches on the mere PRESENCE of a Task-shaped "
                    + "call rather than on a discard — which blocks correct code and gets deleted: "
                    + line.Trim());
        }
    }

    [Test]
    public async Task Detector_FiresOnEveryTaskShapePresentInTheGuardedTree()
    {
        // Ties the detector to reality: every distinct Task-discard shape that
        // existed in the Avalonia shell before #569 is re-checked here, so a
        // future edit to the regex that silently drops one of them fails here
        // instead of leaking a hole in the rule.
        string[] shapesFromTheApp =
        [
            "_ = _workspaceCommands.OpenFileAsync();",              // workspace port call
            "_ = OpenCommand.ExecuteAsync(value);",                 // CommunityToolkit command
            "_ = _contentHost.Board.RefreshCommand.ExecuteAsync(null);",
            "_ = RefreshFileTreeAsync();",                          // own async method
            "_ = Task.Run(async () =>",                             // Task.Run with catch inside
            "_ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>",
            "_ = _pty.DisposeAsync().AsTask().ContinueWith("         // IAsyncDisposable
        ];

        foreach (string shape in shapesFromTheApp)
        {
            await Assert.That(DiscardedTaskExpression(shape)).IsNotNull()
                .Because($"This shape came out of the real Avalonia tree; the detector must still catch it: {shape.Trim()}");
        }
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

    /// <summary>Every non-comment, non-blank source line of the guarded projects.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanGuardedFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        foreach (string project in GuardedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach ((int line, string text) in ScanFile(file))
                {
                    yield return (file, line, text);
                }
            }
        }
    }

    /// <summary>One file, one-based line numbers, comment and blank lines dropped.</summary>
    private static IEnumerable<(int Line, string Text)> ScanFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            string trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (i + 1, text);
        }
    }

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string absolutePath) =>
        (RepoPaths.RepoRoot is null ? absolutePath : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath))
        .Replace('\\', '/');
}
