using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Manages one MCP stdio server subprocess. Spawns it from a <see cref="ProcessStartInfo" />,
///     exposes stdin/stdout for JSON-RPC, pumps stderr to the logger (so it never blocks the
///     stdout parser), and kills the whole process tree on dispose.
/// </summary>
internal sealed class McpProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger<McpProcessClient>? _logger;
    private readonly CancellationTokenSource _stderrCts = new();
    private readonly Task _stderrPump;
    private readonly SafeJobHandle? _job;
    private bool _disposed;

    public McpProcessClient(ProcessStartInfo startInfo, ILogger<McpProcessClient>? logger = null)
    {
        _logger = logger;
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start: {startInfo.FileName}");

        try
        {
            _job = ProcessTree.KillOnCloseJob(_process);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Process-tree setup failed for {Command}; falling back to direct kill", startInfo.FileName);
        }

        _stderrPump = PumpStderrAsync(_stderrCts.Token);
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
        if (_process.HasExited) return;
        ProcessTree.KillTree(_process, _job);
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        try
        {
            var reader = new StreamReader(_process.StandardError.BaseStream, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    _logger?.LogWarning("[mcp stderr] {Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "stderr pump ended");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _stderrCts.Cancel();
        try { await _stderrPump.ConfigureAwait(false); } catch { /* ignore */ }
        _stderrCts.Dispose();

        if (!_process.HasExited)
        {
            try { ProcessTree.KillTree(_process, _job); } catch { /* ignore */ }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* process didn't exit within timeout, proceed with dispose */ }
        }

        _process.Dispose();
    }
}
