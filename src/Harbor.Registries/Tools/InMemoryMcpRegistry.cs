using System.Collections.Concurrent;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.Registries.Tools;
/// <summary>
///     In-memory <see cref="IMcpRegistry" />. Tracks registrations but always returns
///     <see cref="Result{T}.Failure" /> for <see cref="InvokeAsync" /> — a real MCP client
///     (stdio JSON-RPC over a child process) is a separate concern.
/// </summary>
/// <remarks>
///     This stub keeps the <c>mcp</c> builtin tool testable without spinning up an actual
///     MCP server. Production hosts replace this registration with a real implementation.
/// </remarks>
public sealed class InMemoryMcpRegistry : IMcpRegistry
{
    private readonly ILogger<InMemoryMcpRegistry> _logger;
    private readonly ConcurrentDictionary<string, string> _servers = new(StringComparer.Ordinal);

    /// <summary>
    ///     Construct an empty in-memory registry.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public InMemoryMcpRegistry(ILogger<InMemoryMcpRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Result Register(string name, string stdioCommand)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure("MCP server name cannot be empty.");
        if (string.IsNullOrWhiteSpace(stdioCommand))
            return Result.Failure("MCP server stdio command cannot be empty.");

        if (!_servers.TryAdd(name, stdioCommand))
            return Result.Failure($"MCP server '{name}' is already registered.");

        _logger.LogInformation("Registered MCP server {Name}", name);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result Unregister(string name)
    {
        if (!_servers.TryRemove(name, out _))
            return Result.Failure($"MCP server '{name}' is not registered.");
        return Result.Success();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetServerNames()
    {
        // Snapshot into a fresh array — ConcurrentDictionary.GetEnumerator is a snapshot
        // iterator but callers expect a stable list.
        string[] names = new string[_servers.Count];
        int i = 0;
        foreach (string k in _servers.Keys)
            names[i++] = k;
        return names;
    }

    /// <inheritdoc />
    public IReadOnlyList<McpServerInstructions> GetInstructions() =>
        Array.Empty<McpServerInstructions>();

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        string server,
        string method,
        JsonElement args,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            return Task.FromResult(Result.Failure<string>("MCP server name cannot be empty."));
        if (string.IsNullOrWhiteSpace(method))
            return Task.FromResult(Result.Failure<string>("MCP method cannot be empty."));

        if (!_servers.ContainsKey(server))
        {
            string available = _servers.IsEmpty
                ? "(no servers registered)"
                : string.Join(", ", _servers.Keys);
            return Task.FromResult(Result.Failure<string>(
                $"MCP server '{server}' is not registered. Available: {available}"));
        }

        _logger.LogWarning(
            "InMemoryMcpRegistry cannot actually invoke MCP methods — register a real IMcpRegistry implementation. " +
            "Server={Server} Method={Method}", server, method);

        return Task.FromResult(Result.Failure<string>(
            $"MCP server '{server}' is registered but no transport is wired. " +
            "Register a real IMcpRegistry implementation (e.g. stdio JSON-RPC client) to enable calls."));
    }
}
