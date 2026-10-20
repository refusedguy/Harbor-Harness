using System.Net.Http;
using System.Reflection;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1567 — one owner assembles the remote MCP transports' HTTP client.
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
///         The assembly now lives in <see cref="McpHttpClientFactory" /> — one
///         internal type — and each transport holds one of those. #1835 moved it
///         out of a public base class, because a public class may not derive from
///         an internal one (CS0060), and the forced-public base had put a type no
///         consumer could use into the NuGet surface of <c>Harbor.Tools.Builtin</c>.
///         These tests pin what the owner produces (the timeout it is handed, the
///         two accept headers, one client per owner) and what the two transports
///         say about themselves — so "the owner owns it" cannot quietly become
///         "the owner owns it, and so does a transport".
///     </para>
/// </remarks>
public class McpHttpClientFactoryTests
{
    // ---------- the owner ----------

    [Test]
    public async Task Factory_BuildsTheClientWithTheTransportTimeout()
    {
        using var factory = new McpHttpClientFactory(TimeSpan.FromSeconds(7));

        using HttpClient client = factory.Client;

        await Assert.That(client.Timeout).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    [Test]
    public async Task Factory_AddsBothMcpAcceptHeaders()
    {
        using var factory = new McpHttpClientFactory(TimeSpan.FromSeconds(7));

        using HttpClient client = factory.Client;
        string[] accepts = [.. client.DefaultRequestHeaders.Accept.Select(h => h.MediaType ?? string.Empty)];

        await Assert.That(accepts).IsEquivalentTo(new[] { "application/json", "text/event-stream" });
    }

    [Test]
    public async Task Factory_BuildsTheClientLazily()
    {
        using var factory = new McpHttpClientFactory(TimeSpan.FromSeconds(7));

        await Assert.That(ClientOf(factory)).IsNull().Because(
            "a transport that has not made a request has no client — and therefore nothing to dispose. The client "
            + "is built on the first request, not in the constructor.");

        using HttpClient client = factory.Client;

        await Assert.That(ClientOf(factory)).IsNotNull().Because(
            "the first request builds the client, and the same client is reused after — one client per transport.");
    }

    [Test]
    public async Task Factory_ReusesOneClient()
    {
        using var factory = new McpHttpClientFactory(TimeSpan.FromSeconds(7));

        using HttpClient first = factory.Client;
        using HttpClient second = factory.Client;

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

    [Test]
    public async Task EachTransport_OwnsItsOwnFactory()
    {
        await using var http = new McpHttpTransport(
            new Uri("http://127.0.0.1:1/mcp"),
            requestTimeout: TimeSpan.FromSeconds(7));
        await using var sse = new McpSseTransport(new Uri("http://127.0.0.1:1/sse"));

        McpHttpClientFactory httpFactory = FactoryOf(http);
        McpHttpClientFactory sseFactory = FactoryOf(sse);

        await Assert.That(ReferenceEquals(httpFactory, sseFactory)).IsFalse().Because(
            "each transport holds its own factory, so each transport opens its own client — one client per transport, "
            + "and disposing one transport cannot dispose the other's client.");
    }

    // ---------- one owner ----------

    [Test]
    public async Task NoTransport_DeclaresAClientOfItsOwn()
    {
        foreach (Type transport in new[] { typeof(McpHttpTransport), typeof(McpSseTransport) })
        {
            MethodInfo[] own = [.. transport
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(HttpClient))];

            FieldInfo[] held = [.. transport
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType == typeof(HttpClient))];

            await Assert.That(own).IsEmpty().Because(
                $"{transport.Name} assembles an HttpClient of its own, so the client that every remote MCP "
                + "transport shares exists in two places again — and the copies drift (the timeouts already had). "
                + "The factory owns the assembly; a transport holds the factory and inherits nothing.");

            await Assert.That(held).IsEmpty().Because(
                $"{transport.Name} holds an HttpClient field, so the client (and its lifetime) belongs to the "
                + "factory — a transport that held one would be free to dispose a client another transport still "
                + "uses, and the owner would no longer be the only one.");
        }
    }

    [Test]
    public async Task TheFactory_IsTheOnlyTypeThatProducesAClientInTheMcpNamespace()
    {
        Type?[] candidates;
        try
        {
            candidates = typeof(McpHttpTransport).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types;
        }

        Type[] owners = [.. candidates
            .Where(t => t is not null)
            .Select(t => t!)
            .Where(t => t.Namespace == "Harbor.Tools.Mcp" && ProducesHttpClient(t))];

        await Assert.That(owners).IsEquivalentTo(new[] { typeof(McpHttpClientFactory) }).Because(
            "the client must have exactly one owner. A second declaration is a second copy of the assembly step, "
            + "which is the duplication #1567 removed.");
    }

    /// <summary>
    ///     Whether the type declares a member that hands back an
    ///     <see cref="HttpClient" /> — the owner's shape, whatever it is called.
    /// </summary>
    private static bool ProducesHttpClient(Type type)
    {
        foreach (MethodInfo method in type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.ReturnType == typeof(HttpClient))
            {
                return true;
            }
        }

        foreach (PropertyInfo property in type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (property.PropertyType == typeof(HttpClient))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The transport's client owner, read off the transport itself — the
    ///     client belongs to the factory, and the factory belongs to the
    ///     transport.
    /// </summary>
    private static McpHttpClientFactory FactoryOf(object transport)
    {
        FieldInfo? field = transport.GetType()
            .GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{transport.GetType().Name} no longer holds a client factory — the transport and the client have "
                + "come apart, and the transport would be assembling one of its own.");

        return (McpHttpClientFactory)(field.GetValue(transport)
            ?? throw new InvalidOperationException("the factory field was null."));
    }

    /// <summary>
    ///     The transport's declared client timeout — the one thing each
    ///     transport states for itself.
    /// </summary>
    private static TimeSpan ClientTimeoutOf(object transport)
    {
        McpHttpClientFactory factory = FactoryOf(transport);
        using HttpClient client = factory.Client;
        return client.Timeout;
    }

    /// <summary>
    ///     The client the factory has built so far — <see langword="null" />
    ///     while it has built none.
    /// </summary>
    private static HttpClient? ClientOf(McpHttpClientFactory factory)
    {
        FieldInfo? field = factory.GetType()
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "McpHttpClientFactory no longer holds the client it built — the owner has come apart.");

        return (HttpClient?)field.GetValue(factory);
    }
}
