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
//
// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The rule below is a ScanRule: one banned shape (the seven forbidden patterns
// merged into one alternation), no baseline, five planted controls, a discovery
// floor. Enumeration, stripping, matching and the control/discovery verdicts are
// ScanRunner's; this file keeps the issue prose and the test names.
//
// The merge is mechanical, not a re-decision: the old detector reported one hit
// per line (break after the first matching pattern), and a single alternation
// over lines reports the same set — a line matches iff any branch matches. No
// line in `src/` or `apps/` matches two branches at once, and the planted
// bridge lines each match exactly one, so the control verdicts are unchanged.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #878): Harbor may not reach SharpConsoleUI's HTML
///     surface. Two <c>CollapseWhitespace</c> helpers live behind it and they
///     disagree — see ADR-012 before wiring anything up.
/// </summary>
public class VendoredHtmlSurfaceRules
{
    private const string SubId = "VENDORED-HTML-SURFACE";

    /// <summary>
    ///     The bridge into <c>SharpConsoleUI.Html</c>. <c>HtmlControl</c> and
    ///     <c>HtmlBuilder</c> are the only two entry points from the namespaces
    ///     Harbor actually uses; the flow/layout types below are what they reach,
    ///     and naming any of them directly is the same decision one level down.
    ///     The namespace itself is included: a `using` is a reachability decision
    ///     too, and it is the shape someone writes first when wiring the surface
    ///     up.
    /// </summary>
    private static readonly Regex ForbiddenShape = new(
        @"\bHtmlControl\b"
        + @"|\bHtmlBuilder\b"
        + @"|\bHtmlLayoutEngine\b"
        + @"|\bHtmlBlockFlow\b"
        + @"|\bHtmlInlineFlow\b"
        + @"|\bHtmlTableLayout\b"
        + @"|SharpConsoleUI\.Html",
        RegexOptions.Compiled);

    /// <summary>The rule as data: one banned shape, no baseline, five controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "VendoredHtmlSurface",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                ForbiddenShape,
                "decide the fork/replace question in "
                + "docs/adr/ADR-012-vendored-html-collapse-whitespace-pair.md first — "
                + "reaching the surface is a separate decision from fixing it, and it is "
                + "NOT this rule's to make."),
        ],
        Controls =
        [
            new ScanControl("Bridge/Using.cs", "using SharpConsoleUI.Html;", SubId),
            new ScanControl("Bridge/Builder.cs", "private readonly HtmlBuilder _builder = new();", SubId),
            new ScanControl("Bridge/Control.cs", "private readonly HtmlControl _control = new();", SubId),
            // The post-decision shape is not "use a different name" — it is "do not
            // reach the surface". So the quiet case is the CURRENT one: the
            // NickConsoleEx renderer builds a MarkupControl and a window, and that
            // must stay unflagged. A rule that fired here would forbid the fix.
            new ScanControl("Current.cs", """
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
                """, null),
            // This file's own header and ADR-012 both name every forbidden type. A
            // guard that fails on the documentation of the rule is a guard nobody keeps.
            new ScanControl("Prose.cs", """
                // #878: HtmlTableLayout.CollapseWhitespace and HtmlInlineFlow.CollapseWhitespace disagree.
                /// <summary>Never touches SharpConsoleUI.Html; see ADR-012.</summary>
                public sealed class NickConsoleExTuiRenderer { private MarkupControl? _log; }
                """, null),
        ],
        MinHits = 500,
        MustContain =
        [
            "src/Harbor.Tui.NickConsoleEx/NickConsoleExTuiRenderer.cs",
        ],
    };

    [Test]
    public async Task Html_Surface_Stays_Unreachable_From_Harbor()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

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
        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "The walk needs a repository root and must really find src/ and apps — "
                + "src/ and apps/ together hold well over 500 source files. "
                + "A near-zero count means the paths are stale and the rule enforces nothing. "
                + string.Join("; ", discovery));

        // The one project where a call is most likely to appear: the wrapper that
        // already talks to SharpConsoleUI. If the walk stopped covering it, the
        // rule would still be green while the likeliest caller went unchecked.
        await Assert.That(ScanRunner.ScopeFiles(Rule).Any(f => f.Contains("src/Harbor.Tui.NickConsoleEx/")))
            .IsTrue()
            .Because(
                "Harbor.Tui.NickConsoleEx is the only project holding a reference to the submodule, so it "
                + "is where a bridge would be written. A scan that missed it would be green and blind.");
    }

    [Test]
    public async Task Detector_FiresOnTheKnownBridgeCall_AndStaysQuietOnTheCurrentUse()
    {
        // The exact shape a caller would have to write to reach the surface, and
        // the current MarkupControl use that must stay unflagged. The snippets
        // live on Rule.Controls, so the control drives the REAL matcher rather
        // than a second implementation of it.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the bridging shape must be detected, or the rule guards nothing; and "
                + "MarkupControl/WindowBuilder are the surfaces Harbor legitimately uses today. If they are "
                + "flagged the rule forbids the status quo as well as the change, and the only way to make "
                + "CI green would be to widen or delete it. "
                + string.Join("; ", failures));
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // This file's header and ADR-012 both name every forbidden type. A guard
        // that fails on its own documentation is a guard nobody keeps — proved by
        // the Prose.cs control on the rule, driven here through the same verdict.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "comments are stripped before matching, so quoting the forbidden types in order to explain "
                + "the rule does not make the rule's own text a violation. "
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
}
