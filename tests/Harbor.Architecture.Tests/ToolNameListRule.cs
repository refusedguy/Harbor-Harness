// ToolNameListRule.cs — the guard for issue #595.
//
// WHAT #595 REPORTED, AND WHAT IS ACTUALLY THERE
// ----------------------------------------------
// The issue says "eight hand-maintained lists of tool names, two of them already
// wrong". Taking that apart file by file rather than trusting it:
//
//   ROWS 7/8 (the two tool→glyph switches) ARE ALREADY GONE. #680 moved the
//   glyph onto ITool and ToolGlyphTableRule polices the table shape. The
//   `web_fetch` arm the issue quotes no longer exists in product code — grep
//   finds it only in comments documenting the fix. This file must not
//   re-litigate them.
//
//   WHAT REMAINS is not eight lists of one kind. They break into two classes, and
//   only the first is a bug:
//
//   (1) DERIVABLE — a per-tool fact the tool's own declaration already owns.
//       `PathArgExtractionPolicy.DefaultTools` re-states "which tools take a
//       path" while `ToolSafetyProfile.ArgKind == Path` says exactly that, one
//       file away. `ToolCategories.ByTool` re-states "which class is this tool
//       in". `LegacyArgExtractionPolicy`'s switch re-states "which argument holds
//       the subject", which is `ToolSafetyProfile.ArgumentName`. A new tool means
//       editing all three, and omitting one is silent.
//
//   (2) POLICY — a ruleset. `PermissionRuleset.Default`, `PlanDefault` and
//       `ExploreDefault` name tools in order to ALLOW, DENY or ASK. "bash is a
//       Command tool" says nothing about whether the plan agent may run it, so
//       these cannot be derived and must not be deleted. The issue concedes this
//       ("If they keep per-tool rules for a defensible reason, say which, in a
//       comment that names the reason"). They are justified per-file below.
//
//   And one row is simply WRONG, which is the defect class the issue is really
//   about: `ReadGroupBlock.IsReadOnlyTool` lists `"list"`, which no tool
//   registers. That row can never fire. It is `web_fetch` again, under a
//   different name, in a table #680 did not reach — found by walking the
//   product, not by reading the issue.
//
// WHY A GUARD AND NOT A NINTH LIST
// --------------------------------
// The tempting wrong move is to add one canonical "all tool names" array and
// point the lists at it. That is list nine; it would drift identically, because
// the new tool lands in the registry and gets forgotten in the array, and the
// lists stay lists. The fix has to make each derivable row READ the declaration,
// so there is nothing left to keep in sync.
//
// THE RULES
// ---------
// Both rules are stated over the DERIVED vocabulary, never a written one, so a
// new tool re-points them instead of disarming them:
//
//   1. No product file may hand-maintain a set of builtin tool names for a fact
//      the tool's own declaration carries.
//   2. No table may be keyed on a name no tool registers — the dead row. This is
//      the `web_fetch` shape, and it is checked on the tables that rule 1
//      already identified as tool tables, so it cannot fire on a JSON schema
//      property or a CLI verb.
//
// NON-VACUITY
// -----------
// A guard that matches nothing is indistinguishable from a guard that is broken,
// and a broken guard is worse than none because it is believed. Four tests close
// that: discovery must find a real file set and a real vocabulary; the table
// finder must fire on a planted table and stay silent on a comment, on a single
// lookup, and on a table of non-tool words; the dead-row check must catch a
// planted unknown name and stay silent on a planted real one; and the derivation
// must cover a tool that exists NOWHERE in a hand-written list — which is what
// proves the guard tracks tools rather than a fixed roster.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Permissions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #595: the per-tool facts a tool already declares must be read from
///     the tool, not re-listed by name in a table that a new tool silently
///     outgrows.
/// </summary>
public sealed class ToolNameListRule
{
    /// <summary>
    ///     Files allowed to hold a multi-name tool table, each with the reason it
    ///     cannot be derived. An exemption is a decision, not an oversight — the
    ///     reason is printed in the failure message, the way
    ///     <c>ProviderIdDispatchRule.DispatchExemptions</c> documents its own.
    /// </summary>
    /// <remarks>
    ///     Every entry is a POLICY or PRESENTATION file: it names tools in order to
    ///     allow, deny, ask, or to decide how to draw them. Neither is a property
    ///     of the tool, so neither can be read off <c>ITool</c>. A guard that
    ///     cannot tell a policy from a stale copy of a declaration gets the policy
    ///     deleted instead.
    /// </remarks>
    private static readonly Dictionary<string, string> PolicyExemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Abstractions.Contracts/Permissions/BuiltinToolSafetyProfiles.cs"] =
            "This IS the declaration the other rules make the lists read. It is the one place a "
            + "tool name may be written down, because a tool's safety profile is a property of the "
            + "tool and has to be written somewhere. `BuiltinToolSafetyDeclarationsTests` "
            + "cross-checks every row against the tools that actually register, so this table "
            + "cannot rot the way a private list can.",

        ["src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs"] =
            "This IS the policy: each rule names a tool to allow, deny or ask about it. "
            + "Nothing on a tool says which verbs an agent may use.",

        ["src/Harbor.Abstractions/Agents/AgentDefinition.cs"] =
            "The plan/explore agents' default permissions. What an agent may do is a "
            + "statement about the AGENT, not about each tool.",

        ["src/Harbor.Tui.CellForge/Chat/Widgets/ReadGroupBlock.cs"] =
            "ReadGroupBlock is a presentation grouping, not a safety or permission fact: "
            + "it decides which completed cards coalesce into one read-group panel, and a "
            + "tool does not know whether a renderer wants to fold it. Its dead \"list\" "
            + "arm is caught by the dead-row rule below, which is the check that "
            + "actually applies to this file.",

        ["src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs"] =
            "TrackedTools is the set of tools whose edits the Recent-Changes panel "
            + "watches — a projection concern with no tool-side declaration.",

        ["src/Harbor.Ui.Framework.State/Diff/DiffPreview.cs"] =
            "The diff preview must decide which tool calls render a diff. A tool does "
            + "not declare that it is diff-shaped; the renderer owns that.",

        ["src/Harbor.Tui.CellForge.Engine/Rendering/DiffPreview.cs"] =
            "The same renderer-owned decision as the Ui.Framework.State copy, in the "
            + "engine. The duplication between the two renderers is tracked separately; "
            + "neither copy is derivable from a tool.",

        ["src/Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs"] =
            "The tool's own JSON schema. Its property names sit next to the tool's name "
            + "and a schema is data, not a table of tools.",
    };

    /// <summary>
    ///     A file that holds a hand-written tool table today, named so the
    ///     non-vacuity check can prove discovery still sees it. Not an exemption.
    /// </summary>
    private const string KnownTableFileRelativePath =
        "src/Harbor.Application/Permissions/PathArgExtractionPolicy.cs";

    /// <summary>One quoted, tool-shaped word on a code line.</summary>
    private static readonly Regex QuotedWord = new(@"""(?<name>[a-z][a-z0-9_]*)""", RegexOptions.Compiled);

    /// <summary>How far apart two mentions may be and still count as one table.</summary>
    private const int TableWindowLines = 3;

    /// <summary>
    ///     How many tool-SHAPED names a run must contain before it is judged a table
    ///     at all — real tools or not. This is the gate that separates a table from a
    ///     JSON schema, which quotes no tool names in bulk.
    /// </summary>
    private const int TableMinimumNames = 3;

    /// <summary>
    ///     The fraction of a run's distinct names that must be REAL tools for the run
    ///     to count as a tool table: <c>RealToolNumerator / RealToolDenominator</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two earlier thresholds were plain counts, and CI falsified both, which
    ///         is why this is a ratio.
    ///     </para>
    ///     <para>
    ///         "Three REAL tool names" was too strict. A dead row is by definition a
    ///         name that is not a tool, so a table carrying one phantom has one fewer
    ///         real name than a correct one — the rule was least able to see a table
    ///         that was slightly wrong, which is the only kind it exists for. The
    ///         non-vacuity control caught that against a planted
    ///         <c>"read", "write", "web_fetch"</c>.
    ///     </para>
    ///     <para>
    ///         "Two REAL tool names" was too loose. It matched
    ///         <c>SlashCommandCatalog</c>: a list of slash-command names that happens
    ///         to contain <c>tree</c> and <c>skill</c>, two real tools among thirtysix
    ///         names. Those are slash commands (Harbor writes them without a leading
    ///         <c>/</c>), not tools, and the overlap is coincidence. A ratio separates
    ///         the cases — a tool table is almost entirely tool names, while a
    ///         coincidental mention is a small minority of one.
    ///     </para>
    /// </remarks>
    private const int RealToolNumerator = 2;

    private const int RealToolDenominator = 3;

    /// <summary>A tool name spelled as a C# string literal, with the line it was on.</summary>
    private sealed record Mention(string File, int Line, string Name)
    {
        public string RelativePath => SourceScan.Relative(File);

        public override string ToString() => $"{RelativePath}:{Line}  \"{Name}\"";
    }

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     No product file hand-maintains a set of builtin tool names. The SET is
    ///     what makes it a list; a new tool must not require a C# edit here.
    /// </summary>
    [Test]
    public async Task No_Product_File_Hand_Maintains_A_Set_Of_Tool_Names()
    {
        FrozenSet<string> known = ToolNameInventory.Names;
        IReadOnlyList<IReadOnlyList<Mention>> tables = FindToolTables(ReadProductSources(), known);

        var offenders = new List<string>();
        foreach (IReadOnlyList<Mention> table in tables)
        {
            string relative = table.First().RelativePath;
            if (PolicyExemptions.ContainsKey(relative))
            {
                continue;
            }

            offenders.Add(
                $"{relative}: {string.Join(", ", Sorted(table.Select(m => m.Name)))}");
        }

        await Assert.That(offenders).IsEmpty()
            .Because("a per-tool name SET is a list a new tool silently outgrows. If the fact is "
                   + "derivable, read the tool's own declaration (ITool.SafetyProfile carries the "
                   + "safety kind and the argument name) — the same move #680 made for glyphs. If "
                   + "it is genuinely not derivable (a permission decision, a renderer's grouping), "
                   + "add the file to PolicyExemptions with the reason, so the exemption reads as a "
                   + "decision rather than an oversight. Offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>
    ///     THE DEAD ROW. A hard-coded tool table is only dangerous when a row can
    ///     silently never fire, and the specific case the issue quotes is a name
    ///     that is not a tool at all: <c>web_fetch</c> (fixed in #680) and
    ///     <c>list</c> (still present). A row keyed by a name no tool registers is
    ///     a guard that does not guard, and no compiler and no exception says so.
    /// </summary>
    /// <remarks>
    ///     Checked only inside tables rule 1 already recognised as tool tables, so
    ///     a JSON schema property or a CLI verb cannot trip it. A name is judged
    ///     against the DERIVED vocabulary, so this stays honest as tools are added.
    /// </remarks>
    [Test]
    public async Task No_Tool_Table_Is_Keyed_On_A_Name_That_Is_Not_A_Tool()
    {
        FrozenSet<string> known = ToolNameInventory.Names;
        IReadOnlyList<IReadOnlyList<Mention>> tables = FindToolTables(ReadProductSources(), known);

        var offenders = new List<string>();
        foreach (IReadOnlyList<Mention> table in tables)
        {
            foreach (Mention mention in table)
            {
                if (known.Contains(mention.Name))
                {
                    continue;
                }

                offenders.Add(
                    $"{mention.RelativePath}:{mention.Line} keys a tool table on \"{mention.Name}\", "
                    + "which no tool registers. That row can never fire — the same defect as the "
                    + "\"web_fetch\" arm #595 quoted, invisible until something depends on it.");
            }
        }

        await Assert.That(offenders).IsEmpty().Because(string.Join("\n", offenders));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     Discovery really reads the product trees, the tool implementations and
    ///     the declarations. A scan rooted at a path that does not exist returns an
    ///     empty set and satisfies every rule above — the NetArchTest trap, in its
    ///     filesystem form.
    /// </summary>
    [Test]
    public async Task Discovery_Finds_A_Real_Vocabulary_And_A_Real_File_Set()
    {
        IReadOnlyList<string> productFiles = SourceScan.EnumerateProductCsFiles();

        await Assert.That(productFiles.Count).IsGreaterThan(100)
            .Because("the scan found almost no .cs files under src/+apps/; the product trees are "
                   + "wrong and every rule in this file is then satisfied by an empty scan");

        await Assert.That(productFiles.Select(SourceScan.Relative).Contains(KnownTableFileRelativePath))
            .IsTrue()
            .Because($"{KnownTableFileRelativePath} exists and holds a tool table today; if "
                   + "discovery cannot see it, the scan is broken rather than clean");

        await Assert.That(ToolNameInventory.Names.Count).IsGreaterThan(10)
            .Because("the tool-name vocabulary is empty or nearly so, so both rules would accept "
                   + "any table at all");

        await Assert.That(ToolNameInventory.Names.Contains("read")).IsTrue()
            .Because("'read' is a builtin tool; if the vocabulary lost it, both rules would accept "
                   + "the very tables this file exists to remove");

        await Assert.That(ToolNameInventory.Names.Contains("webfetch")).IsTrue()
            .Because("'webfetch' is the tool the issue's \"web_fetch\" arm failed to name. If the "
                   + "vocabulary did not hold it, the dead-row rule would report the real tool as "
                   + "the phantom one — the rule inverted rather than silent");

        await Assert.That(ToolNameInventory.ToolImplementationFiles().Count).IsGreaterThan(10)
            .Because("no tool implementation files were found, so the ToolName.Create half of the "
                   + "vocabulary contributed nothing and a plugin tool would be invisible to it");

        await Assert.That(ToolNameInventory.ArgKinds.Count).IsGreaterThan(10)
            .Because("the declared safety axis is empty, so a gate reasoning about it would be "
                   + "asserting against nothing");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The table-finder is handed synthetic snippets: a
    ///     table keyed by real tool names (must be seen), prose that merely mentions
    ///     tools (must not), a single lookup (must not — that is not a list), and a
    ///     table of non-tool words (must not — that is a schema). It MUST report
    ///     the first and nothing else.
    /// </summary>
    [Test]
    public async Task Non_Vacuity_The_Table_Finder_Fires_On_A_Planted_Table_Only()
    {
        const string plantedTable = """
            private static readonly FrozenSet<string> DefaultTools = new[]
            {
                "read", "write", "edit",
            };
            """;

        const string commentAboutTools = """
            // "read", "write" and "edit" used to be listed here before the derivation landed.
            """;

        const string singleLookup = """
            private static int Rank(string toolName) =>
                toolName == "read" ? 1 : 0;
            """;

        const string nonToolWords = """
            private static readonly FrozenSet<string> Excluded = new[]
            {
                "obj", "bin", "node_modules",
            };
            """;

        FrozenSet<string> known = ToolNameInventory.Names;

        IReadOnlyList<IReadOnlyList<Mention>> planted =
            FindToolTables([("planted.cs", plantedTable)], known);
        IReadOnlyList<IReadOnlyList<Mention>> commented =
            FindToolTables([("commented.cs", commentAboutTools)], known);
        IReadOnlyList<IReadOnlyList<Mention>> single =
            FindToolTables([("lookup.cs", singleLookup)], known);
        IReadOnlyList<IReadOnlyList<Mention>> schema =
            FindToolTables([("schema.cs", nonToolWords)], known);

        await Assert.That(planted.Count).IsEqualTo(1)
            .Because("the planted snippet declares exactly one table of tool names; if the finder "
                   + "saw fewer the rule is vacuous, and if it saw more it is noise that will get "
                   + "worked around rather than obeyed");

        await Assert.That(Sorted(planted[0].Select(m => m.Name)))
            .IsEquivalentTo(new[] { "edit", "read", "write" })
            .Because("the finder must recover the planted table's names in full, or the rule "
                   + "cannot name the offender it reports");

        await Assert.That(commented).IsEmpty()
            .Because("the commented snippet is prose about the old table, not a table; a rule that "
                   + "reads comments fails the moment someone documents the fix");

        await Assert.That(single).IsEmpty()
            .Because("a single quoted tool name is a lookup or a tool naming itself, not a list — "
                   + "flagging it would make the rule unusable");

        await Assert.That(schema).IsEmpty()
            .Because("the schema snippet quotes words but names no tool, so it is data, not a table "
                   + "of tools");

        await Assert.That(known.Contains("read") && known.Contains("write") && known.Contains("edit"))
            .IsTrue()
            .Because("the planted table is keyed by real tool names; if the vocabulary lost them the "
                   + "control would pass for the wrong reason");
    }

    /// <summary>
    ///     THE DEAD-ROW CONTROL. The dead-row rule must reject a table keyed on a
    ///     name no tool registers, and accept the same table once the name is
    ///     corrected. Checked against the derivation, not a fixture, so it stays
    ///     honest as the vocabulary changes.
    /// </summary>
    [Test]
    public async Task Non_Vacuity_The_Dead_Row_Rule_Turns_On_The_Name_And_Not_The_Shape()
    {
        FrozenSet<string> known = ToolNameInventory.Names;

        const string phantomName = """
            private static readonly FrozenSet<string> DefaultTools = new[]
            {
                "read", "write", "web_fetch",
            };
            """;

        const string realNames = """
            private static readonly FrozenSet<string> DefaultTools = new[]
            {
                "read", "write", "webfetch",
            };
            """;

        string[] Phantoms(string source) => DeadRows(
            FindToolTables([("probe.cs", source)], known), known);

        string[] flagged = Phantoms(phantomName);

        await Assert.That(flagged).IsEquivalentTo(new[] { "web_fetch" })
            .Because("'web_fetch' is not a tool — the tool is 'webfetch'. The dead-row rule exists "
                   + "for exactly this, and it must name the offending row rather than the whole table");

        await Assert.That(Phantoms(realNames)).IsEmpty()
            .Because("the same table with the CORRECT name has no dead row; a rule that flagged it "
                   + "would be reporting the shape, not the defect, and would be turned off");

        await Assert.That(known.Contains("webfetch")).IsTrue()
            .Because("'webfetch' must be in the derived vocabulary, or the corrected table above "
                   + "would read as a dead row and the control would prove nothing");
    }

    /// <summary>
    ///     THE DENSITY CONTROL. A list that merely MENTIONS a couple of tool names
    ///     among many non-tool names is not a tool table. CI caught exactly this:
    ///     <c>SlashCommandCatalog</c> holds the slash commands <c>tree</c> and
    ///     <c>skill</c>, which are spelled like two tools, among thirty-six names that
    ///     are not tools at all. A rule that reported that list would be reporting
    ///     coincidence, and the first person to hit it would delete the rule.
    /// </summary>
    [Test]
    public async Task Non_Vacuity_A_Coincidental_Mention_Is_Not_A_Tool_Table()
    {
        FrozenSet<string> known = ToolNameInventory.Names;

        // Shaped like SlashCommandCatalog: two real tool names, many that are not.
        const string slashCommands = """
            private static readonly IReadOnlyList<Command> Definitions =
            [
                new("help", "Show this help screen", ["h"]),
                new("exit", "Exit Harbor", ["quit"]),
                new("sessions", "List recent sessions", []),
                new("tree", "Show the session fork tree", []),
                new("model", "Switch the active LLM model", ["m"]),
                new("skill", "Check skill freshness", ["refresh"]),
                new("storage", "Show the session storage backend", []),
            ];
            """;

        IReadOnlyList<IReadOnlyList<Mention>> tables = FindToolTables([("catalog.cs", slashCommands)], known);

        await Assert.That(tables).IsEmpty()
            .Because("'tree' and 'skill' are slash commands that happen to share a spelling with "
                   + "two tools; the rest are not tools either. A run that is mostly NOT tool "
                   + "names is not a tool table, and reporting it would be reporting coincidence");
    }

    /// <summary>
    ///     THE POINT OF ALL OF IT: a tool that exists in NO hand-written list is
    ///     still covered, because the policy set is derived from the declaration.
    ///     If this fails, a new tool could be added and simply not guarded, which
    ///     is the bug #595 reports — so it is asserted rather than assumed.
    /// </summary>
    [Test]
    public async Task A_Tool_Nobody_Listed_Is_Still_Guarded()
    {
        // Invented here, declared Path() exactly as a new tool would be. It appears
        // in no table in the repository.
        const string brandNewTool = "a_tool_nobody_listed";

        IReadOnlyList<IArgSafetyPolicy> policies = ToolSafetyPolicies.Build(
        [
            new ToolSafetyDeclaration(brandNewTool, ToolSafetyProfile.Path()),
            new ToolSafetyDeclaration("bash", ToolSafetyProfile.Command()),
            new ToolSafetyDeclaration("webfetch", ToolSafetyProfile.Opaque),
        ]);

        var pathGuard = policies.OfType<PathGuardSafetyPolicy>().SingleOrDefault();
        await Assert.That(pathGuard).IsNotNull()
            .Because("a Path() declaration must produce a path guard; without one the derivation is "
                   + "not happening and every tool is unguarded");

        await Assert.That(pathGuard!.AppliesTo(brandNewTool)).IsTrue()
            .Because("the tool declared Path() and nothing anywhere lists it by name — that is what "
                   + "makes adding a tool safe. A guard covering only names someone remembered is "
                   + "the bug this whole file exists to prevent");

        await Assert.That(pathGuard.AppliesTo("webfetch")).IsFalse()
            .Because("an Opaque tool declared an explicit opt-out, so the traversal guard must stand "
                   + "down for it — deriving must not guard more than was asked for");

        var bashGuard = policies.OfType<BashSafetyPolicy>().SingleOrDefault();
        await Assert.That(bashGuard).IsNotNull()
            .Because("a Command() declaration must produce the destructive-command guard");

        await Assert.That(bashGuard!.AppliesTo(brandNewTool)).IsFalse()
            .Because("a Path() tool is not a shell tool; over-guarding would suppress the "
                   + "\"ls *\"-style allow rules that bash depends on");
    }

    // =====================================================================
    // 3. Plumbing.
    // =====================================================================

    /// <summary>
    ///     The product sources, in the shape <see cref="FindToolTables" /> consumes.
    ///     A file that cannot be read is skipped; discovery is what keeps the set
    ///     honest.
    /// </summary>
    private static IReadOnlyList<(string Path, string Source)> ReadProductSources() =>
    [
        .. SourceScan.EnumerateProductCsFiles()
            .Select(path => (Path: path, Source: SourceScan.TryReadAllText(path)))
            .Where(pair => pair.Source is not null)
            .Select(pair => (pair.Path, pair.Source!)),
    ];

    /// <summary>
    ///     The distinct unknown names in the supplied tables, sorted — the dead
    ///     rows the rule reports.
    /// </summary>
    private static string[] DeadRows(
        IReadOnlyList<IReadOnlyList<Mention>> tables,
        FrozenSet<string> known) =>
        [.. Sorted(tables.SelectMany(t => t)
            .Where(m => !known.Contains(m.Name))
            .Select(m => m.Name))];

    /// <summary>
    ///     The distinct values, ordinal-sorted. A failure message whose order
    ///     depends on dictionary iteration is a message that differs between runs
    ///     and cannot be diffed against the previous one.
    /// </summary>
    private static string[] Sorted(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    ///     Every run of quoted words in the supplied sources that qualifies as a
    ///     TOOL table: at least <see cref="TableMinimumNames" /> tool-shaped names of
    ///     which <see cref="TableMinimumRealTools" /> are real tools, all within
    ///     <see cref="TableWindowLines" /> lines of each other in one file.
    /// </summary>
    /// <remarks>
    ///     Requiring tool-shaped names in bulk is what makes this precise. Matching
    ///     on "two adjacent quoted words" instead flags every JSON schema and every
    ///     CLI verb list in the repository — noise a rule gets disabled rather than
    ///     obeyed. A schema names no tools, so it can never reach the threshold.
    /// </remarks>
    private static IReadOnlyList<IReadOnlyList<Mention>> FindToolTables(
        IReadOnlyList<(string Path, string Source)> sources,
        FrozenSet<string> known)
    {
        var tables = new List<IReadOnlyList<Mention>>();

        foreach ((string path, string source) in sources)
        {
            var mentions = new List<Mention>();
            string[] lines = SourceScan.StripComments(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in QuotedWord.Matches(lines[i]))
                {
                    mentions.Add(new Mention(path, i + 1, match.Groups["name"].Value));
                }
            }

            CollectTables(mentions, known, tables);
        }

        return tables;
    }

    /// <summary>
    ///     Splits a file's mentions into candidate runs and keeps the ones that
    ///     clear the tool-name threshold.
    /// </summary>
    private static void CollectTables(
        List<Mention> mentions,
        FrozenSet<string> known,
        List<IReadOnlyList<Mention>> tables)
    {
        Mention[] ordered = [.. mentions.OrderBy(m => m.Line)];

        var run = new List<Mention>();
        foreach (Mention mention in ordered)
        {
            if (run.Count > 0 && mention.Line - run[^1].Line > TableWindowLines)
            {
                CloseRun(run, known, tables);
            }

            run.Add(mention);
        }

        CloseRun(run, known, tables);
    }

    /// <summary>
    ///     Closes a candidate run: keeps it when it is tool-shaped enough and about
    ///     tools, then clears the buffer for the next run.
    /// </summary>
    /// <remarks>
    ///     A run is built per file (see <see cref="CollectTables" />), so every
    ///     mention in it already shares a path and the buffer can be handed over
    ///     directly. Copying it, because the buffer is reused for the next run.
    /// </remarks>
    private static void CloseRun(
        List<Mention> run,
        FrozenSet<string> known,
        List<IReadOnlyList<Mention>> tables)
    {
        if (run.Count > 0)
        {
            string[] names = [.. run.Select(m => m.Name).Distinct(StringComparer.Ordinal)];
            int realTools = names.Count(known.Contains);

            // Density, not a count: a tool table is mostly tool names, so a table
            // holding a phantom still clears the bar while a slash-command list
            // that happens to mention two of them does not.
            bool mostlyTools = realTools * RealToolDenominator >= names.Length * RealToolNumerator;

            if (names.Length >= TableMinimumNames && mostlyTools)
            {
                tables.Add([.. run]);
            }
        }

        run.Clear();
    }
}
