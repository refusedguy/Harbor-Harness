using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Application.Sessions;
using Harbor.TestKit;

namespace Harbor.Core.Tests;

/// <summary>
///     #2679 F041 — the two process-level context overrides. Both flags are
///     opt-in and default off: with no flag set the history-shaping behaviour is
///     exactly what the rest of the suite already pins. The class is fully
///     serialized because the flags are process-wide environment state.
/// </summary>
[NotInParallel]
public class ContextEnvironmentOverrideTests
{
    private const string AutoCompactFlag = "HARBOR_DISABLE_AUTOCOMPACT";
    private const string PruneFlag = "HARBOR_DISABLE_PRUNE";

    [Test]
    public async Task DisableAutocompact_Truthy_SuppressesTheAutomaticVerdict()
    {
        var history = new List<AgentMessage>();
        for (int i = 0; i < 20; i++)
        {
            history.Add(MessageFixtures.User(new string('x', 4000)));
        }

        var tracker = new TokenTracker();
        int estimate = tracker.EstimateTokens(history);
        var model = new ModelInfo(
            "test-model",
            "test",
            "Test Model",
            estimate + 16_383,
            1_024,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        string? previous = Environment.GetEnvironmentVariable(AutoCompactFlag);
        try
        {
            // An explicit non-truthy value is not an opt-out.
            Environment.SetEnvironmentVariable(AutoCompactFlag, "0");
            await Assert.That(tracker.ShouldCompact(history, model)).IsTrue();

            Environment.SetEnvironmentVariable(AutoCompactFlag, "1");
            await Assert.That(tracker.ShouldCompact(history, model)).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutoCompactFlag, previous);
        }
    }

    [Test]
    public async Task DisablePrune_Truthy_KeepsTheFullToolResultOnTheModelPath()
    {
        var converter = new MessageConverter();
        string payload = new string('x', ToolResultContextTrim.MaxToolResultChars + 500);
        var message = new ToolResultMessage(
            "m-trim",
            "session-trim",
            DateTimeOffset.UtcNow,
            new[] { new ToolResultEntry("call-1", "bash", payload, true) });

        string? previous = Environment.GetEnvironmentVariable(PruneFlag);
        try
        {
            Environment.SetEnvironmentVariable(PruneFlag, "0");
            var capped = (LlmToolResultMessage)converter.ToLlmMessages(new AgentMessage[] { message })[0];
            await Assert.That(capped.Output.Length).IsLessThan(payload.Length);

            Environment.SetEnvironmentVariable(PruneFlag, "1");
            var full = (LlmToolResultMessage)converter.ToLlmMessages(new AgentMessage[] { message })[0];
            await Assert.That(full.Output).IsEqualTo(payload);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PruneFlag, previous);
        }
    }
}
