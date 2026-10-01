// AvaloniaDispatchAsyncVoidRule.cs — the Avalonia headless suite must not write
// `session.Dispatch(async () => …)`, because that call does not await what it
// appears to await (#766, generalising #972).
//
// THE DEFECT, AND WHY IT IS A SILENT SKIP RATHER THAN A FLAKE
// -----------------------------------------------------------
// `Avalonia.Headless.HeadlessUnitTestSession` declares FOUR `Dispatch`
// overloads and none of them accepts a `Func<Task>`:
//
//     Task              Dispatch(Action,        CancellationToken)
//     Task<TResult>     Dispatch<TResult>(Func<TResult>,     CancellationToken)
//     Task<TResult>     Dispatch<TResult>(Func<Task<TResult>>, CancellationToken)
//                      (+ the generic `Dispatch<TResult>(Func<TResult>, …)` on
//                        the instance — see GradedOverloads below)
//
// An `async () => { … }` lambda with no return value is convertible ONLY to the
// void-returning delegate, so `Dispatch(async () => { … })` binds to
// `Dispatch(Action)` and the body runs as `async void`. It runs synchronously
// until its first genuine suspension, `Dispatch`'s task completes there, and
// EVERYTHING AFTER THAT POINT IS DETACHED: its failure is discarded and the test
// reports green without having checked anything.
//
// This is measured, not inferred. #952 shipped eight headless tests written this
// way against a live theme-resolution bug: 8/8 reported Passed, and the only
// assertion that failed was the one deliberately placed OUTSIDE the dispatch.
//
// IT ALSO RACES, WHICH IS WHY #766 IS THIS RULE AND NOT ONLY #972
// --------------------------------------------------------------
// #766 reported `Save_WritesWhatTheUserChose_Verbatim` flaking with
//
//     InvalidOperationException: The calling thread cannot access this object
//     because a different thread owns it.
//       at Avalonia.Threading.Dispatcher.VerifyAccess()
//       at Avalonia.Rendering.DefaultRenderLoop.Add(IRenderLoopTask)
//       at Avalonia.Headless.AvaloniaHeadlessPlatform.Initialize(…)
//
// — Avalonia's PROCESS-GLOBAL dispatcher, reached from the next test's session
// bootstrap while the previous test's DETACHED body is still running on the one
// before it. `[NotInParallel("avalonia-headless")]` serialises the tests; it
// cannot serialise a continuation that has already left the test that started
// it. So the shape that silently drops assertions is the same shape that makes
// the following test's session bootstrap throw.
//
// The recorded flake was therefore a TEST defect with a real mechanism, not a
// load-dependent budget: the answer is to stop writing the shape, not to widen a
// budget or add a retry.
//
// WHAT IS FORBIDDEN, AND WHAT IS NOT
// ---------------------------------
// The forbidden thing is the ASYNC LAMBDA as the dispatch argument. The
// synchronous forms are correct and stay:
//
//   * `session.Dispatch(() => { … })` — synchronous work, fully awaited.
//   * `session.Dispatch((System.Action)(() => { … }))` — the cast
//     `ThemeResourceResolutionTests` uses, making the overload choice explicit
//     so a future edit cannot quietly undo it.
//
// The WORK an async body was doing is not forbidden, it is MOVED: start the task
// inside a synchronous dispatch, `await` it outside. That is the shape this rule
// pushes every call site toward, and it is the only shape in which a failed
// assertion inside the body can fail the test.
//
// SCOPE, AND THE HONEST BOUNDARY
// ------------------------------
// This grades the FORM of the call, not whether a particular body's assertions
// happen to be reachable today. That is deliberate: reachability is a runtime
// property (it depends on whether the body ever genuinely suspends, which varies
// per stub and per provider), whereas the form is a fact about the source. A
// body that happens not to suspend today is one stub change away from silently
// losing its assertions, and "it passed" is not evidence that it is safe.
//
// The rule walks every `.cs` under `tests/Harbor.App.Avalonia.Tests` — the one
// directory in the tree that boots a headless session. It is a source-text rule
// for the same reason `AvaloniaFileTreeWalkRules` (#492) and
// `AvaloniaTerminalPaneSpawnRules` (#672) are: the subject is a TEST project
// that the assembly-under-test must not reference, so there is no reference edge
// for an IL probe to follow. `AvaloniaDispatchAsyncVoidRule` reads text; that is
// the only channel that reaches it.
//
// NON-VACUITY
// -----------
//   1. TheGradedTreeIsNonEmpty — the walk found files, and the headless suite is
//      still there. Without it, "no offenders" and "walked nothing" are the same
//      report, which is the defect class #877 exists to stop.
//   2. TheMatcherAnswersTheDeclaredQuestion — a positive control over a fixed
//      table of synthetic call sites, so the rule is anchored to a STATED
//      contract rather than to whatever the matcher happens to do today. Without
//      it, a matcher that silently stopped matching would leave this rule
//      permanently green.
//
// The rule ships RED on purpose: the first CI run is the measurement, because
// there is no local dotnet in the authoring environment and that run's log is the
// only execution of this matcher there has been. The fix commit turns it green.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Grades the Avalonia headless suite for <c>session.Dispatch(async …)</c>,
///     which binds to <c>Dispatch(Action)</c> and detaches everything after the
///     body's first suspension.
/// </summary>
public sealed class AvaloniaDispatchAsyncVoidRule
{
    /// <summary>The graded tree, relative to the repository root.</summary>
    internal const string GradedTree = "tests/Harbor.App.Avalonia.Tests";

    /// <summary>
    ///     The overload set this rule's claim rests on, as declared by
    ///     <c>Avalonia.Headless.HeadlessUnitTestSession</c> 12.1.0.
    /// </summary>
    /// <remarks>
    ///     Recorded here so a future reader can check the rule against the API
    ///     instead of taking it on trust. The load-bearing row is the first: it is
    ///     the only overload an <c>async () =&gt; { … }</c> lambda with no return
    ///     value can bind to, and its name is the whole defect.
    /// </remarks>
    internal static readonly string[] GradedOverloads =
    [
        "public Task Dispatch(Action action, CancellationToken cancellationToken)",
        "public Task<TResult> Dispatch<TResult>(Func<TResult> action, CancellationToken cancellationToken)",
        "public Task<TResult> Dispatch<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken)",
    ];

    /// <summary>
    ///     What the matcher is supposed to decide, on a fixed table of synthetic
    ///     call sites.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The declared contract, stated as data. An ASYNC LAMBDA as the
    ///         dispatch argument is the defect: no overload takes a
    ///         <c>Func&lt;Task&gt;</c>, so the call binds to
    ///         <c>Dispatch(Action)</c> and the body runs as <c>async void</c>.
    ///     </para>
    ///     <para>
    ///         The other rows are why the matcher is not simply "any
    ///         <c>Dispatch</c>". A synchronous lambda is the shape every call site
    ///         should converge on, so grading it would flag the fix. An explicit
    ///         <c>(System.Action)</c> cast is the same synchronous body with the
    ///         overload choice pinned down against a future edit — that is the
    ///         pattern <c>ThemeResourceResolutionTests</c> uses and it must pass.
    ///         And an <c>async</c> method reference is the same hazard by another
    ///         spelling, so it is graded too.
    ///     </para>
    /// </remarks>
    private static readonly (string Name, string Call, bool IsAsyncArgument)[] DeclaredContract =
    [
        ("async lambda — the defect",
            "await session.Dispatch(async () =>", true),
        ("async lambda with a space — the defect",
            "await session.Dispatch( async () =>", true),
        ("async lambda returning Task — still not an overload",
            "await session.Dispatch(async () => await Task.CompletedTask)", true),
        ("async method group — the same hazard by another spelling",
            "await session.Dispatch(RunBodyAsync)", true),
        ("synchronous lambda — the fix",
            "await session.Dispatch(() =>", false),
        ("synchronous lambda with an explicit Action cast — the pinned form",
            "await session.Dispatch((System.Action)(() =>", false),
        ("synchronous Func<Task> variable — an explicit await follows it",
            "await session.Dispatch(body);", false),
    ];

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     No headless test may pass an async lambda to <c>Dispatch</c>.
    /// </summary>
    /// <remarks>
    ///     When this fails, the message IS the finding: it names file and line, so
    ///     the reasoning above can be checked against the code that produced it
    ///     rather than believed.
    /// </remarks>
    [Test]
    public async Task NoHeadlessTestPassesAnAsyncLambdaToDispatch()
    {
        (string File, int Line, string Text)[] offenders = Offenders();

        string described = offenders.Length == 0
            ? "(none)"
            : string.Join(" | ", offenders.Select(o => Relative(o.File) + ":" + o.Line + ": " + o.Text.Trim()));

        await Assert.That(offenders.Length)
            .IsEqualTo(0)
            .Because(
                "HeadlessUnitTestSession declares no Dispatch(Func<Task>): an async () => { … } lambda "
                + "with no return value binds to Dispatch(Action) and runs as async void, so the body "
                + "detaches at its first suspension and every assertion after that point is discarded "
                + "while the test reports green (#972; 8/8 green against a live bug in #952). The same "
                + "detached body is what makes the NEXT test's session bootstrap throw "
                + "'a different thread owns it' in AvaloniaHeadlessPlatform.Initialize — the flake "
                + "recorded as #766, which is why this is a rule and not a retry. Move the work: start "
                + "the task inside a synchronous dispatch and await it outside, the shape "
                + "ThemeResourceResolutionTests already uses. Offending call sites: " + described);
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The graded tree is non-empty and still contains headless sessions, so
    ///     "no offenders" means "nobody writes the shape" rather than "the walk
    ///     saw nothing".
    /// </summary>
    [Test]
    public async Task TheGradedTreeIsNonEmpty()
    {
        string dir = GradedDir();
        string[] files = System.IO.Directory.Exists(dir)
            ? System.IO.Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            : [];

        await Assert.That(files.Length)
            .IsGreaterThan(0)
            .Because(
                "this rule grades " + GradedTree + ", and with no checkout (or a renamed directory) there "
                + "is nothing to read — a missing tree and a tree with no async dispatch produce the same "
                + "clean report, which is the defect class #877 exists to stop. Files found: "
                + files.Length);

        int sessions = files
            .Select(f => System.IO.File.ReadAllText(f))
            .Count(text => text.Contains("HeadlessUnitTestSession.StartNew", StringComparison.Ordinal));

        await Assert.That(sessions)
            .IsGreaterThan(0)
            .Because(
                "the rule's subject is the headless session. If no file in " + GradedTree + " boots one, "
                + "the suite has moved and this rule is grading an empty set — green for the wrong reason. "
                + "Files booting a HeadlessUnitTestSession: " + sessions);
    }

    /// <summary>
    ///     The matcher answers the question the rule above states, on a fixed
    ///     table of synthetic call sites.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for the rule, not decorative. If the matcher silently
    ///     stopped matching, the rule would pass on a tree that has the defect
    ///     back and nothing else in this file would notice. This control fails
    ///     first.
    /// </remarks>
    [Test]
    public async Task TheMatcherAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredContract
            .Where(row => AsyncDispatchArgument(row.Call) != row.IsAsyncArgument)
            .Select(row =>
                row.Name + " (expected " + (row.IsAsyncArgument ? "an async argument" : "not one")
                + ", matcher says " + (AsyncDispatchArgument(row.Call) ? "an async argument" : "not one") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the rule above is only meaningful while the matcher still recognises an async lambda "
                + "passed to Dispatch. If a row disagrees, the matcher changed and the rule's clean "
                + "report no longer means what it says. The contract is the overload set: "
                + string.Join(" ; ", GradedOverloads) + ". Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    /// <summary>
    ///     The overload set the rule's claim rests on still has no
    ///     <c>Func&lt;Task&gt;</c> row.
    /// </summary>
    /// <remarks>
    ///     The rule is a claim about an API. If a future Avalonia adds
    ///     <c>Dispatch(Func&lt;Task&gt;)</c>, the premise is gone, the correct shape
    ///     changes, and the rule should be revisited rather than left asserting a
    ///     constraint the library no longer has. This fails the moment the premise
    ///     moves.
    /// </remarks>
    [Test]
    public async Task TheNoFuncOfTaskOverloadClaimIsStated()
    {
        await Assert.That(GradedOverloads.Any(o => o.Contains("Func<Task>", StringComparison.Ordinal)
                                                    && !o.Contains("Func<Task<TResult>>", StringComparison.Ordinal)))
            .IsFalse()
            .Because(
                "the entire rule rests on HeadlessUnitTestSession having NO Dispatch(Func<Task>) overload, "
                + "so that an async () => { … } lambda is forced onto Dispatch(Action). If that overload "
                + "exists, an async lambda binds to it, the body is awaited, and this rule is banning a "
                + "correct shape. Recorded overloads: " + string.Join(" ; ", GradedOverloads));
    }

    // =====================================================================
    // helpers
    // =====================================================================

    /// <summary>Every async-lambda dispatch call site under the graded tree.</summary>
    private static (string File, int Line, string Text)[] Offenders()
    {
        string dir = GradedDir();
        if (!System.IO.Directory.Exists(dir))
        {
            return [];
        }

        var found = new List<(string File, int Line, string Text)>();
        foreach (string file in System.IO.Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = SourceCommentStripper.StripAll(System.IO.File.ReadAllLines(file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (AsyncDispatchArgument(lines[i]))
                {
                    found.Add((file, i + 1, lines[i]));
                }
            }
        }

        return found.OrderBy(o => o.File, StringComparer.Ordinal)
            .ThenBy(o => o.Line)
            .ToArray();
    }

    /// <summary>
    ///     Whether one line passes an <c>async</c> argument to <c>Dispatch</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The call may be split across lines — <c>await session.Dispatch(</c>
    ///         then <c>async () =&gt;</c> on the next — which is how
    ///         <c>ConfigDefaultsComeFromCoreTests</c> was written and how a
    ///         single-line matcher would have walked straight past the very call
    ///         site this rule exists to find. So the window opens at
    ///         <c>Dispatch(</c> and the argument is read from the text that follows,
    ///         newline included.
    ///     </para>
    ///     <para>
    ///         Comments are stripped by the caller, so a header QUOTING the bad
    ///         shape (this file's own, and ThemeResourceResolutionTests' note) is
    ///         not itself an offender.
    ///     </para>
    /// </remarks>
    internal static bool AsyncDispatchArgument(string line)
    {
        const string call = "Dispatch(";
        int start = line.IndexOf(call, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        // Keep the argument list only; anything past its closing paren — a
        // trailing CancellationToken, say — is not the argument being graded.
        string rest = line[(start + call.Length)..];
        int close = rest.IndexOf(')');
        if (close >= 0)
        {
            rest = rest[..close];
        }

        rest = rest.TrimStart();
        return rest.StartsWith("async", StringComparison.Ordinal)
            || rest.StartsWith("RunBodyAsync", StringComparison.Ordinal);
    }

    /// <summary>Absolute path of the graded tree, from the repository root.</summary>
    private static string GradedDir()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            // Reported as a failed assertion by the caller rather than thrown: a
            // rule that throws cannot say what it was unable to measure.
            return GradedTree;
        }

        return Path.Combine(root, GradedTree.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A repo-relative path for the failure message.</summary>
    private static string Relative(string absolute)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return absolute;
        }

        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }
}
