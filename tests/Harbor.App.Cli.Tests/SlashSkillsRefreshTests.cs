using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     <c>/skills refresh</c> REPL surface (issue #23 slice 2): usage guard,
///     graceful fallback without a model, and the reseed-and-summarize path.
/// </summary>
public class SlashSkillsRefreshTests
{
    [Test]
    public async Task HandleAsync_SkillsWithoutArgs_ReportsUsage()
    {
        var output = await DispatchAsync("/skills", refresh: null);

        await Assert.That(output.Any(l => l.Contains("Usage: /skills refresh"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsRefreshWithoutModel_ReportsNotAvailable()
    {
        var output = await DispatchAsync("/skills refresh", refresh: null);

        await Assert.That(output.Any(l => l.Contains("not available"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsRefresh_ReseedsModelAndSummarizesStale()
    {
        var model = new SkillFreshnessModel();
        int calls = 0;
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
        {
            calls++;
            var entries = new List<SkillFreshnessEntry>
            {
                new("fresh", "aa", "aa"),
                new("drifted", "aa", "bb"),
            };
            model.SetSkills(entries);
            return entries;
        };

        var output = await DispatchAsync("/skills refresh", refresh);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(model.StaleCount).IsEqualTo(1);
        await Assert.That(output.Any(l => l.Contains("2 checked") && l.Contains("1 need attention"))).IsTrue();
    }

    [Test]
    public async Task HandleAsync_SkillsRefresh_AllCurrent_ReportsUpToDate()
    {
        Func<IReadOnlyList<SkillFreshnessEntry>> refresh = () =>
            new List<SkillFreshnessEntry> { new("fresh", "aa", "aa") };

        var output = await DispatchAsync("/skills refresh", refresh);

        await Assert.That(output.Any(l => l.Contains("1 up to date"))).IsTrue();
    }

    /// <summary>Dispatch through the renderer-free overload, capturing writer lines.</summary>
    private static async Task<List<string>> DispatchAsync(string input, Func<IReadOnlyList<SkillFreshnessEntry>>? refresh)
    {
        var configStore = new JsonConfigStore();
        var dispatcher = new SlashCommandDispatcher(
            NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>(),
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(configStore, new AuthStore(configStore)),
            new PermissionService(new FakeAgentRegistry(), NullLogger<PermissionService>.Instance),
            skillRefresh: refresh);
        var lines = new List<string>();
        var outcome = await dispatcher.HandleCoreAsync(input,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: null!, configStore: null!, authStore: null!,
            providers: null!, session: Session.Create("/harbor-skills-tests", "code", "t", "m"));
        await Assert.That(outcome.ShouldQuit).IsFalse();
        return lines;
    }
}
