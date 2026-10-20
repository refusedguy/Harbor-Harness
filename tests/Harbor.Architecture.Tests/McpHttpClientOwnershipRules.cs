// McpHttpClientOwnershipRules.cs — the guard for issue #1567.
//
// WHY THIS FILE EXISTS
// --------------------
// The remote-MCP transports each assembled their own HttpClient:
//
//     private HttpClient GetClient()
//     {
//         if (_client is { } existing) return existing;
//         var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
//         var client = new HttpClient(handler) { Timeout = _requestTimeout };
//         client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
//         client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
//         _client = client;
//         return client;
//     }
//
// — byte-identical in `McpHttpTransport` and `McpSseTransport` apart from one
// line: the timeout. "How a remote MCP transport's client is assembled" was
// therefore knowledge that lived in two places, and the copies drifted: the
// timeouts HAD already diverged (deliberately — the SSE stream outlives any
// single request — but silently, and the divergence was invisible at a glance
// because the two copies looked the same).
//
// The fix moved the assembly into `McpRemoteTransportBase` (the Factory
// Method) and left each transport stating only its timeout.
//
// WHAT IS ENFORCED
// ----------------
//   1. SHAPE (reflection): no concrete remote transport declares a member
//      that produces an `HttpClient`, and none of them holds one either — the
//      client and its lifetime belong to `McpRemoteTransportBase`. The
//      assembly of the client therefore has exactly one owner, and the owner is
//      the base — not "the base and, once more, a transport".
//   2. SHAPE (source): the pooled handler — `new SocketsHttpHandler` — is
//      assembled exactly once in the whole MCP transport tree, in the base.
//      A rule (1) cannot see: a transport may assemble its client without
//      declaring a member that RETURNS one, and it may do so with any handler
//      it likes. The source half is what pins "the handler configuration has
//      one home".
//
// WHY A SOURCE HALF FOR A SOURCE-ONLY FACT
// ----------------------------------------
// "This assembly happens in exactly one place" is not a signature: the fact
// lives in the body of a method. Reflection can only see the shape of what a
// type declares, so the second half of the duplication — a second copy of the
// handler configuration — is invisible to it. The scan is scoped to the MCP
// transport tree (`src/Harbor.Tools.Builtin/Tools/Mcp`), where the two
// transports and their base live; `McpOAuthHandler`'s own client factory is a
// different concern (it builds the OAuth client's factory, not a transport's
// client) and is deliberately not judged here.
//
// NON-VACUITY
// -----------
// Both halves can pass for the wrong reason: the transport discovery can find
// nothing (a guard over an empty set is a green light that means nothing),
// and the source scan can find no file at all (a checkout without the tree).
// Both are closed below.

using System.Net.Http;
using System.Reflection;
using Harbor.Tools.Mcp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Fails the build if the remote-MCP transports grow a second copy of the
///     HTTP-client assembly. See the file header for the incident.
/// </summary>
public class McpHttpClientOwnershipRules
{
    /// <summary>The one type allowed to assemble a transport's client.</summary>
    private static readonly Type Owner = typeof(McpRemoteTransportBase);

    /// <summary>The pooled-handler construction the base owns.</summary>
    private const string PooledHandler = "new SocketsHttpHandler";

    /// <summary>The tree the transports (and their base) live in.</summary>
    private const string TransportTree = "src/Harbor.Tools.Builtin/Tools/Mcp";

    /// <summary>
    ///     No transport assembles a client of its own: no concrete remote
    ///     transport declares a member that produces an
    ///     <see cref="HttpClient" />, and none of them holds one either. The
    ///     client belongs to <see cref="McpRemoteTransportBase" />.
    /// </summary>
    [Test]
    public async Task Assert_NoTransportAssemblesItsOwnHttpClient()
    {
        List<Type> transports = ConcreteTransports();
        var offenders = new List<string>();

        foreach (Type transport in transports)
        {
            foreach (MethodInfo method in transport.GetMethods(DeclaredMembers))
            {
                if (ProducesHttpClient(method.ReturnType))
                {
                    offenders.Add(
                        $"{transport.Name}.{method.Name} returns {method.ReturnType.Name} — the shared client is "
                        + $"assembled by {Owner.Name}, not by a transport");
                }
            }

            foreach (FieldInfo field in transport.GetFields(DeclaredMembers))
            {
                if (field.FieldType == typeof(HttpClient))
                {
                    offenders.Add(
                        $"{transport.Name}.{field.Name} holds an HttpClient — the client (and its lifetime) "
                        + $"belongs to {Owner.Name}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty().Because(
            "a remote-MCP transport assembles its own HttpClient, so the client every transport shares exists in "
            + "two places again — and the copies drift (the timeouts already had: one request-bounded, one "
            + "infinite). The base owns the assembly; a transport states its ClientTimeout and inherits the rest. "
            + "Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    ///     The pooled handler is configured in exactly one place: the base.
    /// </summary>
    [Test]
    public async Task Assert_ThePooledHandlerIsAssembledOnceInTheTransportTree()
    {
        IReadOnlyList<string> files = SourceScan.EnumerateCsFiles(TransportTree);
        var owners = new List<string>();

        foreach (string file in files)
        {
            string? text = SourceScan.TryReadAllText(file);
            if (text is null)
            {
                continue;
            }

            int hits = CountOccurrences(SourceScan.StripComments(text), PooledHandler);
            if (hits > 0)
            {
                owners.Add($"{SourceScan.Relative(file)} ({hits})");
            }
        }

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            $"the scan found no source under {TransportTree}, so it cannot see a violation — a rule over an empty "
            + "tree is a green light that means nothing.");

        await Assert.That(owners.Count).IsEqualTo(1).Because(
            "the pooled handler ('new SocketsHttpHandler') is configured in one place — the base's Factory Method. "
            + "Two copies of that configuration drift, and the drift is invisible because the copies look alike. "
            + "Owners:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, owners)}");

        await Assert.That(owners.FirstOrDefault() ?? string.Empty)
            .StartsWith("src/Harbor.Tools.Builtin/Tools/Mcp/McpRemoteTransportBase.cs")
            .Because(
                "the one place that configures the pooled handler is the base's Factory Method — the owner the "
                + "reflection rule above names. A handler configured anywhere else is a second copy of the "
                + "assembly step, whatever the file is called.");
    }

    /// <summary>
    ///     Non-vacuity of the discovery step: the rule above reasons about the
    ///     transports it finds, and a set of zero cannot be judged.
    /// </summary>
    [Test]
    public async Task Assert_TheGuardActuallyFindsTheTransports()
    {
        List<Type> transports = ConcreteTransports();
        string[] names = [.. transports.Select(t => t.Name)];

        await Assert.That(transports.Count).IsGreaterThanOrEqualTo(2).Because(
            $"expected both builtin remote transports (streamable HTTP + legacy SSE) to be discoverable, found "
            + $"{transports.Count}. A guard over an empty set is a green light that means nothing.");

        await Assert.That(Array.IndexOf(names, "McpHttpTransport") >= 0).IsTrue().Because(
            "the streamable-HTTP transport is one of the two copies #1567 removed — if it were renamed or dropped "
            + $"from the seam, this rule would have silently stopped covering the defect it was written for. "
            + $"Discovered: [{string.Join(", ", names)}]");

        await Assert.That(Array.IndexOf(names, "McpSseTransport") >= 0).IsTrue().Because(
            "same coverage requirement for the legacy SSE transport, which carried the second copy of the same "
            + $"assembly. Discovered: [{string.Join(", ", names)}]");
    }

    /// <summary>
    ///     True for a return type that hands back an <see cref="HttpClient" />,
    /// directly or inside a task.
    /// </summary>
    private static bool ProducesHttpClient(Type returnType)
    {
        if (returnType == typeof(HttpClient))
        {
            return true;
        }

        if (returnType.IsGenericType)
        {
            Type definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0] == typeof(HttpClient);
            }
        }

        return false;
    }

    /// <summary>Every non-abstract class in the assembly that implements the transport seam.</summary>
    private static List<Type> ConcreteTransports()
    {
        Type?[] candidates;
        try
        {
            candidates = typeof(IMcpRemoteTransport).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types;
        }

        var result = new List<Type>();
        foreach (Type? type in candidates)
        {
            if (type is { IsInterface: false, IsAbstract: false } && typeof(IMcpRemoteTransport).IsAssignableFrom(type))
            {
                result.Add(type);
            }
        }

        return result;
    }

    /// <summary>Non-public and public members declared by the type itself.</summary>
    private const BindingFlags DeclaredMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>How many times <paramref name="needle" /> occurs in <paramref name="haystack" />.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
