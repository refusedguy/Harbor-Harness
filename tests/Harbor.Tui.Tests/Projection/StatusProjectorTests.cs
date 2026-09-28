using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.Tests;

public class StatusProjectorTests
{
    [Test]
    public async Task ProjectStatusBar_BootState_ContainsProviderAndIdleGlyph()
    {
        var state = new UiState();
        var model = StatusProjector.ProjectStatusBar(state);

        await Assert.That(model.Segments.Any(s => s.Text == "/")).IsTrue();
        await Assert.That(model.Segments.Any(s => s.Text == "○ idle")).IsTrue();
        await Assert.That(model.Segments.Any(s => s.Align == Alignment.Right && s.Text == "0.0000")).IsTrue();
        await Assert.That(model.Segments.Any(s => s.Align == Alignment.Right && s.Text == "live")).IsTrue();
        await Assert.That(model.Segments.Any(s => s.Text.Contains("agent"))).IsFalse();
        await Assert.That(model.Segments.Any(s => s.Text.Contains("↑"))).IsFalse();
    }

    [Test]
    public async Task ProjectStatusBar_RunningState_GlyphIsAccent()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { Status = "running" } };
        var model = StatusProjector.ProjectStatusBar(state);

        var statusSegment = model.Segments.First(s => s.Text.StartsWith("▌"));
        await Assert.That(statusSegment.Style).IsEqualTo(UiSpanStyle.Accent);
    }

    [Test]
    public async Task ProjectStatusBar_ErrorState_GlyphIsDanger()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { Status = "error" } };
        var model = StatusProjector.ProjectStatusBar(state);

        var statusSegment = model.Segments.First(s => s.Text.StartsWith("✗"));
        await Assert.That(statusSegment.Style).IsEqualTo(UiSpanStyle.Danger);
    }

    [Test]
    public async Task ProjectStatusBar_CompactingState_GlyphIsDefault()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { Status = "compacting" } };
        var model = StatusProjector.ProjectStatusBar(state);

        var statusSegment = model.Segments.First(s => s.Text.StartsWith("◐"));
        await Assert.That(statusSegment.Style).IsEqualTo(UiSpanStyle.Default);
    }

    [Test]
    public async Task ProjectStatusBar_WithAgentName_ContainsAgentSegment()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { AgentName = "code" } };
        var model = StatusProjector.ProjectStatusBar(state);

        await Assert.That(model.Segments.Any(s => s.Text == "agent code" && s.Align == Alignment.Right)).IsTrue();
    }

    [Test]
    public async Task ProjectStatusBar_WithTokens_ContainsTokenSegment()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { Cost = new CostSnapshot(123, 456, 0) } };
        var model = StatusProjector.ProjectStatusBar(state);

        await Assert.That(model.Segments.Any(s => s.Text == "123↑ 456↓")).IsTrue();
    }

    [Test]
    public async Task ProjectStatusBar_WithCost_FormatsCostAsF4()
    {
        var state = new UiState { Chat = ChatDomainState.Empty with { Cost = new CostSnapshot(0, 0, 1.5m) } };
        var model = StatusProjector.ProjectStatusBar(state);

        await Assert.That(model.Segments.Any(s => s.Text == "1.5000")).IsTrue();
    }

    [Test]
    public async Task ProjectStatusBar_WithScroll_ShowsPercentageOrLive()
    {
        var scrolledState = new UiState { Ui = TerminalUiState.Empty with { TotalLines = 10, ViewportLines = 5, ScrollOffset = 5 } };
        var scrolledModel = StatusProjector.ProjectStatusBar(scrolledState);
        await Assert.That(scrolledModel.Segments.Any(s => s.Text == "scroll 100%")).IsTrue();

        var liveState = new UiState { Ui = TerminalUiState.Empty with { TotalLines = 5, ViewportLines = 5 } };
        var liveModel = StatusProjector.ProjectStatusBar(liveState);
        await Assert.That(liveModel.Segments.Any(s => s.Text == "live")).IsTrue();
    }

    [Test]
    public async Task ProjectFooter_JoinsSegmentsWithDoubleSpace()
    {
        var state = new UiState();
        var footer = StatusProjector.ProjectFooter(state);

        await Assert.That(footer).IsEqualTo("/  ○ idle  0.0000  live");
    }

    [Test]
    public async Task Ordered_Left_Center_Right_With_Importance()
    {
        var segments = new UiStatusSegment[]
        {
            new("r-low", Alignment.Right, 0, UiSpanStyle.Dim),
            new("c", Alignment.Center, 2, UiSpanStyle.Default),
            new("r-high", Alignment.Right, 3, UiSpanStyle.Default),
            new("l", Alignment.Left, 1, UiSpanStyle.Default),
        };

        var ordered = StatusSegmentOrdering.Ordered(segments);

        await Assert.That(ordered.Count).IsEqualTo(4);
        await Assert.That(ordered[0].Text).IsEqualTo("l");
        await Assert.That(ordered[1].Text).IsEqualTo("c");
        await Assert.That(ordered[2].Text).IsEqualTo("r-high");
        await Assert.That(ordered[3].Text).IsEqualTo("r-low");
    }
}
