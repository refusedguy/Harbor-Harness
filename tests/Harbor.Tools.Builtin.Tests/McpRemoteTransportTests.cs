using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Resilience;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Remote MCP transports (streamable HTTP + legacy SSE) and their registry
///     integration, verified against local fake MCP servers.
/// </summary>
/// <remarks>
///     #823: the two tests below write <c>HARBOR_MCP_OAUTH_TOKEN</c> and
///     <c>HARBOR_HOME</c> into the process environment, and the product reads both
///     back — the token in <c>McpRegistry.GetTransport</c>'s no-auth branch
///     (McpRegistry.cs:573), the home in <c>McpOAuthTokenCache.DefaultDirectory</c>.
///     <c>McpTransportFactoryTests</c> reaches that same branch through
///     <c>registry.InvokeAsync</c> and is not in the issue's list. Both carry
///     <c>process-env</c> now; neither held any key before, so the scheduler was
///     free to run them at the same time.
/// </remarks>
[NotInParallel("process-env")]
public class McpRemoteTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Drives a round-trip for tests that assert on what the SERVER observed
    ///     (headers, session id, body) rather than on the answer. Asserts the call
    ///     succeeded and disposes the document, so a test can no longer swallow a
    ///     transport failure by discarding the result (#587).
    /// </summary>
    private static async Task RoundTripAndAssertAsync(
        IMcpRemoteTransport transport,
        JsonElement request,
        int? expectedId)
    {
        Result<Maybe<JsonDocument>> result = await transport.TryRoundTripAsync(request, expectedId);
        await Assert.That(result.IsSuccess).IsTrue();
        result.Value.GetValueOrDefault()?.Dispose();
    }

    // ---------- Streamable HTTP ----------

    [Test]
    public async Task HttpTransport_JsonResponse_ReturnsMatchingFrame()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static body => $$"""{"jsonrpc":"2.0","id":{{JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32()}},"result":{"tools":[]} }""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":42,"method":"tools/list","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 42);
        using JsonDocument? response = roundTrip.Value.GetValueOrDefault();

        await Assert.That(roundTrip.IsSuccess).IsTrue();
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("id").GetInt32()).IsEqualTo(42);
        await Assert.That(server.RequestBodies).Contains(b => b.Contains("tools/list"));
    }

    [Test]
    public async Task HttpTransport_Persistent500_TryRoundTrip_ReturnsFailureWithStatus()
    {
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.AddRange([500, 500, 500, 500]);

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        Result<Maybe<JsonDocument>> result = await transport.TryRoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("500");
        await Assert.That(server.HandledRequests).IsEqualTo(3);
    }

    [Test]
    public async Task HttpTransport_Persistent500_RoundTrip_ReturnsNullWithoutThrowing()
    {
        // #587: "returns null" is no longer a representable outcome — the lossy
        // overload is gone from the seam, because null could not tell a 503 apart
        // from a 202-with-no-body. The guarantee this test was written for is
        // unchanged and restated in the shape that replaced it: a persistent 500
        // must not throw, must retry three times, and must come back as a Failure
        // carrying the status rather than as a document or a bare null.
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.AddRange([500, 500, 500, 500]);

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(roundTrip.IsSuccess).IsFalse();
        await Assert.That(server.HandledRequests).IsEqualTo(3);
    }

    [Test]
    public async Task HttpTransport_SseBodyWithoutMatchingFrame_TryRoundTrip_ReturnsFailure()
    {
        using FakeServer server = FakeServer.Start();
        server.SseResponseBody =
            """
            event: message
            data: {"jsonrpc":"2.0","id":999,"result":{"ok":true}}

            """;

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""");
        Result<Maybe<JsonDocument>> result = await transport.TryRoundTripAsync(request.RootElement.Clone(), 7);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no matching JSON-RPC frame");
    }
    [Test]
    public async Task HttpTransport_Transient500_IsRetried()
    {
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.AddRange([500, 200]);
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 1);
        using JsonDocument? response = roundTrip.Value.GetValueOrDefault();

        await Assert.That(roundTrip.IsSuccess).IsTrue();
        await Assert.That(response).IsNotNull();
        await Assert.That(server.HandledRequests).IsEqualTo(2);    }

    [Test]
    public async Task HttpTransport_OAuthToken_IsAttachedAsBearer()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url,
            oauthTokenProvider: _ => Task.FromResult(Result.Success(Maybe<string>.From("tok-123"))));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        await RoundTripAndAssertAsync(transport, request.RootElement.Clone(), 1);

        await Assert.That(server.AuthHeaders).Contains("Bearer tok-123");
    }

    /// <summary>
    ///     #566: a rejected grant is a failure and the transport's diagnostic
    ///     reaches the caller. Before the change the seam was
    ///     <c>Func&lt;…, Task&lt;string?&gt;&gt;</c>, so the only way a provider could
    ///     report an error was to throw and be swallowed by a catch-all into a
    ///     differently-worded string.
    /// </summary>
    [Test]
    public async Task HttpTransport_OAuthProviderFailure_SurfacesTheCause()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url,
            oauthTokenProvider: _ => Task.FromResult(
                Result.Failure<Maybe<string>>("grant rejected: invalid_grant")));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");

        Result<Maybe<JsonDocument>> result = await transport.TryRoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invalid_grant");
        // A rejected grant must not have spent a round-trip proving it.
        await Assert.That(server.HandledRequests).IsEqualTo(0);
    }

    /// <summary>
    ///     #566: absence is not failure. A provider that has no token yet must not
    ///     abort the round-trip — the request goes out unauthenticated, exactly
    ///     as it did when the seam spoke <c>Task&lt;string?&gt;</c> and the transport
    ///     turned the null into <c>Maybe.None</c> itself.
    /// </summary>
    [Test]
    public async Task HttpTransport_OAuthProviderNone_ProceedsUnauthenticated()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url,
            oauthTokenProvider: _ => Task.FromResult(Result.Success(Maybe<string>.None)));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");
        await RoundTripAndAssertAsync(transport, request.RootElement.Clone(), 1);

        await Assert.That(server.HandledRequests).IsGreaterThanOrEqualTo(1);
        foreach (string? header in server.AuthHeaders)
        {
            await Assert.That(header).IsNull();
        }
    }

    /// <summary>
    ///     A hand-written provider that throws is still contained rather than
    ///     escaping the transport — the catch-all is a floor, not a shape the
    ///     transport produces itself.
    /// </summary>
    [Test]
    public async Task HttpTransport_ThrowingOAuthProvider_BecomesAFailure()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url,
            oauthTokenProvider: _ => throw new InvalidOperationException("provider exploded"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""");

        Result<Maybe<JsonDocument>> result = await transport.TryRoundTripAsync(request.RootElement.Clone(), 1);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("provider exploded");
    }

    [Test]
    public async Task HttpTransport_SessionIdFromFirstResponse_IsReplayed()
    {
        using FakeServer server = FakeServer.Start();
        server.SessionIdToAssign = "sess-7";
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        await RoundTripAndAssertAsync(transport, request.RootElement.Clone(), 1);
        await RoundTripAndAssertAsync(transport, request.RootElement.Clone(), 1);

        await Assert.That(server.SessionIds.Skip(1)).Contains("sess-7");
    }

    [Test]
    public async Task HttpTransport_SseResponseStream_IsParsed()
    {
        using FakeServer server = FakeServer.Start();
        server.SseResponseBody =
            """
            event: message
            data: {"jsonrpc":"2.0","id":7,"result":{"ok":true}}

            """;

        await using var transport = new McpHttpTransport(server.Url);
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 7);
        using JsonDocument? response = roundTrip.Value.GetValueOrDefault();

        await Assert.That(roundTrip.IsSuccess).IsTrue();
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("result").GetProperty("ok").GetBoolean()).IsTrue();
    }

    // ---------- Legacy HTTP + SSE ----------

    [Test]
    public async Task SseTransport_EndpointAnnounce_ThenResponseFrame()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":3,"result":{"echo":true}}""";

        await using var transport = new McpSseTransport(new Uri(server.Url + "/sse"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 3);
        using JsonDocument? response = roundTrip.Value.GetValueOrDefault();

        await Assert.That(roundTrip.IsSuccess).IsTrue();
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.RootElement.GetProperty("result").GetProperty("echo").GetBoolean()).IsTrue();
        await Assert.That(server.RequestBodies.Any(b => b.Contains("tools/list"))).IsTrue();
    }

    // ---------- #714: the SSE retry loop was blind to the cause ----------

    /// <summary>
    ///     The regression this issue exists for. A missing key, a revoked token or a
    ///     wrong endpoint is not a blip: the same request will fail identically three
    ///     times, so retrying it is pure latency in front of a guaranteed error — and
    ///     for <c>401</c> it is three more chances for the provider to flag the key.
    ///     <para>
    ///     Asserted on what the SERVER counted, not on the error string: the string
    ///     already reported 401 before the fix (the status was flattened into it), so
    ///     asserting on it would have passed against the retrying transport. The
    ///     request count is the thing that was actually wrong.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SseTransport_UnauthorizedChannel_AsksOnceAndDoesNotRetry()
    {
        using FakeServer server = FakeServer.Start();
        server.GetStatusCode = 401;

        await using var transport = new McpSseTransport(new Uri(server.Url + "/sse"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 4);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(roundTrip.Error).Contains("401");
        await Assert.That(server.HandledRequests)
            .IsEqualTo(1)
            .Because("an unauthenticated channel cannot succeed on a second try — the key, not the socket, is missing");
    }

    /// <summary>
    ///     The same blind retry reached the message endpoint, one hop further along.
    ///     Two requests is the whole honest cost: open the channel, be refused the
    ///     POST. It was six — three full open-and-post round-trips.
    /// </summary>
    [Test]
    public async Task SseTransport_ForbiddenMessageEndpoint_OpensAndPostsOnceEach()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":5,"result":{"echo":true}}""";
        // More refusals than attempts, so the queue cannot run dry mid-retry and let
        // a later attempt succeed — that would hide the defect instead of exposing it.
        server.QueueStatusCodes.AddRange([403, 403, 403, 403, 403, 403]);

        await using var transport = new McpSseTransport(new Uri(server.Url + "/sse"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":5,"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 5);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(roundTrip.Error).Contains("403");
        await Assert.That(server.HandledRequests)
            .IsEqualTo(2)
            .Because("one GET to open the channel plus one POST that was refused; the retry only ever repeated both");
    }

    /// <summary>
    ///     The third blind-retry source, and the one the sibling transport already
    ///     got right: <c>McpHttpTransport</c> resolves the token before entering its
    ///     loop, so a rejected grant costs one attempt and zero requests. Here the
    ///     token was resolved per attempt inside the loop, so a grant that can never
    ///     be granted was asked for three times.
    /// </summary>
    [Test]
    public async Task SseTransport_RejectedOAuthGrant_IsNotRetried()
    {
        int tokenCalls = 0;
        await using var transport = new McpSseTransport(
            new Uri("http://127.0.0.1:1/sse"),
            oauthTokenProvider: _ =>
            {
                tokenCalls++;
                return Task.FromResult(Result.Failure<Maybe<string>>("invalid_grant"));
            });

        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":6,"method":"tools/list","params":{}}""");
        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 6);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(tokenCalls)
            .IsEqualTo(1)
            .Because("a rejected grant is an answer, not an outage — asking again cannot change it");
    }

    /// <summary>
    ///     The other half, and the reason the fix cannot simply be "stop retrying".
    ///     A 5xx on the channel IS a blip and must keep its three attempts — this
    ///     pins the transient set in place so a later reader cannot widen the new
    ///     terminal branch until retrying is dead everywhere.
    /// </summary>
    [Test]
    public async Task SseTransport_ServerErrorOnChannel_StillRetries()
    {
        using FakeServer server = FakeServer.Start();
        server.GetStatusCode = 503;

        await using var transport = new McpSseTransport(new Uri(server.Url + "/sse"));
        using var request = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), 7);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(roundTrip.Error).Contains("503");
        await Assert.That(server.HandledRequests)
            .IsEqualTo(TransientFailurePolicy.DefaultMaxAttempts)
            .Because("5xx is exactly the class of failure the retry budget exists for");
    }

    /// <summary>
    ///     #925, the transport-level half. The policy test above proves the set now
    ///     matches a bare <see cref="SocketException" />; this proves the set is
    ///     actually reached from the wire, where the defect was reported.
    ///     <para>
    ///         The bound is "re-dialled", not "re-dialled N times", and the
    ///         difference is deliberate. How many TCP connections one logical
    ///         attempt consumes depends on whether
    ///         <see cref="System.Net.Http.SocketsHttpHandler" /> wraps the socket
    ///         error or lets it through bare, and on the CI runner that split was
    ///         measured at 23 wrapped / 17 bare out of 40 — so pinning an exact
    ///         count would encode a coin flip as a contract. What is asserted is
    ///         the part the issue is about: the drop is retried rather than
    ///         reported on the first attempt, and the budget is still a ceiling.
    ///     </para>
    ///     <para>
    ///         One shape for both transports, because #925 is the same defect in
    ///         both loops reading the same owner, and a guard that only covered
    ///         one of them is the #572 duplication this file was written to end.
    ///         The caller names the transport so a failure says which one gave up.
    ///     </para>
    /// </summary>
    private static async Task AssertReDialsOnDroppedConnectionAsync(
        Func<Uri, IMcpRemoteTransport> build,
        string path,
        int requestId,
        string transportName)
    {
        using DeadServer server = DeadServer.Start();

        await using IMcpRemoteTransport transport = build(new Uri($"http://127.0.0.1:{server.Port}{path}"));
        using var request = JsonDocument.Parse(
            $$$"""{"jsonrpc":"2.0","id":{{{requestId}}},"method":"tools/list","params":{}}""");

        Result<Maybe<JsonDocument>> roundTrip = await transport.TryRoundTripAsync(request.RootElement.Clone(), requestId);

        await Assert.That(roundTrip.IsFailure).IsTrue();
        await Assert.That(server.ConnectionsAccepted)
            .IsGreaterThan(1)
            .Because(
                $"{transportName} shares the retry owner with its sibling, so a connection dropped below the HTTP layer "
                + "must be re-dialled there too. One dial means the status-less arm is not being reached: the user saw the "
                + "error instead of the reconnect the policy already pays for on the LLM path. The BCL hands this event "
                + "back both wrapped in HttpRequestException and as a bare SocketException, and the set has to answer for "
                + "both — before this, the bare shape fell through and the round-trip reported on its first attempt");
        await Assert.That(server.ConnectionsAccepted)
            .IsLessThanOrEqualTo(TransientFailurePolicy.DefaultMaxAttempts)
            .Because("the retry budget is a ceiling on attempts; a dropped connection must not buy attempts beyond it");
    }

    [Test]
    public Task SseTransport_DroppedConnection_ReDialsInsteadOfReportingOnTheFirstAttempt()
        => AssertReDialsOnDroppedConnectionAsync(
            uri => new McpSseTransport(uri, requestTimeout: TimeSpan.FromSeconds(5)),
            "/sse",
            8,
            "McpSseTransport");

    [Test]
    public Task HttpTransport_DroppedConnection_ReDialsInsteadOfReportingOnTheFirstAttempt()
        => AssertReDialsOnDroppedConnectionAsync(
            uri => new McpHttpTransport(uri, requestTimeout: TimeSpan.FromSeconds(5)),
            "/mcp",
            9,
            "McpHttpTransport");

    // ---------- Registry integration ----------

    [Test]
    public async Task Registry_RemoteUrlConfig_InvokesOverHttp()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static body =>
            $$"""{"jsonrpc":"2.0","id":{{JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32()}},"result":{"tools":[{"name":"echo"}] } }""";

        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, $$"""
                {
                    "mcpServers": {
                        "remote": { "url": "{{server.Url}}", "transport": "http" }
                    }
                }
                """);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
            await Assert.That(registry.RegisterFromConfig(tempFile).IsSuccess).IsTrue();

            using var args = JsonDocument.Parse("{}");
            Result<string> result = await registry.InvokeAsync("remote", "tools/list", args.RootElement);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).Contains("echo");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---------- #566: absence vs. a rejected grant, end to end ----------

    /// <summary>
    ///     The registry wires the handler directly, so an un-logged-in server
    ///     reaches the caller as a token <i>failure</i> carrying the actionable
    ///     hint rather than as a bare 401 from a request that was never going to
    ///     succeed. Before #566 the only copy of that hint lived inside a Failure
    ///     string produced by a code path with no production consumer, so the
    ///     user saw the endpoint's rejection and nothing else.
    ///     <para>
    ///     HARBOR_HOME is redirected to a scratch directory: the registry builds
    ///     its <see cref="McpOAuthHandler" /> with the default cache, which
    ///     resolves under the real harbor home. Without this the test asserts
    ///     against whatever token the developer happens to have cached, and on CI
    ///     it silently found one and the call succeeded.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Registry_RemoteWithAuthAndNoToken_ReportsTheLoginHint()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        string scratch = Directory.CreateTempSubdirectory("harbor-mcp-oauth-hint").FullName;
        string? previousHome = Environment.GetEnvironmentVariable("HARBOR_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", scratch);

            await using var registry = new McpRegistry(null);
            // The auth block is what makes "no token" fatal rather than "go anonymous".
            McpOAuthConfig auth = new() { ClientId = "cid", TokenEndpoint = $"{server.Url}/token" };
            var registered = registry.Register("cloud", server.Url.ToString(), McpTransportNames.Http, null, auth);
            await Assert.That(registered.IsSuccess).IsTrue();

            using var args = JsonDocument.Parse("{}");
            Result<string> result = await registry.InvokeAsync("cloud", "tools/list", args.RootElement);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("harbor mcp login cloud");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_HOME", previousHome);
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    ///     A server with no <c>auth</c> block and no env token must still go out
    ///     unauthenticated: <c>None</c> means "no token", not "refused". The env
    ///     var is read for real here, so the assertion is on the request that was
    ///     actually sent rather than on an internal branch.
    /// </summary>
    [Test]
    public async Task Registry_RemoteWithoutAuthAndNoToken_StillCallsTheEndpoint()
    {
        using FakeServer server = FakeServer.Start();
        server.JsonResponder = static _ => """{"jsonrpc":"2.0","id":1,"result":{}}""";

        string? previous = Environment.GetEnvironmentVariable("HARBOR_MCP_OAUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("HARBOR_MCP_OAUTH_TOKEN", null);

            await using var registry = new McpRegistry(null);
            await Assert.That(registry.Register("anon", server.Url.ToString(), McpTransportNames.Http).IsSuccess).IsTrue();

            using var args = JsonDocument.Parse("{}");
            Result<string> result = await registry.InvokeAsync("anon", "tools/list", args.RootElement);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(server.HandledRequests).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_MCP_OAUTH_TOKEN", previous);
        }
    }

    // ---------- #587: the diagnostic must reach the caller ----------

    /// <summary>
    ///     The regression this issue exists for. A failing transport used to be
    ///     flattened to <c>MCP server 'x' returned no response.</c> one frame above
    ///     this call, so the endpoint, the HTTP status, the attempt count and the
    ///     latency built by the transport reached only the log file. Asserted
    ///     through <see cref="McpRegistry" />, not the transport, because the
    ///     transport was never the thing that lost the context.
    /// </summary>
    [Test]
    public async Task Registry_Persistent503_ReportsEndpointStatusAndAttempts()
    {
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.AddRange([503, 503, 503, 503]);

        await using var registry = new McpRegistry(null);
        // The transport must be named: a two-argument Register(name, value) binds to
        // the stdio overload (a candidate needing no default arguments wins), which
        // would register a subprocess instead of this endpoint.
        await Assert.That(registry.Register("remote", server.Url.ToString(), McpTransportNames.Http).IsSuccess).IsTrue();

        using var args = JsonDocument.Parse("{}");
        Result<string> result = await registry.InvokeAsync("remote", "tools/list", args.RootElement);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("503");
        await Assert.That(result.Error).Contains("attempt");
        await Assert.That(result.Error).Contains(server.Url.ToString());
    }

    /// <summary>
    ///     The other half of the same conflation: "the server answered, and the
    ///     answer carries no document" is a real outcome with no cause to report, so
    ///     it keeps the short wording — and, crucially, says something *different*
    ///     from the 503 above. Before #587 both produced the identical string.
    /// </summary>
    [Test]
    public async Task Registry_AcceptedWithNoBody_ReportsNoResponseAndStaysDistinctFrom503()
    {
        using FakeServer server = FakeServer.Start();
        server.QueueStatusCodes.Add(202);

        await using var registry = new McpRegistry(null);
        await Assert.That(registry.Register("remote", server.Url.ToString(), McpTransportNames.Http).IsSuccess).IsTrue();

        using var args = JsonDocument.Parse("{}");
        Result<string> result = await registry.InvokeAsync("remote", "tools/list", args.RootElement);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no response");
        await Assert.That(result.Error).DoesNotContain("503");
    }

    [Test]
    public async Task Registry_RegisterUrl_RejectsInvalidTransportAndUrl()
    {
        var registry = new McpRegistry(null);
        await Assert.That(registry.Register("a", "not-a-url", "http").IsSuccess).IsFalse();
        await Assert.That(registry.Register("b", "http://localhost/x", "grpc").IsSuccess).IsFalse();
        await Assert.That(registry.Register("c", "http://localhost/x", "sse").IsSuccess).IsTrue();
    }

    // ---------- Fake MCP server ----------

    /// <summary>Minimal HTTP fake: streamable-JSON, SSE-bodied and legacy-SSE MCP servers in one.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
        private readonly Task _acceptLoop;

        public Uri Url { get; }
        public List<int> QueueStatusCodes { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> AuthHeaders { get; } = [];
        public List<string?> SessionIds { get; } = [];
        public int HandledRequests { get; private set; }
        public string? SessionIdToAssign { get; set; }
        public string? SseResponseBody { get; set; }
        public Func<string, string>? JsonResponder { get; set; }

        /// <summary>
        ///     Status for the legacy-SSE <c>GET</c> channel. #714: the 401/403/404
        ///     guards need the channel itself to refuse, and
        ///     <see cref="QueueStatusCodes" /> only ever applied to the POST.
        ///     <see langword="null" /> (the default) keeps the announcing behaviour.
        /// </summary>
        public int? GetStatusCode { get; set; }

        private readonly TaskCompletionSource _postArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeServer(HttpListener listener, Uri url)
        {
            _listener = listener;
            Url = url;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static FakeServer Start()
        {
            int port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new FakeServer(listener, new Uri($"http://127.0.0.1:{port}/mcp"));
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                HandledRequests++;
                AuthHeaders.Add(context.Request.Headers["Authorization"]);
                SessionIds.Add(context.Request.Headers["Mcp-Session-Id"]);

                bool isGet = context.Request.HttpMethod == "GET";
                using var reader = new StreamReader(context.Request.InputStream);
                string body = isGet ? string.Empty : await reader.ReadToEndAsync(_cts.Token);

                if (isGet)
                {
                    // #714: a channel the server refuses outright never announces an
                    // endpoint, so the refusal has to be served before the announce.
                    if (GetStatusCode is { } refused && refused != 200)
                    {
                        context.Response.StatusCode = refused;
                        context.Response.Close();
                        return;
                    }

                    // Legacy SSE channel: announce the POST endpoint, then emit the response.
                    context.Response.ContentType = "text/event-stream";
                    await WriteSseAsync(context.Response, $"event: endpoint\ndata: /message\n\n");
                    // The client POSTs next; once handled, emit the response frame.
                    await WaitForPostAsync(context.Response);
                }
                else
                {
                    RequestBodies.Add(body);
                    _postArrived.TrySetResult();

                    if (SessionIdToAssign is { } sessionId)
                    {
                        context.Response.Headers["Mcp-Session-Id"] = sessionId;
                    }

                    if (SseResponseBody is not null)
                    {
                        context.Response.ContentType = "text/event-stream";
                        byte[] sse = Encoding.UTF8.GetBytes(SseResponseBody);
                        await context.Response.OutputStream.WriteAsync(sse, _cts.Token);
                    }
                    else
                    {
                        int status = QueueStatusCodes.Count > 0 ? QueueStatusCodes[0] : 200;
                        if (QueueStatusCodes.Count > 0)
                        {
                            QueueStatusCodes.RemoveAt(0);
                        }

                        context.Response.StatusCode = status;
                        if (status == 200 && JsonResponder is { } responder)
                        {
                            context.Response.ContentType = "application/json";
                            byte[] json = Encoding.UTF8.GetBytes(responder(body));
                            await context.Response.OutputStream.WriteAsync(json, _cts.Token);
                        }
                    }
                }

                context.Response.Close();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // shutdown
            }
            catch
            {
                try { context.Response.Close(); } catch { /* already gone */ }
            }
        }

        private async Task WriteSseAsync(HttpListenerResponse response, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(bytes, _cts.Token);
            await response.OutputStream.FlushAsync(_cts.Token);
        }

        private async Task WaitForPostAsync(HttpListenerResponse response)
        {
            await _postArrived.Task.WaitAsync(_cts.Token);
            if (JsonResponder is { } responder)
            {
                string payload = $"event: message\ndata: {responder("{}")}\n\n";
                await WriteSseAsync(response, payload);
            }
        }

        private static int GetFreePort()
        {
            var probe = TcpListener.Create(0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* not started */ }
            try { _acceptLoop.Wait(1000); } catch { /* loop ended */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    ///     #831/#925: accepts the TCP connection and hangs up without writing a byte.
    ///     <para>
    ///         Deliberately a raw <see cref="TcpListener" /> rather than an
    ///         extension of <c>FakeServer</c>. <c>FakeServer</c> speaks HTTP, and
    ///         an HTTP server that stays silent still has to produce a status code
    ///         or a clean close — both of which are a different failure class. This
    ///         one never reaches the HTTP layer at all, which is the entire point:
    ///         the failure arrives as <c>HttpRequestException</c> with
    ///         <c>StatusCode == null</c>, the shape #572's hoist could not match.
    ///     </para>
    ///     <para>
    ///         Linger-zero disposal makes the hang-up a TCP RST rather than a
    ///         graceful FIN, so the client's socket errors instead of seeing a
    ///         half-open connection — a FIN would surface as
    ///         <c>HttpRequestError.ConnectionError</c> too, but the RST is the
    ///         shape a genuinely dropped connection produces and does not depend on
    ///         the client noticing a clean shutdown.
    ///     </para>
    ///     <para>
    ///         The accept count is read through <see cref="Volatile" /> because
    ///         the loop increments on a pool thread while the assertions run on the
    ///         test's, and the assertion is the whole proof — a lost increment would
    ///         read as "the retry never happened".
    ///     </para>
    /// </summary>
    private sealed class DeadServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
        private readonly Task _acceptLoop;
        private int _connectionsAccepted;

        private DeadServer(TcpListener listener)
        {
            _listener = listener;
            _acceptLoop = Task.Run(AcceptAndDropAsync);
        }

        public int Port { get; private set; }

        /// <summary>How many times something dialled this listener — the retry count, measured at the far end.</summary>
        public int ConnectionsAccepted => Volatile.Read(ref _connectionsAccepted);

        public static DeadServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new DeadServer(listener)
            {
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            };
            return server;
        }

        private async Task AcceptAndDropAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Interlocked.Increment(ref _connectionsAccepted);
                try
                {
                    // Drop it the way a vanishing peer does: RST, not a tidy close.
                    socket.LingerState = new LingerOption(true, 0);
                }
                catch (SocketException)
                {
                    // Already gone — the hang-up below is the behaviour under test.
                }

                socket.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* not started */ }
            try { _acceptLoop.Wait(1000); } catch { /* loop ended */ }
            _cts.Dispose();
        }
    }
}
