using System.Text.Json;

namespace Harbor.Lsp;

/// <summary>
///     Content-Length framing for one LSP connection (#2613): the byte-level
///     boundary between a JSON-RPC message and the two streams of a language
///     server process.
/// </summary>
/// <remarks>
///     <para>
///         This seam knows nothing about request ids, pending calls or
///         notifications. <see cref="LspClient" /> owns that demux and drives
///         the transport, so a test can replace the pipe with an in-memory
///         frame source without spawning a server — the same gap the
///         diagnostics slice closed with <see cref="LspDiagnosticSink" />.
///     </para>
///     <para>
///         AOT: frame bodies are deserialized through <c>LspJsonContext</c>
///         source generation; no reflection is used.
///     </para>
/// </remarks>
internal interface ILspFrameTransport : IAsyncDisposable
{
    /// <summary>Write one Content-Length framed payload to the wire.</summary>
    Task WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>Read the next frame body, or <see langword="null"/> at end of stream.</summary>
    ValueTask<JsonElement?> ReadFrameAsync(CancellationToken ct);
}
