using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #87.2: tool-call args must be owned (materialized) at creation — a part
///     or event built from a parsed document stays readable after the document
///     is disposed (the OpenAiWire eager-materialization rule, #86).
/// </summary>
public class ToolCallArgsLifetimeTests
{
    private const string ArgsJson = """{"path":"README.md","limit":10}""";

    [Test]
    public async Task ToolCallPart_Create_SurvivesDocumentDisposal()
    {
        ToolCallPart part;
        using (var doc = JsonDocument.Parse(ArgsJson))
        {
            part = ToolCallPart.Create("tc1", "read", doc.RootElement);
        }

        await Assert.That(part.Args.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(part.Args.GetRawText()).IsEqualTo(ArgsJson);
    }

    [Test]
    public async Task ToolCallEndEvent_Create_SurvivesDocumentDisposal()
    {
        ToolCallEndEvent evt;
        using (var doc = JsonDocument.Parse(ArgsJson))
        {
            evt = ToolCallEndEvent.Create("tc1", "read", doc.RootElement);
        }

        await Assert.That(evt.Args.GetRawText()).IsEqualTo(ArgsJson);
    }

    [Test]
    public async Task ToolExecutionStartEvent_Create_SurvivesDocumentDisposal()
    {
        ToolExecutionStartEvent evt;
        using (var doc = JsonDocument.Parse(ArgsJson))
        {
            evt = ToolExecutionStartEvent.Create("tc1", "read", doc.RootElement);
        }

        await Assert.That(evt.Args.GetRawText()).IsEqualTo(ArgsJson);
    }

    [Test]
    public async Task PermissionRequest_Create_SurvivesDocumentDisposal()
    {
        PermissionRequest request;
        using (var doc = JsonDocument.Parse(ArgsJson))
        {
            request = PermissionRequest.Create("read", "README.md", doc.RootElement, Array.Empty<string>());
        }

        await Assert.That(request.Args.GetRawText()).IsEqualTo(ArgsJson);
    }

    [Test]
    public async Task ToolCallPart_Create_DefaultArgs_DoesNotThrow()
    {
        var part = ToolCallPart.Create("tc0", "read", default);

        await Assert.That(part.Args.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }
}
