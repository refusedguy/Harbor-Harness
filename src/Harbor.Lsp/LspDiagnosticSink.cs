using System.Collections.Concurrent;
using System.Text.Json;
using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;

namespace Harbor.Lsp;

/// <summary>
///     Diagnostics half of a language-server session (#2613): the published
///     cache and the <c>textDocument/publishDiagnostics</c> payload handling.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="LspServerSession" /> stays the coordinator. It forwards
///         <see cref="LspClient.ServerNotification" /> here and re-raises
///         <see cref="Published" /> as its own
///         <see cref="LspServerSession.DiagnosticsChanged" />, so the public
///         surface and the event's sender are unchanged.
///     </para>
///     <para>
///         Keeping the cache and the wire-to-domain conversion here makes the
///         diagnostic path testable without spawning a server process. That is
///         the gap this extraction closes. The cache discipline is unchanged
///         (#1095): the published list is built locally and never mutated
///         after publication, so the concurrent reference swap is the whole
///         publish and both the notifying and reading threads stay lock-free.
///     </para>
/// </remarks>
internal sealed class LspDiagnosticSink
{
    private readonly ConcurrentDictionary<string, List<LspDiagnostic>> _diagnostics = new();
    private readonly string _serverId;
    private readonly ILogger _logger;

    public LspDiagnosticSink(string serverId, ILogger logger)
    {
        _serverId = serverId;
        _logger = logger;
    }

    /// <summary>Raised when a server re-publishes diagnostics (payload: the file path).</summary>
    public event EventHandler<LspDiagnosticsChangedEventArgs>? Published;

    /// <summary>Diagnostics last published for the file (a snapshot, never the live list).</summary>
    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string filePath) =>
        _diagnostics.TryGetValue(filePath, out List<LspDiagnostic>? list) ? [.. list] : [];

    /// <summary>
    ///     Consume one server notification. Returns <see langword="true" /> for
    ///     a <c>publishDiagnostics</c> notification; a malformed payload is
    ///     logged and leaves the cache untouched, as before.
    /// </summary>
    public bool HandleNotification(LspNotificationEventArgs args)
    {
        if (args.Method != "textDocument/publishDiagnostics" || args.Parameters.ValueKind != JsonValueKind.Object) return false;

        try
        {
            LspWire.PublishDiagnosticsParams? published =
                args.Parameters.Deserialize(LspJsonContext.Default.PublishDiagnosticsParams);
            if (published is null) return true;

            string filePath = LspServerSession.FromUri(published.Uri);
            var list = new List<LspDiagnostic>(published.Diagnostics.Count);
            foreach (LspWire.DiagnosticDto dto in published.Diagnostics)
            {
                list.Add(new LspDiagnostic(
                    filePath,
                    dto.Range.Start.Line,
                    dto.Range.Start.Character,
                    dto.Range.End.Line,
                    dto.Range.End.Character,
                    (LspSeverity)(dto.Severity ?? (int)LspSeverity.Error),
                    dto.Source ?? _serverId,
                    dto.Message));
            }

            _diagnostics[filePath] = list;
            Published?.Invoke(this, new LspDiagnosticsChangedEventArgs(filePath));
        }
        catch (JsonException ex)
        {
            LspServerSessionLog.MalformedPublishDiagnostics(_logger, ex);
        }

        return true;
    }
}
