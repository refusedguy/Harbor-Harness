// VendoredHtmlSurfaceRules.cs — source-level guard for issue #878.
//
// WHY THIS FILE EXISTS
// --------------------
// #726 filed a vendored pair of `CollapseWhitespace` helpers in
// `external/ConsoleEx/SharpConsoleUI` (a git submodule on
// github.com/nickprotop/ConsoleEx) as ONE behaviour. #878 read both against the
// pin and found they diverge from EACH OTHER:
//
//   HtmlTableLayout.CollapseWhitespace  ASCII six, leading AND trailing trimmed
//   HtmlInlineFlow.CollapseWhitespace   char.IsWhiteSpace, one leading and one
//                                       trailing space KEPT
//
// Measured by re-typing both and comparing over an exhaustive alphabet, they
// disagree on 758 of 781 strings (97.1%). Full text and the per-option costs:
// docs/adr/ADR-012-vendored-html-collapse-whitespace-pair.md.
//
// WHY A GUARD AT ALL, WHEN ADR-012 PROPOSES NONE FOR THE COPIES
// --------------------------------------------------------------
// ADR-012 §7 argues against testing the two copies themselves, and this file
// agrees: they are `private static` inside a submodule at a moving pin, so any
// test on them is reflection over a third party's private API that can only go
// red on a change we cannot make. That argument is about the COPIES.
//
// This guard is about a different and more actionable fact: whether Harbor can
// REACH them at all. ADR-012's whole recommendation rests on that — the reason
// "do nothing" is the default option is that no user path executes either copy.
// If that ever stops being true, the decision ADR-012 defers to the owner is no
// longer a cosmetic question: two shapes that disagree 97% of the time would be
// rendering the same page differently depending on which path a string took.
//
// So the premise is pinned here rather than left as prose. The failure message
// points at ADR-012 and says what has to happen first, because the correct
// response to "someone wired HtmlControl in" is NOT to start using it — it is to
// decide the fork/replace question ADR-012 deliberately leaves open.
//
// This is the shape #564 already established: say the view seam is closed, and
// guard the saying.
//
// SCOPE — AND WHY contrib/ IS NOT IN IT
// -------------------------------------
// The three `contrib/tui/*/Views/DiagnosticsView.cs` copies are read-only to us
// (AGENTS.md) and no CI job compiles `contrib/`. They are also NOT this rule's
// business: they are a different pair (they match `PanelText.SingleLine`, which
// #717 already guards on the live side), and writing a rule over code no compiler
// sees is the exact failure #915/#923/#924 recorded.
//
// `external/` is likewise not scanned — 4 files there name these types by
// definition, and the submodule's own sources are not ours to police. The scan
// covers `src/` and `apps/`: OUR code, the only place a new call could appear.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard:
//   * `Scanner_FindsTheGuardedProjects` — the walk really finds src/ and apps/.
//   * `Detector_FiresOnTheKnownBridgeCall` — fires on the exact shape a caller
//     would have to write, and stays quiet on the current `MarkupControl` use.
//   * `Detector_IgnoresTheExplanationInProse` — this file and ADR-012 both name
//     the forbidden types; documenting them must not trip the rule.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #878): Harbor may not reach SharpConsoleUI's HTML
///     surface. Two <c>CollapseWhitespace</c> helpers live behind it and they
///     disagree — see ADR-012 before wiring anything up.
/// </summary>
public class VendoredHtmlSurfaceRules
{
    /// <summary>
    ///     Projects this rule polices: the only places a new call could appear.
    ///     <c>external/</c> is the submodule's own code and <c>contrib/</c> is
    ///     uncompiled by anything — neither is ours to scan.
    /// </summary>
    private static readonly string[] GuardedProjects = ["src", "apps"];

    /// <summary>
    ///     The bridge into <c>SharpConsoleUI.Html</c>. <c>HtmlControl</c> and
    ///     <c>HtmlBuilder</c> are the only two entry points from the namespaces
    ///     Harbor actually uses; the flow/layout types below are what they reach,
    ///     and naming any of them directly is the same decision one level down.
    /// </summary>
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"\bHtmlControl\b", RegexOptions.Compiled),
        new(@"\bHtmlBuilder\b", RegexOptions.Compiled),
        new(@"\bHtmlLayoutEngine\b", RegexOptions.Compiled),
        new(@"\bHtmlBlockFlow\b", RegexOptions.Compiled),
        new(@"\bHtmlInlineFlow\b", RegexOptions.Compiled),
        new(@"\bHtmlTableLayout\b", RegexOptions.Compiled),
        // The namespace itself: a `using` is a reachability decision too, and it
        // is the shape someone writes first when wiring the surface up.
        new(@"SharpConsoleUI\.Html", RegexOptions.Compiled),
    ];

    /// <summary>
    ///     Strips whole-line <c>//</c> comments and XML doc comments so a file that
    ///     explains this rule — including the guard's own header and ADR-012's
    ///     quotations — is not itself a violation.
    /// </summary>
    private static string StripComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Select(line =>
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            }));

    /// <summary>Repo-relative <c>path:line</c> of every forbidden shape in a file.</summary>
    private static List<string> DetectIn(string relativePath, string source)
    {
        var hits = new List<string>();
        string[] lines = StripComments(source).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // One report per line: a file that does `using SharpConsoleUI.Html;`
            // and then constructs an HtmlControl is one decision, not two.
            foreach (Regex pattern in ForbiddenPatterns)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    hits.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                    break;
                }
            }
        }

        return hits;
    }

    [Test]
    public async Task Html_Surface_Stays_Unreachable_From_Harbor()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        var violations = new List<string>();
        foreach (string file in EnumerateGuardedFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(DetectIn(relative, File.ReadAllText(file)));
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#878). SharpConsoleUI's HTML surface is unreachable from Harbor today, and that is "
                + "the premise ADR-012's recommendation rests on: two `CollapseWhitespace` helpers sit "
                + "behind it and they disagree on 758 of 781 measured strings (ASCII six + trimmed edges "
                + "versus char.IsWhiteSpace + kept edges). Rendering through them would make the same page "
                + "look different depending on which path a string took. Reaching the surface is a separate "
                + "decision from fixing it, and it is NOT this rule's to make: decide the fork/replace "
                + "question in docs/adr/ADR-012-vendored-html-collapse-whitespace-pair.md first, then widen "
                + "or delete this guard deliberately. If HtmlControl is genuinely needed, ADR §6 variant C and "
                + "the #555 feature freeze both apply. "
                + string.Join("\n", violations));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProjects()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The walk needs a repository root; without one this file guards nothing.");

        if (root is null)
        {
            return;
        }

        List<string> files = EnumerateGuardedFiles(root);
        await Assert.That(files.Count).IsGreaterThan(500)
            .Because(
                $"src/ and apps/ together hold well over 500 source files; found {files.Count}. "
                + "A near-zero count means the paths are stale and the rule enforces nothing.");

        // The one project where a call is most likely to appear: the wrapper that
        // already talks to SharpConsoleUI. If the walk stopped covering it, the
        // rule would still be green while the likeliest caller went unchecked.
        await Assert.That(files.Any(f => f.Replace('\\', '/').Contains("src/Harbor.Tui.NickConsoleEx/")))
            .IsTrue()
            .Because(
                "Harbor.Tui.NickConsoleEx is the only project holding a reference to the submodule, so it "
                + "is where a bridge would be written. A scan that missed it would be green and blind.");
    }

    [Test]
    public async Task Detector_FiresOnTheKnownBridgeCall_AndStaysQuietOnTheCurrentUse()
    {
        // The exact shape a caller would have to write to reach the surface.
        const string knownBridge = """
            using SharpConsoleUI.Html;

            public sealed class HtmlLog
            {
                private readonly HtmlBuilder _builder = new();
                private readonly HtmlControl _control = new();
            }
            """;

        // The post-decision shape is not "use a different name" — it is "do not
        // reach the surface". So the quiet case is the CURRENT one: the
        // NickConsoleEx renderer builds a MarkupControl and a window, and that
        // must stay unflagged. A rule that fired here would forbid the fix.
        const string currentUse = """
            public sealed class NickConsoleExTuiRenderer : ITuiRenderer
            {
                private MarkupControl? _log;
                private ConsoleWindowSystem CreateSystem()
                {
                    var driver = new NetConsoleDriver(new NetConsoleDriverOptions { RenderMode = RenderMode.Buffer });
                    var system = new ConsoleWindowSystem(driver, options: new ConsoleWindowSystemOptions());
                    _log = new MarkupControl([]);
                    return new WindowBuilder(system).Build();
                }
            }
            """;

        await Assert.That(DetectIn("Known.cs", knownBridge)).IsNotEmpty()
            .Because("the bridging shape must be detected, or the rule guards nothing");
        await Assert.That(DetectIn("Current.cs", currentUse)).IsEmpty()
            .Because(
                "MarkupControl/WindowBuilder are the surfaces Harbor legitimately uses today. If they are "
                + "flagged the rule forbids the status quo as well as the change, and the only way to make "
                + "CI green would be to widen or delete it.");
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // This file's own header and ADR-012 both name every forbidden type. A
        // guard that fails on the documentation of the rule is a guard nobody keeps.
        const string prose = """
            // #878: HtmlTableLayout.CollapseWhitespace and HtmlInlineFlow.CollapseWhitespace disagree.
            /// <summary>Never touches SharpConsoleUI.Html; see ADR-012.</summary>
            public sealed class NickConsoleExTuiRenderer { private MarkupControl? _log; }
            """;

        await Assert.That(DetectIn("Prose.cs", prose)).IsEmpty()
            .Because(
                "comments are stripped before matching, so quoting the forbidden types in order to explain "
                + "the rule does not make the rule's own text a violation");
    }

    private static List<string> EnumerateGuardedFiles(string root)
    {
        List<string> found = [];
        foreach (string project in GuardedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}external{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(file);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
