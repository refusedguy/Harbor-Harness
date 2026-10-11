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
// Neither rule is keyed on a tool NAME, which is the point of deriving the
// vocabulary: renaming a tool does not disarm either rule, it re-points them.
// Measured, not assumed — taking `read`, `glob`, `lsp`, `ripgrep` and
// `session_read` out of the derived vocabulary one at a time, in each case the
// dead-row rule reports the stale name and rule 1 still sees nine tables. A
// name-keyed rule would have gone quiet on exactly that edit.
//
// THE EXEMPTION LIST IS NOT THE FILE SET
// --------------------------------------
// `PolicyExemptions` is a set of TOLERATED files inside a scan that covers all
// of `src/` and `apps/`, so nothing is unchecked because of it and a rename or
// a move makes a key stop matching loudly. The list's own risk runs the other
// way — an entry outliving the table it tolerates — so a third test re-probes
// every entry against the finder (#860). Two of eight entries carried no table
// at all and are gone; the guard for the guard is what stops the next one.
//
// NON-VACUITY
// -----------
// A guard that matches nothing is indistinguishable from a guard that is broken,
// and a broken guard is worse than none because it is believed. Six tests close
// that: discovery must find a real file set and a real vocabulary; the table
// finder must fire on a planted table and stay silent on a comment, on a single
// lookup, and on a table of non-tool words; a set of exactly TWO real names must
// be seen as a table too (#832 — three was the smallest value that kept the
// corpus green, and #793 shipped the two-name shape it denied); a coincidental
// mention of two tools among thirty-six slash commands must NOT be; the
// dead-row check must catch a planted unknown name and stay silent on a planted
// real one; and the derivation must cover a tool that exists NOWHERE in a
// hand-written list — which is what proves the guard tracks tools rather than a
// fixed roster.

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
    ///     <para>
    ///         This list is NOT the file set the rule checks, and that difference is
    ///         the whole safety argument, so it is measured rather than asserted. The
    ///         scan is <see cref="SourceScan.ProductTrees" /> — every <c>.cs</c> file
    ///         under <c>src/</c> and <c>apps/</c>, 993 of them — and this table is a
    ///         set of tolerated files inside it. Nothing outside the list goes
    ///         unchecked because of the list: a new file carrying a tool table is
    ///         enforced, and a rename or a move makes the key stop matching, which is
    ///         a RED test rather than a silent gap. That is the safe direction, and
    ///         the cost runs the other way — the list cannot tell a live policy from a
    ///         fossil, which is what the liveness test below is for. Six entries, all
    ///         six carrying a table the finder recognises, across nine recognised
    ///         tables in six files and zero offenders; #860 removed the two that
    ///         carried none. The count is a measurement, not a target.
    ///         (#1170 adds a seventh: per-tool permission presentation.)
    ///     </para>
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

        ["src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs"] =
            "TrackedTools is the set of tools whose edits the Recent-Changes panel "
            + "watches — a projection concern with no tool-side declaration.",

        ["src/Harbor.Ui.Framework.State/Diff/DiffPreview.cs"] =
            "The diff preview must decide which tool calls render a diff. A tool does "
            + "not declare that it is diff-shaped; the renderer owns that.",

        ["src/Harbor.Tui.CellForge/Chat/Panels/PermissionPresentation.cs"] =
            "Per-tool permission presentation (opencode steal #1170): which gate "
            + "renders a diff, a command, a pattern or file:line:col. A tool does "
            + "not declare how its permission prompt is drawn; the renderer owns that.",
    };

    /// <summary>
    ///     The file that used to hold the hand-written tool table, named so the
    ///     non-vacuity check can prove discovery still sees it. Not an exemption.
    /// </summary>
    /// <remarks>
    ///     Renamed from "Known", and the claim behind it corrected, because it no
    ///     longer holds one: <c>PathArgExtractionPolicy</c> derived its set away in
    ///     #595 (it reads the <c>ToolArgKind.Path</c> rows of
    ///     <c>BuiltinToolSafetyProfiles.All</c>), so the file now quotes no tool name
    ///     and the finder recognises no table in it. The constant stays as the
    ///     discovery canary, because a scan rooted at a wrong path returns an empty
    ///     set and would satisfy every rule in this file — the trap the check exists
    ///     to catch. What it can no longer do is assert a property the file does not
    ///     have: it proves discovery SEES the path, and it cannot tell "discovery sees
    ///     a file" from "discovery sees a table". The vocabulary assertions beside it
    ///     carry that weight instead, because they read the derivation and so cannot
    ///     rot into a claim about one file's contents. Same rename
    ///     <c>ProviderIdDispatchRule</c> made to its
    ///     <c>FormerTableFileRelativePath</c>.
    /// </remarks>
    private const string FormerTableFileRelativePath =
        "src/Harbor.Application/Permissions/PathArgExtractionPolicy.cs";

    /// <summary>One quoted, tool-shaped word on a code line.</summary>
    private static readonly Regex QuotedWord = new(@"""(?<name>[a-z][a-z0-9_]*)""", RegexOptions.Compiled);

    /// <summary>How far apart two mentions may be and still count as one table.</summary>
    private const int TableWindowLines = 3;

    /// <summary>
    ///     How many tool-SHAPED names a run must contain before it is judged a table
    ///     at all — real tools or not. Two, and two is a floor rather than a
    ///     preference: one quoted name is a lookup, two of them are a set.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This was 3 (#832), which declared that a set of exactly two is not a
    ///         set — and #793 shipped exactly that set. <c>HasSupervisionTools</c>
    ///         string-matched <c>"session_read"</c> and <c>"session_steer"</c>
    ///         inline, so the recipe rendered on the default <c>code</c> agent while
    ///         telling the model to steer with a tool <c>ResolveTools</c> had not
    ///         offered it. Nothing about the shape was exotic; two names three lines
    ///         apart is the smallest possible instance of the defect.
    ///     </para>
    ///     <para>
    ///         It was 3 because 3 was the smallest value that kept the corpus green,
    ///         not because anything distinguishes a triple from a pair. What the count
    ///         is usually credited with — telling a table from a JSON schema — is
    ///         already done by the density ratio, and measurably so: the schema runs
    ///         in <c>PatchTool.cs</c> and <c>RipGrepTool.cs</c> carry 6 and 12
    ///         distinct names of which exactly ONE is a tool, and the ratio rejects
    ///         them without the count ever being consulted. Take the schema job away
    ///         and what the count still does that the ratio does not is the
    ///         single-name floor, which is what this constant now is.
    ///     </para>
    ///     <para>
    ///         The cost was measured BEFORE the move, not rationalised after it. Over
    ///         the 26 derived names and the 167 quoted occurrences in <c>src/</c> +
    ///         <c>apps/</c>, exactly ONE run holds precisely two distinct real tool
    ///         names — the <c>session_broadcast</c> / <c>session_inbox</c> pair in
    ///         <c>BuiltinToolSafetyProfiles.cs</c> — and that file already holds an
    ///         exemption with a stated reason. The names are the citation and not a
    ///         line range: a <c>.cs:NNN</c> in prose is an anchor nothing re-probes, and
    ///         it drifts the first time a row is inserted above it, which is the same
    ///         defect <see cref="FormerTableFileRelativePath" /> had. So the move adds
    ///         one table to the corpus and zero offenders. A threshold that reddens a
    ///         hundred innocent tables is the outcome that was looked for, and it is
    ///         not the one that happened.
    ///     </para>
    ///     <para>
    ///         At two names the ratio has nothing left to arbitrate, which is why two
    ///         is a floor and not a compromise: a two-name run is a table exactly when
    ///         both names are real tools, and two real tool names three lines apart IS
    ///         the hand-maintained-name-set shape. The coincidence class the ratio
    ///         exists to exclude — <c>SlashCommandCatalog</c>'s <c>tree</c> and
    ///         <c>skill</c> among thirty-six slash commands — is a seven-name run, so
    ///         it is rejected by the ratio and never depended on this number.
    ///     </para>
    /// </remarks>
    private const int TableMinimumNames = 2;

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

    /// <summary>
    ///     THE EXEMPTION STAYS HONEST. <see cref="PolicyExemptions" /> is a list of
    ///     files whose tool tables are tolerated, so an entry that no longer
    ///     tolerates anything grants a permission nothing reads — and the next
    ///     person to add a real table to that file inherits it without re-reading
    ///     the reason.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the same check <c>ProviderIdDispatchRule</c> runs over
    ///         <c>DispatchExemptions</c>, and the same liveness half
    ///         <see cref="ExemptionReason" /> deliberately does NOT do: that helper
    ///         checks that a row states an argument, and says outright that whether
    ///         "the debt it describes still exists" is "a different question" which
    ///         "the tables that can answer it already do it by liveness — every
    ///         baseline row is re-probed against reality and fails when the violation
    ///         it grandfathers is gone". This table is one of the ones that can answer
    ///         it, because the rule it suppresses can be re-run on the one file.
    ///     </para>
    ///     <para>
    ///         Liveness is measured against the finder, not against the reason prose.
    ///         <c>RipGrepTool.cs</c>'s entry said "the tool's own JSON schema … a
    ///         schema is data, not a table of tools", but the reason is not the test:
    ///         the schema run there is twelve distinct names of which exactly one is a
    ///         tool, so the density ratio rejects it before the count is consulted,
    ///         and the file contributes no table at all. Probing the finder rather
    ///         than the prose is what makes the row falsifiable — the prose can be
    ///         true of a file the rule would never have flagged.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Every_Policy_Exemption_Names_A_File_That_Still_Carries_A_Tool_Table()
    {
        FrozenSet<string> known = ToolNameInventory.Names;

        // Ordered so a failure message does not depend on dictionary iteration —
        // the same reason `Sorted` exists.
        var rows = PolicyExemptions
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (Key: kv.Key, Row: new ExemptionReason.Row(kv.Value, null)));

        IReadOnlyList<string> unreasoned = ExemptionReason.RowsWithoutAReason(
            $"{nameof(ToolNameListRule)}.{nameof(PolicyExemptions)}",
            rows);

        await Assert.That(unreasoned).IsEmpty()
            .Because("an entry here suppresses rule 1 for a whole file, and one that does not say why "
                   + "is indistinguishable from an oversight: " + string.Join(" | ", unreasoned));

        HashSet<string> scanned = SourceScan.EnumerateProductCsFiles()
            .Select(SourceScan.Relative)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> carrying = FindToolTables(ReadProductSources(), known)
            .Select(table => table[0].RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        var dead = new List<string>();
        foreach (string path in PolicyExemptions.Keys.Order(StringComparer.Ordinal))
        {
            if (!scanned.Contains(path))
            {
                dead.Add($"{path} — names a file the scan does not see, so the entry was dead on arrival");
            }
            else if (!carrying.Contains(path))
            {
                dead.Add($"{path} — carries no tool table the finder recognises, so nothing is being "
                       + "exempted and the entry only widens the rule");
            }
        }

        await Assert.That(dead).IsEmpty()
            .Because("a dead exemption is worse than no entry: the next person to add a table to that "
                   + "file inherits a permission nobody re-reads, and the guard cannot tell a live "
                   + "policy from a stale copy of a declaration. Delete the entry and let the rule "
                   + "apply — if the file really does need one, the finder will say so and the reason "
                   + "can be written against a table that exists. Dead entries: " + string.Join(" | ", dead));
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

        await Assert.That(productFiles.Select(SourceScan.Relative).Contains(FormerTableFileRelativePath))
            .IsTrue()
            .Because($"{FormerTableFileRelativePath} exists and is inside the scanned set — it is the "
                   + "file that used to carry the derived-away table, so its absence means discovery is "
                   + "broken rather than the tree being clean");

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
    ///     THE TWO-NAME CONTROL (issue #832). A set of exactly TWO tool names is a
    ///     set, and <see cref="TableMinimumNames" /> currently declares that it is
    ///     not — so a two-name hand-maintained list is invisible to both rules.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is not hypothetical. <c>SystemPromptBuilder.HasSupervisionTools
    ///         </c> string-matched <c>"session_read"</c> and <c>"session_steer"</c>
    ///         inline (#793), and the OR shipped: the predicate rendered the recipe
    ///         while naming a tool the turn had not been offered, because
    ///         <c>PermissionRuleset.Default</c> ALLOWS <c>session_read</c> and ASKS
    ///         for <c>session_steer</c> while <c>ResolveTools</c> keeps only Allow.
    ///         Every default <c>code</c> turn was told to steer with a tool it could
    ///         not call. #818 derived the role and closed the defect, and recorded the
    ///         threshold as "measured-no" rather than guessing at it.
    ///     </para>
    ///     <para>
    ///         Both syntactic forms are planted, because the rule reads names and not
    ///         syntax: a predicate comparing two names, and a two-element initialiser.
    ///         A guard that caught only one of them would leave the other as a
    ///         spelling that gets past it.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Non_Vacuity_A_Two_Name_Tool_Set_Is_A_Table()
    {
        FrozenSet<string> known = ToolNameInventory.Names;

        // The shape #793 shipped: two names compared inline, three lines apart or
        // fewer, both real tools.
        const string twoNamePredicate = """
            private static bool HasSupervisionTools(IReadOnlyList<string> tools)
            {
                foreach (string name in tools)
                {
                    if (name == "session_read" || name == "session_steer")
                    {
                        return true;
                    }
                }

                return false;
            }
            """;

        // The same pair as a table literal — the shape the issue describes.
        const string twoNameSet = """
            private static readonly FrozenSet<string> SupervisionTools = new[]
            {
                "session_read", "session_steer",
            };
            """;

        IReadOnlyList<IReadOnlyList<Mention>> fromPredicate =
            FindToolTables([("prompt.cs", twoNamePredicate)], known);
        IReadOnlyList<IReadOnlyList<Mention>> fromSet =
            FindToolTables([("policy.cs", twoNameSet)], known);

        await Assert.That(fromPredicate.Count).IsEqualTo(1)
            .Because("two real tool names compared in one predicate is a hand-maintained set of tool "
                   + "names, which is what both rules exist to remove. If the finder cannot see it, the "
                   + "threshold is admitting that a set of two is not a set — and that is the shape #793 "
                   + "shipped, so the blindness is measured, not hypothetical");

        await Assert.That(Sorted(fromPredicate[0].Select(m => m.Name)))
            .IsEquivalentTo(new[] { "session_read", "session_steer" })
            .Because("the finder must recover the names in full or it cannot name the offender it "
                   + "reports");

        await Assert.That(fromSet.Count).IsEqualTo(1)
            .Because("the same pair written as an initialiser is the same defect; a rule that saw only "
                   + "the predicate form would leave the literal form as a spelling that gets past it");

        await Assert.That(Sorted(fromSet[0].Select(m => m.Name)))
            .IsEquivalentTo(new[] { "session_read", "session_steer" })
            .Because("the names, not the count of runs, are what makes the report actionable");

        await Assert.That(known.Contains("session_read") && known.Contains("session_steer")).IsTrue()
            .Because("both are declared in BuiltinToolSafetyProfiles, so the control is measuring the "
                   + "threshold rather than a vocabulary that lost the names");
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
    ///     which at least <see cref="RealToolNumerator" /> in
    ///     <see cref="RealToolDenominator" /> are real tools, all within
    ///     <see cref="TableWindowLines" /> lines of each other in one file.
    /// </summary>
    /// <remarks>
    ///     Requiring tool-shaped names in bulk is what makes this precise, and at two
    ///     names the density ratio is what keeps it precise: a schema quotes words but
    ///     names no tool, so it can never reach the ratio, and a CLI verb list that
    ///     happens to spell two tools among thirty-six names fails it too. What the
    ///     count still adds over the ratio is the single-name floor — one quoted name
    ///     is a lookup, which is a fact about syntax rather than about tools.
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
