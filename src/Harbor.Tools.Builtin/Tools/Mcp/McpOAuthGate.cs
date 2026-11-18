using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     The OAuth half of a registered remote MCP server. It owns the lazily
///     created <see cref="McpOAuthHandler" /> and the #566 boundary that turns
///     "no token" into an actionable login hint when the server declared an
///     <c>auth</c> block. The caller keeps the decision about whether absence
///     is fatal: a server without an <c>auth</c> block sends requests
///     unauthenticated when the legacy environment fallback is unset.
///     <para>
///     Extracted from <c>McpRegistry.ServerEntry</c> (#2609) so the token rule
///     can be exercised without registering a server or opening a transport.
///     The registry still decides when to build the gate and which token
///     provider to hand to the transport.
///     </para>
/// </summary>
internal sealed class McpOAuthGate : IDisposable
{
    private readonly string _serverName;
    private readonly McpRemoteEndpoint _remote;
    private McpOAuthHandler? _oauth;

    public McpOAuthGate(string serverName, McpRemoteEndpoint remote)
    {
        _serverName = serverName;
        _remote = remote;
    }

    /// <summary>
    ///     Resolve one access token for a transport round trip. The handler
    ///     reports absence as <c>Maybe.None</c> because it cannot know whether a
    ///     missing token is fatal for the server. The gate knows the server
    ///     declared <c>auth</c>, so it converts absence into the login hint the
    ///     user needs. A rejected grant is already a failure and passes through
    ///     untouched.
    /// </summary>
    public async Task<Result<Maybe<string>>> RequireTokenAsync(
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        McpOAuthHandler oauth = OAuthFor(logger);
        Result<Maybe<string>> token =
            await oauth.TryGetAccessTokenResultAsync(cancellationToken).ConfigureAwait(false);
        if (token.IsFailure)
        {
            return token;
        }

        return token.Value.HasValue
            ? token
            : Result.Failure<Maybe<string>>(oauth.LoginHint);
    }

    private McpOAuthHandler OAuthFor(ILogger? logger)
    {
        if (_oauth is null)
        {
            Uri endpoint = new(_remote.Url, UriKind.Absolute);
            _oauth = new McpOAuthHandler(_serverName, endpoint, _remote.OAuth!, logger: logger);
        }

        return _oauth;
    }

    public void Dispose()
    {
        _oauth?.Dispose();
        _oauth = null;
    }
}
