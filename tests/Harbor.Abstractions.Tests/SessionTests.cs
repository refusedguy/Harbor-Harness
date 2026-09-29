using System.Text.Json;
using Harbor.Abstractions.Models;
namespace Harbor.Abstractions.Tests;
public class SessionTests
{
    [Test]
    public async Task Session_Create_Generates_Id_And_Timestamps()
    {
        var session = Session.Create("/home/user/project", "code", "anthropic", "claude-opus-4");
        await Assert.That(string.IsNullOrEmpty(session.Id)).IsFalse();
        await Assert.That(session.Directory).IsEqualTo("/home/user/project");
        await Assert.That(session.Agent).IsEqualTo("code");
        await Assert.That(session.Model).IsEqualTo("claude-opus-4");
        await Assert.That(session.ProviderId).IsEqualTo("anthropic");
        await Assert.That(session.CreatedAt <= DateTimeOffset.UtcNow).IsTrue();
    }

    [Test]
    public async Task SessionMetadata_Empty_Has_ZeroValues()
    {
        var meta = SessionMetadata.Empty;
        await Assert.That(meta.Cost).IsEqualTo(0m);
        await Assert.That(meta.TokensInput).IsEqualTo(0);
        await Assert.That(meta.TokensOutput).IsEqualTo(0);
        await Assert.That(meta.MessageCount).IsEqualTo(0);
    }

    [Test]
    public async Task SessionMetadata_AddUsage_Accumulates()
    {
        var meta = SessionMetadata.Empty;
        var updated = meta.AddUsage(new Usage(100, 50, 25, 10, 5), new Pricing(3m, 15m));
        await Assert.That(updated.TokensInput).IsEqualTo(100);
        await Assert.That(updated.TokensOutput).IsEqualTo(50);
        await Assert.That(updated.TokensReasoning).IsEqualTo(25);
        await Assert.That(updated.TokensCacheRead).IsEqualTo(10);
        await Assert.That(updated.TokensCacheWrite).IsEqualTo(5);
        await Assert.That(updated.MessageCount).IsEqualTo(1);
    }

    [Test]
    public async Task Pricing_CalculateCost_Correct()
    {
        var pricing = new Pricing(15m, 75m, 1.5m, 18.75m);
        decimal cost = pricing.CalculateCost(new Usage(1_000_000, 1_000_000, 0, 1_000_000, 1_000_000));
        await Assert.That(cost).IsEqualTo(15m + 75m + 1.5m + 18.75m);
    }

    /// <summary>
    ///     #653: the token fold must not lose its cost half. A turn with no
    ///     cache traffic bills input + output only.
    /// </summary>
    [Test]
    public async Task SessionMetadata_AddUsage_WithoutCache_PricesInputAndOutput()
    {
        var updated = SessionMetadata.Empty.AddUsage(
            new Usage(1_000_000, 500_000), new Pricing(3m, 15m));

        await Assert.That(updated.Cost).IsEqualTo(3m + 7.5m);
        await Assert.That(updated.IsCostKnown).IsTrue();
    }

    /// <summary>
    ///     #653: cached tokens bill at their own rates, so a cached turn costs
    ///     FAR less than the same turn billed at the full input rate. This is the
    ///     arithmetic the two deleted $3/$15 constants threw away.
    /// </summary>
    [Test]
    public async Task SessionMetadata_AddUsage_WithCache_PricesCacheAtItsOwnRates()
    {
        var pricing = new Pricing(3m, 15m, CacheReadPerMillion: 0.30m, CacheWritePerMillion: 3.75m);

        var updated = SessionMetadata.Empty.AddUsage(
            new Usage(
                InputTokens: 100_000,
                OutputTokens: 200_000,
                ReasoningTokens: null,
                CacheReadTokens: 1_000_000,
                CacheWriteTokens: 400_000),
            pricing);

        // in $0.30 + out $3.00 + cache-read $0.30 + cache-write $1.50.
        await Assert.That(updated.Cost).IsEqualTo(5.10m);

        // The same tokens billed as if the cache-read ones were fresh input cost
        // $7.80 — the fold reports 35% less, which is the entire point of the
        // cache rates and exactly what a constants-only formula cannot express.
        var billedAsInput = pricing.CalculateCost(
            new Usage(1_100_000, 200_000, null, null, 400_000));
        await Assert.That(billedAsInput).IsEqualTo(7.80m);
        await Assert.That(updated.Cost).IsLessThan(billedAsInput);
    }

    /// <summary>
    ///     #653: a model with no published price (Ollama — or a paid provider
    ///     whose catalogue entry carries no rates) contributes no cost AND says
    ///     so, so the UI can render "unknown" instead of a "$0.0000" that reads
    ///     as "free".
    /// </summary>
    [Test]
    public async Task SessionMetadata_AddUsage_UnknownPricing_ReportsTheCostAsUnknown()
    {
        var updated = SessionMetadata.Empty.AddUsage(
            new Usage(10_000_000, 10_000_000, null, 5_000_000, 5_000_000), Pricing.Unknown);

        await Assert.That(updated.Cost).IsEqualTo(0m);
        await Assert.That(updated.IsCostKnown).IsFalse();

        // Tokens still land in full — only the money is a floor.
        await Assert.That(updated.TokensInput).IsEqualTo(10_000_000);
        await Assert.That(updated.TokensCacheRead).IsEqualTo(5_000_000);
    }

    /// <summary>
    ///     #653: the flag describes the CURRENT model's price, not the sum — a
    ///     priced turn after an unpriced one restores a real, known total, because
    ///     the answer it carries is "can the model in use be priced".
    /// </summary>
    [Test]
    public async Task SessionMetadata_AddUsage_CostKnown_FollowsTheLastFoldedPricing()
    {
        var meta = SessionMetadata.Empty
            .AddUsage(new Usage(1_000, 1_000), Pricing.Unknown)
            .AddUsage(new Usage(1_000_000, 0), new Pricing(3m, 15m));

        await Assert.That(meta.IsCostKnown).IsTrue();
        await Assert.That(meta.Cost).IsEqualTo(3m);
    }

    [Test]
    public async Task Pricing_Unknown_IsRecognisedAsSuch()
    {
        await Assert.That(Pricing.Unknown.IsUnknown).IsTrue();
        await Assert.That(new Pricing(0.001m, 0.002m).IsUnknown).IsFalse();
        await Assert.That(new Pricing(0m, 0m, 0m, 0m).IsUnknown).IsTrue();
        // A free-but-real rate table and an unpriced model are indistinguishable
        // from the numbers alone — which is why IsCostKnown is a published bit.
        await Assert.That(new Pricing(0m, 0m, 1m, 1m).IsUnknown).IsFalse();
    }

    [Test]
    public async Task Pricing_Unknown_Has_ZeroCost()
    {
        decimal cost = Pricing.Unknown.CalculateCost(new Usage(1000, 1000));
        await Assert.That(cost).IsEqualTo(0m);
    }
}

public class MessageTests
{
    [Test]
    public async Task UserMessage_Role_IsUser()
    {
        var msg = new UserMessage("id", "session", DateTimeOffset.UtcNow, "hello", "code", "claude-opus-4");
        await Assert.That(msg.Role).IsEqualTo("user");
    }

    [Test]
    public async Task AssistantMessage_Role_IsAssistant()
    {
        var msg = AssistantMessage.Empty("session", "claude-opus-4");
        await Assert.That(msg.Role).IsEqualTo("assistant");
    }

    [Test]
    public async Task AssistantMessage_AppendText_AddsTextPart()
    {
        var msg = AssistantMessage.Empty("session", "claude-opus-4");
        var updated = msg.AppendText("Hello");
        await Assert.That(updated.Parts.Count).IsEqualTo(1);
        await Assert.That(((TextPart)updated.Parts[0]).Text).IsEqualTo("Hello");
    }

    [Test]
    public async Task AssistantMessage_AppendToolCall_AddsToolCallPart()
    {
        var msg = AssistantMessage.Empty("session", "claude-opus-4");
        var args = JsonDocument.Parse("{}").RootElement;
        var updated = msg.AppendToolCall(new ToolCallPart("call-1", "read", args));
        await Assert.That(updated.Parts.Count).IsEqualTo(1);
        await Assert.That(((ToolCallPart)updated.Parts[0]).ToolName).IsEqualTo("read");
    }

    [Test]
    public async Task AssistantMessage_WithFinish_SetsStopReason()
    {
        var msg = AssistantMessage.Empty("session", "claude-opus-4");
        var updated = msg.WithFinish(StopReason.Stop, new Usage(100, 50));
        await Assert.That(updated.StopReason).IsEqualTo(StopReason.Stop);
        await Assert.That(updated.Usage.InputTokens).IsEqualTo(100);
    }

    [Test]
    public async Task ToolResult_Success_Factory()
    {
        var result = ToolResult.Success("done");
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo("done");
    }

    [Test]
    public async Task ToolResult_Error_Factory()
    {
        var result = ToolResult.Error("failed");
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).IsEqualTo("failed");
    }

    [Test]
    public async Task ProjectIdFor_IsDeterministicSha256()
    {
        // Pinned value: SHA-256("/tmp/x") first 8 bytes, lowercase hex.
        // string.GetHashCode (the old implementation) is randomized per
        // process and would fail this across restarts by design.
        await Assert.That(Session.ProjectIdFor("/tmp/x")).IsEqualTo("2e56aa36f538b33b");
        await Assert.That(Session.ProjectIdFor("/tmp/x")).IsEqualTo(Session.ProjectIdFor("/tmp/x"));
        await Assert.That(Session.ProjectIdFor("/tmp/y")).IsNotEqualTo(Session.ProjectIdFor("/tmp/x"));
    }

    [Test]
    public async Task Session_Create_UsesDeterministicProjectId()
    {
        var session = Session.Create("/tmp/x", "code", "test", "test-model");
        await Assert.That(session.ProjectId).IsEqualTo("2e56aa36f538b33b");
    }
}
