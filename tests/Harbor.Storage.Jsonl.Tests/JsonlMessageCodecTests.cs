using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
namespace Harbor.Storage.Jsonl.Tests;
/// <summary>
///     Unit tests for <see cref="JsonlMessageCodec.DeserializeMessage" /> —
///     the malformed-line diagnostics contract (ROP-B П.10): every missing or
///     invalid mandatory field fails with a field-specific message instead of
///     an exception ladder, and valid lines round-trip.
/// </summary>
public class JsonlMessageCodecTests
{
    private static Result<AgentMessage> Decode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());
    }

    private const string ValidUserLine = """
        {"id":"m1","createdAt":"2026-01-01T00:00:00Z","role":"user","payload":{"content":"hi","agent":"code","model":"p/m"}}
        """;

    [Test]
    public async Task Deserialize_ValidUserLine_ReturnsUserMessage()
    {
        var result = Decode(ValidUserLine);

        await Assert.That(result.IsSuccess).IsTrue();
        var user = (UserMessage)result.Value;
        await Assert.That(user.Id).IsEqualTo("m1");
        await Assert.That(user.SessionId).IsEqualTo("sess-1");
        await Assert.That(user.Content).IsEqualTo("hi");
    }

    [Test]
    public async Task Deserialize_MissingId_FailsWithFieldDiagnostic()
    {
        var result = Decode("""{"createdAt":"2026-01-01T00:00:00Z","role":"user","payload":{}}""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).StartsWith("missing 'id':");
    }

    [Test]
    public async Task Deserialize_EmptyId_FailsWithEmptyDiagnostic()
    {
        var result = Decode("""{"id":"","createdAt":"2026-01-01T00:00:00Z","role":"user","payload":{}}""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("'id' is null or empty");
    }

    [Test]
    public async Task Deserialize_MissingCreatedAt_FailsWithFieldDiagnostic()
    {
        var result = Decode("""{"id":"m1","role":"user","payload":{"content":"x","agent":"code","model":"p/m"}}""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("missing/invalid 'createdAt'");
    }

    [Test]
    public async Task Deserialize_MissingRole_Fails()
    {
        var result = Decode("""{"id":"m1","createdAt":"2026-01-01T00:00:00Z","payload":{}}""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("message m1: missing 'role'");
    }

    [Test]
    public async Task Deserialize_UnknownRole_FailsWithRoleName()
    {
        var result = Decode("""{"id":"m1","createdAt":"2026-01-01T00:00:00Z","role":"system","payload":{}}""");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("message m1: unknown role 'system'");
    }

    [Test]
    public async Task Deserialize_AssistantInvalidStopReason_FailsWithDiagnostic()
    {
        var result = Decode("""
            {"id":"a1","createdAt":"2026-01-01T00:00:00Z","role":"assistant",
             "payload":{"parts":[],"stopReason":"not-a-reason","model":"p/m"}}
            """);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invalid stopReason");
    }

    [Test]
    public async Task Deserialize_AssistantMissingModel_Fails()
    {
        var result = Decode("""
            {"id":"a1","createdAt":"2026-01-01T00:00:00Z","role":"assistant",
             "payload":{"parts":[],"stopReason":"stop"}}
            """);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("assistant message a1: missing 'model'");
    }
}
