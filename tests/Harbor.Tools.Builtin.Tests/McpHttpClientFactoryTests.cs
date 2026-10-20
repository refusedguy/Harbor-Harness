using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1567 — one factory seam owns the remote MCP transports' HTTP client.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="McpHttpTransport" /> and <see cref="McpSseTransport" /> each
///         carried their own <c>GetClient()</c>: the same handler, the same client,
///         the same two <c>Accept</c> headers — differing only in the timeout. The
///         knowledge of "how a client is assembled" therefore lived in two places,
///         and a change to one had to be mirrored in the other or the copies
///         drifted. The timeouts HAD already drifted: deliberately, but silently.
///     </para>
///     <para>
///         The fix moves the assembly into <see cref="McpRemoteTransportBase" />
///         and leaves each transport stating only its
///         <see cref="McpRemoteTransportBase.ClientTimeout" />. These tests pin
///         what the seam produces (the timeout it is handed, the two accept
///         headers, one client per transport) and what the two transports say
///         about themselves — so "the base owns it" cannot quietly become "the
///         base owns it, and so does a transport".
///     </para>
/// </remarks>
public class McpHttpClientFactoryTests
{
    // ---------- the seam ----------

    [Test]
    public async Task Factory_BuildsTheClientWithTheTransportTimeout()
    {
        await using var probe = new ProbeTransport(TimeSpan.FromSeconds(7));

        using HttpClient client = probe.Build();

        await Assert.That(client.Timeout).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    [Test]
    public async Task Factory_AddsBothMcpAcceptHeaders()
    {
        await using var probe = new ProbeTransport(TimeSpan.FromSeconds(7));

        using HttpClient client = probe.Build();
        string[] accepts = [.. client.DefaultRequestHeaders.Accept.Select(h => h.MediaType ?? string.Empty)];

        await Assert.That(accepts).IsEquivalentTo(new[] { "application/json", "text/event-stream" });
    }

    [Test]
    public async Task GetClient_ReusesOneClientPerTransport()
    {
        await using var probe = new ProbeTransport(TimeSpan.FromSeconds(7));

        using HttpClient first = probe.Client();
        using HttpClient second = probe.Client();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    // ---------- what each transport says ----------

    [Test]
    public async Task HttpTransport_ClientTimeout_IsTheRequestTimeout()
    {
        await using var transport = new McpHttpTransport(
            new Uri("http://127.0.0.1:1/mcp"),
            requestTimeout: TimeSpan.FromSeconds(7));

        await Assert.That(ClientTimeoutOf(transport)).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    [Test]
    public async Task HttpTransport_DefaultClientTimeout_IsOneMinute()
    {
        await using var transport = new McpHttpTransport(new Uri("http://127.0.0.1:1/mcp"));

        await Assert.That(ClientTimeoutOf(transport)).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task SseTransport_ClientTimeout_IsInfinite()
    {
        // The GET stream outlives any single request, so the CLIENT is not
        // bounded; each attempt is bounded by its own linked CTS instead.
        await using var transport = new McpSseTransport(new Uri("http://127.0.0.1:1/sse"));

        await Assert.That(ClientTimeoutOf(transport)).IsEqualTo(Timeout.InfiniteTimeSpan);
    }

    // ---------- one owner ----------

    [Test]
    public async Task BothTransports_DeriveFromTheOneBase()
    {
        await Assert.That(typeof(McpHttpTransport).BaseType).IsEqualTo(typeof(McpRemoteTransportBase));
        await Assert.That(typeof(McpSseTransport).BaseType).IsEqualTo(typeof(McpRemoteTransportBase));
    }

    [Test]
    public async Task NoTransport_DeclaresAClientOfItsOwn()
    {
        foreach (Type transport in new[] { typeof(McpHttpTransport), typeof(McpSseTransport) })
        {
            MethodInfo[] own = [.. transport
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(HttpClient))];

            await Assert.That(own).IsEmpty().Because(
                $"{transport.Name} assembles an HttpClient of its own, so the client that every remote MCP "
                + "transport shares exists in two places again — and the copies drift (the timeouts already had). "
                + "The base owns the assembly; a transport states its ClientTimeout and inherits the rest.");
        }
    }

    [Test]
    public async Task TheFactory_IsDeclaredOnceInTheAssembly()
    {
        Type?[] candidates;
        try
        {
            candidates = typeof(McpRemoteTransportBase).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types;
        }

        Type[] owners = [.. candidates
            .Where(t => t is not null)
            .Select(t => t!)
            .Where(t => t.GetMethod(
                "CreateHttpClient",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)];

        await Assert.That(owners).IsEquivalentTo(new[] { typeof(McpRemoteTransportBase) }).Because(
            "the Factory Method must have exactly one owner. A second declaration is a second copy of the "
            + "assembly step, which is the duplication #1567 removed.");
    }

    /// <summary>
    ///     The transport's declared client timeout, read off the base's protected
    ///     property — the one thing each transport states for itself.
    /// </summary>
    private static TimeSpan ClientTimeoutOf(object transport)
    {
        PropertyInfo property = typeof(McpRemoteTransportBase)
            .GetProperty("ClientTimeout", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "McpRemoteTransportBase.ClientTimeout is gone — the transports have no way to say which "
                + "client they need, and the base is guessing.");

        return (TimeSpan)(property.GetValue(transport)
            ?? throw new InvalidOperationException("ClientTimeout returned null."));
    }

    /// <summary>
    ///     A transport that only says what its timeout is — the shape the two
    ///     builtin transports now have.
    /// </summary>
    private sealed class ProbeTransport(TimeSpan timeout) : McpRemoteTransportBase
    {
        protected override TimeSpan ClientTimeout => timeout;

        public HttpClient Build() => CreateHttpClient();

        public HttpClient Client() => GetClient();

        public override Task<Result<Maybe<JsonElement>>> TryRoundTripAsync(
            JsonElement request,
            int? expectedId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success(Maybe<JsonElement>.None));

        public override ValueTask DisposeAsync()
        {
            DisposeClient();
            return ValueTask.CompletedTask;
        }
    }
}
