using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #477 — the remote MCP transport choice is a registration seam
///     (<see cref="IMcpTransportFactory" /> + <see cref="McpTransportResolver" />),
///     not a string ternary, and the same table both validates and dispatches.
///     A misspelled transport must fail loudly — never fall through to a default.
/// </summary>
public class McpTransportFactoryTests
{
    // ---------- the seam itself ----------

    [Test]
    public async Task Default_ExposesBothBuiltinTransports()
    {
        await Assert.That(McpTransportResolver.Default.SupportedNames.ToArray())
            .IsEquivalentTo(new[] { McpTransportNames.Http, McpTransportNames.Sse });
    }

    [Test]
    public async Task Canonicalize_UnknownName_FailsAndNamesTheSupportedSet()
    {
        Result<string> result = McpTransportResolver.Default.Canonicalize("htpp");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("htpp");
        await Assert.That(result.Error).Contains("http");
        await Assert.That(result.Error).Contains("sse");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Canonicalize_BlankName_FailsLoudly(string? transport)
    {
        Result<string> result = McpTransportResolver.Default.Canonicalize(transport);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("is not supported");
    }

    [Test]
    public async Task Canonicalize_NullName_FailsLoudly()
    {
        Result<string> result = McpTransportResolver.Default.Canonicalize(null);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("(null)");
    }

    [Test]
    [Arguments("http", "http")]
    [Arguments("HTTP", "http")]
    [Arguments("  sse  ", "sse")]
    [Arguments("Sse", "sse")]
    public async Task Canonicalize_KnownName_IsCaseInsensitiveAndCanonical(string input, string expected)
    {
        Result<string> result = McpTransportResolver.Default.Canonicalize(input);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(expected);
    }

    [Test]
    public async Task Create_UnknownName_FailsInsteadOfDefaultingToABuiltin()
    {
        var request = new McpTransportRequest(new Uri("https://example.com/mcp"), null, null, null);

        Result<IMcpRemoteTransport> result = McpTransportResolver.Default.Create("htpp", request);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("htpp");
    }

    [Test]
    public async Task Create_KnownName_BuildsTheMatchingTransport()
    {
        var request = new McpTransportRequest(new Uri("https://example.com/mcp"), null, null, null);

        Result<IMcpRemoteTransport> http = McpTransportResolver.Default.Create(McpTransportNames.Http, request);
        Result<IMcpRemoteTransport> sse = McpTransportResolver.Default.Create(McpTransportNames.Sse, request);

        await Assert.That(http.IsSuccess).IsTrue();
        await Assert.That(http.Value).IsTypeOf<McpHttpTransport>();
        await Assert.That(sse.IsSuccess).IsTrue();
        await Assert.That(sse.Value).IsTypeOf<McpSseTransport>();
    }

    [Test]
    public async Task Resolver_DuplicateTransportName_ThrowsAtComposition()
    {
        var ex = Assert.Throws<ArgumentException>(() => new McpTransportResolver(
        [
            new StubTransportFactory("http"),
            new StubTransportFactory("HTTP"),
        ]));

        await Assert.That(ex!.Message).Contains("http");
    }

    [Test]
    public async Task Resolver_BlankTransportName_ThrowsAtComposition()
        => await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            _ = new McpTransportResolver([new StubTransportFactory("  ")]);
            await Task.CompletedTask;
        });

    [Test]
    public async Task Compose_AppendsExtrasToTheBuiltins()
    {
        var resolver = McpTransportResolver.Compose([new StubTransportFactory("inproc")]);

        await Assert.That(resolver.SupportedNames.ToArray())
            .IsEquivalentTo(new[] { McpTransportNames.Http, McpTransportNames.Sse, "inproc" });
    }

    [Test]
    public async Task Compose_ExtraShadowingABuiltin_ThrowsRatherThanSilentlyReplacing()
        => await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            _ = McpTransportResolver.Compose([new StubTransportFactory("sse")]);
            await Task.CompletedTask;
        });

    // ---------- registry: validation and dispatch share the seam ----------

    [Test]
    [Arguments("htpp")]
    [Arguments("grpc")]
    [Arguments("http2")]
    [Arguments("https")]
    [Arguments("ssee")]
    public async Task Register_MisspelledTransport_FailsLoudlyAndRegistersNothing(string transport)
    {
        var registry = new McpRegistry();

        Result registered = registry.Register("srv", "https://example.com/mcp", transport);

        await Assert.That(registered.IsFailure).IsTrue();
        await Assert.That(registered.Error).Contains("is not supported");
        // The loud failure is the whole point: the server must not land in the
        // registry where it would later be dispatched through a default.
        await Assert.That(registry.GetServerNames()).IsEmpty();
        await Assert.That(registry.GetRemoteRegistration("srv").IsFailure).IsTrue();
    }

    [Test]
    public async Task Register_BlankTransport_FailsLoudly()
    {
        var registry = new McpRegistry();

        await Assert.That(registry.Register("a", "https://example.com/mcp", "").IsFailure).IsTrue();
        await Assert.That(registry.Register("b", "https://example.com/mcp", "  ").IsFailure).IsTrue();
        await Assert.That(registry.GetServerNames()).IsEmpty();
    }

    [Test]
    public async Task Register_CanonicalizesTransportName()
    {
        var registry = new McpRegistry();

        Result registered = registry.Register("srv", "https://example.com/mcp", "SSE");
        Result<McpRemoteRegistration> view = registry.GetRemoteRegistration("srv");

        await Assert.That(registered.IsSuccess).IsTrue();
        await Assert.That(view.IsSuccess).IsTrue();
        await Assert.That(view.Value.Transport).IsEqualTo(McpTransportNames.Sse);
    }

    [Test]
    public async Task RegisterFromConfig_OmittedTransport_DefaultsToHttp()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            {
                "mcpServers": {
                    "plain": { "url": "https://example.com/mcp" }
                }
            }
            """);

            var registry = new McpRegistry();
            Result loaded = registry.RegisterFromConfig(tempFile);
            Result<McpRemoteRegistration> view = registry.GetRemoteRegistration("plain");

            await Assert.That(loaded.IsSuccess).IsTrue();
            await Assert.That(view.IsSuccess).IsTrue();
            await Assert.That(view.Value.Transport).IsEqualTo(McpTransportNames.Http);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Register_TwoStringArgs_BindsToTheStdioOverload()
    {
        // Overload trap, pinned deliberately: Register(name, string) resolves to
        // the stdio form (a candidate needing no default arguments wins), so a
        // remote server must name its transport — or come from mcp.json.
        var registry = new McpRegistry();

        Result registered = registry.Register("srv", "https://example.com/mcp");

        await Assert.That(registered.IsSuccess).IsTrue();
        await Assert.That(registry.GetRemoteRegistration("srv").IsFailure).IsTrue();
        await Assert.That(registry.GetServerNames().ToArray()).IsEquivalentTo(new[] { "srv" });
    }

    [Test]
    public async Task Register_CustomTransport_AcceptedByRegistrationAlone()
    {
        // A resolver carrying only "inproc" proves the accepted set lives in the
        // seam: the builtins are no longer hardcoded in McpRegistry.
        var resolver = new McpTransportResolver([new StubTransportFactory("inproc")]);
        var registry = new McpRegistry(null, resolver);

        await Assert.That(registry.Register("a", "https://example.com/mcp", "inproc").IsSuccess).IsTrue();
        // …and the builtins are genuinely absent from this registry.
        await Assert.That(registry.Register("b", "https://example.com/mcp", "sse").IsFailure).IsTrue();
        await Assert.That(registry.GetServerNames().ToArray()).IsEquivalentTo(new[] { "a" });
    }

    // ---------- registry: dispatch routes through the seam ----------

    [Test]
    public async Task Invoke_RoutesThroughTheRegisteredFactory()
    {
        var factory = new StubTransportFactory("inproc") { Response = """{"result":{"tools":[]}}""" };
        var resolver = new McpTransportResolver([factory]);
        var registry = new McpRegistry(null, resolver);
        Result registered = registry.Register("srv", "https://example.com/mcp", "inproc");
        await Assert.That(registered.IsSuccess).IsTrue();

        using var args = System.Text.Json.JsonDocument.Parse("{}");
        Result<string> result = await registry.InvokeAsync("srv", "tools/list", args.RootElement);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).Contains("tools");
        await Assert.That(factory.CreateCalls).IsEqualTo(1);
        await Assert.That(factory.LastRequest!.Endpoint.ToString()).IsEqualTo("https://example.com/mcp");
    }

    [Test]
    public async Task Invoke_CachesTheTransportPerServer()
    {
        var factory = new StubTransportFactory("inproc") { Response = """{"result":{}}""" };
        var registry = new McpRegistry(null, new McpTransportResolver([factory]));
        Result registered = registry.Register("srv", "https://example.com/mcp", "inproc");
        await Assert.That(registered.IsSuccess).IsTrue();

        using var args = System.Text.Json.JsonDocument.Parse("{}");
        await registry.InvokeAsync("srv", "tools/list", args.RootElement);
        await registry.InvokeAsync("srv", "tools/list", args.RootElement);

        // Session ids / connection state must survive across calls.
        await Assert.That(factory.CreateCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Invoke_FactoryRefuses_FailsLoudlyAndIsNotRetriedThroughAnotherTransport()
    {
        var factory = new StubTransportFactory("inproc") { CreateFailure = "no route to host" };
        var registry = new McpRegistry(null, new McpTransportResolver([factory]));
        Result registered = registry.Register("srv", "https://example.com/mcp", "inproc");
        await Assert.That(registered.IsSuccess).IsTrue();

        using var args = System.Text.Json.JsonDocument.Parse("{}");
        Result<string> result = await registry.InvokeAsync("srv", "tools/list", args.RootElement);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no route to host");
        await Assert.That(factory.CreateCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Invoke_TransportAnswersNothing_FailsLoudlyInsteadOfReportingSuccess()
    {
        // A factory that cannot answer must not turn into a silent success.
        var factory = new StubTransportFactory("inproc") { Response = null };
        var registry = new McpRegistry(null, new McpTransportResolver([factory]));
        Result registered = registry.Register("srv", "https://example.com/mcp", "inproc");
        await Assert.That(registered.IsSuccess).IsTrue();

        using var args = System.Text.Json.JsonDocument.Parse("{}");
        Result<string> result = await registry.InvokeAsync("srv", "tools/list", args.RootElement);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("no response");
    }

    // ---------- config path ----------

    [Test]
    public async Task RegisterFromConfig_MisspelledTransport_SkipsTheServerInsteadOfDefaulting()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            {
                "mcpServers": {
                    "typo": { "url": "https://example.com/mcp", "transport": "htpp" },
                    "fine": { "url": "https://example.com/mcp", "transport": "http" }
                }
            }
            """);

            var registry = new McpRegistry();
            Result loaded = registry.RegisterFromConfig(tempFile);

            // The file itself parses — the bad server is dropped, the good one stays.
            await Assert.That(loaded.IsSuccess).IsTrue();
            await Assert.That(registry.GetServerNames().ToArray()).IsEquivalentTo(new[] { "fine" });
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ---------- test double ----------

    /// <summary>
    ///     A transport strategy that answers from memory — proves the registry
    ///     reaches whatever factory is registered instead of an HTTP/SSE pair.
    /// </summary>
    private sealed class StubTransportFactory : IMcpTransportFactory
    {
        public StubTransportFactory(string name) => Name = name;

        public string Name { get; }

        public int CreateCalls { get; private set; }

        public McpTransportRequest? LastRequest { get; private set; }

        /// <summary>Raw JSON-RPC response envelope; null → the transport answers nothing.</summary>
        public string? Response { get; init; }

        /// <summary>Non-null → <see cref="Create" /> refuses, as a real factory would on a bad endpoint.</summary>
        public string? CreateFailure { get; init; }

        public Result<IMcpRemoteTransport> Create(McpTransportRequest request)
        {
            CreateCalls++;
            LastRequest = request;
            return CreateFailure is not null
                ? Result.Failure<IMcpRemoteTransport>(CreateFailure)
                : Result.Success<IMcpRemoteTransport>(new StubTransport(Response));
        }
    }

    private sealed class StubTransport(string? response) : IMcpRemoteTransport
    {
        // #587: the seam is Result<Maybe<JsonDocument>>. A null `response` models a
        // server that answers with no document (Maybe.None) — NOT a transport
        // failure, which is what the registry's "returned no response" branch is for.
        public Task<Result<Maybe<System.Text.Json.JsonDocument>>> TryRoundTripAsync(
            System.Text.Json.JsonElement request,
            int? expectedId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(response is null
                ? Result.Success(Maybe<System.Text.Json.JsonDocument>.None)
                : Result.Success(Maybe<System.Text.Json.JsonDocument>.From(
                    System.Text.Json.JsonDocument.Parse(response))));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
