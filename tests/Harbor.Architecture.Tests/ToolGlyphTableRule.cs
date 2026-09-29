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
//   * src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs
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
// The names to hunt for are read out of `BuiltinToolSafetyProfiles.All` (#557's
// inventory, one row per builtin tool) unioned with every `ToolName.Create("…")`
// literal in the tool implementations, so a new tool re-points the guard instead
// of silently disarming it. A guard pinned to a hard-coded name list stops
// guarding the day a tool is added — which is precisely the bug this file exists
// to prevent.
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

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Permissions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #680: no product file may map a tool name to a glyph in a switch or
///     expression arm. The glyph belongs to the tool's own declaration
///     (<c>ITool.Glyph</c>), not to a table in a view-model, a projection or an
///     app service.
/// </summary>
public sealed class ToolGlyphTableRule
{
    /// <summary>Product trees scanned for tool-name-keyed glyph arms.</summary>
    private static readonly string[] ProductTrees = ["src", "apps"];

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
        "src/Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs";

    /// <summary>
    ///     One glyph table arm: a quoted tool name, <c>=&gt;</c>, and a quoted
    ///     glyph. The glyph side is bounded to one or two UTF-16 units so the
    ///     match is a GLYPH mapping and not a string-valued lookup table — a
    ///     `"chat" => "ChatUserBrush"` arm is a resource-key map, not a glyph.
    /// </summary>
    private static readonly Regex ArmShape = new(
        @"""(?<name>[a-z][a-z0-9_]*)""\s*=>\s*""(?<glyph>[^""]{1,2})""",
        RegexOptions.Compiled);

    /// <summary>Every <c>ToolName.Create("…")</c> literal, so plugin tools are covered too.</summary>
    private static readonly Regex ToolNameLiteral = new(
        @"ToolName\.Create\(\s*""(?<name>[a-z][a-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary>One violation, ready to be printed in a failure message.</summary>
    private sealed record GlyphArm(string File, int Line, string ToolName, string Glyph)
    {
        public string RelativePath => Path.GetRelativePath(RepoPaths.RepoRoot ?? ".", File).Replace('\\', '/');

        public override string ToString() =>
            $"{RelativePath}:{Line}  \"{ToolName}\" => \"{Glyph}\"";
    }

    /// <summary>
    ///     Every builtin tool name the guard hunts for, read out of #557's
    ///     safety inventory unioned with the names the tool implementations
    ///     actually declare. Value-agnostic by construction — see the file header.
    /// </summary>
    private static readonly Lazy<FrozenSet<string>> ToolNames = new(CollectToolNames);

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
        IReadOnlyList<GlyphArm> found = Scan(ReadProductFiles(ProductFiles()));
        FrozenSet<string> names = ToolNames.Value;

        var offenders = found
            .Where(arm => names.Contains(arm.ToolName))
            .Where(arm => !GlyphExemptions.ContainsKey(arm.RelativePath))
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because("these arms are rows of a tool-name-keyed glyph table. The glyph is declared on "
                   + "the tool itself (ITool.Glyph, next to DisplayName) and travels with the "
                   + "tool-call event into the UI state, so adding a tool must not require editing "
                   + "a view-model, a projection or an app service (#680, same shape as #595). "
                   + "Note that the catalogue this replaced keyed the web-fetch tool as \"web_fetch\" "
                   + "while the tool is \"webfetch\" — that arm was dead. Offenders: "
                   + string.Join(", ", offenders));
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
        IReadOnlyList<string> files = ProductFiles();

        await Assert.That(files.Count).IsGreaterThan(100)
            .Because("the scan found almost no .cs files under src/+apps/; the product trees are "
                   + "wrong, and every rule in this file is then satisfied by an empty scan");

        var relative = files
            .Select(p => Path.GetRelativePath(RepoPaths.RepoRoot ?? ".", p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(relative.Contains(FormerCatalogueFileRelativePath)).IsTrue()
            .Because("this file is the one that used to hold the catalogue; if discovery cannot see "
                   + "it, the scan is broken rather than clean");

        await Assert.That(ToolNames.Value.Count).IsGreaterThan(10)
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

        FrozenSet<string> names = ToolNames.Value;

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

    // =====================================================================
    // 3. Plumbing.
    // =====================================================================

    /// <summary>
    ///     The tool-name inventory: #557's builtin declarations first (that list is
    ///     already maintained, and missing a row there is a build failure), then
    ///     every name a tool implementation actually declares — which is how a
    ///     plugin tool, absent from the builtin list, still gets hunted.
    /// </summary>
    private static FrozenSet<string> CollectToolNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (ToolSafetyDeclaration declaration in BuiltinToolSafetyProfiles.All)
        {
            if (!string.IsNullOrWhiteSpace(declaration.ToolName))
            {
                names.Add(declaration.ToolName);
            }
        }

        foreach (string file in ToolImplementationFiles())
        {
            foreach (Match match in ToolNameLiteral.Matches(File.ReadAllText(file)))
            {
                string name = match.Groups["name"].Value;
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Every <c>*.cs</c> under the tool implementations, sample plugins included.</summary>
    private static IReadOnlyList<string> ToolImplementationFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var files = new List<string>();
        foreach (string relative in new[]
                 {
                     "src/Harbor.Tools.Builtin",
                     "samples/plugins",
                     "samples/plugins-cs",
                 })
        {
            string dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p)));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Every product <c>*.cs</c> under <c>src/</c> and <c>apps/</c>.</summary>
    private static IReadOnlyList<string> ProductFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var files = new List<string>();
        foreach (string tree in ProductTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p)));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    ///     <c>contrib/</c> is out of support and out of CI, <c>tests/</c> holds the
    ///     rules themselves, and <c>obj/</c>/<c>bin/</c> hold stale copies. A
    ///     worktree checkout of this repo would otherwise be scanned as if it were
    ///     product code.
    /// </summary>
    private static bool IsBuildOutput(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.Ordinal)
            || normalised.Contains("/bin/", StringComparison.Ordinal)
            || normalised.Contains("/tests/", StringComparison.Ordinal)
            || normalised.Contains("/contrib/", StringComparison.Ordinal)
            || normalised.Contains("/.worktrees/", StringComparison.Ordinal);
    }

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

    /// <summary>Reads the product files, in the shape <see cref="Scan" /> consumes.</summary>
    private static List<(string, string)> ReadProductFiles(IReadOnlyList<string> files)
    {
        var sources = new List<(string, string)>(files.Count);
        foreach (string file in files)
        {
            try
            {
                sources.Add((file, File.ReadAllText(file)));
            }
            catch (IOException)
            {
                // A file that vanished between enumeration and read is not a rule
                // failure; the discovery test is what keeps the set honest.
            }
        }

        return sources;
    }
}
