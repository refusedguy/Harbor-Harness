using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;

namespace Harbor.Tui.Tests;

/// <summary>
///     Pins the [UX9] statusline rebuild (issue #269, epic #260): codex
///     <c>/statusline</c> content — model, context-%, cost, queue.
/// </summary>
public class StatuslineRebuildTests
{
    private static ToolExecutionStartEvent ToolStart(string id = "tc_1") =>
        ToolExecutionStartEvent.Create(id, "read", JsonDocument.Parse("{}").RootElement);

    private static ToolExecutionEndEvent ToolEnd(string id = "tc_1") =>
        new(id, ToolResult.Success("ok"), IsError: false);

    [Test]
    public async Task Queue_TracksToolRoundTrip_AndRendersSegment()
    {
        var vm = new StatusBarViewModel();
        await Assert.That(vm.Formatted.Contains("queue:")).IsEqualTo(false);

        await vm.UpdateFromEventAsync(ToolStart());
        await Assert.That(vm.QueuedCount).IsEqualTo(1);
        await Assert.That(vm.Formatted).Contains("queue: 1");

        await vm.UpdateFromEventAsync(ToolEnd());
        await Assert.That(vm.QueuedCount).IsEqualTo(0);
        await Assert.That(vm.Formatted.Contains("queue:")).IsEqualTo(false);
    }

    [Test]
    public async Task Queue_NeverGoesNegative()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(ToolEnd());
        await Assert.That(vm.QueuedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Queue_ClearedOnAgentEnd()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(ToolStart("tc_1"));
        await vm.UpdateFromEventAsync(ToolStart("tc_2"));
        await vm.UpdateFromEventAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(vm.QueuedCount).IsEqualTo(0);
        await Assert.That(vm.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task Cost_AccumulatesFromModelPricing_OnStepFinish()
    {
        // Pricing(3, 15): 1000 in + 100 out = 0.003 + 0.0015 = $0.0045.
        var vm = new StatusBarViewModel();
        vm.SetModel(new ModelInfo("m", "p", "M", 10_000, 4000, false, false, true, new Pricing(3m, 15m), string.Empty));

        var usage = new Usage(1000, 100);
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new StepFinishEvent(0, "stop", usage), AssistantMessage.Empty("s1", "m")));

        await Assert.That(vm.Cost).IsEqualTo(0.0045m);
        await Assert.That(vm.Formatted).Contains("$0.0045");
    }

    [Test]
    public async Task SessionStats_SyncsAuthoritativeTotals()
    {
        var vm = new StatusBarViewModel();
        var metadata = SessionMetadata.Empty with { Cost = 1.5m, TokensInput = 10, TokensOutput = 5 };
        await vm.UpdateFromEventAsync(new SessionStatsEvent("s1", metadata));

        await Assert.That(vm.Cost).IsEqualTo(1.5m);
        await Assert.That(vm.TokensIn).IsEqualTo(10);
        await Assert.That(vm.TokensOut).IsEqualTo(5);
    }

    [Test]
    public async Task Formatted_CodexOrder_CtxBeforeCost()
    {
        var vm = new StatusBarViewModel
        {
            Provider = "anthropic",
            Model = "claude-opus-4",
            Status = "running",
        };
        vm.SetModel(new ModelInfo("m", "p", "M", 10_000, 4000, false, false, true, new Pricing(0, 0), string.Empty));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new StepFinishEvent(0, "stop", new Usage(3000, 500)), AssistantMessage.Empty("s1", "m")));

        string formatted = vm.Formatted;
        await Assert.That(formatted).Contains("ctx:");
        await Assert.That(formatted.IndexOf("ctx:", StringComparison.Ordinal) < formatted.IndexOf('$')).IsEqualTo(true);
    }

    [Test]
    public async Task ResetCommand_ClearsQueue()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(ToolStart());
        vm.ResetCommand.Execute(null);
        await Assert.That(vm.QueuedCount).IsEqualTo(0);
    }
}
