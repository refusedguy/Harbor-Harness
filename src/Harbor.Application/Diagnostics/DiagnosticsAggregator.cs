using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Diagnostics;

/// <summary>
///     Carries the freshly composed diagnostic snapshot to whoever renders it.
/// </summary>
/// <param name="snapshot">The whole snapshot, owned by the aggregator after the event returns.</param>
public sealed class DiagnosticsSnapshotEventArgs(IReadOnlyList<DiagnosticIssue> snapshot) : EventArgs
{
    /// <summary>The snapshot as of this notification.</summary>
    public IReadOnlyList<DiagnosticIssue> Snapshot { get; } = snapshot;
}

/// <summary>
///     The headless owner of every diagnostic the harness knows about, from both
///     producers, and the only place either of them is interpreted.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two producers, one snapshot, explicitly tagged.</b>
///         <list type="number">
///             <item>
///                 <description>
///                     <b>Language servers</b> — <see cref="ILspService" /> already
///                     parses <c>textDocument/publishDiagnostics</c> into
///                     <see cref="LspDiagnostic" /> with the server's OWN severity
///                     and real file/line, and raises
///                     <see cref="ILspService.DiagnosticsChanged" /> when a file is
///                     re-published. This type subscribes, fetches the fresh set
///                     and maps it. No pattern matching anywhere on this path.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     <b>Tool output</b> — a build log or a test run has no core API
///                     behind it, so <see cref="ToolOutputIssueDetector" /> extracts
///                     rows from the text of a <c>ToolExecutionEndEvent</c>. These are
///                     tagged <see cref="DiagnosticIssueSource.ToolOutput" /> so no
///                     renderer can present them as server-reported.
///                 </description>
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Why this type exists at all.</b> #674 found the tool-output half
///         re-implemented inside a projection layer, and the language-server half
///         not connected anywhere: the sidebar spelled its counts as literal zeros
///         while a second, parallel text channel stood in for the feature. A
///         renderer now receives the classified rows and does nothing but draw
///         them.
///     </para>
///     <para>
///         <b>Threading.</b> Bus publishes arrive on whichever thread ran the tool;
///         the LSP notification arrives on the language server's reader loop. Both
///         take the same lock, compose a fresh immutable snapshot, and raise
///         <see cref="Changed" /> <i>outside</i> it, so a slow subscriber cannot
///         stall a language server's socket.
///     </para>
/// </remarks>
public sealed class DiagnosticsAggregator : IDisposable
{
    /// <summary>
    ///     Tool-output rows kept, newest last. Older rows fall off the front: a
    ///     long session's diagnostics list is a «what is broken right now»
    ///     surface, not a second transcript, and every retained row is copied into
    ///     the immutable snapshot a renderer holds.
    /// </summary>
    public const int MaxToolOutputIssues = 200;

    private readonly Lock _sync = new();
    private readonly SortedDictionary<string, IReadOnlyList<DiagnosticIssue>> _languageServer
        = new(StringComparer.Ordinal);

    private readonly List<DiagnosticIssue> _toolOutput = [];
    private readonly IDisposable[] _subscriptions;
    private readonly ILspService? _lsp;
    private readonly ILogger<DiagnosticsAggregator>? _logger;
    private IReadOnlyList<DiagnosticIssue> _snapshot = [];
    private bool _disposed;

    /// <summary>
    ///     Create an aggregator and start observing. Either dependency may be
    ///     absent (a minimal host, a test): the aggregator then reports whichever
    ///     half it can see, which is the whole point of tagging the two apart.
    /// </summary>
    /// <param name="eventBus">Agent events. Null observes nothing.</param>
    /// <param name="lsp">Language-server facade. Null disables the LSP half.</param>
    /// <param name="logger">Fault sink for the detached LSP fetch.</param>
    public DiagnosticsAggregator(
        IEventBus? eventBus,
        ILspService? lsp = null,
        ILogger<DiagnosticsAggregator>? logger = null)
    {
        _logger = logger;
        _lsp = lsp;

        var subscriptions = new List<IDisposable>(2);
        if (eventBus is not null)
        {
            subscriptions.Add(eventBus.Subscribe<ToolExecutionEndEvent>(OnToolExecutionEnd));
            subscriptions.Add(eventBus.Subscribe<AgentErrorEvent>(OnAgentError));
        }

        if (lsp is not null)
        {
            lsp.DiagnosticsChanged += OnLspDiagnosticsChanged;
        }

        _subscriptions = [.. subscriptions];
    }

    /// <summary>Raised after every change, carrying the whole fresh snapshot.</summary>
    public event EventHandler<DiagnosticsSnapshotEventArgs>? Changed;

    /// <summary>
    ///     Everything classified so far, language-server rows first (grouped by
    ///     file) then tool-output rows in arrival order. The order is stable so a
    ///     renderer can diff two snapshots.
    /// </summary>
    public IReadOnlyList<DiagnosticIssue> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    ///     Drop everything — both halves. Called when the host switches session,
    ///     since diagnostics of the previous session's files are not diagnostics
    ///     of the new one.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _toolOutput.Clear();
            _languageServer.Clear();
        }

        Publish();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unhook the LSP event too: it is a plain CLR event, so a subscriber
        // that never unsubscribes keeps this aggregator — and every snapshot it
        // holds — alive for the life of the language-server facade.
        if (_lsp is not null)
        {
            _lsp.DiagnosticsChanged -= OnLspDiagnosticsChanged;
        }

        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private ValueTask OnToolExecutionEnd(ToolExecutionEndEvent @event, CancellationToken _)
    {
        string output = @event.Result.Output;
        if (string.IsNullOrWhiteSpace(output))
        {
            return ValueTask.CompletedTask;
        }

        // The tool name is not on the end event, so the detector's own producer
        // label ("csharp", "rust", …) is what a row carries; the tool is
        // available by joining ToolCallId back to ToolExecutionStartEvent, which
        // costs a second subscription for a column nobody renders today.
        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect("tool", output);
        if (found.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            _toolOutput.AddRange(found);
            TrimToolOutput();
        }

        Publish();
        return ValueTask.CompletedTask;
    }

    private ValueTask OnAgentError(AgentErrorEvent @event, CancellationToken _)
    {
        string message = @event.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return ValueTask.CompletedTask;
        }

        IReadOnlyList<DiagnosticIssue> found = ToolOutputIssueDetector.Detect("agent", message);
        var rows = new List<DiagnosticIssue>(found.Count + 1);
        rows.AddRange(found);

        if (found.Count == 0)
        {
            // An agent error that matches no detector is still an error: report
            // the message itself rather than letting it vanish because no
            // pattern fired. This is the one place the caller invents a fallback,
            // and it is deliberately not in the detector — «no detector fired» is
            // the honest answer for a build log, and only a top-level failure
            // warrants this.
            rows.Add(new DiagnosticIssue(
                DiagnosticIssueSource.ToolOutput,
                DiagnosticIssueSeverity.Error,
                "agent",
                null,
                0,
                message));
        }

        lock (_sync)
        {
            _toolOutput.AddRange(rows);
            TrimToolOutput();
        }

        Publish();
        return ValueTask.CompletedTask;
    }

    private void OnLspDiagnosticsChanged(object? sender, LspDiagnosticsChangedEventArgs args)
    {
        if (_lsp is null)
        {
            return;
        }

        // The notification only names the file; the fresh set is a call away.
        // That call is async and this thread is the language server's reader
        // loop, so it is detached with its fault observed (§FP-006) rather than
        // awaited — blocking here would stall the socket.
        TaskFireAndForget.Forget(
            RefreshLanguageServerAsync(_lsp, args.FilePath, CancellationToken.None),
            ex => _logger?.LogWarning(ex, "Diagnostics: LSP refresh for {File} failed", args.FilePath));
    }

    private async Task RefreshLanguageServerAsync(ILspService lsp, string filePath, CancellationToken ct)
    {
        IReadOnlyList<LspDiagnostic> diagnostics = await lsp.GetDiagnosticsAsync(filePath, ct).ConfigureAwait(false);

        var rows = new List<DiagnosticIssue>(diagnostics.Count);
        foreach (LspDiagnostic diagnostic in diagnostics)
        {
            rows.Add(new DiagnosticIssue(
                DiagnosticIssueSource.LanguageServer,
                SeverityOf(diagnostic.Severity),
                diagnostic.Source,
                diagnostic.FilePath,
                diagnostic.Line + 1,
                diagnostic.Message));
        }

        lock (_sync)
        {
            // An empty publication is a CLEAR, not an absence: the server is
            // saying «this file is clean now». Dropping the key rather than
            // storing an empty list is the same answer and keeps the composition
            // free of dead slices.
            if (rows.Count == 0)
            {
                _languageServer.Remove(filePath);
            }
            else
            {
                _languageServer[filePath] = rows;
            }
        }

        Publish();
    }

    /// <summary>
    ///     The one place <see cref="LspSeverity" /> becomes a display severity.
    ///     Every member is named and the discard THROWS: an unmapped code would
    ///     silently read as «not an error», which is the failure this whole issue
    ///     is about — a path that looks connected and reports nothing.
    /// </summary>
    /// <remarks>
    ///     Public because it IS the contract between the protocol's numbering and
    ///     this type's, and every host that composes rows by hand needs it.
    /// </remarks>
    public static DiagnosticIssueSeverity SeverityOf(LspSeverity severity) => severity switch
    {
        LspSeverity.Error => DiagnosticIssueSeverity.Error,
        LspSeverity.Warning => DiagnosticIssueSeverity.Warning,
        LspSeverity.Information => DiagnosticIssueSeverity.Information,
        LspSeverity.Hint => DiagnosticIssueSeverity.Hint,
        LspSeverity.None => DiagnosticIssueSeverity.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "unmapped LSP severity"),
    };

    private void TrimToolOutput()
    {
        int excess = _toolOutput.Count - MaxToolOutputIssues;
        if (excess > 0)
        {
            _toolOutput.RemoveRange(0, excess);
        }
    }

    private void Publish()
    {
        DiagnosticsSnapshotEventArgs args;
        lock (_sync)
        {
            _snapshot = Compose();
            args = new DiagnosticsSnapshotEventArgs(_snapshot);
        }

        Changed?.Invoke(this, args);
    }

    private IReadOnlyList<DiagnosticIssue> Compose()
    {
        int total = _toolOutput.Count;
        foreach (IReadOnlyList<DiagnosticIssue> rows in _languageServer.Values)
        {
            total += rows.Count;
        }

        var composed = new List<DiagnosticIssue>(total);
        foreach (IReadOnlyList<DiagnosticIssue> rows in _languageServer.Values)
        {
            composed.AddRange(rows);
        }

        composed.AddRange(_toolOutput);
        return composed;
    }
}
