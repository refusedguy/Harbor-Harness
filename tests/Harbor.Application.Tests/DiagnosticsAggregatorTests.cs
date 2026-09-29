using Harbor.Abstractions.Events;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Models;
using Harbor.Application.Diagnostics;
using Harbor.TestKit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #674: the headless core is the only thing that decides what an
///     issue is. These cover the two halves it owns — the tool-output detector
///     that took over the projection layer's regex table, and the aggregator that
///     composes it with what the language servers already reported.
/// </summary>
public class DiagnosticsAggregatorTests
{
    // ── The detector ────────────────────────────────────────────────────────

    [Test]
    [Arguments("error CS0246: The type or namespace name 'Foo' could not be found", "csharp")]
    [Arguments("error MSB3021: Unable to copy file", "csharp")]
    [Arguments("error[E0308]: mismatched types", "rust")]
    [Arguments("File \"app.py\", line 10, in <module>", "python")]
    [Arguments("TypeError: Cannot read properties of undefined", "node")]
    [Arguments("System.NullReferenceException: Object reference not set", "exception")]
    [Arguments("warning: unused variable 'x'", "warning")]
    public async Task Detect_ClassifiesTheShapesTheProjectionUsedToOwn(string row, string expected)
    {
        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect("tool", row);

        await Assert.That(found.Count).IsEqualTo(1)
            .Because("this is the exact table #674 moved out of PanelExtractors, verbatim. A shape "
                   + "that used to light up the panel and no longer does is a deleted feature, not a "
                   + "moved one. Row: " + row);
        await Assert.That(found[0].Source).IsEqualTo(DiagnosticIssueSource.ToolOutput);
        await Assert.That(found[0].Producer).IsEqualTo(expected);
    }

    [Test]
    public async Task Detect_TagsEveryRowAsToolOutput()
    {
        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect(
            "tool",
            "error CS0001: bad\nerror CS0002: worse\nwarning CS0168: unused");

        foreach (DiagnosticIssue issue in found)
        {
            await Assert.That(issue.Source).IsEqualTo(DiagnosticIssueSource.ToolOutput)
                .Because("the tag is what stops a renderer presenting a build-log line as something a "
                       + "language server reported. #674 exists because nothing used to say which was which.");
        }
    }

    /// <summary>
    ///     Severity comes from the row, not from which detector fired. A
    ///     <c>warning:</c>-prefixed C# diagnostic is a warning even though
    ///     <c>CS####</c> is nominally the error pattern.
    /// </summary>
    [Test]
    public async Task Detect_AWarningPrefixedCSharpRowIsAWarning()
    {
        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect(
            "tool",
            "warning CS0168: The variable 'x' is declared but never used");

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Severity).IsEqualTo(DiagnosticIssueSeverity.Warning);
        await Assert.That(found[0].Producer).IsEqualTo("csharp");
    }

    /// <summary>
    ///     «No detector fired» is the honest answer for clean output. A detector
    ///     that invented a row to avoid returning nothing would turn every
    ///     successful <c>npm test</c> into an error.
    /// </summary>
    [Test]
    public async Task Detect_CleanOutputYieldsNothing()
    {
        await Assert.That(ToolOutputIssueDetector.Detect("tool", "All 42 tests passed.")).IsEmpty();
        await Assert.That(ToolOutputIssueDetector.Detect("tool", string.Empty)).IsEmpty();
        await Assert.That(ToolOutputIssueDetector.Detect("tool", "   \n  \n")).IsEmpty();
    }

    /// <summary>
    ///     A megabyte-long build log must not become a second transcript: the
    ///     per-output cap is what stops one runaway tool result from filling the
    ///     panel.
    /// </summary>
    [Test]
    public async Task Detect_StopsAtThePerOutputCap()
    {
        var log = new System.Text.StringBuilder();
        for (int i = 0; i < ToolOutputIssueDetector.MaxIssuesPerOutput + 25; i++)
        {
            _ = log.Append("error CS000").Append(i % 10).Append(": nope\n");
        }

        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect("tool", log.ToString());

        await Assert.That(found.Count).IsEqualTo(ToolOutputIssueDetector.MaxIssuesPerOutput);
    }

    // ── The aggregator ──────────────────────────────────────────────────────

    [Test]
    public async Task Aggregator_PublishesToolOutputAsToolOutput()
    {
        var bus = new FakeEventBus();
        using var aggregator = new DiagnosticsAggregator(bus);

        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1",
            ToolResult.Success("error CS0246: The type or namespace name 'Foo' could not be found"),
            IsError: false));

        IReadOnlyList<DiagnosticIssue> snapshot = aggregator.Snapshot;
        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(snapshot[0].Source).IsEqualTo(DiagnosticIssueSource.ToolOutput);
        await Assert.That(snapshot[0].Producer).IsEqualTo("csharp");
    }

    /// <summary>
    ///     The half that was never connected. A language server's report reaches
    ///     the snapshot with the SERVER'S severity, and a line that is 0-based on
    ///     the wire becomes 1-based here, because the display type says 1-based
    ///     and the protocol says otherwise.
    /// </summary>
    [Test]
    public async Task Aggregator_MapsLanguageServerReportsWithTheServersOwnSeverity()
    {
        var bus = new FakeEventBus();
        var lsp = new FakeLspService();
        using var aggregator = new DiagnosticsAggregator(bus, lsp);

        await lsp.PublishAndWaitAsync(
            aggregator,
            "src/a.cs",
            new LspDiagnostic("src/a.cs", 0, 0, 0, 0, LspSeverity.Error, "csharp", "CS0246: type not found"),
            new LspDiagnostic("src/a.cs", 41, 0, 41, 0, LspSeverity.Warning, "csharp", "CS0168: unused"));

        IReadOnlyList<DiagnosticIssue> snapshot = aggregator.Snapshot;
        await Assert.That(snapshot.Count).IsEqualTo(2);

        await Assert.That(snapshot[0].Source).IsEqualTo(DiagnosticIssueSource.LanguageServer);
        await Assert.That(snapshot[0].Severity).IsEqualTo(DiagnosticIssueSeverity.Error);
        await Assert.That(snapshot[0].FilePath).IsEqualTo("src/a.cs");
        await Assert.That(snapshot[0].Line).IsEqualTo(1)
            .Because("LspDiagnostic.Line is 0-based, mirroring the protocol; the display contract is "
                   + "1-based. Off by one here and every reported line points at the wrong source line.");

        await Assert.That(snapshot[1].Severity).IsEqualTo(DiagnosticIssueSeverity.Warning);
        await Assert.That(snapshot[1].Line).IsEqualTo(42);
    }

    /// <summary>
    ///     An empty publication is a CLEAR, not an absence. Keeping a zero-length
    ///     entry would leave a file that the server has since declared clean
    ///     occupying space in the composition forever.
    /// </summary>
    [Test]
    public async Task Aggregator_AnEmptyPublicationClearsThatFilesRows()
    {
        var bus = new FakeEventBus();
        var lsp = new FakeLspService();
        using var aggregator = new DiagnosticsAggregator(bus, lsp);

        await lsp.PublishAndWaitAsync(
            aggregator,
            "src/a.cs",
            new LspDiagnostic("src/a.cs", 0, 0, 0, 0, LspSeverity.Error, "csharp", "still broken"));
        await Assert.That(aggregator.Snapshot.Count).IsEqualTo(1);

        await lsp.PublishAndWaitAsync(aggregator, "src/a.cs");

        await Assert.That(aggregator.Snapshot).IsEmpty()
            .Because("the server republished src/a.cs with no diagnostics, which is how it says «this "
                   + "file is clean now». Reporting the stale error would be the alternative.");
    }

    [Test]
    public async Task Aggregator_ResetDropsBothProducers()
    {
        var bus = new FakeEventBus();
        var lsp = new FakeLspService();
        using var aggregator = new DiagnosticsAggregator(bus, lsp);

        await lsp.PublishAndWaitAsync(
            aggregator,
            "src/a.cs",
            new LspDiagnostic("src/a.cs", 0, 0, 0, 0, LspSeverity.Error, "csharp", "broken"));

        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1", ToolResult.Success("error CS0001: nope"), IsError: true));
        await Assert.That(aggregator.Snapshot.Count).IsEqualTo(2);

        aggregator.Reset();

        await Assert.That(aggregator.Snapshot).IsEmpty()
            .Because("the previous session's broken files are not this session's diagnostics, and a "
                   + "snapshot that carried them over would report failures the user cannot reproduce.");
    }

    /// <summary>
    ///     An agent-level failure that matches no detector is still a failure.
    ///     The fallback lives in the aggregator rather than the detector, because
    ///     «no pattern fired» is the correct answer for a build log and only a
    ///     top-level error warrants a row regardless.
    /// </summary>
    [Test]
    public async Task Aggregator_AnAgentErrorThatMatchesNoDetectorIsStillReported()
    {
        var bus = new FakeEventBus();
        using var aggregator = new DiagnosticsAggregator(bus);

        await bus.PublishAsync(new AgentErrorEvent("the transport fell over mid-stream"));

        IReadOnlyList<DiagnosticIssue> snapshot = aggregator.Snapshot;
        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(snapshot[0].Severity).IsEqualTo(DiagnosticIssueSeverity.Error);
        await Assert.That(snapshot[0].Producer).IsEqualTo("agent");
        await Assert.That(snapshot[0].Message).IsEqualTo("the transport fell over mid-stream");
    }

    [Test]
    public async Task Aggregator_WithoutAnLspServiceItStillReportsToolOutput()
    {
        var bus = new FakeEventBus();
        using var aggregator = new DiagnosticsAggregator(bus);

        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1", ToolResult.Success("TypeError: boom"), IsError: true));

        await Assert.That(aggregator.Snapshot.Count).IsEqualTo(1)
            .Because("the two producers are independent by design. A host with no language server "
                   + "must still get the half it can see, which is what the per-row Source tag is for.");
    }

    /// <summary>
    ///     Every protocol severity has a display severity, and the unnamed arm
    ///     throws. An unmapped code that defaulted to «not an error» is precisely
    ///     the failure #674 is about: a path that looks connected and reports
    ///     nothing.
    /// </summary>
    [Test]
    public async Task SeverityOf_MapsEveryProtocolSeverity()
    {
        await Assert.That(DiagnosticsAggregator.SeverityOf(LspSeverity.Error))
            .IsEqualTo(DiagnosticIssueSeverity.Error);
        await Assert.That(DiagnosticsAggregator.SeverityOf(LspSeverity.Warning))
            .IsEqualTo(DiagnosticIssueSeverity.Warning);
        await Assert.That(DiagnosticsAggregator.SeverityOf(LspSeverity.Information))
            .IsEqualTo(DiagnosticIssueSeverity.Information);
        await Assert.That(DiagnosticsAggregator.SeverityOf(LspSeverity.Hint))
            .IsEqualTo(DiagnosticIssueSeverity.Hint);
        await Assert.That(DiagnosticsAggregator.SeverityOf(LspSeverity.None))
            .IsEqualTo(DiagnosticIssueSeverity.Error)
            .Because("None is the emergency arm that exists only to keep the numbering aligned; "
                   + "treating it as an error is what the protocol's own comment prescribes.");

        await Assert.That(() => DiagnosticsAggregator.SeverityOf((LspSeverity)99))
            .Throws<ArgumentOutOfRangeException>()
            .Because("an unmapped severity must be loud. Defaulting it to a non-error value would hide "
                   + "a protocol change behind a panel that still renders, which no test would catch.");
    }

    /// <summary>
    ///     The snapshot is a single message carrying every row, so a renderer
    ///     can never hold a half-updated mix of the old set and the new.
    /// </summary>
    [Test]
    public async Task Aggregator_RaisesChangedWithTheWholeSnapshot()
    {
        var bus = new FakeEventBus();
        using var aggregator = new DiagnosticsAggregator(bus);

        var received = new List<IReadOnlyList<DiagnosticIssue>>();
        aggregator.Changed += (_, args) => received.Add(args.Snapshot);

        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc1", ToolResult.Success("error CS0001: one"), IsError: true));
        await bus.PublishAsync(new ToolExecutionEndEvent(
            "tc2", ToolResult.Success("error CS0002: two"), IsError: true));

        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0].Count).IsEqualTo(1);
        await Assert.That(received[1].Count).IsEqualTo(2)
            .Because("each notification carries the WHOLE snapshot, not a delta. A renderer that "
                   + "replaced its list from a delta would have to re-apply ordering it cannot see.");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Stands in for the language server. Stores per-file rows and raises
    ///     the same <c>DiagnosticsChanged</c> the real facade re-exports, so the
    ///     aggregator's subscription is exercised as written.
    /// </summary>
    private sealed class FakeLspService : ILspService
    {
        private readonly Dictionary<string, IReadOnlyList<LspDiagnostic>> _byFile = [];

        public event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

        public void Publish(string filePath, params LspDiagnostic[] diagnostics)
        {
            _byFile[filePath] = diagnostics;
            DiagnosticsChanged?.Invoke(this, new LspDiagnosticsChangedEventArgs(filePath));
        }

        public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(
            string filePath, CancellationToken ct = default) =>
            ValueTask.FromResult(
                _byFile.TryGetValue(filePath, out IReadOnlyList<LspDiagnostic>? rows) ? rows : []);

        public ValueTask OpenFileAsync(string filePath, string text, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseFileAsync(string filePath) => ValueTask.CompletedTask;

        public bool SupportsFile(string filePath) => true;

        public ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default) =>
            ValueTask.FromResult<LspLocation?>(null);

        public ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<LspLocation>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        ///     Publish for a file and wait for the refresh it triggers. The
        ///     aggregator deliberately does NOT block the notifying thread — it
        ///     is a language server's reader loop — so the fetch lands on the
        ///     thread pool and a test has to wait for the resulting snapshot
        ///     rather than assume it is there. The watcher is attached before the
        ///     publish for that reason: subscribing afterwards would wait for an
        ///     event that has already gone by.
        /// </summary>
        public async Task PublishAndWaitAsync(
            DiagnosticsAggregator aggregator,
            string filePath,
            params LspDiagnostic[] diagnostics)
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, DiagnosticsSnapshotEventArgs args) => arrived.TrySetResult();

            aggregator.Changed += OnChanged;
            try
            {
                Publish(filePath, diagnostics);
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                aggregator.Changed -= OnChanged;
            }
        }
    }
}
