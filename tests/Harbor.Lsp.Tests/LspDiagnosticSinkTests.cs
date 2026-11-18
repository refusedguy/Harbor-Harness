using System.Text.Json;
using Harbor.Abstractions.Lsp;
using Harbor.Lsp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     The diagnostics slice is testable without spawning a server (#2613):
///     the session forwards notifications to <see cref="LspDiagnosticSink" />,
///     which owns the cache and the wire-to-domain conversion.
/// </summary>
public class LspDiagnosticSinkTests
{
    private static LspNotificationEventArgs Publish(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new LspNotificationEventArgs("textDocument/publishDiagnostics", doc.RootElement.Clone());
    }

    [Test]
    public async Task PublishDiagnostics_Cached_AndRaised()
    {
        var sink = new LspDiagnosticSink("my-ls", NullLogger.Instance);
        string? raisedFor = null;
        sink.Published += (_, args) => raisedFor = args.FilePath;

        string expectedPath = LspServerSession.FromUri("file:///tmp/a.ts");
        bool handled = sink.HandleNotification(Publish("""
            {"uri":"file:///tmp/a.ts","diagnostics":[
              {"range":{"start":{"line":1,"character":2},"end":{"line":1,"character":6}},
               "severity":2,"source":"fake","message":"warn"}]}
            """));

        await Assert.That(handled).IsTrue();
        await Assert.That(raisedFor).IsEqualTo(expectedPath);
        IReadOnlyList<LspDiagnostic> diagnostics = sink.GetDiagnostics(expectedPath);
        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(LspSeverity.Warning);
        await Assert.That(diagnostics[0].Source).IsEqualTo("fake");
        await Assert.That(diagnostics[0].Message).IsEqualTo("warn");
        await Assert.That(diagnostics[0].Line).IsEqualTo(1);
        await Assert.That(diagnostics[0].Column).IsEqualTo(2);
    }

    [Test]
    public async Task MissingSourceAndSeverity_UseDefaults()
    {
        var sink = new LspDiagnosticSink("my-ls", NullLogger.Instance);
        string expectedPath = LspServerSession.FromUri("file:///tmp/b.ts");

        sink.HandleNotification(Publish("""
            {"uri":"file:///tmp/b.ts","diagnostics":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},
               "message":"oops"}]}
            """));

        IReadOnlyList<LspDiagnostic> diagnostics = sink.GetDiagnostics(expectedPath);
        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(diagnostics[0].Source).IsEqualTo("my-ls");
        await Assert.That(diagnostics[0].Severity).IsEqualTo(LspSeverity.Error);
    }

    [Test]
    public async Task OtherNotification_Ignored()
    {
        var sink = new LspDiagnosticSink("my-ls", NullLogger.Instance);
        bool raised = false;
        sink.Published += (_, _) => raised = true;

        bool handled = sink.HandleNotification(
            new LspNotificationEventArgs("textDocument/didOpen", default));

        await Assert.That(handled).IsFalse();
        await Assert.That(raised).IsFalse();
        await Assert.That(sink.GetDiagnostics("/tmp/a.ts")).IsEmpty();
    }

    [Test]
    public async Task MalformedPayload_LeavesCacheUntouched()
    {
        var sink = new LspDiagnosticSink("my-ls", NullLogger.Instance);
        bool raised = false;
        sink.Published += (_, _) => raised = true;

        bool handled = sink.HandleNotification(Publish(
            """{"uri":"file:///tmp/a.ts","diagnostics":"not-an-array"}"""));

        await Assert.That(handled).IsTrue();
        await Assert.That(raised).IsFalse();
        await Assert.That(sink.GetDiagnostics(LspServerSession.FromUri("file:///tmp/a.ts"))).IsEmpty();
    }
}
