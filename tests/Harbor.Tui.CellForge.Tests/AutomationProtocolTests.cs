using System;
using System.IO;
using System.Text.Json;
using Harbor.Tui.CellForge.CodeGen;
using Harbor.Tui.CellForge.CodeGen.Automation;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2438: the dev-only automation protocol. The tree comes from the
///     parsed DSL arena, the transport speaks SSE <c>data:</c> lines only, and
///     the method table reports Timeout, NotFound and Ambiguous by code.
/// </summary>
public class AutomationProtocolTests
{
    private const string Markup = """
        panel "root":
            .id "root-id"
            .x 1
            .y 2
            .width 30
            .height 10
            text "child":
                .id "child-id"
        """;

    [Test]
    public async Task GetTree_ReadsIdentityTypeBoundsAndChildrenFromArena()
    {
        var session = Session(Markup);

        using var document = JsonDocument.Parse(session.ExecuteJson(
            "{\"id\":\"r1\",\"method\":\"getTree\",\"params\":{}}"));
        var root = document.RootElement;

        await Assert.That(root.GetProperty("id").GetString()).IsEqualTo("r1");
        var tree = root.GetProperty("result").GetProperty("tree");
        await Assert.That(tree.GetProperty("type").GetString()).IsEqualTo("Panel");
        await Assert.That(tree.GetProperty("testId").GetString()).IsEqualTo("root-id");
        await Assert.That(tree.GetProperty("bounds").GetProperty("x").GetInt32()).IsEqualTo(1);
        await Assert.That(tree.GetProperty("bounds").GetProperty("width").GetInt32()).IsEqualTo(30);

        var children = tree.GetProperty("children");
        await Assert.That(children.GetArrayLength()).IsEqualTo(1);
        await Assert.That(children[0].GetProperty("type").GetString()).IsEqualTo("Text");
        await Assert.That(children[0].GetProperty("testId").GetString()).IsEqualTo("child-id");
    }

    [Test]
    public async Task MethodTable_ReportsNotFoundAmbiguousAndTimeout()
    {
        const string duplicate = """
            panel "root":
                .id "dup"
                text "a":
                    .id "dup"
                text "b":
                    .id "one"
            """;
        var session = Session(duplicate);

        await Assert.That(ErrorCode(session, "getTree", "{\"testId\":\"missing\"}")).IsEqualTo("NotFound");
        await Assert.That(ErrorCode(session, "getTree", "{\"testId\":\"dup\"}")).IsEqualTo("Ambiguous");
        await Assert.That(ErrorCode(session, "testId", "{\"value\":\"one\",\"timeoutMs\":50}")).IsEqualTo("");
        await Assert.That(ErrorCode(session, "testId", "{\"value\":\"missing\",\"timeoutMs\":10}")).IsEqualTo("Timeout");
    }

    [Test]
    public async Task Clock_PausesResumesAndFastForwards()
    {
        var session = Session(Markup);
        await Assert.That(ErrorCode(session, "clock", "{\"action\":\"pause\"}")).IsEqualTo("");
        await Assert.That(ClockState(session, "{\"action\":\"now\"}")).Contains("\"paused\":true");

        await Assert.That(ErrorCode(session, "clock", "{\"action\":\"fastForward\",\"ms\":40}")).IsEqualTo("");
        await Assert.That(ClockState(session, "{\"action\":\"now\"}")).Contains("\"nowMs\":40");

        await Assert.That(ErrorCode(session, "clock", "{\"action\":\"resume\"}")).IsEqualTo("");
        await Assert.That(ClockState(session, "{\"action\":\"now\"}")).Contains("\"paused\":false");
    }

    [Test]
    public async Task Transport_IgnoresNonDataLines_AndCorrelatesResponses()
    {
        using var output = new StringWriter();
        using var input = new StringReader("");
        var transport = new AutomationTransport(Session(Markup), input, output);

        await Assert.That(transport.ProcessLine("a log line")).IsFalse();
        await Assert.That(transport.ProcessLine(
            "data: {\"id\":\"a\",\"method\":\"getTree\",\"params\":{}}")).IsTrue();
        await Assert.That(transport.ProcessLine(
            "data: {\"id\":\"b\",\"method\":\"testId\",\"params\":{\"value\":\"none\"}}")).IsTrue();

        await Assert.That(transport.IgnoredLines).IsEqualTo(1);
        await Assert.That(transport.ProcessedLines).IsEqualTo(2);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0].StartsWith("data: ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(lines[0]).Contains("\"id\":\"a\"");
        await Assert.That(lines[0]).Contains("\"tree\"");
        await Assert.That(lines[1]).Contains("\"id\":\"b\"");
        await Assert.That(lines[1]).Contains("NotFound");
    }

    [Test]
    public async Task MalformedRequest_GetsProtocolErrorWithEmptyCorrelationId()
    {
        var session = Session(Markup);

        await Assert.That(ErrorCode(session, "bad json")).IsEqualTo("Protocol");
        using var document = JsonDocument.Parse(session.ExecuteJson("bad json"));
        await Assert.That(document.RootElement.GetProperty("id").GetString()).IsEqualTo("");
    }

    private static AutomationSession Session(string markup)
    {
        var parsed = MarkupParser.Parse(markup);
        if (!parsed.IsValid)
            throw new InvalidOperationException("test markup did not parse");
        return new AutomationSession(HeadlessTree.Build(parsed.Arena));
    }

    private static string ErrorCode(AutomationSession session, string method, string parameters) =>
        ErrorCode(session, "{\"id\":\"r\",\"method\":\"" + method + "\",\"params\":" + parameters + "}");

    private static string ErrorCode(AutomationSession session, string json)
    {
        using var document = JsonDocument.Parse(session.ExecuteJson(json));
        var root = document.RootElement;
        return root.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ClockState(AutomationSession session, string parameters)
    {
        string json = "{\"id\":\"r\",\"method\":\"clock\",\"params\":" + parameters + "}";
        using var document = JsonDocument.Parse(session.ExecuteJson(json));
        return document.RootElement.GetProperty("result").GetProperty("clock").GetRawText();
    }
}
