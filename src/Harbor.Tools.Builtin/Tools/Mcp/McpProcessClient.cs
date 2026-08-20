using System.Diagnostics;

namespace Harbor.Tools.Mcp;

internal sealed class McpProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public McpProcessClient(ProcessStartInfo startInfo)
    {
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start: {startInfo.FileName}");
    }

    public Stream Stdout => _process.StandardOutput.BaseStream;
    public Stream Stdin => _process.StandardInput.BaseStream;
    public Stream Stderr => _process.StandardError.BaseStream;
    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;

    public async Task WaitForExitAsync(CancellationToken ct = default)
        => await _process.WaitForExitAsync(ct).ConfigureAwait(false);

    public void Kill()
    {
        if (!_process.HasExited)
            _process.Kill(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_process.HasExited)
        {
            _process.Kill(true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { /* process didn't exit within timeout, proceed with dispose */ }
        }

        _process.Dispose();
    }
}
