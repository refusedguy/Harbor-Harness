// AvaloniaHeadlessSessionHelperRule.cs — #1819: the Avalonia headless suite opens
// ONE session, through ONE helper.
//
// THE DEFECT THIS RULE HOLDS CLOSED
// ---------------------------------
// `tests/Harbor.App.Avalonia.Tests` is the only project in the tree that boots a
// headless Avalonia session, and until #1819 it booted one TWENTY-SIX times: eight
// files each wrote
//
//     await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
//     await session.Dispatch((System.Action)(() => { … }), CancellationToken.None);
//
// with three spellings of "the app" (`typeof(App)` in eighteen of them,
// `typeof(global::Harbor.App.Avalonia.App)` in the other eight) and a manual
// `(System.Action)` cast at every site.
//
// The copies were IDENTICAL, which is exactly why they were worth collapsing:
// twenty-six places to remember the cast, the overload choice and the `typeof(...)`
// spelling. #1819 collapsed them into `Support/AvaloniaSession.cs`, and this file
// is the ratchet: the copies must not come back.
//
// WHY A SOURCE-TEXT RULE AND NOT AN IL PROBE
// --------------------------------------------
// The same reason as its sibling `AvaloniaDispatchAsyncVoidRule`, which grades the
// same tree: the subject is a TEST project that this project must not reference
// (there is no reference edge to follow), so text is the only channel that reaches
// it.
//
// COMMENTS ARE NOT CODE
// ---------------------
// `ThemeTokenResolvabilityTests`'s own header QUOTES the old shape
// (`HeadlessUnitTestSession.StartNew(typeof(App))`) to explain why the runtime is
// asked at all, and the helper's header quotes it too. A rule that graded prose
// would go red on the very sentence that documents the rule — and a rule that is
// red for that reason gets muted, and a muted rule catches nothing. The walk
// therefore strips comments first (`SourceCommentStripper`, the shared lexer).
//
// NON-VACUITY
// -----------
//   1. TheGradedTreeIsNonEmpty — the walk found files. Without it, "no offenders"
//      and "walked nothing" are the same report, which is the defect class #877
//      exists to stop.
//   2. TheHelperStillOpensTheSession — the helper exists AND still opens the
//      session. If the helper were deleted, the walk would report "no offenders"
//      for the wrong reason: the subject would have MOVED, not been fixed.
//   3. TheMatcherAnswersTheDeclaredQuestion — a positive control over a fixed
//      table of synthetic lines, so the rule is anchored to a STATED contract
//      rather than to whatever the matcher happens to do today.

namespace Harbor.Architecture.Tests;

/// <summary>
///     The Avalonia headless suite opens ONE session, through ONE helper.
/// </summary>
public sealed class AvaloniaHeadlessSessionHelperRule
{
    /// <summary>The graded tree, relative to the repository root.</summary>
    internal const string GradedTree = "tests/Harbor.App.Avalonia.Tests";

    /// <summary>The one file allowed to open a headless session.</summary>
    internal const string HelperFile = "tests/Harbor.App.Avalonia.Tests/Support/AvaloniaSession.cs";

    /// <summary>The call that opens a headless session.</summary>
    internal const string SessionCall = "HeadlessUnitTestSession.StartNew";

    /// <summary>
    ///     Directories this walk never descends into: build output is not the
    ///     repository, and a measurement that counted it would report a number about
    ///     something other than its subject.
    /// </summary>
    private static readonly string[] NeverWalked = ["bin", "obj"];

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     No file in the Avalonia headless suite opens a session except the helper.
    /// </summary>
    /// <remarks>
    ///     When this fails, the message IS the finding: it names file and line, so the
    ///     reasoning above can be checked against the code that produced it rather
    ///     than believed.
    /// </remarks>
    [Test]
    public async Task NoHeadlessSessionIsOpenedOutsideTheHelper()
    {
        (string File, int Line, string Text)[] offenders = Offenders();

        string described = offenders.Length == 0
            ? "(none)"
            : string.Join(" | ", offenders.Select(o => Relative(o.File) + ":" + o.Line + ": " + o.Text.Trim()));

        await Assert.That(offenders.Length)
            .IsEqualTo(0)
            .Because(
                "the Avalonia headless session is opened in ONE place since #1819 — " + HelperFile + " — and every "
                + "call site goes through it. A second opener is a second copy of the StartNew+Dispatch+cast dance, "
                + "and every copy is a place where the (System.Action) cast, the overload choice and the "
                + "`typeof(...)` spelling of the App can drift apart from each other. Offending call sites: "
                + described);
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The graded tree is non-empty, so "no offenders" means "nobody opens a
    ///     session" rather than "the walk saw nothing".
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
                "this rule grades " + GradedTree + ", and with no checkout (or a renamed directory) there is "
                + "nothing to read — a missing tree and a tree with no session opener produce the same clean "
                + "report, which is the defect class #877 exists to stop. Files found: " + files.Length);
    }

    /// <summary>
    ///     The helper still exists AND still opens the session.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for the rule above. A deleted helper would leave the walk
    ///     reporting "no offenders" — but the session would have MOVED, not been
    ///     fixed, and the suite would have stopped booting Avalonia at all.
    /// </remarks>
    [Test]
    public async Task TheHelperStillOpensTheSession()
    {
        string? helper = HelperPath();

        await Assert.That(helper)
            .IsNotNull()
            .Because(
                "the rule's subject is " + HelperFile + ". Without it this walk would report 'no offenders' for "
                + "the wrong reason: the session would have MOVED rather than been fixed.");

        string[] lines = SourceCommentStripper.StripAll(System.IO.File.ReadAllLines(helper!));
        int opens = lines.Count(line => MentionsSessionOpen(line));

        await Assert.That(opens)
            .IsGreaterThan(0)
            .Because(
                "the helper exists but no longer opens a session, so the rule is grading a set that has moved "
                + "rather than a set that has been fixed. Session opens in the helper: " + opens);
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The matcher answers the question the rule above states, on a fixed table.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for the rule, not decorative. If the matcher silently stopped
    ///     matching, the rule would pass on a tree that has the copies back, and
    ///     nothing else in this file would notice. This control fails first.
    /// </remarks>
    [Test]
    public async Task TheMatcherAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredContract
            .Where(row => MentionsSessionOpen(row.Line) != row.Mentions)
            .Select(row =>
                row.Name + " (expected " + (row.Mentions ? "a session open" : "no session open")
                + ", matcher says " + (MentionsSessionOpen(row.Line) ? "a session open" : "no session open") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "the rule above is only meaningful while the matcher still recognises the call that opens a "
                + "session. If a row disagrees, the matcher changed and the rule's clean report no longer means "
                + "what it says. Mismatches: " + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    /// <summary>
    ///     The declared question, as a table. The matcher is a substring test for
    ///     <see cref="SessionCall" />, so the rows that keep it honest are the ones
    ///     that MENTION the type without OPENING a session.
    /// </summary>
    private static readonly (string Name, string Line, bool Mentions)[] DeclaredContract =
    [
        ("the session call — the subject",
            "await using var session = HeadlessUnitTestSession.StartNew(AppType);", true),
        ("the session call with a typeof argument",
            "HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App))", true),
        ("the type alone — not a session open",
            "typeof(HeadlessUnitTestSession)", false),
        ("the dispatch call — the other half of the dance, but not an opener",
            "await session.Dispatch((System.Action)(() => result = body()), ct);", false),
        ("an unrelated call",
            "var app = global::Avalonia.Application.Current;", false),
    ];

    // =====================================================================
    // helpers
    // =====================================================================

    /// <summary>Whether one line opens a headless session.</summary>
    internal static bool MentionsSessionOpen(string line)
        => line.Contains(SessionCall, StringComparison.Ordinal);

    /// <summary>Every session open outside the helper.</summary>
    private static (string File, int Line, string Text)[] Offenders()
    {
        string dir = GradedDir();
        if (!System.IO.Directory.Exists(dir))
        {
            return [];
        }

        var found = new List<(string File, int Line, string Text)>();
        foreach (string file in SourceFiles(dir))
        {
            if (Relative(file).Equals(HelperFile, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(System.IO.File.ReadAllLines(file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (MentionsSessionOpen(lines[i]))
                {
                    found.Add((file, i + 1, lines[i]));
                }
            }
        }

        return found.OrderBy(o => o.File, StringComparer.Ordinal)
            .ThenBy(o => o.Line)
            .ToArray();
    }

    /// <summary>Every source file under <paramref name="dir" />, build output excluded.</summary>
    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (string file in System.IO.Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            bool isBuildOutput = NeverWalked.Any(never => file.Contains(
                $"{Path.DirectorySeparatorChar}{never}{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

            if (isBuildOutput)
            {
                continue;
            }

            yield return file;
        }
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

    /// <summary>Absolute path of the helper, or null when there is no checkout to find it in.</summary>
    private static string? HelperPath()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return null;
        }

        return Path.Combine(root, HelperFile.Replace('/', Path.DirectorySeparatorChar));
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
