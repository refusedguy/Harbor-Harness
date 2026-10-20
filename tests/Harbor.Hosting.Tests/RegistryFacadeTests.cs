using Harbor.Abstractions.Agents;
using Harbor.Application.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Issue #1592 (Structural/Facade): <c>RegistryFacade.Compose</c> is the
///     single build step of the eager registries. Pins that the composed
///     container hands out the REAL sub-agent runner — the deferred forwarder
///     is created and attached inside the facade and never leaks past its
///     boundary — and that the published runner honours the singleton
///     contract the module used to provide.
/// </summary>
[NotInParallel("hosting")]
public class RegistryFacadeTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task Facade_ComposedContainer_ResolvesRealRunner_NotForwarder()
    {
        using var sp = CompositionFixtures.Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });

        var runner = sp.GetRequiredService<ISubAgentRunner>();

        // Resolution is the attach point: the real runner comes out of the
        // container, the forwarder exists only inside RegistryFacade.Compose.
        await Assert.That(runner).IsTypeOf<SubAgentRunner>();

        // Attach parity with HostBuilder's warmup: an attached runner reports
        // spawn-legality, a detached forwarder fails CanSpawn (→ false).
        await Assert.That(runner.CanSpawn).IsTrue();
    }

    [Test]
    public async Task Facade_ComposedContainer_RunnerIsSingleton()
    {
        using var sp = CompositionFixtures.Compose(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" });

        await Assert.That(sp.GetRequiredService<ISubAgentRunner>())
            .IsSameReferenceAs(sp.GetRequiredService<ISubAgentRunner>());
    }

    [Test]
    public async Task Facade_StandardPreset_RunnerStillResolves_AsRealRunner()
    {
        // The desktop subset drops the task tool, but the ISubAgentRunner
        // registration stays — view-models/slash commands resolve it the same
        // way, and the facade attaches identically there.
        using var sp = CompositionFixtures.Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
            ToolSet = HarborToolSetKind.Standard10,
            IncludeMcpTools = false,
        });

        var runner = sp.GetRequiredService<ISubAgentRunner>();

        await Assert.That(runner).IsTypeOf<SubAgentRunner>();
        await Assert.That(runner.CanSpawn).IsTrue();
    }
}
