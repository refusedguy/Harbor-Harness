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

    public Result Register(string name, string stdioCommand)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("Server name cannot be empty.");
        if (string.IsNullOrWhiteSpace(stdioCommand))
            return Result.Failure("stdioCommand cannot be empty.");
        if (_servers.ContainsKey(name))
            return Result.Failure($"MCP server '{name}' is already registered.");

        _servers[name] = new ServerEntry(stdioCommand);
        _logger?.LogInformation("Registered MCP server: {Name} -> {Command}", name, stdioCommand);
        return Result.Success();
    }

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

            int loaded = 0;
            foreach (var property in root.EnumerateObject())
            {
                string name = property.Name;
                JsonElement value = property.Value;

                string stdioCommand;
                if (value.ValueKind == JsonValueKind.String)
                {
                    stdioCommand = value.GetString() ?? string.Empty;
                }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    if (!value.TryGetProperty("command", out var commandEl) || commandEl.ValueKind != JsonValueKind.String)
                    {
                        _logger?.LogWarning("MCP server '{Name}' missing 'command' string in config", name);
                        continue;
                    }

                    var command = commandEl.GetString() ?? string.Empty;
                    if (value.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string> { command };
                        foreach (var arg in argsEl.EnumerateArray())
                        {
                            if (arg.ValueKind == JsonValueKind.String)
                                parts.Add(arg.GetString()!);
                            else
                                parts.Add(arg.GetRawText());
                        }
                        stdioCommand = string.Join(" ", parts);
                    }
                    else
                    {
                        stdioCommand = command;
                    }
                }
                else
                {
                    _logger?.LogWarning("MCP server '{Name}' has unsupported config type", name);
                    continue;
                }

                var result = Register(name, stdioCommand);
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
            await using var transport = new McpJsonRpcTransport(process.Stdin, process.Stdout);
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
        private readonly string _command;
        private McpProcessClient? _process;

        public ServerEntry(string command) => _command = command;

        public McpProcessClient? GetProcess()
        {
            if (_process is { HasExited: false }) return _process;
            _process?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            var parts = _command.AsSpan().Trim();
            var spaceIdx = parts.IndexOf(' ');
            string fileName, arguments;

            if (spaceIdx >= 0)
            {
                fileName = parts[..spaceIdx].ToString();
                arguments = parts[(spaceIdx + 1)..].ToString();
            }
            else
            {
                fileName = parts.ToString();
                arguments = string.Empty;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                _process = new McpProcessClient(startInfo);
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
