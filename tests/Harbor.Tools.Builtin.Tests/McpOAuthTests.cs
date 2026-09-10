using System.Net;
using System.Text;
using System.Text.Json;
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

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private static HttpClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHandler(respond));

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
    public async Task Handler_CacheHit_ReturnsToken_WithoutHttp()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("cached", null, DateTimeOffset.UtcNow.AddHours(1)));
        bool called = false;
        var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache,
            () => { called = true; return new HttpClient(new StubHandler(_ => Json(new { }))); });

        string token = await handler.GetAccessTokenAsync();

        await Assert.That(token).IsEqualTo("cached");
        await Assert.That(called).IsFalse();
    }

    [Test]
    public async Task Handler_NoToken_ThrowsLoginRequired()
    {
        var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), new McpOAuthTokenCache(_root));

        await Assert.That(async () => await handler.GetAccessTokenAsync())
            .Throws<McpOAuthLoginRequiredException>();
    }

    [Test]
    public async Task Handler_ExpiredWithRefresh_RefreshesAndCaches()
    {
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("old", "rt", DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var http = StubClient(_ => Json(new { access_token = "fresh", expires_in = 3600 }));
        var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig { TokenEndpoint = "https://x/token", ClientId = "cid" },
            cache, () => http);

        string token = await handler.GetAccessTokenAsync();

        await Assert.That(token).IsEqualTo("fresh");
        await Assert.That(cache.Load("srv")!.AccessToken).IsEqualTo("fresh");
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
        var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
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
