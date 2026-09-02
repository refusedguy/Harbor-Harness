using Harbor.Ipc;
using Harbor.Ipc.Client;
using Harbor.Ipc.Protocol;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor status [--all]</c> — discovery lite (sprint 6 zone T).
///     Without flags prints the local daemon state; with <c>--all</c> polls
///     every host from <c>~/.harbor/hosts.json</c> in parallel with a short
///     timeout and reports who answers the IPC handshake. No mDNS zoo — the
///     catalog in hosts.json is the source of truth.
/// </summary>
public sealed class StatusCommand : ICommand
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public StatusCommand(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    public string Name => "status";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        bool all = args.Any(a => a is "--all" or "-a");

        if (!all)
        {
            _output.WriteLine("Use 'harbor status --all' to probe every host from ~/.harbor/hosts.json.");
            return 0;
        }

        var catalog = HostsCatalog.Load(HostsCatalog.DefaultPath);
        if (catalog.IsFailure)
        {
            _error.WriteLine(catalog.Error);
            return 1;
        }

        if (catalog.Value.Count == 0)
        {
            _output.WriteLine($"No hosts configured in {HostsCatalog.DefaultPath}.");
            _output.WriteLine("Add entries like:");
            _output.WriteLine("""  { "dell": { "kind": "tailscale", "host": "dell.tail1234.ts.net", "port": 48710 } }""");
            return 0;
        }

        var probes = catalog.Value
            .Select(kv => ProbeHostAsync(kv.Key, kv.Value, ct))
            .ToArray();

        string[] lines = await Task.WhenAll(probes).ConfigureAwait(false);
        foreach (string line in lines.OrderBy(l => l, StringComparer.Ordinal))
        {
            _output.WriteLine(line);
        }

        return 0;
    }

    /// <summary>One bounded host probe → a single report line.</summary>
    private static async Task<string> ProbeHostAsync(string name, EndpointDescriptor descriptor, CancellationToken ct)
    {
        string kindLabel = descriptor switch
        {
            EndpointDescriptor.Uds u => $"uds   {u.Path}",
            EndpointDescriptor.Tcp t => $"tcp   {t.Host}:{t.Port}",
            EndpointDescriptor.Tailscale ts => $"tails {ts.ConnectHost}:{ts.Port}",
            _ => "?"
        };

        string? psk = descriptor switch
        {
            EndpointDescriptor.Tcp t2 => t2.Psk,
            EndpointDescriptor.Tailscale ts2 => ts2.Psk,
            _ => null
        };

        IIpcClientTransport transport;
        switch (descriptor)
        {
            case EndpointDescriptor.Uds u:
                transport = new ClientPipeTransport(u.Path, NullLogger.Instance);
                break;
            case EndpointDescriptor.Tcp t:
                transport = new TcpClientTransport(t.Host, t.Port, NullLogger.Instance);
                break;
            case EndpointDescriptor.Tailscale ts:
                transport = new TcpClientTransport(ts.ConnectHost, ts.Port, NullLogger.Instance);
                break;
            default:
                return $"{name,-16} ?         unknown";
        }

        await using (transport.ConfigureAwait(false))
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);
            try
            {
                var client = new MessagePackRpcClient(transport, NullLogger.Instance, psk);
                await using (client.ConfigureAwait(false))
                {
                    await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                    var response = await client.SendAsync(new ConnectRequest(), timeoutCts.Token).ConfigureAwait(false);
                    bool alive = response is OkResponse;
                    return $"{name,-16} {kindLabel}  {(alive ? "alive" : "degraded")}";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                string reason = ex is OperationCanceledException ? "timeout" : "unreachable";
                return $"{name,-16} {kindLabel}  down ({reason})";
            }
        }
    }
}
