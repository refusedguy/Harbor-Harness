// CellForgeMascotMoveOutRules.cs — the guard for issue #1619.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #1619 named the mascot as the "trial balloon" for the move-out
// mechanics: a decoration cluster (MascotDirector + MascotPanel +
// AmbientMascot, ~574 lines) with exactly TWO inbound references
// (ChatScreenLayout and ChatScreenBridge) and no role in the frame loop's
// skeleton. The move itself is a directory + namespace extraction whose
// bodies are byte-equal — so nothing about the COMPILATION proves it
// happened. A move that leaves no trace is a move the next person undoes
// without knowing it happened, and the copy that comes back is a silent fork:
// two MascotPanel types under one name, one of them painted.
//
// So this file pins the three things that ARE checkable facts about the
// extraction:
//
//   1. ONE HOME — each mascot file lives in Chat/Mascot/ and declares
//      `namespace Harbor.Tui.CellForge.Mascot;`; the Chat/Widgets/ copy is
//      gone, and no other file under the chat assembly declares those types.
//   2. TWO CONSUMERS — the inbound-reference claim the issue rests on is
//      MEASURED, not asserted: outside the mascot area, the product code
//      naming a mascot type is exactly the declared set. A third consumer is
//      a decision (declare it in the table in the same commit), not an
//      accident — and the declared set is what makes the next move-out
//      possible at all.
//   3. NON-VACUITY — the area really holds the files, the scan really finds
//      the two consumers, and each mascot type is declared exactly once.
//      Without this, a broken glob or a renamed directory would leave every
//      rule above green while enforcing nothing.
//
// WHY THE MASCOT DID NOT GO THE WAY #1620/#1627 WENT
// --------------------------------------------------
// #1620 (batch A) and #1627 (batch B) moved leaves INTO
// Harbor.Tui.CellForge.Engine, and the mascot was on both plans — MascotMode
// went in batch A, the rest of the cluster was still queued. The engine
// cannot host the rest: MascotPanel paints the UIR painter grid (the global
// Cell/ScreenBuffer aliases → Harbor.Ui.Framework.Rendering) and reads
// ChatPalette (Harbor.DesignSystem); MascotDirector binds StatusViewModel,
// StatusPanel, StatusBarMode, AgentPhase and PanelFx. All of it is UIR
// vocabulary the engine's zero-Harbor-import rule forbids — exactly the
// painting widgets batch B declined (GaugeBar, CalendarView, RuleView,
// PanelChrome, BreakdownStrip, DiffViewerOverlay), for exactly the same
// reason. Batch B's verdict stands, so #1619 moves the cluster into its OWN
// area inside the chat assembly: the same boundary the engine lane
// established, one layer down.
//
// WHAT THE MOVE DID NOT TOUCH
// ---------------------------
// MascotMode/MascotModeEnv stay in the engine (#1620 — a merged, reviewed
// decision, and the batch-A guard row names that file). MascotReaction stays
// in Harbor.Ui.Framework.Rendering (it is the UIR's own vocabulary, next to
// AgentPhase). Neither is part of this cluster, and moving them back would
// undo a merged batch.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Pins the #1619 mascot move-out: the decoration cluster lives in its own
///     area, no copy shadows it, and its inbound references stay the declared
///     set.
/// </summary>
public sealed class CellForgeMascotMoveOutRules
{
    /// <summary>The chat assembly the mascot cluster lives in.</summary>
    private const string ChatProject = "src/Harbor.Tui.CellForge";

    /// <summary>The mascot's own area, relative to the repository root.</summary>
    private const string MascotArea = "src/Harbor.Tui.CellForge/Chat/Mascot";

    /// <summary>The namespace the cluster declares after the move-out.</summary>
    private const string MascotNamespace = "namespace Harbor.Tui.CellForge.Mascot;";

    /// <summary>
    ///     The moved cluster: the file, the type declarations it carries, and
    ///     where the file lived before #1619.
    /// </summary>
    private static readonly (string FileName, string[] Declarations, string OldPath)[] MovedCluster =
    [
        ("MascotDirector.cs", ["class MascotDirector"], "src/Harbor.Tui.CellForge/Chat/Widgets/MascotDirector.cs"),
        ("MascotPanel.cs", ["class MascotPanel"], "src/Harbor.Tui.CellForge/Chat/Widgets/MascotPanel.cs"),
        ("AmbientMascot.cs", ["class AmbientMascot", "enum MascotMood"], "src/Harbor.Tui.CellForge/Chat/Widgets/AmbientMascot.cs"),
    ];

    /// <summary>
    ///     The types the cluster owns. A mention of one of these outside the
    ///     mascot area is an inbound reference, and that is what the consumer
    ///     rule below grades.
    /// </summary>
    private static readonly string[] MascotTypeNames =
    [
        "AmbientMascot",
        "MascotDirector",
        "MascotPanel",
        "MascotMood",
    ];

    /// <summary>
    ///     The product code allowed to name a mascot type. #1619 measured two
    ///     inbound references; a third one is a decision to be declared in this
    ///     table in the same commit, not a silent drift.
    /// </summary>
    private static readonly string[] DeclaredProductConsumers =
    [
        "src/Harbor.Tui.CellForge/Chat/Widgets/ChatScreenLayout.cs",
        "src/Harbor.Tui.CellForge/Chat/Streaming/ChatScreenBridge.cs",
    ];

    /// <summary>Matches a mention of one of the mascot type names as a whole word.</summary>
    private static readonly Regex MascotTypeMention = new(
        @"\b(" + string.Join("|", MascotTypeNames) + @")\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Rule 1: one home, no shadow ─────────────────────────────────────────

    /// <summary>
    ///     The mascot cluster lives in its own area and no chat-widget copy is
    ///     back beside it.
    /// </summary>
    /// <remarks>
    ///     The reverse half is the load-bearing one, and it is the same hazard
    ///     batch A guards for the engine leaves: a copy in <c>Chat/Widgets/</c>
    ///     under the OLD namespace would win resolution for chat callers while
    ///     the extracted area kept its own copy — two <c>MascotPanel</c> types
    ///     under one name, and the one nobody remembers is the one that paints.
    /// </remarks>
    [Test]
    public async Task TheMascotClusterLivesInItsOwnAreaAndNoChatWidgetCopyIsBack()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var drift = new List<string>();

        foreach ((string fileName, string[] declarations, string oldPath) in MovedCluster)
        {
            string newPath = Path.Combine(root, MascotArea.Replace('/', Path.DirectorySeparatorChar), fileName);
            if (!File.Exists(newPath))
            {
                drift.Add($"{MascotArea}/{fileName} — the mascot's new home is gone; "
                          + "either the cluster moved again (update the table in the same commit) or the move-out was reverted");
                continue;
            }

            string text = File.ReadAllText(newPath);
            if (!text.Contains(MascotNamespace, StringComparison.Ordinal))
            {
                drift.Add($"{MascotArea}/{fileName} — does not declare `{MascotNamespace}`; "
                          + "the area and the namespace must agree, or the extraction is half-done");
            }

            foreach (string declaration in declarations)
            {
                if (!text.Contains(declaration, StringComparison.Ordinal))
                {
                    drift.Add($"{MascotArea}/{fileName} — no longer declares `{declaration}`; "
                              + "the cluster's shape changed (update the table in the same commit)");
                }
            }

            string old = Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(old))
            {
                drift.Add($"{oldPath} — the pre-move-out copy is back; it would shadow the extracted "
                          + "one for chat callers and fork the cluster under one name");
            }
        }

        // No OTHER file under the chat assembly may declare a mascot type: the
        // extraction is one home per type, not a move that left a second copy
        // under a name of its own.
        foreach (string file in EnumerateCsFiles(ChatProject))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(MascotArea + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach ((string fileName, string[] declarations, _) in MovedCluster)
            {
                foreach (string declaration in declarations)
                {
                    if (text.Contains(declaration, StringComparison.Ordinal))
                    {
                        drift.Add($"{relative} — declares `{declaration}`, which the mascot area owns; "
                                  + "one home per type (the moved file is "
                                  + $"{fileName})");
                    }
                }
            }
        }

        await Assert.That(drift).IsEmpty()
            .Because(
                "#1619 moved the mascot cluster (MascotDirector, MascotPanel, AmbientMascot) out of "
                + "Chat/Widgets/ into its own area, Chat/Mascot/, under the namespace "
                + "Harbor.Tui.CellForge.Mascot. The bodies are byte-equal, so only a rule like this one "
                + "makes the extraction a fact: one home per type, and no copy left behind to shadow it. "
                + "Drift: " + string.Join("; ", drift));
    }

    // ── Rule 2: the inbound-reference set ───────────────────────────────────

    /// <summary>
    ///     The mascot's product consumers are exactly the declared set.
    /// </summary>
    /// <remarks>
    ///     The issue's whole premise — "входящих ссылок всего 2" — is what
    ///     makes the mascot a good move-out candidate, and a premise that is
    ///     only written down is a premise that decays. Comments are stripped
    ///     before the scan, so the prose that mentions the mascot (the code
    ///     generator's doc comment, for one) is not mistaken for a reference.
    /// </remarks>
    [Test]
    public async Task TheMascotClusterHasOnlyItsDeclaredProductConsumers()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var consumers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string tree in new[] { "src", "apps" })
        {
            foreach (string file in EnumerateCsFiles(tree))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith(MascotArea + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);
                string code = string.Join('\n', SourceCommentStripper.StripAll(lines));
                if (MascotTypeMention.IsMatch(code))
                {
                    consumers.Add(relative);
                }
            }
        }

        var unexpected = consumers.Except(DeclaredProductConsumers, StringComparer.Ordinal).ToList();
        var missing = DeclaredProductConsumers.Except(consumers, StringComparer.Ordinal).ToList();

        var drift = new List<string>();
        drift.AddRange(unexpected.Select(c => $"{c} — names a mascot type but is not a declared consumer; "
                                             + "declare it in DeclaredProductConsumers in the same commit"));
        drift.AddRange(missing.Select(c => $"{c} — a declared consumer no longer names a mascot type; "
                                            + "the row is stale and the extraction has moved on"));

        await Assert.That(drift).IsEmpty()
            .Because(
                "#1619 measured the mascot's inbound references as exactly two — ChatScreenLayout (the footer "
                + "cat) and ChatScreenBridge (the panel cat) — and that is what makes the cluster a "
                + "move-out candidate at all. A third inbound reference is a decision to be written down in "
                + "the same commit, not a drift nobody declared. Drift: " + string.Join("; ", drift));
    }

    // ── Non-vacuity ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The move-out rules above are not vacuous: the area holds the files,
    ///     the consumer scan reaches the product code, and each mascot type is
    ///     declared exactly once in the repository.
    /// </summary>
    [Test]
    public async Task TheMascotMoveOutIsNotVacuous()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        int areaFiles = EnumerateCsFiles(MascotArea).Count();
        await Assert.That(areaFiles).IsGreaterThanOrEqualTo(MovedCluster.Length)
            .Because(
                "The mascot area must actually hold the moved files. An empty or missing area would make "
                + "the one-home rule green for the wrong reason — the same failure mode this file exists "
                + "to end.");

        int declaredTypes = 0;
        foreach (string file in EnumerateCsFiles(ChatProject))
        {
            string text = File.ReadAllText(file);
            foreach (var cluster in MovedCluster)
            {
                declaredTypes += cluster.Declarations.Count(d => text.Contains(d, StringComparison.Ordinal));
            }
        }

        await Assert.That(declaredTypes).IsEqualTo(MovedCluster.Sum(m => m.Declarations.Length))
            .Because(
                "Each mascot type must be declared exactly once in the chat assembly. A second declaration "
                + "is a fork the one-home rule cannot see if it lives in a file the rule does not read.");

        var consumers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string tree in new[] { "src", "apps" })
        {
            foreach (string file in EnumerateCsFiles(tree))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith(MascotArea + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                string code = string.Join('\n', SourceCommentStripper.StripAll(File.ReadAllLines(file)));
                if (MascotTypeMention.IsMatch(code))
                {
                    consumers.Add(relative);
                }
            }
        }

        await Assert.That(consumers.Count).IsGreaterThanOrEqualTo(2)
            .Because(
                "The consumer scan must reach the product code that names the mascot. Finding fewer than "
                + "the two declared consumers means the scan stopped matching (a renamed type, a stripped "
                + "word boundary) and the consumer rule above would be enforcing nothing.");
    }

    // ── Discovery ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Every <c>*.cs</c> file under a repository-relative directory, build
    ///     output excluded.
    /// </summary>
    private static IReadOnlyList<string> EnumerateCsFiles(string repoRelativeDir)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, repoRelativeDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }
}
