using System.Collections.Immutable;
using System.Linq;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.App.Avalonia.Tests;

public class StatusProjectionTests
{
    [Test]
    public async Task ProjectStatusBar_IdleState_ContainsIdleStatusAndProviderModel()
    {
        var state = new UiState
        {
            Status = "idle",
            Provider = "ollama",
            Model = string.Empty,
            AgentName = string.Empty,
            Cost = new CostSnapshot(0, 0, 0m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var left = model.Segments.First(s => s.Align == Alignment.Left);
        await Assert.That(left.Text).IsEqualTo("ollama/");

        var center = model.Segments.First(s => s.Align == Alignment.Center);
        await Assert.That(center.Text).IsEqualTo("○ idle");
        await Assert.That(center.Style).IsEqualTo(UiSpanStyle.Default);
    }

    [Test]
    public async Task ProjectStatusBar_RunningState_ContainsRunningGlyphAndAccentStyle()
    {
        var state = new UiState
        {
            Status = "running",
            Provider = "openai",
            Model = "gpt-4o",
            AgentName = "code",
            IsAgentRunning = true,
            Cost = new CostSnapshot(0, 0, 0m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var center = model.Segments.First(s => s.Align == Alignment.Center);
        await Assert.That(center.Text).IsEqualTo("▌ running");
        await Assert.That(center.Style).IsEqualTo(UiSpanStyle.Accent);

        var agent = model.Segments.First(s => s.Align == Alignment.Right && s.Text.StartsWith("agent "));
        await Assert.That(agent.Text).IsEqualTo("agent code");
    }

    [Test]
    public async Task ProjectStatusBar_WithTokens_ContainsCompactTokenSegment()
    {
        var state = new UiState
        {
            Status = "idle",
            Provider = "anthropic",
            Model = "claude-opus-4",
            AgentName = "code",
            Cost = new CostSnapshot(1234, 5678, 0m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var tokens = model.Segments.First(s => s.Text == "1234↑ 5678↓");
        await Assert.That(tokens.Align).IsEqualTo(Alignment.Right);
        await Assert.That(tokens.Style).IsEqualTo(UiSpanStyle.Dim);
        await Assert.That(tokens.Importance).IsEqualTo(2);
    }

    [Test]
    public async Task ProjectStatusBar_WithCost_ContainsUsdCostSegment()
    {
        var state = new UiState
        {
            Status = "idle",
            Provider = "openai",
            Model = "gpt-4o-mini",
            AgentName = string.Empty,
            Cost = new CostSnapshot(500, 200, 0.0123m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var cost = model.Segments.First(s => s.Text == "0.0123");
        await Assert.That(cost.Align).IsEqualTo(Alignment.Right);
        await Assert.That(cost.Style).IsEqualTo(UiSpanStyle.Dim);
        await Assert.That(cost.Importance).IsEqualTo(1);
    }

    [Test]
    public async Task ProjectStatusBar_ErrorStatus_ContainsErrorGlyphAndDangerStyle()
    {
        var state = new UiState
        {
            Status = "error",
            Provider = "ollama",
            Model = string.Empty,
            AgentName = string.Empty,
            Cost = new CostSnapshot(0, 0, 0m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var center = model.Segments.First(s => s.Align == Alignment.Center);
        await Assert.That(center.Text).IsEqualTo("✗ error");
        await Assert.That(center.Style).IsEqualTo(UiSpanStyle.Danger);
    }

    [Test]
    public async Task ProjectStatusBar_CompactingStatus_ContainsCompactingGlyph()
    {
        var state = new UiState
        {
            Status = "compacting",
            Provider = "openai",
            Model = "gpt-4o",
            AgentName = "code",
            Cost = new CostSnapshot(0, 0, 0m),
            Lines = ImmutableArray<ChatLine>.Empty,
            TotalLines = 0,
            ViewportLines = 0,
            ScrollOffset = 0
        };

        var model = StatusProjector.ProjectStatusBar(state);

        var center = model.Segments.First(s => s.Align == Alignment.Center);
        await Assert.That(center.Text).IsEqualTo("◐ compacting");
        await Assert.That(center.Style).IsEqualTo(UiSpanStyle.Default);
    }
}
