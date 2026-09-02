using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Harbor.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>A remote MCP endpoint served over HTTP instead of a stdio subprocess.</summary>
internal sealed record McpRemoteEndpoint(
    string Url,
    string Transport,
    IReadOnlyDictionary<string, string>? Headers);

public sealed class McpRegistry : IMcpRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ServerEntry> _servers = new();
    private readonly ILogger<McpRegistry>? _logger;
    private int _nextId;
    private bool _disposed;

    public McpRegistry(ILogger<McpRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Register an MCP server by name with a single shell command (legacy form). The
    ///     command line is tokenized with shell-like quoting rules (<see cref="McpArgvParser" />)
    ///     into a program + arguments and launched as-is.
    /// </summary>
    public Result Register(string name, string stdioCommand)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Server name cannot be empty.");

        // ROP-A Z1 п.7: passthrough failure → Bind; no intermediate .Value read.
        return McpArgvParser.ParseCommand(stdioCommand)
            .Map(tokens => new McpServerStartInfo { Command = tokens[0], Args = tokens[1..] })
            .Bind(startInfo => Register(name, startInfo));
    }

    /// <summary>
    ///     Register an MCP server by name with an explicit spawn description. This is the
    ///     preferred form — it supports arguments, working directory, and environment overrides
    ///     without shell-quoting the command line.
    /// </summary>
    public Result Register(string name, McpServerStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Server name cannot be empty.");
        if (startInfo is null)
            return Result.Failure("startInfo cannot be null.");
        if (string.IsNullOrWhiteSpace(startInfo.Command))
            return Result.Failure("startInfo.Command cannot be empty.");

        return RegisterInternal(name, startInfo, null);
    }

    /// <summary>
    ///     Register a remote MCP server reachable over HTTP (streamable HTTP, or the
    ///     legacy HTTP+SSE transport when <paramref name="transport" /> is <c>"sse"</c>).
    ///     Nothing is connected until the first call — transports connect lazily.
    /// </summary>
    public Result Register(string name, string url, string transport = "http", IReadOnlyDictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Server name cannot be empty.");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Result.Failure($"Server url '{url}' is not a valid absolute http(s) URL.");
        if (!string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(transport, "sse", StringComparison.OrdinalIgnoreCase))
            return Result.Failure($"MCP transport '{transport}' is not supported (expected 'http' or 'sse').");

        return RegisterInternal(name, null, new McpRemoteEndpoint(url, transport.ToLowerInvariant(), headers));
    }

    private Result RegisterInternal(string name, McpServerStartInfo? startInfo, McpRemoteEndpoint? remote)
    {
        if (_servers.ContainsKey(name))
            return Result.Failure($"MCP server '{name}' is already registered.");

        _servers[name] = new ServerEntry(startInfo, remote);
        _logger?.LogInformation(
            "Registered MCP server: {Name} -> {Target}",
            name,
            remote is not null ? $"{remote.Transport}:{remote.Url}" : startInfo!.Command);
        return Result.Success();
    }

    /// <summary>
    ///     Load servers from a standard mcp.json file. Supports both the legacy flat map
    ///     (<c>"name": "command"</c> / <c>"name": {"command": ...}</c>) and the industry
    ///     <c>mcpServers</c> map (with <c>args</c>, <c>cwd</c>, <c>env</c>, <c>disabled</c>).
    ///     Missing file → treated as empty config. <c>${projectRoot}</c>, <c>${home}</c>,
    ///     <c>${harborHome}</c> macros in command/args/cwd/env are expanded. Unknown fields
    ///     are ignored.
    /// </summary>
    public Result RegisterFromConfig(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure("MCP config path cannot be empty.");

        if (!File.Exists(path))
        {
            _logger?.LogInformation("MCP config file not found: {Path}", path);
            return Result.Success();
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger?.LogWarning("MCP config root is not an object: {Path}", path);
                return Result.Failure("MCP config root must be an object.");
            }

            JsonElement servers = root;
            if (root.TryGetProperty("mcpServers", out var mcpServers) && mcpServers.ValueKind == JsonValueKind.Object)
                servers = mcpServers;

            int loaded = 0;
            foreach (var property in servers.EnumerateObject())
            {
                string name = property.Name;
                JsonElement value = property.Value;

                // Legacy flat form: "name": "command line" — kept for backward compatibility.
                if (value.ValueKind == JsonValueKind.String)
                {
                    // ROP-A Z1 п.18: the log is glued to the result and the
                    // counter falls out of one Match expression.
                    var registered = Register(name, value.GetString() ?? string.Empty)
                        .TapError(e => _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, e));
                    loaded += registered.IsSuccess ? 1 : 0;
                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object)
                {
                    _logger?.LogWarning("MCP server '{Name}' config is not an object", name);
                    continue;
                }

                if (value.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True)
                {
                    _logger?.LogInformation("MCP server '{Name}' is disabled; skipping", name);
                    continue;
                }

                // Static instructions hint (ROP-D Z3): surfaced to the system
                // prompt via IMcpRegistry.GetInstructions() without any
                // connection — the dynamic source is the `initialize`
                // response harvested in InvokeAsync.
                string? staticInstructions =
                    value.TryGetProperty("instructions", out var insEl) && insEl.ValueKind == JsonValueKind.String
                        ? insEl.GetString()
                        : null;

                Result registration;
                if (value.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    // Remote form: {"url": "...", "transport": "http"|"sse", "headers": {...}}
                    string? transport =
                        value.TryGetProperty("transport", out var transportEl) && transportEl.ValueKind == JsonValueKind.String
                            ? transportEl.GetString()
                            : "http";

                    Dictionary<string, string>? headers = null;
                    if (value.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
                    {
                        headers = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var h in headersEl.EnumerateObject())
                            if (h.Value.ValueKind == JsonValueKind.String)
                                headers[h.Name] = h.Value.GetString()!;
                    }

                    registration = Register(name, urlEl.GetString() ?? string.Empty, transport ?? "http", headers);
                }
                else
                {
                    if (!value.TryGetProperty("command", out var commandEl) || commandEl.ValueKind != JsonValueKind.String)
                    {
                        _logger?.LogWarning("MCP server '{Name}' config has neither 'url' nor 'command'", name);
                        continue;
                    }

                    var command = commandEl.GetString() ?? string.Empty;
                    var args = new List<string>();
                    if (value.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                        foreach (var a in argsEl.EnumerateArray())
                            if (a.ValueKind == JsonValueKind.String) args.Add(a.GetString()!);

                    string? cwd = value.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                        ? cwdEl.GetString()
                        : null;

                    Dictionary<string, string>? env = null;
                    if (value.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                    {
                        env = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var e in envEl.EnumerateObject())
                            if (e.Value.ValueKind == JsonValueKind.String)
                                env[e.Name] = e.Value.GetString()!;
                    }

                    registration = Register(name, new McpServerStartInfo
                    {
                        Command = command,
                        Args = args,
                        WorkingDirectory = cwd,
                        Environment = env
                    });
                }

                if (registration.IsSuccess)
                {
                    loaded++;
                    if (!string.IsNullOrWhiteSpace(staticInstructions))
                        _servers[name].SetInstructions(staticInstructions);
                }
                else
                {
                    _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, registration.Error);
                }
            }

            _logger?.LogInformation("Loaded {Count} MCP server(s) from config", loaded);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load MCP config from {Path}", path);
            return Result.Failure($"Failed to load MCP config: {ex.Message}");
        }
    }

    public Result Unregister(string name)
    {
        if (_servers.TryRemove(name, out var entry))
        {
            entry.DisposeSync();
            _logger?.LogInformation("Unregistered MCP server: {Name}", name);
            return Result.Success();
        }
        return Result.Failure($"MCP server '{name}' is not registered.");
    }

    public IReadOnlyList<string> GetServerNames() => _servers.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyList<McpServerInstructions> GetInstructions()
    {
        // Stable snapshot ordered by server name so the prompt block is
        // deterministic across turns (feeds the prompt-builder content hash).
        List<McpServerInstructions>? snapshot = null;
        foreach (string name in _servers.Keys.ToArray())
        {
            string? instructions = _servers[name].Instructions;
            if (string.IsNullOrWhiteSpace(instructions))
            {
                continue;
            }

            snapshot ??= new List<McpServerInstructions>();
            snapshot.Add(new McpServerInstructions(name, instructions));
        }

        if (snapshot is null)
        {
            return Array.Empty<McpServerInstructions>();
        }

        snapshot.Sort(static (a, b) => string.CompareOrdinal(a.ServerName, b.ServerName));
        return snapshot;
    }

    public async Task<Result<string>> InvokeAsync(string server, string method, JsonElement args, CancellationToken cancellationToken = default)
    {
        if (!_servers.TryGetValue(server, out var entry))
            return Result.Failure<string>($"MCP server '{server}' is not registered.");

        if (entry.IsRemote)
            return await InvokeRemoteAsync(entry, server, method, args, cancellationToken).ConfigureAwait(false);

        var process = entry.GetProcess();
        if (process is null)
            return Result.Failure<string>($"MCP server '{server}' process is not running.");

        try
        {
            await using var transport = new McpJsonRpcTransport(process.Stdout, process.Stdin);
            int id = ++_nextId;

            using var requestDoc = JsonDocument.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{args.GetRawText()}}}");
            await transport.WriteAsync(requestDoc.RootElement.Clone(), cancellationToken).ConfigureAwait(false);

            var response = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (response is null)
                return Result.Failure<string>($"MCP server '{server}' returned no response.");

            using (response)
            {
                return ProcessResponse(response, server, method, entry);
            }
        }
        catch (Exception ex)
        {
            // ROP-A Z1 п.8: cancellation rethrows (Esc ≠ fake server failure,
            // §4.5 via ResultErrors policy); the rest keeps the MCP message.
            if (ex is OperationCanceledException) throw;

            return Result.Failure<string>($"MCP call to '{server}.{method}' failed: {ex.Message}");
        }
    }

    /// <summary>Remote (HTTP/SSE) call path: per-entry cached transport, same JSON-RPC framing as the stdio path.</summary>
    private async Task<Result<string>> InvokeRemoteAsync(
        ServerEntry entry,
        string server,
        string method,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        IMcpRemoteTransport? transport = entry.GetTransport(_logger);
        if (transport is null)
            return Result.Failure<string>($"MCP server '{server}' transport could not be created.");

        try
        {
            int id = ++_nextId;
            using var requestDoc = JsonDocument.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{args.GetRawText()}}}");
            JsonDocument? response = await transport
                .RoundTripAsync(requestDoc.RootElement.Clone(), id, cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
                return Result.Failure<string>($"MCP server '{server}' returned no response.");

            using (response)
            {
                return ProcessResponse(response, server, method, entry);
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;

            return Result.Failure<string>($"MCP call to '{server}.{method}' failed: {ex.Message}");
        }
    }

    /// <summary>Shared JSON-RPC response handling: error mapping + `instructions` harvest (ROP-D Z3).</summary>
    private Result<string> ProcessResponse(JsonDocument response, string server, string method, ServerEntry entry)
    {
        if (response.RootElement.TryGetProperty("error", out var error))
        {
            string msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown" : "unknown";
            return Result.Failure<string>($"MCP error from '{server}': {msg}");
        }

        var resultElement = response.RootElement.GetProperty("result");
        if (method == "initialize"
            && resultElement.ValueKind == JsonValueKind.Object
            && resultElement.TryGetProperty("instructions", out var initIns)
            && initIns.ValueKind == JsonValueKind.String
            && entry.TrySetInstructions(initIns.GetString()))
        {
            _logger?.LogDebug("Captured MCP instructions from '{Server}' initialize", server);
        }

        return Result.Success(resultElement.GetRawText());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kv in _servers)
        {
            await kv.Value.DisposeAsync().ConfigureAwait(false);
            _servers.TryRemove(kv.Key, out _);
        }
    }

    private sealed class ServerEntry : IAsyncDisposable
    {
        private readonly McpServerStartInfo? _startInfo;
        private readonly McpRemoteEndpoint? _remote;
        private readonly object _transportGate = new();
        private McpProcessClient? _process;
        private IMcpRemoteTransport? _transport;
        private volatile string? _instructions;

        public ServerEntry(McpServerStartInfo? startInfo, McpRemoteEndpoint? remote)
        {
            _startInfo = startInfo;
            _remote = remote;
        }

        public bool IsRemote => _remote is not null;

        public string? Instructions => _instructions;

        /// <summary>
        ///     Lazily create the remote transport (first writer wins under the gate);
        ///     the instance is cached so streamable-HTTP session ids survive across calls.
        ///     OAuth placeholder: the full OAuth2 authorization-code flow is deferred —
        ///     until then hosts may export the <c>HARBOR_MCP_OAUTH_TOKEN</c> environment
        ///     variable, which is attached as a Bearer token by the transports.
        /// </summary>
        public IMcpRemoteTransport? GetTransport(ILogger? logger)
        {
            if (_remote is null)
            {
                return null;
            }

            lock (_transportGate)
            {
                if (_transport is { } cached)
                {
                    return cached;
                }

                Uri endpoint = new(_remote.Url, UriKind.Absolute);
                Func<string?> oauthTokenProvider = static () => Environment.GetEnvironmentVariable("HARBOR_MCP_OAUTH_TOKEN");
                _transport = string.Equals(_remote.Transport, "sse", StringComparison.OrdinalIgnoreCase)
                    ? new McpSseTransport(endpoint, _remote.Headers, oauthTokenProvider, logger)
                    : new McpHttpTransport(endpoint, _remote.Headers, oauthTokenProvider, logger);
                return _transport;
            }
        }

        public void SetInstructions(string? instructions) => _instructions = instructions;

        /// <summary>First writer wins; later handshakes never clobber a hint.</summary>
        public bool TrySetInstructions(string? instructions)
        {
            if (string.IsNullOrWhiteSpace(instructions) || _instructions is not null)
            {
                return false;
            }

            _instructions = instructions;
            return true;
        }

        public McpProcessClient? GetProcess()
        {
            if (_startInfo is null)
            {
                return null; // remote entries have no subprocess
            }

            if (_process is { HasExited: false }) return _process;
            _process?.DisposeSync();

            var psi = new ProcessStartInfo
            {
                FileName = _startInfo.Command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (_startInfo.Args is { Count: > 0 })
                foreach (var a in _startInfo.Args)
                    psi.ArgumentList.Add(a);

            if (!string.IsNullOrWhiteSpace(_startInfo.WorkingDirectory))
                psi.WorkingDirectory = _startInfo.WorkingDirectory;

            if (_startInfo.Environment is { Count: > 0 })
                foreach (var (k, v) in _startInfo.Environment)
                    psi.Environment[k] = v;

            try
            {
                _process = new McpProcessClient(psi);
                return _process;
            }
            catch
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_process is not null)
                return _process.DisposeAsync();
            if (_transport is not null)
                return _transport.DisposeAsync();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        ///     Synchronous teardown for sync contracts (e.g. <c>IMcpRegistry.Unregister</c>).
        ///     The stdio process is torn down synchronously; the remote transport is
        ///     abandoned (its HttpClient is finalized) — no sync-over-async bridging.
        /// </summary>
        public void DisposeSync()
        {
            _process?.DisposeSync();
            _transport = null;
        }
    }
}
