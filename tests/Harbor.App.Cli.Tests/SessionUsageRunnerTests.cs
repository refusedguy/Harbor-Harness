using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

public class SessionUsageRunnerTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemorySessionStore _store = new();

    private async Task<string> SeedWithUsageAsync(decimal cost, bool costKnown, int input, int output)
    {
        var session = await _store.CreateAsync("/tmp", "code", "test", "test-model").ConfigureAwait(false);
        await _store.UpdateStatsAsync(session.Id, new SessionMetadata(
            Cost: cost,
            TokensInput: input,
            TokensOutput: output,
            TokensReasoning: 7,
            TokensCacheRead: 11,
            TokensCacheWrite: 13,
            MessageCount: 3,
            TimeCompacting: null,
            IsCostKnown: costKnown)).ConfigureAwait(false);
        return session.Id;
    }

    private Task<int> RunAsync(string id)
        => SessionUsageRunner.RunAsync(_out, _err, _store, id);

    [Test]
    public async Task Reports_TokenBreakdown_And_Priced_Cost()
    {
        string id = await SeedWithUsageAsync(0.0123m, costKnown: true, input: 100, output: 50);

        int exit = await RunAsync(id);

        await Assert.That(exit).IsEqualTo(0);
        string text = _out.ToString();
        await Assert.That(text).Contains($"Session {id}");
        await Assert.That(text).Contains("LLM calls:");
        await Assert.That(text).Contains("Input tokens:");
        await Assert.That(text).Contains("Cache write tokens:");
        await Assert.That(text).Contains("100");
        await Assert.That(text).Contains("$0.0123");
    }

    [Test]
    public async Task Unknown_Cost_Is_Not_Quoted_As_Zero()
    {
        string id = await SeedWithUsageAsync(0m, costKnown: false, input: 0, output: 0);

        int exit = await RunAsync(id);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("price unknown");
        await Assert.That(_out.ToString()).DoesNotContain("$0.0000");
    }

    [Test]
    public async Task Missing_Session_Is_Store_Failure()
    {
        int exit = await RunAsync("no-such-session");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("Cannot read usage");
    }

    [Test]
    public async Task Empty_Id_Is_Argument_Error()
    {
        int exit = await RunAsync("  ");

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(_err.ToString()).Contains("Usage: harbor sessions usage");
    }
}
