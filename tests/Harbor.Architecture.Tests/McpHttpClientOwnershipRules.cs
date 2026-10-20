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
// The fix moved the assembly into ONE owner. It was first a public base class
// (`McpRemoteTransportBase`), which is what C# forces when a public class is
// to reuse a shared implementation: a public class may not derive from an
// internal one (CS0060), so the base had to be public — and it put a type no
// consumer of `Harbor.Tools.Builtin` could use into the package's NuGet
// surface. #1835 replaced that inheritance with composition: the owner is now
// an INTERNAL type (`McpHttpClientFactory`), each transport holds one, and
// the public surface shrinks by one type. The invariants below are the ones
// that must survive the change of shape.
//
// WHAT IS ENFORCED
// ----------------
//   1. SHAPE (reflection): no concrete remote transport declares a member
//      that produces an `HttpClient`, and none of them holds one either — the
//      client and its lifetime belong to the factory, not to a transport. The
//      assembly of the client therefore has exactly one owner, and the owner is
//      not a transport.
//   2. OWNERSHIP (reflection): exactly ONE type in the MCP namespace produces
//      an `HttpClient`, and it is NOT public. The second half is new in
//      #1835 and it is the point of the change: the shared assembly must not
//      cost the package's public surface, so the owner has to be internal. A
//      public owner is exactly the defect #1715 introduced.
//   3. SHAPE (source): the pooled handler — `new SocketsHttpHandler` — is
//      assembled exactly once in the whole MCP transport tree, in the owner's
//      file. A rule (1) cannot see: a transport may assemble its client without
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
// transports and their client owner live; `McpOAuthHandler`'s own client
// factory is a different concern (it builds the OAuth client's factory, not a
// transport's client) and is deliberately not judged here.
//
// NON-VACUITY
// -----------
// Both halves can pass for the wrong reason: the transport discovery can find
// nothing (a guard over an empty set is a green light that means nothing),
// and the source scan can find no file at all (a checkout without the tree).
// Both are closed below — and so is the matcher itself: the source half is
// run over a SYNTHETIC second copy of the pooled handler (which must be
// reported) and over PROSE that merely mentions it (which must not be).

using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harbor.Tools.Mcp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Fails the build if the remote-MCP transports grow a second copy of the
///     HTTP-client assembly, or if the one owner of that assembly becomes part
///     of the package's public surface. See the file header for the incident.
/// </summary>
public class McpHttpClientOwnershipRules
{
    /// <summary>The pooled-handler construction exactly one file may carry.</summary>
    private const string PooledHandler = "new SocketsHttpHandler";

    /// <summary>The tree the transports (and their client owner) live in.</summary>
    private const string TransportTree = "src/Harbor.Tools.Builtin/Tools/Mcp";

    /// <summary>The file that owns the pooled-handler configuration.</summary>
    private const string OwnerFile = "src/Harbor.Tools.Builtin/Tools/Mcp/McpHttpClientFactory.cs";

    /// <summary>The namespace the MCP transport types live in.</summary>
    private const string McpNamespace = "Harbor.Tools.Mcp";

    /// <summary>Public + non-public, static + instance, declared on the type itself.</summary>
    private const BindingFlags DeclaredMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    ///     No transport assembles a client of its own: no concrete remote
    ///     transport declares a member that produces an
    ///     <see cref="HttpClient" />, and none of them holds one either. The
    ///     client belongs to the factory, and the factory is not a transport.
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
                        + $"assembled by {OwnerName()}, not by a transport");
                }
            }

            foreach (FieldInfo field in transport.GetFields(DeclaredMembers))
            {
                if (field.FieldType == typeof(HttpClient))
                {
                    offenders.Add(
                        $"{transport.Name}.{field.Name} holds an HttpClient — the client (and its lifetime) "
                        + $"belongs to {OwnerName()}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty().Because(
            "a remote-MCP transport assembles its own HttpClient, so the client every transport shares exists in "
            + "two places again — and the copies drift (the timeouts already had: one request-bounded, one "
            + "infinite). The factory owns the assembly; a transport holds the factory and inherits nothing. "
            + "Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    ///     The assembly of the client has exactly one owner in the MCP
    ///     namespace, and that owner is INTERNAL (#1835).
    /// </summary>
    /// <remarks>
    ///     The public half is the defect this rule exists for. A public class
    ///     cannot derive from an internal one (CS0060), so sharing the client
    ///     assembly by inheritance forces the base public — and a public base
    ///     nobody outside the assembly can use is a type added to the NuGet
    ///     surface of <c>Harbor.Tools.Builtin</c> for a pure code-reuse reason.
    ///     Composition removes it; this rule keeps it removed.
    /// </remarks>
    [Test]
    public async Task Assert_TheClientHasExactlyOneOwnerAndItIsNotPublic()
    {
        List<Type> owners = ClientOwners();

        await Assert.That(owners.Count).IsEqualTo(1).Because(
            "the remote MCP transports' client must have exactly one owner in the MCP namespace — the factory that "
            + "assembles it. A second owner is a second copy of the assembly step (the duplication #1567 removed), "
            + "and a public one puts a type no consumer can use into the package's NuGet surface (#1715, undone by "
            + "#1835). Owners: "
            + $"{string.Join(", ", owners.Select(o => o.FullName ?? o.Name))}");

        Type owner = owners[0];
        await Assert.That(owner.IsPublic).IsFalse().Because(
            $"{owner.Name} owns the remote MCP transports' client assembly, so it is internal: a public owner is a "
            + "public type added to the NuGet surface of Harbor.Tools.Builtin for a pure code-reuse reason. "
            + "Harbor.Tools.Builtin is packable but NOT yet published, so the surface change costs external "
            + "consumers nothing today — it would cost them everything at the first publication.");
    }

    /// <summary>
    ///     The pooled handler is configured in exactly one place: the owner's
    ///     file.
    /// </summary>
    [Test]
    public async Task Assert_ThePooledHandlerIsAssembledOnceInTheTransportTree()
    {
        IReadOnlyList<(string Path, string Text)> sources = McpTreeSources();
        List<string> owners = PooledHandlerOwners(sources);

        await Assert.That(sources.Count).IsGreaterThan(0).Because(
            $"the scan found no source under {TransportTree}, so it cannot see a violation — a rule over an empty "
            + "tree is a green light that means nothing.");

        await Assert.That(owners.Count).IsEqualTo(1).Because(
            "the pooled handler ('new SocketsHttpHandler') is configured in one place — the client's factory. "
            + "Two copies of that configuration drift, and the drift is invisible because the copies look alike. "
            + "Owners:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, owners)}");

        await Assert.That(owners.FirstOrDefault() ?? string.Empty)
            .StartsWith(OwnerFile)
            .Because(
                "the one place that configures the pooled handler is the client's factory — the owner the "
                + "reflection rule above names. A handler configured anywhere else is a second copy of the "
                + "assembly step, whatever the file is called.");
    }

    /// <summary>
    ///     Non-vacuity of the source matcher itself: a synthetic SECOND copy
    ///     of the pooled handler must be reported.
    /// </summary>
    /// <remarks>
    ///     The rule above passes when the tree holds exactly one owner. That
    ///     is also what a matcher that stopped matching would produce — one
    ///     owner, counted — so the rule cannot tell "the tree is clean" from
    ///     "the counter sees nothing". This control runs the SAME counter over
    ///     a synthetic pair of files and requires it to report both.
    /// </remarks>
    [Test]
    public async Task Assert_TheSourceScanDetectsASyntheticSecondPooledHandler()
    {
        (string Path, string Text)[] synthetic =
        [
            (OwnerFile, "var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };"),
            ("src/Harbor.Tools.Builtin/Tools/Mcp/McpSseTransport.cs", "var handler = new SocketsHttpHandler { };"),
        ];

        List<string> owners = PooledHandlerOwners(synthetic);

        await Assert.That(owners.Count).IsEqualTo(2).Because(
            "the pooled-handler counter must see BOTH copies of the configuration. A counter that stopped matching "
            + "would report one owner, and the real rule would then pass over a second copy of the assembly step "
            + "— the exact defect #1567 removed. Owners:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, owners)}");

        await Assert.That(owners[1]).StartsWith("src/Harbor.Tools.Builtin/Tools/Mcp/McpSseTransport.cs").Because(
            "the synthetic second copy must be named, not merely counted: a counter that reported two owners without "
            + "saying which files would still let a reviewer guess wrong.");
    }

    /// <summary>
    ///     The other direction: prose that merely MENTIONS the pooled handler
    ///     is not a configuration.
    /// </summary>
    /// <remarks>
    ///     The comment-stripping half of the matcher is load-bearing for the
    ///     same reason the counting half is: the file that owns the handler
    ///     documents what it does, and the rule's own header quotes the code
    ///     it forbids. Without stripping, the explanation of the rule would
    ///     trip the rule.
    /// </remarks>
    [Test]
    public async Task Assert_ProseAboutThePooledHandlerIsNotAConfiguration()
    {
        (string Path, string Text)[] prose =
        [
            (OwnerFile, "// the ONE place a transport's client is assembled: new SocketsHttpHandler lives here"),
            ("src/Harbor.Tools.Builtin/Tools/Mcp/McpSseTransport.cs", "/// <c>new SocketsHttpHandler</c> is configured elsewhere"),
        ];

        List<string> owners = PooledHandlerOwners(prose);

        await Assert.That(owners).IsEmpty().Because(
            "a mention of the handler in prose is not a configuration of it. Stripping comments is what keeps this "
            + "rule from firing on the very documentation that explains it — and a stripper that stopped working "
            + "would turn every explanation of the rule into a violation of it. Owners:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, owners)}");
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
    ///     directly or inside a task.
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

    /// <summary>
    ///     Whether the type declares ANY member that hands back an
    ///     <see cref="HttpClient" /> — a method or a property.
    /// </summary>
    private static bool DeclaresAnHttpClientProducer(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(DeclaredMembers))
        {
            if (ProducesHttpClient(method.ReturnType))
            {
                return true;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(DeclaredMembers))
        {
            if (ProducesHttpClient(property.PropertyType))
            {
                return true;
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

    /// <summary>
    ///     Every type in the MCP namespace that declares a member producing an
    ///     <see cref="HttpClient" /> — the candidates for "the owner".
    /// </summary>
    /// <remarks>
    ///     Compiler-generated types are excluded, and they have to be: the
    ///     lambda inside <c>McpOAuthHandler</c>'s constructor
    ///     (<c>() =&gt; new HttpClient()</c>) compiles into a nested
    ///     <c>&lt;&gt;c</c> display class whose method returns an
    ///     <see cref="HttpClient" />. That closure is a compiler artefact,
    ///     not a second place where the assembly is written — the OAuth client
    ///     is a different concern (it builds the OAuth client's factory, not a
    ///     transport's client), and this rule deliberately does not judge it.
    /// </remarks>
    private static List<Type> ClientOwners()
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
            if (type is null || type.IsInterface || type.Namespace != McpNamespace)
            {
                continue;
            }

            if (IsCompilerGenerated(type))
            {
                continue;
            }

            if (DeclaresAnHttpClientProducer(type))
            {
                result.Add(type);
            }
        }

        return result;
    }

    /// <summary>
    ///     Whether the type is a compiler artefact — a lambda closure or a
    ///     generated iterator, not a declaration anybody wrote.
    /// </summary>
    private static bool IsCompilerGenerated(Type type)
        => type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || type.Name.Contains('<', StringComparison.Ordinal);

    /// <summary>
    ///     The name the failure messages use for the owner — the type that
    ///     assembles the transports' client.
    /// </summary>
    private static string OwnerName()
    {
        List<Type> owners = ClientOwners();
        return owners.Count == 1 ? owners[0].Name : "the client's factory";
    }

    /// <summary>
    ///     Every source file of the MCP tree, as a repo-relative path and its
    ///     text.
    /// </summary>
    private static IReadOnlyList<(string Path, string Text)> McpTreeSources()
    {
        var result = new List<(string Path, string Text)>();

        foreach (string file in SourceScan.EnumerateCsFiles(TransportTree))
        {
            string? text = SourceScan.TryReadAllText(file);
            if (text is null)
            {
                continue;
            }

            result.Add((SourceScan.Relative(file), text));
        }

        return result;
    }

    /// <summary>
    ///     The files that configure the pooled handler, each with the number of
    ///     occurrences — the rule's counting half, over a given set of sources.
    /// </summary>
    private static List<string> PooledHandlerOwners(IReadOnlyList<(string Path, string Text)> sources)
    {
        var owners = new List<string>();

        foreach ((string path, string text) in sources)
        {
            int hits = CountOccurrences(SourceScan.StripComments(text), PooledHandler);
            if (hits > 0)
            {
                owners.Add($"{path} ({hits})");
            }
        }

        return owners;
    }

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
