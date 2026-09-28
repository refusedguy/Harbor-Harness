using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Everything a remote MCP transport needs to open one client connection.
///     Handed to <see cref="IMcpTransportFactory.Create" /> so
///     <see cref="McpRegistry" /> never learns a transport's constructor
///     signature (#477).
/// </summary>
/// <param name="Endpoint">Absolute http(s) endpoint of the remote MCP server.</param>
/// <param name="Headers">
///     Extra request headers; an explicit <c>Authorization</c> entry wins over
///     <paramref name="OAuthTokenProvider" />.
/// </param>
/// <param name="OAuthTokenProvider">
///     Bearer-token supplier, or null for an unauthenticated endpoint.
/// </param>
/// <param name="Logger">Optional logger the transport should report through.</param>
public sealed record McpTransportRequest(
    Uri Endpoint,
    IReadOnlyDictionary<string, string>? Headers,
    Func<CancellationToken, Task<string?>>? OAuthTokenProvider,
    ILogger? Logger);

/// <summary>
///     Strategy for one remote MCP transport kind (#477). <see cref="McpRegistry" />
///     resolves a factory by <see cref="Name" /> instead of branching on the
///     transport string, so a new transport is a registration, not an edit to
///     the registry.
/// </summary>
public interface IMcpTransportFactory
{
    /// <summary>
    ///     Config name of the transport kind (<c>"http"</c>, <c>"sse"</c>, …).
    ///     Matched case-insensitively against the mcp.json <c>transport</c> field.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Build a transport for <paramref name="request" />, or
    ///     <c>Failure</c> when it cannot be created — the failure is surfaced
    ///     to the caller, never silently downgraded to another transport.
    /// </summary>
    Result<IMcpRemoteTransport> Create(McpTransportRequest request);
}

/// <summary>Streamable-HTTP transport kind (<c>"http"</c>, the mcp.json default).</summary>
public sealed class McpHttpTransportFactory : IMcpTransportFactory
{
    /// <inheritdoc />
    public string Name => McpTransportNames.Http;

    /// <inheritdoc />
    public Result<IMcpRemoteTransport> Create(McpTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Result.Success<IMcpRemoteTransport>(new McpHttpTransport(
            request.Endpoint, request.Headers, request.OAuthTokenProvider, request.Logger));
    }
}

/// <summary>Legacy HTTP+SSE transport kind (<c>"sse"</c>).</summary>
public sealed class McpSseTransportFactory : IMcpTransportFactory
{
    /// <inheritdoc />
    public string Name => McpTransportNames.Sse;

    /// <inheritdoc />
    public Result<IMcpRemoteTransport> Create(McpTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Result.Success<IMcpRemoteTransport>(new McpSseTransport(
            request.Endpoint, request.Headers, request.OAuthTokenProvider, request.Logger));
    }
}

/// <summary>Canonical names of the two builtin remote MCP transport kinds.</summary>
public static class McpTransportNames
{
    /// <summary>Streamable HTTP (MCP spec 2025-03-26) — the default when mcp.json omits <c>transport</c>.</summary>
    public const string Http = "http";

    /// <summary>Legacy HTTP+SSE.</summary>
    public const string Sse = "sse";
}

/// <summary>
///     Name → factory table for remote MCP transports (#477). Frozen at
///     construction, so the set of accepted <c>transport</c> values and the
///     dispatch that builds one are the same seam: an unknown name can never
///     reach the dispatch and fall through to a default transport.
/// </summary>
public sealed class McpTransportResolver
{
    /// <summary>The builtin transports (streamable HTTP + legacy SSE).</summary>
    public static McpTransportResolver Default { get; } = Compose(null);

    private readonly FrozenDictionary<string, IMcpTransportFactory> _byName;
    private readonly string _supported;

    /// <summary>
    ///     Build a resolver over exactly <paramref name="factories" />. A blank
    ///     or duplicate <see cref="IMcpTransportFactory.Name" /> is a
    ///     composition bug and throws here rather than shadowing a transport
    ///     at call time.
    /// </summary>
    public McpTransportResolver(IEnumerable<IMcpTransportFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        var byName = new Dictionary<string, IMcpTransportFactory>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var ordered = new List<IMcpTransportFactory>();
        foreach (IMcpTransportFactory factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory);
            string name = factory.Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    $"MCP transport factory {factory.GetType().Name} has a blank Name.", nameof(factories));
            string canonical = name.Trim().ToLowerInvariant();
            if (!byName.TryAdd(canonical, factory))
                throw new ArgumentException(
                    $"MCP transport '{canonical}' is registered twice ({factory.GetType().Name}).", nameof(factories));

            names.Add(canonical);
            ordered.Add(factory);
        }

        Factories = ordered;
        SupportedNames = names;
        _byName = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _supported = string.Join(", ", names);
    }

    /// <summary>The registered factories, in registration order.</summary>
    public IReadOnlyList<IMcpTransportFactory> Factories { get; }

    /// <summary>Canonical (lowercase) names accepted in the <c>transport</c> config field.</summary>
    public IReadOnlyList<string> SupportedNames { get; }

    /// <summary>
    ///     The builtins plus <paramref name="extraTransports" />. Extras
    ///     registered under a builtin name are rejected by the constructor —
    ///     shadowing a built-in transport silently is the bug this seam exists
    ///     to prevent.
    /// </summary>
    public static McpTransportResolver Compose(IEnumerable<IMcpTransportFactory>? extraTransports = null)
    {
        var all = new List<IMcpTransportFactory>(2)
        {
            new McpHttpTransportFactory(),
            new McpSseTransportFactory(),
        };
        if (extraTransports is not null)
            all.AddRange(extraTransports);
        return new McpTransportResolver(all);
    }

    /// <summary>
    ///     Validate a config transport name and return its canonical form.
    ///     <c>Failure</c> for an unknown name — the caller must not substitute
    ///     a default.
    /// </summary>
    public Result<string> Canonicalize(string? transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
            return Result.Failure<string>(Unsupported(transport ?? "(null)"));

        string name = transport.Trim();
        if (!_byName.TryGetValue(name, out IMcpTransportFactory? factory))
            return Result.Failure<string>(Unsupported(transport));

        return Result.Success(factory.Name.Trim().ToLowerInvariant());
    }

    /// <summary>
    ///     Build the transport registered under <paramref name="canonicalName" />
    ///     (a name already returned by <see cref="Canonicalize" />). An unknown
    ///     name is a <c>Failure</c>, not a default transport.
    /// </summary>
    public Result<IMcpRemoteTransport> Create(string canonicalName, McpTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_byName.TryGetValue(canonicalName, out IMcpTransportFactory? factory))
            return Result.Failure<IMcpRemoteTransport>(Unsupported(canonicalName));

        return factory.Create(request);
    }

    private string Unsupported(string transport)
        => $"MCP transport '{transport}' is not supported (supported: {_supported}).";
}
