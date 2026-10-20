using System.Net;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.TestKit;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for the MCP OAuth2 layer — config parsing, discovery, PKCE URL
///     building, code exchange / refresh against a stubbed wire, token cache
///     round-trips, handler cache/refresh/login-hint paths, loopback query
///     parsing, and remote-entry loading through registry + loader.
/// </summary>
public class McpOAuthTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-mcp-oauth").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    /// <summary>
    ///     A stub handler that records its own disposal, so a test can SEE the
    ///     client being torn down with the component that owns it.
    /// </summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Json(new { }));
    }

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private static HttpClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHttpMessageHandler(respond));

    [Test]
    public async Task OAuthConfig_Parse_ReadsAuthBlock()
    {
        using var doc = JsonDocument.Parse("""
            {"url": "https://mcp.example.com/mcp", "transport": "sse",
             "auth": {"clientId": "cid", "scopes": ["a", "b"], "tokenEndpoint": "https://x/token"}}
            """);
        var cfg = McpOAuthConfig.Parse(doc.RootElement);

        await Assert.That(cfg).IsNotNull();
        await Assert.That(cfg!.ClientId).IsEqualTo("cid");
        await Assert.That(cfg.Scopes).IsEquivalentTo(["a", "b"]);
        await Assert.That(cfg.TokenEndpoint).IsEqualTo("https://x/token");
    }

    [Test]
    public async Task OAuthConfig_Parse_MissingAuth_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"url": "https://mcp.example.com/mcp"}""");
        await Assert.That(McpOAuthConfig.Parse(doc.RootElement)).IsNull();
    }

    [Test]
    public async Task Flow_DiscoverAsync_PrefersExplicitEndpoints_NoHttp()
    {
        bool called = false;
        using var http = StubClient(_ => { called = true; return Json(new { }); });
        var cfg = new McpOAuthConfig { AuthorizationEndpoint = "https://x/auth", TokenEndpoint = "https://x/token" };

        var endpoints = await McpOAuthFlow.DiscoverAsync(http, new Uri("https://mcp.example.com/mcp"), cfg);

        await Assert.That(called).IsFalse();
        await Assert.That(endpoints.AuthorizationEndpoint).IsEqualTo("https://x/auth");
        await Assert.That(endpoints.TokenEndpoint).IsEqualTo("https://x/token");
    }

    [Test]
    public async Task Flow_DiscoverAsync_ReadsWellKnownMetadata()
    {
        using var http = StubClient(req => req.RequestUri!.AbsolutePath.Contains(".well-known")
            ? Json(new { authorization_endpoint = "https://x/auth", token_endpoint = "https://x/token" })
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var endpoints = await McpOAuthFlow.DiscoverAsync(
            http, new Uri("https://mcp.example.com/mcp"), new McpOAuthConfig());

        await Assert.That(endpoints.AuthorizationEndpoint).IsEqualTo("https://x/auth");
        await Assert.That(endpoints.TokenEndpoint).IsEqualTo("https://x/token");
    }

    [Test]
    public async Task Flow_BuildAuthorizationUrl_HasPkceAndScopes()
    {
        string url = McpOAuthFlow.BuildAuthorizationUrl(
            "https://x/auth", "cid", "http://127.0.0.1:9/callback", "challenge", "state1", ["a b"]);

        await Assert.That(url).Contains("response_type=code");
        await Assert.That(url).Contains("code_challenge=challenge");
        await Assert.That(url).Contains("code_challenge_method=S256");
        await Assert.That(url).Contains("state=state1");
        await Assert.That(url).Contains("scope=a%20b");
    }

    [Test]
    public async Task Flow_PkcePair_VerifierAndChallengeDiffer()
    {
        var (verifier, challenge) = McpOAuthFlow.NewPkcePair();
        await Assert.That(verifier.Length).IsGreaterThanOrEqualTo(43);
        await Assert.That(challenge).IsNotEqualTo(verifier);
        await Assert.That(McpOAuthFlow.NewState()).IsNotEqualTo(McpOAuthFlow.NewState());
    }

    [Test]
    public async Task Flow_ExchangeCodeAsync_ReturnsTokens()
    {
        using var http = StubClient(_ => Json(new { access_token = "at", refresh_token = "rt", expires_in = 100 }));
        var result = await McpOAuthFlow.ExchangeCodeAsync(
            http, "https://x/token", "cid", null, "code", "http://127.0.0.1:9/callback", "verifier");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.AccessToken).IsEqualTo("at");
        await Assert.That(result.Value.RefreshToken).IsEqualTo("rt");
    }

    [Test]
    public async Task Flow_ExchangeCodeAsync_ErrorStatus_ReturnsFailure()
    {
        using var http = StubClient(_ => Json(new { error = "bad" }, HttpStatusCode.BadRequest));
        var result = await McpOAuthFlow.ExchangeCodeAsync(
            http, "https://x/token", "cid", null, "code", "http://127.0.0.1:9/callback", "verifier");

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Flow_RefreshAsync_SendsRefreshGrant()
    {
        string? grant = null;
        using var http = StubClient(req =>
        {
            string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            grant = body;
            return Json(new { access_token = "at2", expires_in = 50 });
        });
        var result = await McpOAuthFlow.RefreshAsync(http, "https://x/token", "cid", null, "rt");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(grant).Contains("grant_type=refresh_token");
        await Assert.That(grant).Contains("refresh_token=rt");
    }

    [Test]
    public async Task TokenCache_SaveLoad_RoundTrips()
    {
        var cache = new McpOAuthTokenCache(_root);
        var tokens = new McpOAuthTokens("at", "rt", DateTimeOffset.UtcNow.AddHours(1));
        cache.Save("srv", tokens);

        var loaded = cache.Load("srv");
        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.AccessToken).IsEqualTo("at");
        await Assert.That(loaded.IsExpired(DateTimeOffset.UtcNow)).IsFalse();
    }

    [Test]
    public async Task TokenCache_MissingOrCorrupt_ReturnsNull()
    {
        var cache = new McpOAuthTokenCache(_root);
        await Assert.That(cache.Load("nope")).IsNull();
        File.WriteAllText(Path.Combine(_root, "mcp-oauth-bad.json"), "{oops");
        await Assert.That(cache.Load("bad")).IsNull();
    }

    [Test]
    public async Task TokenCache_ExpiredToken_Detected()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("at", null, DateTimeOffset.UtcNow.AddMinutes(-5)));
        await Assert.That(cache.Load("srv")!.IsExpired(DateTimeOffset.UtcNow)).IsTrue();
    }

    [Test]
    public async Task Handler_Logout_ThenLookup_IsAbsenceAgain()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("at", null, DateTimeOffset.UtcNow.AddHours(1)));
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache);

        handler.Logout();

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task Handler_CacheHit_ReturnsToken_WithoutHttp()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("cached", null, DateTimeOffset.UtcNow.AddHours(1)));
        int requests = 0;
        using var http = StubClient(_ =>
        {
            requests++;
            return Json(new { });
        });
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache, () => http);

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasValue).IsTrue();
        await Assert.That(result.Value.GetValueOrDefault()).IsEqualTo("cached");

        // The property is "a cache hit touches the network ZERO times" — not
        // "the client was never built". #1854 moved the client from per-call to
        // per-handler, so the client exists from the start; what must not
        // happen is a REQUEST.
        await Assert.That(requests).IsEqualTo(0).Because(
            "a cached token is served from disk: no request may reach the authorization server. "
            + "The client exists (one per handler, #1854); the wire must stay quiet.");
    }

    /// <summary>
    ///     #1854: the handler keeps ONE client. The factory is called once,
    ///     however many OAuth calls run — before the fix it was called at every
    ///     call site, so each refresh minted a fresh
    ///     <see cref="HttpClient" />, a fresh
    ///     <see cref="System.Net.Http.SocketsHttpHandler" /> and a fresh
    ///     connection pool, none of them ever disposed.
    /// </summary>
    [Test]
    public async Task Handler_ReusesOneClient_AcrossCalls()
    {
        int factoryCalls = 0;
        using var http = StubClient(_ => Json(new { access_token = "fresh", expires_in = 3600 }));
        var cache = new McpOAuthTokenCache(_root);

        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig { TokenEndpoint = "https://x/token", ClientId = "cid" },
            cache, () => { factoryCalls++; return http; });

        // Two refreshes, not one: the cache is re-armed with an expired token
        // before each call, so both calls reach the token endpoint. A second
        // call that found a fresh token would prove nothing.
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        Result<Maybe<string>> first = await handler.TryGetAccessTokenResultAsync();
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        Result<Maybe<string>> second = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(first.Value.GetValueOrDefault()).IsEqualTo("fresh");
        await Assert.That(second.Value.GetValueOrDefault()).IsEqualTo("fresh");

        await Assert.That(factoryCalls).IsEqualTo(1).Because(
            "the handler speaks over ONE client: the factory is called once, however many OAuth calls "
            + "run. Before #1854 it was called at each of the five call sites, so two refreshes meant "
            + "two HttpClients, two SocketsHttpHandlers and two connection pools — and nobody disposed "
            + "either of them.");
    }

    /// <summary>
    ///     #1854: the client belongs to the handler that speaks over it, and it
    ///     dies with it — the rule the remote transports' client factory already
    ///     follows (#1567). The old per-call clients were disposed by nobody.
    /// </summary>
    [Test]
    public async Task Handler_DisposesTheClientItSpeaksOver()
    {
        var tracking = new TrackingHandler();

        // disposeHandler: true so the client's teardown is visible through its
        // handler — the client does not dispose a handler it did not create.
        using var http = new HttpClient(tracking, disposeHandler: true);
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), new McpOAuthTokenCache(_root), () => http);

        await Assert.That(tracking.Disposed).IsFalse().Because(
            "the client is alive for as long as the handler that owns it.");

        handler.Dispose();

        await Assert.That(tracking.Disposed).IsTrue().Because(
            "the handler owns the client it speaks over, so disposing the handler disposes the client "
            + "and its handler — and the connection pool with it. A client that outlives its owner is "
            + "the leak #1854 was about.");
    }

    /// <summary>
    ///     #566: an absent token is absence, not failure. It used to throw, and
    ///     then to be a null, and then to be a Failure whose text the handler
    ///     itself had to re-parse to tell apart from a rejected grant.
    /// </summary>
    [Test]
    public async Task Handler_NoToken_IsAbsenceNotFailure()
    {
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), new McpOAuthTokenCache(_root));

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task Handler_ExpiredWithRefresh_RefreshesAndCaches()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var http = StubClient(_ => Json(new { access_token = "fresh", expires_in = 3600 }));
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig { TokenEndpoint = "https://x/token", ClientId = "cid" },
            cache, () => http);

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.GetValueOrDefault()).IsEqualTo("fresh");
        await Assert.That(cache.Load("srv")!.AccessToken).IsEqualTo("fresh");
    }

    /// <summary>
    ///     The other half of the boundary #566 draws: a token DID exist and the
    ///     authorization server rejected the refresh grant. That is an error, it
    ///     stays a <c>Failure</c>, and it must NOT be confused with "no token" —
    ///     which is why the outcome can no longer ride on the failure channel's
    ///     text.
    /// </summary>
    [Test]
    public async Task Handler_ExpiredWithRefresh_RejectedGrant_IsAFailure()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var http = StubClient(_ => Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest));
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig { TokenEndpoint = "https://x/token", ClientId = "cid" },
            cache, () => http);

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("refresh failed");
    }

    /// <summary>
    ///     A refresh token we cannot spend because the server never advertised a
    ///     token endpoint is absence, not a rejected grant — the two used to be
    ///     distinguished only by the <c>"LoginRequired:"</c> / <c>"RefreshFailed:"</c>
    ///     prefix this file no longer emits.
    /// </summary>
    [Test]
    public async Task Handler_ExpiredWithRefresh_NoTokenEndpoint_IsAbsence()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var http = StubClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache, () => http);

        Result<Maybe<string>> result = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasNoValue).IsTrue();
    }

    /// <summary>
    ///     The actionable half of absence. Exposed as its own member because the
    ///     lookup reports "no token" as <c>Maybe.None</c> and the caller decides
    ///     whether that is fatal for the server it is talking to (#566).
    /// </summary>
    /// <summary>
    ///     The three outcomes stay distinguishable at the type level, which is the
    ///     whole point of #566: a rejected grant is the only one that reaches the
    ///     failure channel, so a caller branching on <c>IsFailure</c> is not
    ///     guessing from the text.
    /// </summary>
    [Test]
    public async Task Handler_ThreeOutcomes_AreDistinguishableWithoutParsingText()
    {
        // 1. absence
        using var empty = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), new McpOAuthTokenCache(_root));
        Result<Maybe<string>> absent = await empty.TryGetAccessTokenResultAsync();

        // 2. a token
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("at", null, DateTimeOffset.UtcNow.AddHours(1)));
        using var loggedIn = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache);
        Result<Maybe<string>> present = await loggedIn.TryGetAccessTokenResultAsync();

        // 3. a rejected grant
        var staleCache = new McpOAuthTokenCache(_root);
        staleCache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var http = StubClient(_ => Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest));
        using var stale = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig { TokenEndpoint = "https://x/token", ClientId = "cid" },
            staleCache, () => http);
        Result<Maybe<string>> rejected = await stale.TryGetAccessTokenResultAsync();

        await Assert.That(absent.IsSuccess).IsTrue();
        await Assert.That(absent.Value.HasNoValue).IsTrue();

        await Assert.That(present.IsSuccess).IsTrue();
        await Assert.That(present.Value.HasValue).IsTrue();
        await Assert.That(present.Value.GetValueOrDefault()).IsEqualTo("at");

        await Assert.That(rejected.IsFailure).IsTrue();
    }

    [Test]
    public async Task Handler_LoginHint_NamesTheServerAndTheCommand()
    {
        using var handler = new McpOAuthHandler("cloud", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), new McpOAuthTokenCache(_root));

        await Assert.That(handler.LoginHint).Contains("cloud");
        await Assert.That(handler.LoginHint).Contains("harbor mcp login cloud");
    }

    [Test]
    public async Task Flow_RegisterClientResult_Unreachable_ReturnsFailure()
    {
        using var http = StubClient(_ => throw new HttpRequestException("down"));
        var result = await McpOAuthFlow.RegisterClientResultAsync(
            http, "https://x/register", "http://127.0.0.1:9/callback", ["a"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Unreachable");
    }

    [Test]
    public async Task Flow_RegisterClientResult_Reject_ReturnsNullSuccess()
    {
        using var http = StubClient(_ => Json(new { error = "rejected" }, HttpStatusCode.BadRequest));
        var result = await McpOAuthFlow.RegisterClientResultAsync(
            http, "https://x/register", "http://127.0.0.1:9/callback", ["a"]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task Loopback_ParseQueryResult_Malformed_ReturnsFailure()
    {
        var result = McpLoopbackListener.ParseQueryResult("GARBAGE", "code", "s1");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("MalformedCallback");
    }

    [Test]
    public async Task TokenCache_LoadResult_DistinguishesMissAndCorrupt()
    {
        var cache = new McpOAuthTokenCache(_root);
        var miss = cache.LoadResult("nope");
        await Assert.That(miss.IsFailure).IsTrue();
        await Assert.That(miss.Error).Contains("CacheMiss");

        File.WriteAllText(Path.Combine(_root, "mcp-oauth-bad2.json"), "{oops");
        var corrupt = cache.LoadResult("bad2");
        await Assert.That(corrupt.IsFailure).IsTrue();
        await Assert.That(corrupt.Error).Contains("CacheCorrupt");
    }



    [Test]
    public async Task OAuthConfig_ParseResult_MissingAuth_ReturnsNullSuccess()
    {
        using var doc = JsonDocument.Parse("""{"url": "https://mcp.example.com/mcp"}""");
        var result = McpOAuthConfig.ParseResult(doc.RootElement);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.HasNoValue).IsTrue();
    }

    [Test]
    public async Task Loopback_ParseQuery_ValidatesState()
    {
        await Assert.That(
            McpLoopbackListener.ParseQuery("GET /callback?code=abc&state=s1 HTTP/1.1", "code", "s1"))
            .IsEqualTo("abc");
        await Assert.That(
            McpLoopbackListener.ParseQuery("GET /callback?code=abc&state=evil HTTP/1.1", "code", "s1"))
            .IsNull();
    }

    [Test]
    public async Task Registry_RemoteWithAuth_ExposesRegistration()
    {
        using var loggerFactory = LoggerFactory.Create(b => { });
        var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>(), McpTransportResolver.Default);
        var oauth = new McpOAuthConfig { ClientId = "cid" };
        var registered = registry.Register("cloud", "https://mcp.example.com/mcp", "http", null, oauth);

        await Assert.That(registered.IsSuccess).IsTrue();
        var reg = registry.GetRemoteRegistration("cloud");
        await Assert.That(reg.IsSuccess).IsTrue();
        await Assert.That(reg.Value.OAuth!.ClientId).IsEqualTo("cid");
        await Assert.That(registry.GetRemoteRegistration("missing").IsFailure).IsTrue();
    }

    [Test]
    public async Task Loader_RemoteEntry_ParsesUrlTransportAuth()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
                {"mcpServers": {"cloud": {
                    "url": "https://mcp.example.com/mcp",
                    "transport": "sse",
                    "headers": {"X-Tenant": "t"},
                    "auth": {"clientId": "cid", "scopes": ["r"]}
                }}}
                """);
            var loader = new McpServersConfigLoader(_root);
            var entries = loader.Load(tempFile);

            await Assert.That(entries.Count).IsEqualTo(1);
            await Assert.That(entries[0].Remote).IsNotNull();
            await Assert.That(entries[0].Remote!.Url).IsEqualTo("https://mcp.example.com/mcp");
            await Assert.That(entries[0].Remote.Transport).IsEqualTo("sse");
            await Assert.That(entries[0].Remote.Headers!["X-Tenant"]).IsEqualTo("t");
            await Assert.That(entries[0].Remote.OAuth!.ClientId).IsEqualTo("cid");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
