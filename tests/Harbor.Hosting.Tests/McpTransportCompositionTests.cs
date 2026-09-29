using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #477 — the remote MCP transport seam must survive composition: the
///     resolver and its factories land in the container, the composed
///     <see cref="IMcpRegistry" /> honours the same table, and a transport name
///     nobody registered is a loud failure at registration time — never a silent
///     fall-through to a default transport.
/// </summary>
[NotInParallel("hosting")]
public class McpTransportCompositionTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AddHarbor_PublishesTheTransportSeam()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
        });

        McpTransportResolver resolver = sp.GetRequiredService<McpTransportResolver>();
        await Assert.That(resolver.SupportedNames.ToArray())
            .IsEquivalentTo(new[] { McpTransportNames.Http, McpTransportNames.Sse });
        await Assert.That(sp.GetServices<IMcpTransportFactory>().Count()).IsEqualTo(2);
        // Singleton — the container and the registry share one table.
        await Assert.That(resolver).IsSameReferenceAs(sp.GetRequiredService<McpTransportResolver>());
    }

    [Test]
    public async Task AddHarbor_ExtraTransport_WidensTheAcceptedSetWithoutTouchingTheRegistry()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            McpTransports = [new StubTransportFactory("inproc")],
        });

        McpTransportResolver resolver = sp.GetRequiredService<McpTransportResolver>();
        await Assert.That(resolver.SupportedNames.ToArray())
            .IsEquivalentTo(new[] { McpTransportNames.Http, McpTransportNames.Sse, "inproc" });
        await Assert.That(sp.GetServices<IMcpTransportFactory>().Count()).IsEqualTo(3);

        var registry = (McpRegistry)sp.GetRequiredService<IMcpRegistry>();
        await Assert.That(registry.Register("srv", "https://example.com/mcp", "inproc").IsSuccess).IsTrue();
        // A name nobody registered is rejected loudly, and never lands in the
        // registry where it would be dispatched through a default.
        Result typo = registry.Register("srv2", "https://example.com/mcp", "inprocc");
        await Assert.That(typo.IsFailure).IsTrue();
        await Assert.That(typo.Error).Contains("inprocc");
        await Assert.That(registry.GetServerNames().ToArray()).IsEquivalentTo(new[] { "srv" });
    }

    [Test]
    public async Task AddHarbor_ExtraTransportShadowingABuiltin_ThrowsAtComposition()
    {
        // A shadowed builtin would silently change the meaning of every
        // existing mcp.json — that must never boot.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using var sp = Compose(new HarborComposeOptions
            {
                HarborDir = TempHarborDir(),
                DefaultStorageBackend = "memory",
                McpTransports = [new StubTransportFactory("sse")],
            });
            await Task.CompletedTask;
        });
    }

    private sealed class StubTransportFactory(string name) : IMcpTransportFactory
    {
        public string Name => name;

        public Result<IMcpRemoteTransport> Create(McpTransportRequest request) =>
            Result.Success<IMcpRemoteTransport>(new StubTransport());
    }

    private sealed class StubTransport : IMcpRemoteTransport
    {
        public Task<System.Text.Json.JsonDocument?> RoundTripAsync(
            System.Text.Json.JsonElement request,
            int? expectedId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<System.Text.Json.JsonDocument?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
