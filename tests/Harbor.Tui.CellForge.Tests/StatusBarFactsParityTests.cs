using System.Globalization;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #488 drift guard. Two surfaces paint the same session numbers — the
///     <c>UiStatusBarModel</c> projection (Avalonia / Termina / SpectreTui) and
///     this CellForge footer row — and they used to disagree: the footer called
///     the projector, threw two of its cells away, and rebuilt tokens and cost
///     from raw state under a second formatting rule. One renderer could show
///     <c>$0.0000</c> where the other showed nothing (#457 shipped exactly that).
/// </summary>
/// <remarks>
///     Two complementary locks, because either alone is defeatable:
///     <list type="number">
///         <item>behaviour — for a matrix of states, every cell the footer paints is byte-identical to the
///         corresponding projected segment (or, for the optional cells, absent from both);</item>
///         <item>shape — the layout no longer calls <c>ProjectStatusBar</c> and no longer formats a cost or
///         a token count itself, and <c>"F4"</c> occurs exactly once across the whole status path.</item>
///     </list>
///     Together: re-adding a private formatting rule to the layout red-lights
///     both tests, so the divergence cannot come back unnoticed.
/// </remarks>
public class StatusBarFactsParityTests
{
    /// <summary>Sentinel so a missing cell reads as a value in a failure message.</summary>
    private const string NoCell = "<no cell>";

    private static UiState State(
        string status = "idle",
        string provider = "prov",
        string model = "m",
        string agent = "code",
        long tokensIn = 0,
        long tokensOut = 0,
        decimal costUsd = 0m,
        int scrollOffset = 0,
        int viewportLines = 0,
        int totalLines = 0) => new()
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = scrollOffset,
                ViewportLines = viewportLines,
                TotalLines = totalLines
            },
            Chat = ChatDomainState.Empty with
            {
                Status = status,
                Provider = provider,
                Model = model,
                AgentName = agent,
                Cost = new CostSnapshot(tokensIn, tokensOut, costUsd)
            }
        };

    /// <summary>The footer row, as one string per cell.</summary>
    private static string[] Footer(UiState state)
    {
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        int n = StatusProjectorPanel.BuildSegments(state, ws);
        var cells = new string[n];
        for (int i = 0; i < n; i++)
        {
            cells[i] = ws[i].Text;
        }

        return cells;
    }

    /// <summary>The projected status bar, as one string per segment.</summary>
    private static string[] Projected(UiState state)
    {
        var segments = StatusProjector.ProjectStatusBar(state).Segments;
        var cells = new string[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            cells[i] = segments[i].Text;
        }

        return cells;
    }

    /// <summary>The cell carrying a "$" — the cost — or null when neither surface emits one.</summary>
    private static string? CostCellOf(IEnumerable<string> cells) =>
        cells.FirstOrDefault(c => c.Contains('$', StringComparison.Ordinal));

    /// <summary>The cell carrying a token arrow — the token count — or null when neither surface emits one.</summary>
    private static string? TokenCellOf(IEnumerable<string> cells) =>
        cells.FirstOrDefault(c => c.Contains('↑', StringComparison.Ordinal));

    [Test]
    [Arguments("0")]
    [Arguments("0.00004")]
    [Arguments("0.0123")]
    [Arguments("12.5")]
    [Arguments("-1")]
    public async Task Cost_IsTheSameCellOnBothSurfaces(string costText)
    {
        // Costs travel as text: `decimal` is not a legal attribute-argument type
        // (CS0182), and parsing keeps the values exact rather than round-tripping
        // them through a double.
        var state = State("running", tokensIn: 1500, tokensOut: 300, costUsd: decimal.Parse(costText, CultureInfo.InvariantCulture));

        await Assert.That(CostCellOf(Footer(state)) ?? NoCell)
            .IsEqualTo(CostCellOf(Projected(state)) ?? NoCell);
    }

    [Test]
    [Arguments(0L, 0L)]
    [Arguments(123L, 456L)]
    [Arguments(1500L, 300L)]
    [Arguments(1_400_000L, 2L)]
    [Arguments(-5L, 0L)]
    public async Task Tokens_AreTheSameCellOnBothSurfaces(long tokensIn, long tokensOut)
    {
        var state = State("running", tokensIn: tokensIn, tokensOut: tokensOut, costUsd: 0.0042m);

        await Assert.That(TokenCellOf(Footer(state)) ?? NoCell)
            .IsEqualTo(TokenCellOf(Projected(state)) ?? NoCell);
    }

    [Test]
    public async Task ZeroCost_HidesTheCellOnBothSurfaces()
    {
        var state = State("running", tokensIn: 2000, tokensOut: 1000, costUsd: 0m);

        // The #457 contract, now shared: a session that has spent nothing paints
        // no money cell at all — not "$0.0000" on one host and nothing on the other.
        await Assert.That(CostCellOf(Footer(state))).IsNull();
        await Assert.That(CostCellOf(Projected(state))).IsNull();
    }

    [Test]
    public async Task SpentCost_ShowsTheSameDollarCellOnBothSurfaces()
    {
        var state = State("running", tokensIn: 1500, tokensOut: 300, costUsd: 0.0042m);

        await Assert.That(CostCellOf(Footer(state))).IsEqualTo("$0.0042");
        await Assert.That(CostCellOf(Projected(state))).IsEqualTo("$0.0042");
    }

    [Test]
    public async Task SpentTokens_ShowTheSameCompactCellOnBothSurfaces()
    {
        var state = State("running", tokensIn: 1234, tokensOut: 5678, costUsd: 0m);

        await Assert.That(TokenCellOf(Footer(state))).IsEqualTo("1.2K↑ 5.7K↓");
        await Assert.That(TokenCellOf(Projected(state))).IsEqualTo("1.2K↑ 5.7K↓");
    }

    [Test]
    public async Task FullRow_PaintsEveryProjectedCellVerbatim()
    {
        var state = State("running", tokensIn: 1500, tokensOut: 300, costUsd: 0.0042m, scrollOffset: 60, viewportLines: 20, totalLines: 100);

        string[] footer = Footer(state);
        string row = string.Join(" | ", footer);
        foreach (string projected in Projected(state))
        {
            await Assert.That(row).Contains(projected);
        }

        // The footer adds host-driven slots the projection knows nothing about
        // (spinner, retry, freshness pill, run duration) — never a re-worded
        // copy of a projected cell, and always in the documented order.
        await Assert.That(row)
            .IsEqualTo("prov/m | ▌ running | agent code | scroll 75% | 1.5K↑ 300↓ | $0.0042");
    }

    [Test]
    public async Task StatusAccent_IsMappedFromTheSameProjectedStyle()
    {
        var state = State("error", costUsd: 0.0042m);
        var ws = new StatusSeg[StatusProjectorPanel.MaxSegments];
        int n = StatusProjectorPanel.BuildSegments(state, ws);

        int idx = Array.FindIndex(ws, 0, n, seg => seg.Text == "✗ error");
        await Assert.That(idx).IsGreaterThanOrEqualTo(0);
        await Assert.That(ws[idx].Accent).IsEqualTo(StatusProjectorPanel.MapAccent(UiSpanStyle.Danger));
        await Assert.That(ws[idx].Accent).IsEqualTo(StatusAccent.Error);
    }

    [Test]
    public async Task Layout_DoesNotRebuildTheCellsItWasGiven()
    {
        string layout = ReadRepoFile("src", "Harbor.Tui.CellForge", "Chat", "Widgets", "ChatScreenLayout.cs");
        string projector = ReadRepoFile("src", "Harbor.Ui.Framework.Projection", "Projection", "StatusProjector.cs");

        // #488 was about a projection that was computed and thrown away: the
        // footer called ProjectStatusBar, read four of six cells back out, and
        // rebuilt tokens and cost from raw state under a second formatting
        // rule. The ban below is the one that mattered — no number of its own —
        // and it is unchanged.
        //
        // #568 removes the rest. The footer now READS the projected bar, so the
        // "ProjectStatusBar(" ban is inverted into its opposite: the call is
        // required, because a footer that derives its own row is the fan-out
        // the issue is about. Asserting its absence would have protected the
        // duplication.
        await Assert.That(layout).Contains("StatusProjector.ProjectStatusBar(state)");
        await Assert.That(layout).DoesNotContain("CostUsd");
        await Assert.That(layout).DoesNotContain("TokensIn");
        await Assert.That(layout).DoesNotContain("TokensOut");

        // …and neither does the projector: both read StatusBarFacts.
        await Assert.That(projector).DoesNotContain("ToString(");
        await Assert.That(projector).Contains("StatusBarFacts.Of(state)");
    }

    [Test]
    public async Task CostFormatting_ExistsInExactlyOnePlaceAcrossTheStatusPath()
    {
        // Every file that renders the status bar, counted as a whole: a second
        // rule anywhere in this set turns a rendered number back into a
        // per-host opinion.
        string[] statusPath =
        [
            "src/Harbor.Ui.Framework.State/State/StatusBarText.cs",
            "src/Harbor.Ui.Framework.Projection/Projection/StatusBarFacts.cs",
            "src/Harbor.Ui.Framework.Projection/Projection/StatusProjector.cs",
            "src/Harbor.Tui.CellForge/Chat/Widgets/ChatScreenLayout.cs",
        ];

        var perFile = new List<int>(statusPath.Length);
        foreach (string relative in statusPath)
        {
            perFile.Add(CountOccurrences(ReadRepoFile(relative.Split('/')), "\"F4\""));
        }

        await Assert.That(perFile.Sum()).IsEqualTo(1);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadRepoFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(segments).ToArray()));

    /// <summary>Walks up from the test binaries to the repo root (<c>Harbor.slnx</c>).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("repo root (Harbor.slnx) not found from " + AppContext.BaseDirectory);
    }
}
