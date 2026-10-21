// ToolGlyphTableRule.cs — the guard for issue #680.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #680 reported that `ChatViewModelBase` holds a hand-written glyph
// catalogue for ten builtin tools and rebuilds a structured tool call by
// re-parsing the rendered transcript line. Taking the claim apart file by file,
// the parse and the catalogue turned out to be THREE separate copies of the same
// table, and two of the three disagreed with each other and with the tools:
//
//   * src/Harbor.Desktop.ViewModels/ViewModels/ChatViewModelBase.cs
//       "edit" => "✎" … "web_fetch" => "🌍"
//   * apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs
//       "edit" => "✎" … "web_fetch" => "⇣"    ← same ten names, different glyphs
//   * src/Harbor.Ui.Framework.Projection/Projection/PanelRows.cs
//       a four-arm subset with its own default
//
// And all three keyed the web-fetch tool as "web_fetch", while the tool itself is
// `ToolName.Create("webfetch")` — so that arm was dead in every catalogue. That is
// the exact shape #595 called ("eight hand-written lists of tool names, two of
// them already wrong"): a per-tool table that adding a tool silently outgrows.
//
// THE RULE
// --------
// No product file may map a TOOL NAME to a GLYPH in a switch or expression arm.
// The glyph is a property of the tool, so it is declared next to the tool's
// DisplayName and travels with the tool-call event into the UI state — a table
// keyed by tool name is the Open/Closed violation #560 removed from the provider
// picker and #680 removes from every tool surface.
//
// It is deliberately narrower than "the tool name appears anywhere". A lookup
// (`Find("webfetch")`), a permission rule (`new("read", "*", Allow)`) and a
// safety declaration are not tables; flagging them would make the rule noise.
//
// THE RULE IS VALUE-AGNOSTIC
// --------------------------
// The names to hunt for come from `ToolNameInventory.Names`, which reads
// `BuiltinToolSafetyProfiles.All` (#557's inventory, one row per builtin tool)
// unioned with every `ToolName.Create("…")` literal in the tool implementations,
// so a new tool re-points the guard instead of silently disarming it. A guard
// pinned to a hard-coded name list stops guarding the day a tool is added —
// which is precisely the bug this file exists to prevent, and the reason the
// reader lives in a shared type rather than in this file (see #595).
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan that
// is broken, and a broken guard is worse than none because it is believed. Two
// tests close that: discovery must find a real file set containing the files that
// used to hold the tables, and the same matcher must fire on a planted arm while
// staying silent on a comment, on a non-arm use of the same name, and on an arm
// keyed by something that is not a tool.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan is a line-level regex over code lines; it is not a C# parser. An arm
// written across lines (`"read"\n    => "▸"`) is missed. The limitation is bounded
// by construction: the only way to bring a table back is to make the arm DO work,
// and arms are string literals on code lines.
//
// SHARED INFRASTRUCTURE (#595)
// ---------------------------
// The tool-name vocabulary and the product-file walk this gate used to carry
// privately now live in `ToolNameInventory` and `SourceScan`, so #595's guard
// hunts the same names from the same derivation instead of growing a second,
// independently-drifting copy of both.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #680: no product file may map a tool name to a glyph in a switch or
///     expression arm. The glyph belongs to the tool's own declaration
///     (<c>ITool.Glyph</c>), not to a table in a view-model, a projection or an
///     app service.
/// </summary>
public sealed class ToolGlyphTableRule
{
    /// <summary>
    ///     Files allowed to carry a tool-name-keyed glyph arm, each with the
    ///     reason it cannot be data instead. An exemption is a decision, not an
    ///     oversight — the reason is printed in the failure message, the same way
    ///     <c>ProviderIdDispatchRule.DispatchExemptions</c> documents its
    ///     exemptions.
    /// </summary>
    /// <remarks>
    ///     Empty on purpose. The three catalogues that needed entries (the two
    ///     chat tool-card reconcilers and the diff-preview panel) now read the
    ///     glyph the core published, so nothing is exempt today. The list stays
    ///     because the escape hatch must exist before someone needs it — a rule
    ///     with no documented way to except a file gets worked around in worse
    ///     ways.
    /// </remarks>
    private static readonly Dictionary<string, string> GlyphExemptions = new(StringComparer.Ordinal);

    /// <summary>
    ///     The file that used to hold one of the three catalogues, named so the
    ///     non-vacuity check can prove discovery still sees it. Not an exemption.
    /// </summary>
    private const string FormerCatalogueFileRelativePath =
        "src/Harbor.Desktop.ViewModels/ViewModels/ChatViewModelBase.cs";

    /// <summary>
    ///     One glyph table arm: a quoted tool name, <c>=&gt;</c>, and a quoted
    ///     glyph. The glyph side is bounded to one or two UTF-16 units so the
    ///     match is a GLYPH mapping and not a string-valued lookup table — a
    ///     `"chat" => "ChatUserBrush"` arm is a resource-key map, not a glyph.
    /// </summary>
    private static readonly Regex ArmShape = new(
        @"""(?<name>[a-z][a-z0-9_]*)""\s*=>\s*""(?<glyph>[^""]{1,2})""",
        RegexOptions.Compiled);

    /// <summary>One violation, ready to be printed in a failure message.</summary>
    private sealed record GlyphArm(string File, int Line, string ToolName, string Glyph)
    {
        public string RelativePath => Path.GetRelativePath(RepoPaths.RepoRoot ?? ".", File).Replace('\\', '/');

        public override string ToString() =>
            $"{RelativePath}:{Line}  \"{ToolName}\" => \"{Glyph}\"";
    }

    /// <summary>The single banned shape's sub-id, shared by the rule data and the parser.</summary>
    private const string GlyphArmSubId = "TOOL-NAME-TO-GLYPH-ARM";

    // MECHANISM (#1086, step 2, conveyor h1)
    // -------------------------------------
    // The ban below is a ScanRule: the ArmShape as a Forbidden row carrying the
    // existing Regex object directly, four planted controls, the former
    // catalogue file as the MustContain floor, a discovery floor. Enumeration,
    // matching and the control/discovery verdicts are ScanRunner's; this file
    // keeps the issue prose, the pattern, the exemption hatch, and the test names.
    //
    // The verdict is ParseGlyphArms through the Func-overload, carrying the
    // original line scan verbatim — the //-* line skip, every shape match on a
    // line, the ToolNameInventory filter as the rule's half (the matcher reports
    // shapes, the inventory decides which shapes are tools), the empty
    // GlyphExemptions hatch. A file-wide baseline row would excuse a second arm
    // in the same file, and there is nothing to excuse today — the catalogue
    // this replaced is gone — so the baseline stays empty and the hatch stays a
    // line filter inside the parser. The planted controls grade this parser, so
    // "it can fail" means something.

    /// <summary>The glyph-table ban as data: one shape, four planted controls, a floor.</summary>
    private static readonly ScanRule GlyphRule = new()
    {
        Id = "ToolGlyphTable",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                GlyphArmSubId,
                ArmShape,
                "the glyph is declared on the tool itself (ITool.Glyph, next to DisplayName) "
                + "and travels with the tool-call event into the UI state, so adding a tool must "
                + "not require editing a view-model, a projection or an app service. "
                + "See issue #680."),
        ],
        Controls =
        [
            // Two arms of the right SHAPE, only one keyed by a tool name —
            // the shape matcher must see both, the inventory keeps the tool one.
            new ScanControl("Planted/TwoArms.cs", """
                private static string Glyph(string toolName) => toolName switch
                {
                    "read" => "▸",
                    "welcome" => "hi",
                    _ => "?",
                };
                """, GlyphArmSubId),
            // Same shape, keyed by STATUS names — the rule must not report it.
            new ScanControl("NearMiss/StatusNames.cs", """
                private static string Bar(string status) => status switch
                {
                    "running" => "▌",
                    _ => "?",
                };
                """, null),
            // Prose ABOUT the old catalogue — a rule that reads comments fails
            // the moment someone documents the fix.
            new ScanControl("NearMiss/Comment.cs", """
                // "read" => "▸" is what the old catalogue looked like.
                """, null),
            // A block-comment continuation line is skipped like a // line.
            new ScanControl("NearMiss/BlockComment.cs", """
                * "read" => "▸" is what the old catalogue looked like.
                """, null),
        ],
        MinHits = 500,
        MustContain = [FormerCatalogueFileRelativePath],
        CustomParse = ParseGlyphArms,
    };

    /// <summary>
    ///     The custom parser: the original line scan over one file's raw source —
    ///     the //-* skip verbatim, every shape match, the name inventory and the
    ///     exemption hatch as the rule's half.
    /// </summary>
    private static IEnumerable<ScanHit> ParseGlyphArms(string displayPath, string rawSource)
    {
        FrozenSet<string> names = ToolNameInventory.Names;
        foreach (GlyphArm arm in Scan([(displayPath, rawSource)]))
        {
            if (!names.Contains(arm.ToolName))
            {
                continue;
            }

            if (GlyphExemptions.ContainsKey(displayPath))
            {
                continue;
            }

            yield return new ScanHit(GlyphArmSubId, displayPath, arm.Line, $"\"{arm.ToolName}\" => \"{arm.Glyph}\"");
        }
    }

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     No product file maps a tool name to a glyph in a switch or expression
    ///     arm. A new tool must not require a C# edit to get an icon.
    /// </summary>
    [Test]
    public async Task NoProductFile_MapsAToolNameToAGlyph()
    {
        List<string> violations = ScanRunner.Evaluate(GlyphRule);

        await Assert.That(violations).IsEmpty()
            .Because("these arms are rows of a tool-name-keyed glyph table. The glyph is declared on "
                   + "the tool itself (ITool.Glyph, next to DisplayName) and travels with the "
                   + "tool-call event into the UI state, so adding a tool must not require editing "
                   + "a view-model, a projection or an app service (#680, same shape as #595). "
                   + "Note that the catalogue this replaced keyed the web-fetch tool as \"web_fetch\" "
                   + "while the tool is \"webfetch\" — that arm was dead. Offenders: "
                   + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     Discovery really reads the product trees. A scan rooted at a path that
    ///     does not exist returns an empty set and satisfies every rule above —
    ///     the NetArchTest trap, in its filesystem form.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealProductFileSet()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(GlyphRule);

        await Assert.That(discovery).IsEmpty()
            .Because("src/ + apps/ hold far more than 500 C# files, including the file that used to hold "
                   + "one of the three catalogues. A smaller count — or a scope that stopped seeing "
                   + "that file — means the tree walk broke and the rule above would be "
                   + "reporting 'clean' about nothing. "
                   + string.Join("; ", discovery));

        await Assert.That(ToolNameInventory.Names.Count).IsGreaterThan(10)
            .Because("the tool-name inventory is empty or nearly so, so the rule would accept any "
                   + "arm; it is read out of BuiltinToolSafetyProfiles plus the ToolName.Create "
                   + "literals, both of which are non-trivial in this repository");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The matcher is handed a synthetic snippet that
    ///     holds a planted tool→glyph arm, a clean snippet, a comment, and an arm
    ///     keyed by something that is not a tool. It MUST report the first and
    ///     nothing else. A matcher that stopped working would report nothing and
    ///     the rule above would go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Matcher_FiresOnAPlantedArmOnly()
    {
        // Two arms of the right SHAPE, only one of which is keyed by a tool name.
        // Both halves of the rule are exercised: the shape matcher must see two,
        // and the name inventory must keep exactly the tool one.
        const string plantedSource = """
            private static string Glyph(string toolName) => toolName switch
            {
                "read" => "▸",
                "welcome" => "hi",
                _ => "?",
            };
            """;

        // Same shape, keyed by STATUS names — the shape matcher may see it, the
        // rule must not report it.
        const string cleanSource = """
            private static string Bar(string status) => status switch
            {
                "running" => "▌",
                _ => "?",
            };
            """;

        // Prose ABOUT the old catalogue. A rule that reads comments fails the
        // moment someone documents the fix.
        const string commentSource = """
            // "read" => "▸" is what the old catalogue looked like.
            """;

        FrozenSet<string> names = ToolNameInventory.Names;

        IReadOnlyList<GlyphArm> planted = Scan([("probe.cs", plantedSource)]);
        IReadOnlyList<GlyphArm> clean = Scan([("clean.cs", cleanSource)]);
        IReadOnlyList<GlyphArm> comment = Scan([("commented.cs", commentSource)]);

        await Assert.That(planted.Count).IsEqualTo(2)
            .Because("the planted snippet declares exactly two arms of the tool→glyph shape; if the "
                   + "shape matcher found fewer, the rule is vacuous");

        await Assert.That(planted.Where(a => names.Contains(a.ToolName)).Select(a => a.ToolName))
            .IsEquivalentTo(new[] { "read" })
            .Because("the name inventory is what separates a tool→glyph table from a status→glyph "
                   + "table, so exactly the 'read' arm must survive the filter");

        await Assert.That(clean.Where(a => names.Contains(a.ToolName))).IsEmpty()
            .Because("the clean snippet maps STATUS names, not tool names, so the rule must not "
                   + "report it — that is precisely what the name inventory is for");

        await Assert.That(comment).IsEmpty()
            .Because("the commented snippet is prose about the old catalogue, not an arm");

        await Assert.That(names.Contains("read")).IsTrue()
            .Because("'read' is a builtin tool name; if it is missing from the inventory the rule "
                   + "would accept the very catalogue this file was written to remove");
    }

    /// <summary>
    ///     The planted controls — the tool arm and the three near-misses — drive
    ///     the REAL parser: the offender reported under its sub-id, the
    ///     near-misses silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(GlyphRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the four planted controls must agree with the matcher cases above through the "
                + "rule's own parser — otherwise the rule guards nothing, or forbids the fix. "
                + string.Join("; ", failures));
    }

    // =====================================================================
    // 3. Plumbing.
    // =====================================================================

    /// <summary>
    ///     The tool-name inventory: #557's builtin declarations first (that list is
    ///     already maintained, and missing a row there is a build failure), then
    ///     every name a tool implementation actually declares — which is how a
    ///     plugin tool, absent from the builtin list, still gets hunted.
    /// </summary>
    /// <summary>
    ///     Every glyph-table-shaped arm in the supplied sources, keyed by file
    ///     name. Name filtering is the RULE's job, not the matcher's — the
    ///     non-vacuity control depends on being able to see both halves.
    /// </summary>
    private static IReadOnlyList<GlyphArm> Scan(IReadOnlyList<(string Path, string Source)> sources)
    {
        var arms = new List<GlyphArm>();

        foreach ((string path, string source) in sources)
        {
            string[] lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in ArmShape.Matches(line))
                {
                    string name = match.Groups["name"].Value;
                    string glyph = match.Groups["glyph"].Value;
                    if (glyph.Length is 0 or > 2)
                    {
                        continue;
                    }

                    arms.Add(new GlyphArm(path, i + 1, name, glyph));
                }
            }
        }

        return arms;
    }
}
