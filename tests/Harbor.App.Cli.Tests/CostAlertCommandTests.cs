// CostAlertCommandTests.cs — issue #3109.
//
// CostAlertThreshold was a parsed value with zero product callers. The REPL now
// owns one threshold store: /cost-alert parses its argument into it, and the
// frame loop compares every SessionStatsEvent snapshot against it. These tests
// drive the real slash dispatcher and the real stats observer, so they fail if
// the argument is dropped, the threshold is not stored, or the warning never
// reaches the REPL output.

using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

public class CostAlertCommandTests
{
    private static SlashCommandDispatcher CreateDispatcher()
    {
        var config = new JsonConfigStore();
        return new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(config, new AuthStore(config)),
            new PermissionService(new FakeAgentRegistry(), NullLogger<PermissionService>.Instance));
    }

    [Test]
    public async Task CostAlert_Amount_StoresThresholdAndWarnsOnStatsSnapshot()
    {
        var dispatcher = CreateDispatcher();
        var config = new JsonConfigStore();

        List<string> setLines = await DispatchAsync(dispatcher, "/cost-alert 1.50", config);
        await Assert.That(setLines.Any(l => l.Contains("set to $1.50", StringComparison.Ordinal))).IsTrue();
        await Assert.That(dispatcher.CostAlerts.Threshold.IsEnabled).IsTrue();
        await Assert.That(dispatcher.CostAlerts.Threshold.AmountUsd).IsEqualTo(1.50m);

        var written = new List<string>();
        ReplLifecycle.NotifyCostAlert(
            dispatcher.CostAlerts,
            Stats(2.00m),
            written.Add);

        await Assert.That(written.Count).IsEqualTo(1);
        await Assert.That(written[0]).Contains("$2.00");
        await Assert.That(written[0]).Contains("$1.50");

        // One crossing, one warning: repeated identical snapshots stay quiet.
        ReplLifecycle.NotifyCostAlert(dispatcher.CostAlerts, Stats(2.00m), written.Add);
        await Assert.That(written.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CostAlert_Off_DisablesComparison()
    {
        var dispatcher = CreateDispatcher();
        var config = new JsonConfigStore();

        await DispatchAsync(dispatcher, "/cost-alert 1.50", config);
        List<string> offLines = await DispatchAsync(dispatcher, "/cost-alert off", config);
        await Assert.That(offLines.Any(l => l.Contains("disabled", StringComparison.Ordinal))).IsTrue();
        await Assert.That(dispatcher.CostAlerts.Threshold.IsEnabled).IsFalse();

        var written = new List<string>();
        ReplLifecycle.NotifyCostAlert(dispatcher.CostAlerts, Stats(9.99m), written.Add);
        await Assert.That(written).IsEmpty();
    }

    [Test]
    public async Task CostAlert_InvalidArgument_LeavesThresholdUntouched()
    {
        var dispatcher = CreateDispatcher();
        var config = new JsonConfigStore();

        await DispatchAsync(dispatcher, "/cost-alert 1.50", config);
        List<string> lines = await DispatchAsync(dispatcher, "/cost-alert nonsense", config);

        await Assert.That(lines.Any(l => l.StartsWith("Usage: /cost-alert", StringComparison.Ordinal))).IsTrue();
        await Assert.That(dispatcher.CostAlerts.Threshold.AmountUsd).IsEqualTo(1.50m);
    }

    [Test]
    public async Task CostAlert_IsRegisteredInTheReplPaletteCatalog()
    {
        var catalog = ReplCommandCatalog.CreateDefault();

        bool found = catalog.TryResolve("cost-alert", out var command);

        await Assert.That(found).IsTrue();
        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Id).IsEqualTo("cost-alert");
    }

    private static SessionStatsEvent Stats(decimal costUsd) =>
        new("session-1", SessionMetadata.Empty with { Cost = costUsd });

    private static async Task<List<string>> DispatchAsync(
        SlashCommandDispatcher dispatcher, string input, JsonConfigStore config)
    {
        var lines = new List<string>();
        SlashCommandOutcome outcome = await dispatcher.HandleCoreAsync(
            input,
            lines.Add,
            _ => Task.FromResult(string.Empty),
            agent: null!,
            agentRegistry: new FakeAgentRegistry(),
            configStore: config,
            authStore: new AuthStore(config),
            providers: new FakeProviderRegistry(new ScriptedLlmClient()),
            session: Session.Create("/harbor-cost-alert", "code", "test-provider", "test-model"));

        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }
}
