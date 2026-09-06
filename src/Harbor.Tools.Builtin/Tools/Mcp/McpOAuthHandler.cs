using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Per-server OAuth state machine: serve a cached/fresh access token to
///     transports, refresh when stale, and run the interactive browser login
///     (loopback redirect) on demand. Loopback uses a raw
///     <see cref="TcpListener" /> with minimal HTTP parsing — no
///     <c>HttpListener</c> URL-ACL quirks on any OS.
/// </summary>
public sealed class McpOAuthHandler
{
    private readonly string _server;
    private readonly Uri _serverUrl;
    private readonly McpOAuthConfig _config;
    private readonly McpOAuthTokenCache _cache;
    private readonly Func<HttpClient> _httpFactory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Construct a per-server OAuth handler.
    /// </summary>
    /// <param name="server">Registered server name (token-cache key).</param>
    /// <param name="serverUrl">MCP endpoint URL (discovery base).</param>
    /// <param name="config">OAuth settings from mcp.json.</param>
    /// <param name="cache">Token cache (defaults to the harbor home).</param>
    /// <param name="httpFactory">HTTP factory (tests inject a stubbed client).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public McpOAuthHandler(
        string server,
        Uri serverUrl,
        McpOAuthConfig config,
        McpOAuthTokenCache? cache = null,
        Func<HttpClient>? httpFactory = null,
        ILogger? logger = null)
    {
        _server = server;
        _serverUrl = serverUrl;
        _config = config;
        _cache = cache ?? new McpOAuthTokenCache();
        _httpFactory = httpFactory ?? (() => new HttpClient());
        _logger = logger;
    }

    /// <summary>
    ///     Valid access token for transports: cache hit, else refresh-token
    ///     flow. Throws <see cref="McpOAuthLoginRequiredException" /> when
    ///     interactive login is needed (transports surface the message; the
    ///     user runs <c>harbor mcp login &lt;server&gt;</c>).
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = _cache.Load(_server);
            if (cached is not null && !cached.IsExpired(DateTimeOffset.UtcNow))
                return cached.AccessToken;

            if (cached?.RefreshToken is { Length: > 0 } refresh)
            {
                var refreshed = await TryRefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
                if (refreshed is not null)
                    return refreshed;
            }

            throw new McpOAuthLoginRequiredException(_server);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Null-tolerant variant for transports: null when login is required.</summary>
    public async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (McpOAuthLoginRequiredException)
        {
            return null;
        }
    }

    private async Task<string?> TryRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var endpoints = await McpOAuthFlow.DiscoverAsync(_httpFactory(), _serverUrl, _config, ct).ConfigureAwait(false);
        if (endpoints.TokenEndpoint is null)
            return null;
        string clientId = _config.ClientId ?? "harbor-mcp";
        var result = await McpOAuthFlow.RefreshAsync(
            _httpFactory(), endpoints.TokenEndpoint, clientId, _config.ClientSecret, refreshToken, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger?.LogWarning("MCP OAuth refresh failed for '{Server}': {Error}", _server, result.Error);
            return null;
        }

        _cache.Save(_server, result.Value);
        return result.Value.AccessToken;
    }

    /// <summary>
    ///     Interactive login: dynamic registration (when needed), browser
    ///     authorize URL, loopback code capture, code exchange + cache.
    ///     Returns a human-readable summary line.
    /// </summary>
    /// <param name="openBrowser">Opens the authorize URL (tests stub it).</param>
    /// <param name="waitForCodeAsync">Waits for the loopback redirect (tests stub it).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result<string>> LoginAsync(
        Func<string, Task>? openBrowser = null,
        Func<string, string, Task<string?>>? waitForCodeAsync = null,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await McpOAuthFlow.DiscoverAsync(_httpFactory(), _serverUrl, _config, cancellationToken).ConfigureAwait(false);
        if (endpoints.AuthorizationEndpoint is null || endpoints.TokenEndpoint is null)
            return Result.Failure<string>(
                $"Cannot OAuth-login '{_server}': no authorization/token endpoint discovered and none configured. " +
                "Add explicit authorizationEndpoint/tokenEndpoint to the server's auth block in mcp.json.");

        string clientId = _config.ClientId ?? string.Empty;
        if (string.IsNullOrEmpty(clientId) && endpoints.RegistrationEndpoint is not null)
        {
            string redirectProbe = LoopbackRedirectUri(0);
            string? registered = await McpOAuthFlow.RegisterClientAsync(
                _httpFactory(), endpoints.RegistrationEndpoint, redirectProbe, _config.Scopes, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(registered))
            {
                clientId = registered;
                _logger?.LogInformation("MCP OAuth dynamic registration for '{Server}' yielded a client id", _server);
            }
        }

        if (string.IsNullOrEmpty(clientId))
            clientId = "harbor-mcp";

        var (verifier, challenge) = McpOAuthFlow.NewPkcePair();
        string state = McpOAuthFlow.NewState();
        var listener = new McpLoopbackListener(_config.RedirectPort);
        string redirectUri = listener.RedirectUri;
        string url = McpOAuthFlow.BuildAuthorizationUrl(
            endpoints.AuthorizationEndpoint, clientId, redirectUri, challenge, state, _config.Scopes);

        try
        {
            if (openBrowser is not null)
                await openBrowser(url).ConfigureAwait(false);
            else
                OpenBrowser(url);

            string? code = waitForCodeAsync is not null
                ? await waitForCodeAsync(redirectUri, state).ConfigureAwait(false)
                : await listener.WaitForCodeAsync(state, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(code))
                return Result.Failure<string>("OAuth login timed out waiting for the browser redirect.");

            var exchanged = await McpOAuthFlow.ExchangeCodeAsync(
                _httpFactory(), endpoints.TokenEndpoint, clientId, _config.ClientSecret,
                code, redirectUri, verifier, cancellationToken).ConfigureAwait(false);
            if (exchanged.IsFailure)
                return Result.Failure<string>($"OAuth code exchange failed: {exchanged.Error}");

            _cache.Save(_server, exchanged.Value);
            return Result.Success($"Logged in to MCP server '{_server}' (token cached, expires {exchanged.Value.ExpiresAtUtc:u}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<string>($"OAuth login failed: {ex.Message}");
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drop cached tokens (logout).</summary>
    public void Logout() => _cache.Clear(_server);

    private static string LoopbackRedirectUri(int port) => $"http://127.0.0.1:{port}/callback";

    private void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not open browser for MCP OAuth login; visit {Url} manually", url);
            throw new InvalidOperationException(
                $"Could not open a browser automatically. Visit this URL manually: {url}");
        }
    }
}

/// <summary>Thrown when a transport needs a token but no usable one exists.</summary>
public sealed class McpOAuthLoginRequiredException : InvalidOperationException
{
    /// <summary>Initialize with a default message.</summary>
    public McpOAuthLoginRequiredException()
        : this(string.Empty)
    {
    }

    /// <summary>Initialize with the registered server name.</summary>
    public McpOAuthLoginRequiredException(string server)
        : base($"MCP server '{server}' needs OAuth login. Run: harbor mcp login {server}")
    {
        Server = server;
    }

    /// <summary>Initialize with the registered server name and inner exception.</summary>
    public McpOAuthLoginRequiredException(string server, Exception innerException)
        : base($"MCP server '{server}' needs OAuth login. Run: harbor mcp login {server}", innerException)
    {
        Server = server;
    }

    /// <summary>Registered server name.</summary>
    public string Server { get; }
}

/// <summary>
///     Minimal loopback HTTP listener for the OAuth redirect: accepts one
///     connection, parses the request line for <c>?code=…&amp;state=…</c>,
///     answers with a static page, validates state.
/// </summary>
public sealed class McpLoopbackListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private bool _disposed;

    /// <summary>
    ///     Construct a loopback listener for the OAuth redirect.
    /// </summary>
    /// <param name="port">Fixed port, or 0 for any free port.</param>
    public McpLoopbackListener(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback";
    }

    /// <summary>Redirect URI to register with the OAuth server.</summary>
    public string RedirectUri { get; }

    /// <summary>Wait for one redirect carrying a state-matched code (60s cap).</summary>
    public async Task<string?> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            string request = Encoding.ASCII.GetString(buffer, 0, read);
            string? code = ParseQuery(request, "code", expectedState);
            string body = code is not null
                ? "<html><body><h1>Harbor: login complete — return to the terminal.</h1></body></html>"
                : "<html><body><h1>Harbor: login failed (state mismatch) — retry.</h1></body></html>";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, timeout.Token).ConfigureAwait(false);
            return code;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public static string? ParseQuery(string requestLine, string key, string expectedState)
    {
        // Request line: GET /callback?code=..&state=.. HTTP/1.1
        int q = requestLine.IndexOf('?');
        int sp = requestLine.IndexOf(' ', q < 0 ? 0 : q);
        if (q < 0 || sp < 0)
            return null;
        string? code = null;
        string? state = null;
        foreach (string pair in requestLine.Substring(q + 1, sp - q - 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0)
                continue;
            string k = Uri.UnescapeDataString(pair[..eq]);
            string v = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (k == "code") code = v;
            else if (k == "state") state = v;
        }

        return state == expectedState ? code : null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
