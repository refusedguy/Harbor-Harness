// CellForgeWidgetAxisRules.cs — GUARD for issue #564.
//
// THE QUESTION #564 ASKS, AND THE ANSWER
// ---------------------------------------
// "Is the CellForge widget set closed at compile time, and can a plugin add a
// widget?" #564 measured it and reported the cost of an in-tree widget (4-5
// files, all structural). What it did not settle is the question that decides
// whether "closed" is a defect or the correct answer: **is the widget axis a
// DECLARED extension axis, or only an internal contract?**
//
// Measured against the tree, it is an internal contract, and the three facts
// that make it one are all structural rather than editorial:
//
//   * `Panel` is `public` because it has to be — `Harbor.Tui.CellForge.Engine`
//     is a separate assembly (post-#435/#436), and the 45 chat widgets are its
//     peers. Public visibility is a consequence of the assembly split, not a
//     promise to anyone outside it.
//   * There is NO DOOR. The one shipped `LayoutTree` is constructed in exactly
//     one place — `new LayoutTree()` at
//     `src/Harbor.Tui.CellForge/Chat/Widgets/ChatScreenLayout.cs:876`, inside the
//     static factory `ChatScreen.Build(...)`, whose seven parameters are all
//     in-tree. No plugin is ever handed a tree, a `ChatScreen`, or a `Panel`.
//     `LayoutTree` itself is public with a public `AddRoot`/`Split`, so a
//     third party CAN build a tree — it is a library surface for tests and
//     goldens, and a tree nobody paints is not a widget axis.
//   * The axis that IS declared for plugins, and IS live end to end, is the
//     PANEL axis: `ITuiPanelPlugin` → `IPluginLoadHost.RegisterPanelProvider` →
//     `PanelRegistryPluginAdapter` → `CellForgePanelRegistry` → the dock. It
//     paints rows of text, which is a real (narrow) capability, not a widget.
//
// So "closed at compile time" is not a defect. The DEFECT is the third thing,
// and it is the same shape as #620: a seam the product collects and never
// renders, taught by the documentation as the way to add a panel.
//
// THE VIEW SEAM: A DOOR, A MARKER, A DISPATCH, AND NO CONSUMER
// -----------------------------------------------------------
// `ITuiPlugin` passes #620's `ExtensionAxisFreezeRule`, and passing it is
// precisely why the problem survived. That rule grades a marker and a DOOR, and
// this seam has both: `ITuiPlugin` is dispatched in `PluginRegistrar.Register`
// and `host.RegisterTuiPlugin(tuiPlugin)` really is invoked. What is missing is
// a fourth thing, which #620's marker/door pair has no name for:
//
//   * `IPluginLoadHost.TuiPlugins`
//     (`src/Harbor.Hosting/Modules/PluginLoadHostAdapter.cs:91`) has NO reader
//     anywhere under `src/` or `apps/`. Confirmed by scan, and by the fact that
//     the only other `TuiPlugins` hits in the repository are a test fake whose
//     property is named `RegisteredTuiPlugins` and therefore is not even a
//     compile coupling to the interface.
//   * therefore `ITuiPlugin.RegisterTui(ViewRegistry, ViewModelRegistry)` has no
//     call site in the product, and a plugin that implements it loads, logs
//     success, and paints nothing.
//   * and in the canonical renderer it could not paint even if it were called:
//     `CellForgeTuiRenderer` draws the screen through `ChatScreenLayout` →
//     `LayoutTree.PaintAll`, not through the base renderer's four
//     `ShouldRenderPlacement` placements (CF-F-001, `CellForgeTuiRenderer.cs:
//     208`). A registered `ITuiView` would be asked to paint into a buffer the
//     cell-diff pipeline also owns.
//
// The promise is not only in the prose. `ITuiPlugin`'s own XML doc
// (`src/Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs:14-21`) teaches
// "Register a new view — append a custom panel to any `TuiViewPlacement`", and
// its sample registers at `SidebarRight` — a placement
// `BaseTuiRenderer.ShouldRenderPlacement` closes with `_ => false`. So even the
// fix #564 recommends ("call `RegisterTui` from `BaseTuiRenderer.Initialize
// Async`") would leave the documented example on the dead arm. That is why the
// fix here is the honest one: SAY IT IS CLOSED, and guard the saying.
//
// WHY A GUARD AND NOT JUST A SENTENCE
// -----------------------------------
// A document is exactly what the next agent re-derives from scratch, and the
// docs gate (`.github/workflows/docs.yml`) checks links, anchors, encoding,
// headings and fence balance — never whether a stated FACT is still true. The
// same gap `DefaultModelDocClaimTests` (#649) was written for. So "the view
// seam is closed" is made a build failure the day it stops being true, and the
// closure is stated where a plugin author will actually read it: the six
// documents that teach plugin authoring, and the interface's own IntelliSense.
//
// THE RULES, AND WHICH ARE RED ON THE TREE THIS LANDS ON
// ------------------------------------------------------
//   1. The shipped layout tree has exactly one construction site, and it is in
//      the declared set. GREEN. This is the "closed at compile time" claim,
//      made mechanical: a second `new LayoutTree(` in product code means a
//      second home for chrome, which is a decision to adjudicate, not an
//      accident.
//   2. The panel axis is the plugin-reachable CellForge surface: its door is
//      invoked on a receiver and CellForge's receiving registry exists. GREEN.
//      Without this, rule 1 plus rule 3 would be indistinguishable from
//      "nothing is extensible", which is a different and much worse finding.
//   3. The declared status of the view seam matches what the product actually
//      renders. GREEN. Two-sided on purpose — see below.
//   4. Every teaching document carries the closure statement on a line that
//      also names `ITuiPlugin`. **RED** on the tree this lands on: six files
//      teach the seam and none of them says it is closed.
//   5. `ITuiPlugin`'s own XML doc carries it. **RED**, same reason — and this is
//      the one a plugin author reads first.
//   6. The declared perimeter settles against the files that really name the
//      seam. GREEN. This is what stops rules 4 and 5 from being satisfied by
//      quietly deleting an entry.
//   7. Non-vacuity. GREEN by construction; it is what makes 1-6 mean anything.
//
// WHY RULE 3 IS TWO-SIDED
// -----------------------
// A guard that pins "the view seam is closed" and nothing else is a freeze on a
// decision, and this repository has already learned that lesson twice: #555
// froze an axis that was genuinely open (fixed by #739, with the carve-out
// guarded rather than asserted), and #620 found two axes frozen that were
// half-open. So the status lives in ONE declared place, and the product decides
// when it moves: wire a reader for `TuiPlugins` and a call for `RegisterTui`,
// and rule 3 goes red and says the status must be flipped to Open — which
// turns rule 4 red until the documents stop calling it closed. The document
// cannot be wrong in either direction without a deliberate edit to this file.
//
// PERIMETER, AND WHY IT IS DECLARED
// ---------------------------------
// `TeachingDocs` and `DescribedNotTaught` are both declared here, and rule 6
// proves the two together are exactly the set of files that name the seam. The
// walk is deliberately BOUNDED — the repository root, `docs/`, `src/*/README.md`
// and `samples/plugins/*/README.md` — because an unbounded walk of this
// repository finds the same 15 files about 30 more times inside `.worktrees/`,
// `graft/`, `.serena/` and `.ai-factory/`, none of which is the product. The
// perimeter is the reader-facing surface, and the settlement rule is what makes
// declaring it safe: a new document that teaches the seam fails rule 6 until
// somebody classifies it, so the set cannot be narrowed by omission.
//
// `DescribedNotTaught` is a JUDGEMENT, and is recorded as one — the reason sits
// next to every entry. Each of those files names the seam in a layer diagram, a
// file table, a completed checklist row or a DIM example, and a sentence about
// closure in any of them would be noise. The cost of misclassifying one is
// stated rather than hidden: a document that teaches the seam while sitting in
// `DescribedNotTaught` is judged by review, not by this file, and the
// mechanical part — the file must still name the seam, so an entry cannot go
// stale — holds either way.
//
// `docs/specs/`, `docs/adr/`, `docs/.kilo-docs/` and `CHANGELOG.md` are OUTSIDE
// the walk, not inside a judgement: a spec is the design as it was decided and
// an ADR is a record of a past decision, and demanding that history keep
// agreeing with today is how a changelog becomes a thing to fix. `docs/*.md` is
// walked non-recursively, so those three directories are excluded by geometry
// rather than by an exception list that could be widened to hide a live file.
//
// NON-VACUITY
// -----------
//   * `PerimeterSettlement` is itself a non-vacuity control: the walk is
//     compared against 15 declared entries, so a broken path or a matcher that
//     stopped matching reports an EMPTY derived set and goes red — the opposite
//     of a silent pass.
//   * `Perimeter_IsLive_AndTheSeamMatcher_StillDiscriminates` additionally
//     drives the REAL predicate with a planted naming line and a planted
//     non-naming line, and requires a floor on the files found and the entries
//     declared, because a scanner that cannot fail is not a guard.
//   * `TeachingDocs` and `DescribedNotTaught` are each asserted non-empty. One
//     of the two being empty would mean the classification had collapsed into
//     "everything" or "nothing" while still passing every other rule.
//
// KNOWN LIMITS — STATED, NOT HIDDEN
// ---------------------------------
// * The closure token is an ADMISSION, not a fix. A document can satisfy rule 4
//   by writing the token in a footnote while still teaching the seam in prose
//   two paragraphs above. What the rule guarantees is that the closure is
//   stated and stays stated; whether the surrounding teaching was actually
//   corrected is a review question and is not mechanical.
// * The token is one line, so the pairing is line-level, not section-level. A
//   400-line document that mentions the seam in nine places needs the token on
//   a line that names it — it does not need nine tokens.
// * Rules 1-3 read source text, not a reference graph. A `TuiPlugins` reader
//   reached by reflection, or a `RegisterTui` call compiled from a delegate,
//   would not be seen. The same boundary `ExtensionAxisFreezeRule` records for
//   "nothing calls it", and for the same reason: these are members on a host
//   object no plugin is ever handed, so a property hand-off cannot fake a read.
// * `TuiPlugins` is matched as `.TuiPlugins` — a read through a receiver. The
//   declaration on `PluginLoadHostAdapter` has no leading dot, so re-writing
//   the property cannot make a dead seam look wired. That asymmetry is the whole
//   point, and it is the same one `ExtensionAxisFreezeRule.OpenedDoor` uses.
//
// STATUS: never compiled. Local dotnet builds are forbidden in this repository
// (8 concurrent agents on a 7 GB box), so CI is the only thing that has ever run
// this code and the first CI run is the first build. Treat the C# as
// unverified until then; a genuine compile error in the first build is the
// point of sending it rather than a defect to reason about locally.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The reading of a markdown or source line this file needs, exposed so the
///     non-vacuity control can drive the REAL predicates rather than a second
///     implementation of them.
/// </summary>
internal static partial class CellForgeSeamProbe
{
    /// <summary>
    ///     The pinned closure marker. A document states the seam is closed by
    ///     carrying this phrase; the guard requires it on a line that also names
    ///     the marker interface, so a file cannot pass by mentioning closure in
    ///     an unrelated section.
    /// </summary>
    internal const string ClosureToken = "closed seam (#564)";

    /// <summary>The marker interface whose status this file governs.</summary>
    internal const string ViewSeamMarker = "ITuiPlugin";

    /// <summary>
    ///     Bounded markdown perimeter, part one: the repository root and
    ///     <c>docs/</c>, non-recursively. Non-recursion is what excludes
    ///     <c>specs/</c>, <c>adr/</c> and <c>.kilo-docs/</c> by geometry rather
    ///     than by an exclusion list that could later be widened.
    /// </summary>
    private static readonly string[] TopLevelMarkdownRoots = ["", "docs"];

    /// <summary>
    ///     Bounded markdown perimeter, part two: one level of component READMEs
    ///     under each of these. A project README describes its own assembly and
    ///     is a documentation surface a reader opens, which <c>docs/</c> is not a
    ///     substitute for; nothing deeper is walked, so <c>bin/</c> and
    ///     <c>obj/</c> are never entered.
    /// </summary>
    private static readonly string[] ComponentReadmeRoots = ["src", "samples/plugins"];

    /// <summary>Product trees the code-side probes read.</summary>
    private static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>Whether the line names the view seam at all.</summary>
    internal static bool NamesSeam(string line) => NamesSeamRegex().IsMatch(line);

    /// <summary>
    ///     Whether the line both names the marker interface AND carries the
    ///     closure token — the adjacency the teaching rule requires.
    /// </summary>
    internal static bool StatesTheClosure(string line) =>
        line.Contains(ViewSeamMarker, StringComparison.Ordinal)
        && line.Contains(ClosureToken, StringComparison.Ordinal);

    /// <summary>
    ///     Every markdown file in the bounded perimeter, repo-relative and
    ///     sorted. Non-recursive per root, which is what keeps this walk from
    ///     finding the same document thirty times inside a sibling worktree.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateMarkdown(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return [];
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string relativeRoot in TopLevelMarkdownRoots)
        {
            string absolute = relativeRoot.Length == 0 ? repoRoot : Path.Combine(repoRoot, relativeRoot);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.md", SearchOption.TopDirectoryOnly))
            {
                found.Add(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
            }
        }

        foreach (string relativeRoot in ComponentReadmeRoots)
        {
            string absolute = Path.Combine(repoRoot, relativeRoot);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string child in Directory.EnumerateDirectories(absolute))
            {
                foreach (string path in Directory.EnumerateFiles(child, "README.md", SearchOption.TopDirectoryOnly))
                {
                    found.Add(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
                }
            }
        }

        return [.. found];
    }

    /// <summary>The perimeter files that name the seam, as the walk found them.</summary>
    internal static IReadOnlyList<string> FindNamingDocuments(string? repoRoot)
    {
        var found = new List<string>();
        foreach (string relative in EnumerateMarkdown(repoRoot))
        {
            string? text = SourceScan.TryReadAllText(Path.Combine(repoRoot!, relative));
            if (text is not null && NamesSeamRegex().IsMatch(text))
            {
                found.Add(relative);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Every line of <paramref name="relative" /> that states the closure.
    ///     A missing file yields nothing, so a renamed document is reported as
    ///     "does not state the closure" by rule 4 rather than crashing the run —
    ///     but rule 6 then fails the settlement, which is the louder signal.
    /// </summary>
    internal static IReadOnlyList<string> FindClosureStatements(string? repoRoot, string relative)
    {
        if (repoRoot is null)
        {
            return [];
        }

        string? text = SourceScan.TryReadAllText(Path.Combine(repoRoot, relative));
        if (text is null)
        {
            return [];
        }

        var lines = new List<string>();
        string[] all = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < all.Length; i++)
        {
            if (StatesTheClosure(all[i]))
            {
                lines.Add($"{relative}:{i + 1}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>
    ///     Every product source line matching <paramref name="matcher" />, as
    ///     <c>file:line</c>. Comments are stripped first: this repository
    ///     justifies every rule in prose, and a <c>/// new LayoutTree()</c> in a
    ///     doc comment would otherwise read as a second construction site.
    /// </summary>
    internal static IReadOnlyList<string> FindProductHits(Regex matcher)
    {
        var hits = new List<string>();
        foreach (string file in SourceScan.EnumerateCsFiles(ProductTrees))
        {
            string? text = SourceScan.TryReadAllText(file);
            if (text is null)
            {
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            for (int i = 0; i < lines.Length; i++)
            {
                if (matcher.IsMatch(lines[i]))
                {
                    hits.Add($"{SourceScan.Relative(file)}:{i + 1}");
                }
            }
        }

        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    /// <summary>
    ///     Whether the product declares a type named <paramref name="typeName" />
    ///     — a type-existence probe, so the receiving end of an axis is checked
    ///     as a DECLARATION rather than inferred from a call.
    /// </summary>
    internal static bool ProductDeclaresType(string typeName)
    {
        Regex declaration = TypeDeclarationRegex(typeName);
        foreach (string file in SourceScan.EnumerateCsFiles(ProductTrees))
        {
            string? text = SourceScan.TryReadAllText(file);
            if (text is null)
            {
                continue;
            }

            if (declaration.IsMatch(SourceScan.StripComments(text)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A reader of the seam: <c>.TuiPlugins</c> — a property read THROUGH A
    ///     RECEIVER. The declaration on <c>PluginLoadHostAdapter</c> has no
    ///     leading dot, so the seam cannot be made to look consumed by rewriting
    ///     the property, which is the asymmetry that makes this a probe.
    /// </summary>
    [GeneratedRegex(@"\.\s*TuiPlugins\b")]
    internal static partial Regex SeamReaderRegex();

    /// <summary>
    ///     A call of the seam's own method through a receiver:
    ///     <c>plugin.RegisterTui(views, vms)</c>. The interface declaration
    ///     <c>public void RegisterTui(…)</c> has no leading dot and cannot match.
    /// </summary>
    [GeneratedRegex(@"\.\s*RegisterTui\s*\(")]
    internal static partial Regex SeamCallRegex();

    /// <summary>A construction of the shipped layout tree.</summary>
    [GeneratedRegex(@"\bnew\s+LayoutTree\s*\(")]
    internal static partial Regex LayoutTreeConstructionRegex();

    /// <summary>The panel axis' door, invoked on a receiver.</summary>
    [GeneratedRegex(@"\.\s*RegisterPanelProvider\s*\(")]
    internal static partial Regex PanelDoorRegex();

    /// <summary>Anything that names the view seam in prose.</summary>
    [GeneratedRegex(@"ITuiPlugin|ITuiView|ViewRegistry|TuiViewBase")]
    internal static partial Regex NamesSeamRegex();

    /// <summary>A declaration of the named type, for the receiving-end probe.</summary>
    private static Regex TypeDeclarationRegex(string typeName) =>
        new($@"\b(class|record|struct|interface)\s+{Regex.Escape(typeName)}\b", RegexOptions.Compiled);
}

/// <summary>
///     Guard for issue #564: the CellForge widget axis is an internal contract
///     and not a declared extension axis, the one plugin-reachable CellForge
///     surface is the panel axis, and the documents that teach the dead view
///     seam are required to say that it is closed.
/// </summary>
public sealed class CellForgeWidgetAxisRules
{
    /// <summary>
    ///     The ONE place the widget axis' status is written down. This is the
    ///     deliberate single-place edit a change of decision costs, and rules 3
    ///     and 4 read it rather than the documents.
    /// </summary>
    internal const string ViewSeamStatus = "Closed";

    /// <summary>
    ///     Where the shipped <c>LayoutTree</c> is allowed to be built. Exactly one
    ///     entry, because the widget axis being closed is the claim this file
    ///     exists to keep true: a second construction site in product code is a
    ///     second home for chrome, and the layout the renderer paints is decided
    ///     by the static factory's parameters rather than by anything a plugin
    ///     supplies.
    /// </summary>
    internal static readonly FrozenSet<string> AllowedTreeBuilders =
        new[] { "src/Harbor.Tui.CellForge/Chat/Widgets/ChatScreenLayout.cs" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The documents that TEACH the view seam — the recipe a plugin author
    ///     actually follows. Each must state the closure on a line that also
    ///     names the marker. This is the set #564 is really about: it is the
    ///     difference between a documented contract and a promise with no door.
    /// </summary>
    internal static readonly FrozenSet<string> TeachingDocs =
        new[]
        {
            "AGENTS.md",
            "CLAUDE.md",
            "README.md",
            "docs/EXAMPLES.md",
            "docs/PLUGIN_DEVELOPMENT.md",
            "docs/PLUGIN_SYSTEM.md",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The documents that NAME the seam without teaching it. The reason is
    ///     recorded per entry because the classification is a judgement, and a
    ///     judgement nobody wrote down is a judgement the next reader reopens.
    /// </summary>
    internal static readonly FrozenSet<string> DescribedNotTaught =
        new[]
        {
            // Layer diagram and the contract inventory. A closure sentence in a
            // box-drawing diagram would be noise; rule 3 keeps the fact honest.
            "docs/ARCHITECTURE.md",
            // The layer-permitted-dependencies table, one row.
            "docs/ARCHITECTURE_LAYERS.md",
            // One §ARCH row about which project a plugin assembly may reference.
            "docs/CODE_PRINCIPLES_AUDIT.md",
            // Already reports the seam as dead — a DIM example in §6.
            "docs/PATTERNS.md",
            // A delivered-checklist row for the contract, not an instruction.
            "docs/ROADMAP.md",
            // A historical note about a freeze test that once overrode a member.
            "docs/ROP-API-INVENTORY.md",
            // Generated coverage table listing documented public members.
            "docs/XML_DOC_AUDIT.md",
            // Component file table for the load host.
            "src/Harbor.Plugins.Host/README.md",
            // Component file table for the assembly declaring ITuiPlugin. The
            // interface's OWN doc carries the closure instead (rule 5), which is
            // where an IntelliSense reader meets it.
            "src/Harbor.Terminal.Abstractions/README.md",
            // REMOVED in #794: `samples/plugins/Harbor.Plugin.TodoWrite/README.md`.
            // It used to sit here as a "one-line sample blurb that also names
            // the seam", and that blurb was a fabrication — commit 487a68a0
            // added the README claiming the sample implements `ITuiPlugin` and
            // ships a `TodoPanelPlugin`, in the same commit, and the sample has
            // only ever declared `TodoWritePlugin : IToolPlugin` and
            // `TodoWriteTool : ITool`. #794 corrected the README, which removed
            // its only seam mention, so the entry is stale by the settlement
            // rule rather than by omission. Recorded here because the issue
            // that found it said this edit belonged in its own change, where the
            // perimeter is the visible decision instead of a side effect.
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     The interface whose IntelliSense a plugin author reads first. It
    ///     carries the closure because it is where the promise is made in the
    ///     strongest form — an itemised capability list, with a code sample
    ///     registering at a placement the base renderer never paints.
    /// </summary>
    internal const string SeamInterface = "src/Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs";

    /// <summary>The CellForge-side registry that receives the panel axis.</summary>
    private const string PanelRegistryType = "CellForgePanelRegistry";

    private static readonly Lazy<IReadOnlyList<string>> MarkdownFiles = new(
        () => CellForgeSeamProbe.EnumerateMarkdown(RepoPaths.RepoRoot));

    private static readonly Lazy<IReadOnlyList<string>> NamingDocuments = new(
        () => CellForgeSeamProbe.FindNamingDocuments(RepoPaths.RepoRoot));

    /// <summary>
    ///     A guard that cannot find the checkout would report every document as
    ///     silent, and silence is a pass. This throws instead, so a bad
    ///     perimeter is a red run rather than a green one.
    /// </summary>
    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        ArgumentNullException.ThrowIfNull(root);
        return root;
    }

    /// <summary>
    ///     THE WIDGET AXIS STAYS CLOSED. Every construction of the shipped
    ///     layout tree is inside the declared set, and the declared set is
    ///     itself real.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the mechanical form of the answer to #564's question. The
    ///         widget set is closed at compile time not because a rule forbids a
    ///         new <c>Panel</c> subclass — nothing forbids that, and in-tree
    ///         widgets are an ordinary, healthy thing to add — but because the
    ///         one tree the renderer paints is built by a static factory whose
    ///         parameters are all in-tree. A plugin that subclasses <c>Panel</c>
    ///         has a widget nobody will ever lay out.
    ///     </para>
    ///     <para>
    ///         So the rule is not "no new widget": it is "no second home for the
    ///         tree". A widget added in-tree costs the structural splice #564
    ///         measured, and that is the correct price. What this rule forbids is
    ///         the shape that would make the closure a lie — a tree built
    ///         somewhere a plugin could reach, or one built from data the host
    ///         hands out.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task WidgetAxis_TheShippedLayoutTreeHasNoSecondHome()
    {
        IReadOnlyList<string> hits = CellForgeSeamProbe.FindProductHits(CellForgeSeamProbe.LayoutTreeConstructionRegex());
        var outside = new SortedSet<string>(StringComparer.Ordinal);
        var builders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string hit in hits)
        {
            string file = hit[..hit.LastIndexOf(':')];
            builders.Add(file);
            if (!AllowedTreeBuilders.Contains(file))
            {
                outside.Add(hit);
            }
        }

        string[] stale = [.. AllowedTreeBuilders.Where(b => !builders.Contains(b))];

        await Assert.That(string.Join(" | ", outside)).IsEmpty()
            .Because(
                "the CellForge widget set is an internal contract, not a declared extension axis, and "
                + "the reason is structural: the renderer paints one LayoutTree, built by the static "
                + "factory ChatScreen.Build(...) whose parameters are all in-tree, and no plugin is ever "
                + "handed a tree, a ChatScreen or a Panel. That is the correct design — the defect #564 "
                + "found is the documents, not the closure. A `new LayoutTree(` in product code OUTSIDE "
                + "ChatScreenLayout.cs means a second home for the chrome, which is either a widget axis "
                + "opening without a decision or a build that paints nothing. Allowed builders: "
                + string.Join(", ", AllowedTreeBuilders) + ". Offenders: "
                + (outside.Count == 0 ? "(none)" : string.Join(", ", outside)));

        await Assert.That(string.Join(" | ", stale)).IsEmpty()
            .Because(
                "AllowedTreeBuilders is the recorded excuse for a construction site, and an excuse with "
                + "nothing behind it is how a freeze quietly becomes a suggestion — the same rule "
                + "ExtensionAxisFreezeRule applies to its own sealed axis list. If the layout tree moved "
                + "or the factory was split, update the entry in the same commit that moved it. Stale: "
                + (stale.Length == 0 ? "(none)" : string.Join(", ", stale)));
    }

    /// <summary>
    ///     The panel axis is the plugin-reachable CellForge surface: its door is
    ///     invoked on a receiver, and CellForge's receiving registry exists.
    /// </summary>
    /// <remarks>
    ///     Rules 1 and 3 together say "the widget axis is closed" and "the view
    ///     seam renders nothing". Without this rule those two statements are
    ///     indistinguishable from "nothing about CellForge is extensible", which
    ///     is a different finding and a much worse one: it would be true of the
    ///     product and false of the documentation's own advice to implement
    ///     <c>ITuiPanelPlugin</c>. The axis is real — <c>ITuiPanelPlugin</c> →
    ///     <c>RegisterPanelProvider</c> → <c>PanelRegistryPluginAdapter</c> →
    ///     <c>CellForgePanelRegistry</c> → the dock — and it paints rows of text.
    ///     Narrow, but reachable, which is the whole difference.
    /// </remarks>
    [Test]
    public async Task PanelAxis_IsThePluginReachableCellForgeSurface()
    {
        IReadOnlyList<string> doorHits = CellForgeSeamProbe.FindProductHits(CellForgeSeamProbe.PanelDoorRegex());

        await Assert.That(string.Join(" | ", doorHits)).IsNotEmpty()
            .Because(
                "the redirect this issue's documentation fix points at has to be a real door, or the fix "
                + "is a dead end pointed in a different direction. IPluginLoadHost.RegisterPanelProvider "
                + "is declared and implemented (McpPluginLoadHost, PluginLoadHostAdapter), and "
                + "PanelRegistryPluginAdapter invokes it on the host — that is the ITuiPanelPlugin axis, "
                + "the one seam that reaches CellForge. If this is empty, the panel axis closed and the "
                + "documents have been redirected to a door that is not there. Matched: "
                + (doorHits.Count == 0 ? "(none)" : string.Join(", ", doorHits)));

        await Assert.That(CellForgeSeamProbe.ProductDeclaresType(PanelRegistryType)).IsTrue()
            .Because(
                "a door the plugin invokes is only half an axis; the other half is something on the "
                + "CellForge side that RECEIVES it. CellForgePanelRegistry is that end — it is seeded, "
                + "attached to the screen dock and painted. Without it the panel providers are collected "
                + "into a dictionary no frame reads, which is precisely the shape #620 found and the "
                + "shape this file is meant not to reintroduce on the axis being redirected to.");
    }

    /// <summary>
    ///     The declared status of the view seam matches what the product
    ///     actually renders — in BOTH directions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Closed</c> requires that nothing reads the collected plugins and
    ///         nothing calls the collected method: no <c>.TuiPlugins</c> and no
    ///         <c>.RegisterTui(</c> anywhere in product code. <c>Open</c> requires
    ///         both. Neither reading can be satisfied by a declaration, because
    ///         both probes are anchored on a receiver.
    ///     </para>
    ///     <para>
    ///         This is the rule that makes the closure a decision rather than a
    ///         freeze. If a future change wires a renderer to read
    ///         <c>IPluginLoadHost.TuiPlugins</c> and calls <c>RegisterTui</c>,
    ///         the seam is genuinely open and this test goes RED and says so —
    ///         at which point <see cref="ViewSeamStatus" /> is flipped to
    ///         <c>Open</c> here, and rule 4 in turn goes red until the documents
    ///         stop calling it closed. The documents therefore cannot be wrong in
    ///         either direction without a deliberate edit to this file, which is
    ///         the difference between a guard and a paragraph.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task ViewSeam_DeclaredStatusMatchesWhatTheProductRenders()
    {
        IReadOnlyList<string> readers = CellForgeSeamProbe.FindProductHits(CellForgeSeamProbe.SeamReaderRegex());
        IReadOnlyList<string> calls = CellForgeSeamProbe.FindProductHits(CellForgeSeamProbe.SeamCallRegex());
        int consumed = readers.Count + calls.Count;

        bool expectOpen = string.Equals(ViewSeamStatus, "Open", StringComparison.Ordinal);

        await Assert.That(consumed > 0).IsEqualTo(expectOpen)
            .Because(
                "ITuiPlugin is the axis this file is about, and its status is written down exactly once, "
                + "as ViewSeamStatus. It has to agree with the product in both directions, or the status "
                + "is the thing that is wrong rather than the code. A reader of "
                + "IPluginLoadHost.TuiPlugins, or a call of ITuiPlugin.RegisterTui on a receiver, is what "
                + "makes the seam real: PluginRegistrar dispatches the marker and invokes "
                + "host.RegisterTuiPlugin(...), which is the whole reason this seam PASSES "
                + "ExtensionAxisFreezeRule — a marker and a door are present and connected, and the "
                + "renderer that would consume them is not. Declared '" + ViewSeamStatus + "', found "
                + consumed + " consumer(s)"
                + (consumed == 0 ? " (the seam is collected and never rendered)." : ".")
                + " Readers: " + (readers.Count == 0 ? "(none)" : string.Join(", ", readers))
                + " Calls: " + (calls.Count == 0 ? "(none)" : string.Join(", ", calls)));
    }

    /// <summary>
    ///     Every document that teaches the view seam states, on a line that
    ///     names it, that the seam is closed and which axis to use instead.
    /// </summary>
    /// <remarks>
    ///     <b>RED on the tree this lands on.</b> Six documents teach
    ///     <c>ITuiPlugin</c> — <c>AGENTS.md</c>'s "Add a TUI plugin", a TUI view
    ///     and a TUI view model recipe, <c>CLAUDE.md</c>'s plugin-flow diagram
    ///     and its add-a-view bullets, <c>README.md</c>'s contract inventory,
    ///     <c>docs/EXAMPLES.md</c> §25's panel recipe, and the two plugin
    ///     references — and not one of them says that nothing ever calls
    ///     <c>RegisterTui</c>. A plugin author following any of them writes a
    ///     view, registers it, sees no exception and no log line, and ships
    ///     something that is never painted. That is the whole of #564, and a
    ///     document is the only place it is actually decided.
    /// </remarks>
    [Test]
    public async Task TeachingDocs_StateThatTheViewSeamIsClosed()
    {
        string root = RequireRepoRoot();
        var silent = new List<string>();

        foreach (string relative in TeachingDocs.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (CellForgeSeamProbe.FindClosureStatements(root, relative).Count == 0)
            {
                silent.Add(relative);
            }
        }

        await Assert.That(string.Join(" | ", silent)).IsEmpty()
            .Because(
                "a documented contract that no shipped renderer honours is worse than no contract, "
                + "because the next reader trusts it. ITuiPlugin is dispatched and its host door is "
                + "invoked, so it passes #620's axis freeze, and nothing consumes the result: "
                + "IPluginLoadHost.TuiPlugins has no reader and ITuiPlugin.RegisterTui has no call site, "
                + "so a plugin view is never painted by any renderer — and in the canonical CellForge "
                + "screen it could not be, because that screen is drawn by the cell-diff layout tree "
                + "rather than by the base renderer's four placements. Each document that teaches this "
                + "seam must say so, on a line naming it: "
                + CellForgeSeamProbe.ClosureToken + " — add a plugin panel through ITuiPanelPlugin / "
                + "IPanelRegistry instead. Silent: "
                + (silent.Count == 0 ? "(none)" : string.Join(", ", silent)));
    }

    /// <summary>
    ///     <c>ITuiPlugin</c>'s own XML doc states the closure. This is the
    ///     promise in its strongest form, and it is the first thing a plugin
    ///     author meets.
    /// </summary>
    /// <remarks>
    ///     <b>RED on the tree this lands on.</b> The interface doc itemises three
    ///     capabilities — "Register a new view — append a custom panel to ANY
    ///     <c>TuiViewPlacement</c> (status bar, chat history, sidebar, overlay,
    ///     …)", override-a-builtin, register-a-view-model — and its sample
    ///     registers at <c>SidebarRight</c>, which
    ///     <c>BaseTuiRenderer.ShouldRenderPlacement</c> closes with
    ///     <c>_ =&gt; false</c>. So even #564's recommended fix, calling
    ///     <c>RegisterTui</c> from the renderer, would leave the interface's own
    ///     example on the dead arm. The XML doc is not a description of the
    ///     type; it is the contract, and it has to carry the closure too.
    /// </remarks>
    [Test]
    public async Task SeamInterface_DocumentsThatItIsClosed()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> statements = CellForgeSeamProbe.FindClosureStatements(root, SeamInterface);

        await Assert.That(string.Join(" | ", statements)).IsNotEmpty()
            .Because(
                "ITuiPlugin's XML doc is the strongest and most-read form of the promise: it itemises "
                + "the capabilities and ships a code sample, and IntelliSense shows it to every plugin "
                + "author before any markdown does. Its sample registers at TuiViewPlacement.SidebarRight, "
                + "one of the three placements ShouldRenderPlacement answers with `_ => false`, so the "
                + "example is not merely unreached but unreachABLE through the documented route. Either "
                + "the seam is closed — in which case the doc says so, carrying the token "
                + CellForgeSeamProbe.ClosureToken + " on a line naming it — or it is open, in which case "
                + "ViewSeamStatus in this file is wrong and rule 3 is the test that will say so. File: "
                + SeamInterface + ". Found " + statements.Count + " statement(s).");
    }

    /// <summary>
    ///     The declared perimeter settles: every markdown file in the bounded
    ///     walk that names the seam is classified, as teaching or as described.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is what makes rules 4 and 5 safe. A guard whose coverage is a
    ///         hand-written list fails open: the next person adds a document that
    ///         teaches the seam, does not add it to the list, and the guard stays
    ///         green over a lie. So the list is compared against the files the
    ///         walk actually found, in both directions — an unclassified file
    ///         fails, and so does a classified entry that no longer names the
    ///         seam.
    ///     </para>
    ///     <para>
    ///         The walk is bounded on purpose: this repository carries the same
    ///         documents a second and third time under <c>.worktrees/</c>,
    ///         <c>graft/</c>, <c>.serena/</c> and <c>.ai-factory/</c>, none of
    ///         which is the product, and an unbounded walk would report ~30×
    ///         the real set. <c>docs/</c> is walked non-recursively, so
    ///         <c>specs/</c>, <c>adr/</c> and <c>.kilo-docs/</c> fall outside by
    ///         geometry rather than by an exclusion list that could later be
    ///         widened to hide a live file.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task PerimeterSettlesAgainstTheFilesThatNameTheSeam()
    {
        RequireRepoRoot();
        IReadOnlyList<string> naming = NamingDocuments.Value;
        var declared = new SortedSet<string>(TeachingDocs, StringComparer.Ordinal);
        declared.UnionWith(DescribedNotTaught);

        var unclassified = new SortedSet<string>(naming, StringComparer.Ordinal);
        unclassified.ExceptWith(declared);

        var stale = new SortedSet<string>(declared, StringComparer.Ordinal);
        stale.ExceptWith(naming);

        await Assert.That(string.Join(" | ", unclassified)).IsEmpty()
            .Because(
                "every document that names the view seam has to be classified: one that teaches it must "
                + "state the closure (TeachingDocs), one that only describes it must say why it does not "
                + "(DescribedNotTaught). A new document that mentions ITuiPlugin, ITuiView, ViewRegistry "
                + "or TuiViewBase and appears in neither list is the exact failure this rule exists to "
                + "catch — the guard would be green over a document that teaches a dead seam. "
                + "Walked " + MarkdownFiles.Value.Count + " markdown file(s). Unclassified: "
                + (unclassified.Count == 0 ? "(none)" : string.Join(", ", unclassified)));

        await Assert.That(string.Join(" | ", stale)).IsEmpty()
            .Because(
                "a declared entry that no longer names the seam is a stale classification, and a stale "
                + "classification is how a perimeter stops describing the repository: the file was "
                + "renamed, deleted, or rewritten to stop mentioning the seam, and the entry survives to "
                + "excuse a file nobody is reading. Delete the entry in the same commit. Stale: "
                + (stale.Count == 0 ? "(none)" : string.Join(", ", stale)));
    }

    /// <summary>
    ///     The walk really ran, the seam matcher still discriminates, and neither
    ///     classification has collapsed.
    /// </summary>
    /// <remarks>
    ///     Four mechanisms, all of which go RED rather than passing quietly:
    ///     the walk found more than a token number of markdown files and more
    ///     than a token number of them name the seam; the REAL
    ///     <see cref="CellForgeSeamProbe.NamesSeam" /> predicate fires on a
    ///     planted naming line and stays silent on a planted non-naming one; the
    ///     REAL <see cref="CellForgeSeamProbe.StatesTheClosure" /> predicate
    ///     fires only when the line carries both halves, so a document cannot
    ///     pass by mentioning closure in an unrelated section; and both
    ///     classification sets are non-empty, so the settlement rule above cannot
    ///     be satisfied by classifying everything as teaching or everything as
    ///     described.
    /// </remarks>
    [Test]
    public async Task Perimeter_IsLive_AndTheSeamMatcherStillDiscriminates()
    {
        await Assert.That(MarkdownFiles.Value.Count).IsGreaterThan(50)
            .Because(
                "the bounded walk is the input to every documentation rule in this file, so a broken path, "
                + "a renamed root or a filter that matches nothing would leave the derived set empty — and "
                + "an empty set is what a guard looks like when it is enforcing nothing. The repository "
                + "root and docs/ are walked non-recursively. Found: " + MarkdownFiles.Value.Count);

        await Assert.That(NamingDocuments.Value.Count).IsGreaterThan(TeachingDocs.Count)
            .Because(
                "if the walk found no more naming documents than there are teaching documents, the "
                + "settlement rule would hold without the described set meaning anything, and the "
                + "classification would have been guessed rather than measured. Found "
                + NamingDocuments.Value.Count + " naming document(s) against "
                + TeachingDocs.Count + " teaching and " + DescribedNotTaught.Count + " described.");

        string[] naming =
        [
            "public interface ITuiPlugin : IPlugin { }",
            "- Add a view — implement `ITuiView` and register it via `ViewRegistry`.",
            "| Views/ | `ITuiView`, `TuiViewBase<TVm>`, `TuiViewPlacement` |",
        ];
        string[] notNaming =
        [
            "- Add a tool — implement `IToolPlugin` and declare a `ToolSafetyProfile`.",
            "| Panels | `ITuiPanelPlugin` registers through `IPanelRegistry` |",
            "The theme axis is data: no C# names a theme.",
        ];

        await Assert.That(string.Join(" | ", naming.Where(static l => !CellForgeSeamProbe.NamesSeam(l)))).IsEmpty()
            .Because(
                "the seam matcher is a plain alternation over four names. If any of those four stopped "
                + "matching, every document rule in this file would pass vacuously while reading as though "
                + "it enforced something. Each planted line names the seam by one of the four.");

        await Assert.That(string.Join(" | ", notNaming.Where(CellForgeSeamProbe.NamesSeam))).IsEmpty()
            .Because(
                "the same matcher in the other direction: a document that teaches the panel axis, which "
                + "IS reachable, must not be swept into the view seam's perimeter by a matcher that is "
                + "looser than its name suggests. Planted lines name the panel axis and a tool.");

        string[] onlyClosure = ["This contract is closed. Nothing calls it."];
        string[] onlyName = ["- `ITuiPlugin` — adds TUI views + view models"];

        await Assert.That(CellForgeSeamProbe.StatesTheClosure(onlyClosure[0])).IsFalse()
            .Because(
                "the closure statement has to be ON THE SAME LINE as the marker. A document that states "
                + "the closure in a preamble and then teaches the seam in a table two hundred lines later "
                + "has not fixed anything, and a rule that accepted it would report the teaching as "
                + "corrected.");

        await Assert.That(CellForgeSeamProbe.StatesTheClosure(onlyName[0])).IsFalse()
            .Because(
                "the other half of the adjacency, and the state every teaching document is in today: the "
                + "line names the seam and says nothing about it being closed. That is the RED this guard "
                + "was written to produce.");
    }
}
