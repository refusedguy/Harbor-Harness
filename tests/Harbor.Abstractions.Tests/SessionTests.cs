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
        var updated = meta.AddUsage(new Usage(100, 50, 25, 10, 5));
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
}
