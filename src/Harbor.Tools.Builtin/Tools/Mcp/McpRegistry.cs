using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

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

        var parsed = McpArgvParser.ParseCommand(stdioCommand);
        if (parsed.IsFailure)
            return Result.Failure(parsed.Error);

        var tokens = parsed.Value;
        return RegisterInternal(name, new McpServerStartInfo { Command = tokens[0], Args = tokens[1..] });
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

        return RegisterInternal(name, startInfo);
    }

    private Result RegisterInternal(string name, McpServerStartInfo startInfo)
    {
        if (_servers.ContainsKey(name))
            return Result.Failure($"MCP server '{name}' is already registered.");

        _servers[name] = new ServerEntry(startInfo);
        _logger?.LogInformation("Registered MCP server: {Name} -> {Command}", name, startInfo.Command);
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
                    var legacyResult = Register(name, value.GetString() ?? string.Empty);
                    if (legacyResult.IsSuccess)
                        loaded++;
                    else
                        _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, legacyResult.Error);
                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object)
                {
                    _logger?.LogWarning("MCP server '{Name}' config is not an object", name);
                    continue;
                }

                if (!value.TryGetProperty("command", out var commandEl) || commandEl.ValueKind != JsonValueKind.String)
                {
                    _logger?.LogWarning("MCP server '{Name}' missing 'command' string in config", name);
                    continue;
                }

                if (value.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True)
                {
                    _logger?.LogInformation("MCP server '{Name}' is disabled; skipping", name);
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

                var startInfo = new McpServerStartInfo
                {
                    Command = command,
                    Args = args,
                    WorkingDirectory = cwd,
                    Environment = env
                };

                var result = RegisterInternal(name, startInfo);
                if (result.IsSuccess)
                    loaded++;
                else
                    _logger?.LogWarning("Failed to register MCP server '{Name}': {Error}", name, result.Error);
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
            entry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _logger?.LogInformation("Unregistered MCP server: {Name}", name);
            return Result.Success();
        }
        return Result.Failure($"MCP server '{name}' is not registered.");
    }

    public IReadOnlyList<string> GetServerNames() => _servers.Keys.ToArray();

    public async Task<Result<string>> InvokeAsync(string server, string method, JsonElement args, CancellationToken cancellationToken = default)
    {
        if (!_servers.TryGetValue(server, out var entry))
            return Result.Failure<string>($"MCP server '{server}' is not registered.");

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

            if (response.RootElement.TryGetProperty("error", out var error))
            {
                string msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown" : "unknown";
                return Result.Failure<string>($"MCP error from '{server}': {msg}");
            }

            return Result.Success(response.RootElement.GetProperty("result").GetRawText());
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"MCP call to '{server}.{method}' failed: {ex.Message}");
        }
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
        private readonly McpServerStartInfo _startInfo;
        private McpProcessClient? _process;

        public ServerEntry(McpServerStartInfo startInfo) => _startInfo = startInfo;

        public McpProcessClient? GetProcess()
        {
            if (_process is { HasExited: false }) return _process;
            _process?.DisposeAsync().AsTask().GetAwaiter().GetResult();

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
            return ValueTask.CompletedTask;
        }
    }
}
