using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-D-002: the status footer projects from <see cref="UiState"/> via
/// <see cref="StatusProjectorPanel"/> (glyphs + scroll from
/// <c>StatusProjector</c>, numbers via <c>StatusMappers</c>) instead of
/// hand-assembled view-model strings. Panel geometry and truncation behavior
/// are unchanged — only the data source moves.
/// </summary>
public class StatusProjectorPanelTests
{
    private static UiState MockState(
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
            Status = status,
            Provider = provider,
            Model = model,
            AgentName = agent,
            Cost = new CostSnapshot(tokensIn, tokensOut, costUsd),
            ScrollOffset = scrollOffset,
            ViewportLines = viewportLines,
            TotalLines = totalLines,
        };

    private static int Build(UiState state, StatusSeg[] workspace, string? retry = null, TimeSpan? elapsed = null) =>
        StatusProjectorPanel.BuildSegments(state, workspace, retry, elapsed);

    private static string Joined(StatusSeg[] workspace, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(" | ");
            }

            sb.Append(workspace[i].Text);
        }

        return sb.ToString();
    }

    private static int Find(StatusSeg[] workspace, int count, string text)
    {
        for (int i = 0; i < count; i++)
        {
            if (workspace[i].Text == text)
            {
                return i;
            }
        }

        return -1;
    }

    [Test]
    public async Task Running_ProjectsGlyphChromeAgentTokensCostAndLive()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running", tokensIn: 1500, tokensOut: 300, costUsd: 0.0042m), ws);

        string row = Joined(ws, n);
        await Assert.That(row).Contains("▌ running");
        await Assert.That(row).Contains("prov/m");
        await Assert.That(row).Contains("agent code");
        await Assert.That(row).Contains("1.5K↑ 300↓");
        await Assert.That(row).Contains("$0.0042");
        await Assert.That(row).Contains("live");
        await Assert.That(StatusProjectorPanel.MapMode("running")).IsEqualTo(StatusBarMode.Running);
        await Assert.That(StatusProjectorPanel.MapRhythm(StatusBarMode.Running)).IsEqualTo(SpinnerRhythm.Working);
    }

    [Test]
    public async Task Idle_ProjectsIdleGlyph_AndNoSpinner()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("idle"), ws);

        await Assert.That(Joined(ws, n)).Contains("○ idle");
        await Assert.That(StatusProjectorPanel.MapMode("idle")).IsEqualTo(StatusBarMode.Idle);
        await Assert.That(StatusProjectorPanel.MapRhythm(StatusBarMode.Idle).HasValue).IsFalse();
    }

    [Test]
    public async Task Error_ProjectsErrorGlyph()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("error"), ws);

        string row = Joined(ws, n);
        await Assert.That(row).Contains("✗ error");
        await Assert.That(StatusProjectorPanel.MapMode("error")).IsEqualTo(StatusBarMode.Idle);
    }

    [Test]
    public async Task Compacting_ProjectsCompactingGlyph_AndWorkingRhythm()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("compacting"), ws);

        await Assert.That(Joined(ws, n)).Contains("◐ compacting");
        await Assert.That(StatusProjectorPanel.MapMode("compacting")).IsEqualTo(StatusBarMode.Compacting);
        await Assert.That(StatusProjectorPanel.MapRhythm(StatusBarMode.Compacting)).IsEqualTo(SpinnerRhythm.Working);
    }

    [Test]
    public async Task ZeroCost_HidesCostSegment_ButKeepsTokens()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running", tokensIn: 2000, tokensOut: 1000, costUsd: 0m), ws);

        string row = Joined(ws, n);
        await Assert.That(row).DoesNotContain("$");
        await Assert.That(row).Contains("2.0K↑ 1.0K↓");
    }

    [Test]
    public async Task ZeroTokens_HidesTokenSegment()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("idle"), ws);

        await Assert.That(Joined(ws, n)).DoesNotContain("↑");
    }

    [Test]
    public async Task ScrollOffset_ProjectsScrollPercent()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running", scrollOffset: 40, viewportLines: 20, totalLines: 100), ws);

        string row = Joined(ws, n);
        await Assert.That(row).Contains("scroll 50%");
        await Assert.That(row).DoesNotContain("live");
    }

    [Test]
    public async Task RetryLine_BecomesFixedWarningSegment()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running"), ws, retry: RetryCountdown.Line(2, 5, 4));

        int idx = Find(ws, n, "retry 2/5 in 4s");
        await Assert.That(idx >= 0).IsTrue();
        await Assert.That(ws[idx].Accent).IsEqualTo(StatusAccent.Warning);
        await Assert.That(ws[idx].FixedPriority).IsTrue();
    }

    [Test]
    public async Task Elapsed_FormatsViaDurationToText()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running"), ws, elapsed: TimeSpan.FromSeconds(1.5));

        await Assert.That(Joined(ws, n)).Contains("1.5s");
    }

    [Test]
    public async Task SubMillisecondElapsed_HidesSegment()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("running"), ws, elapsed: TimeSpan.FromTicks(5));

        string row = Joined(ws, n);
        await Assert.That(row).DoesNotContain("ms");
        await Assert.That(row).DoesNotContain("1.5s");
    }

    [Test]
    public async Task EmptyProviderAndModel_HidesChromeSegment()
    {
        var ws = new StatusSeg[12];
        int n = Build(MockState("idle", provider: string.Empty, model: string.Empty), ws);

        await Assert.That(Joined(ws, n)).DoesNotContain("/");
    }

    [Test]
    public async Task MapMode_Unknown_FallsBackToIdle()
    {
        await Assert.That(StatusProjectorPanel.MapMode("frobnicate")).IsEqualTo(StatusBarMode.Idle);
        await Assert.That(StatusProjectorPanel.MapMode(null)).IsEqualTo(StatusBarMode.Idle);
    }

    [Test]
    public async Task Panel_PaintsProjectedRow_WithSpinner_AndNoCost()
    {
        var composer = new ComposerController();
        var status = new StatusViewModel { Model = "m" };
        var screen = ChatScreen.Build(composer, status, includeSidebar: false);
        screen.Status.ProjectedState = MockState("running", tokensIn: 1500, tokensOut: 300, costUsd: 0m);

        var buffer = new ScreenBuffer(80, 8);
        screen.Tree.Solve(80, 8);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(buffer);
        }

        string art = GridDump.Art(buffer);
        // First paint → Tick = 1 → WorkingFrames[1].
        await Assert.That(art).Contains(SpinnerStrip.WorkingFrames[1]);
        await Assert.That(art).Contains("running");
        await Assert.That(art).Contains("live");
        await Assert.That(art).DoesNotContain("$");
    }

    [Test]
    public async Task Panel_SetProjectedRetry_FeedsRetrySlot()
    {
        var panel = new StatusPanel("s", new StatusViewModel(), minWidth: 10, minHeight: 1);
        panel.SetProjectedRetry(2, 5, 4);

        await Assert.That(panel.ProjectedRetry).IsEqualTo("retry 2/5 in 4s");
    }
}
