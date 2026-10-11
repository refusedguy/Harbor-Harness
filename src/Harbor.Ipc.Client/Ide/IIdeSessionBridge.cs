using System.Text;

namespace Harbor.Ipc.Ide;

/// <summary>
///     Contract for an IDE session bridge — binds an editor-facing NDJSON JSON-RPC
///     stream to a live Harbor host through <see cref="IHarborClient"/>.
///     Implementations drive the bridge lifecycle: connect, serve requests until
///     the editor closes stdin, then dispose.
/// </summary>
public interface IIdeSessionBridge : IAsyncDisposable
{
    /// <summary>Session the bridge is bound to (null until bound by host glue).</summary>
    string? SessionId { get; }

    /// <summary>
    ///     Serve requests until the editor closes stdin. Returns when the read
    ///     loop ends; in-flight runs are cancelled by <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task RunAsync(CancellationToken ct = default);
}
