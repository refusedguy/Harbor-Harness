namespace Harbor.Hosting.Tests;

/// <summary>
///     Issue #1525 (Structural/Facade): <c>TuiBackendRegistry.ResolveBackend</c> is the
///     single-call facade over the <c>Build</c> + <c>Resolve</c> + fallback dance.
///     Pins that the facade resolves a known id, an alias, and an unknown id (→ the
///     single build-dependent <c>FallbackBackendId</c> rule), with and without the
///     plugin door — and that it agrees with the explicit two-step call it wraps.
/// </summary>
public class TuiBackendFacadeTests
{
    [Test]
    public async Task Facade_KnownId_ResolvesBackend()
    {
        ITuiRendererFactory viaFacade = TuiBackendRegistry.ResolveBackend("cellforge");

        await Assert.That(viaFacade.BackendId).IsEqualTo("cellforge");
        await Assert.That(viaFacade.BackendId)
            .IsEqualTo(TuiBackendRegistry.Resolve(TuiBackendRegistry.Build(), "cellforge").BackendId);
    }

    [Test]
    public async Task Facade_KnownId_NormalizesTrimAndCase()
    {
        ITuiRendererFactory viaFacade = TuiBackendRegistry.ResolveBackend("  PLAIN  ");

        await Assert.That(viaFacade.BackendId).IsEqualTo("plain");
    }

    [Test]
    public async Task Facade_Alias_ResolvesCanonicalBackend()
    {
        ITuiRendererFactory viaFacade = TuiBackendRegistry.ResolveBackend("consoleex");

        await Assert.That(viaFacade.BackendId).IsEqualTo("cellforge");
        await Assert.That(viaFacade.BackendId)
            .IsEqualTo(TuiBackendRegistry.Resolve(TuiBackendRegistry.Build(), "consoleex").BackendId);
    }

    [Test]
    public async Task Facade_UnknownId_FallsBackToFallbackBackendId()
    {
        ITuiRendererFactory viaFacade = TuiBackendRegistry.ResolveBackend("bogus-tui-1525");

        // The single fallback rule is build-dependent (ansi with Spectre, plain
        // without), so the test names the rule, not a literal id — green in both builds.
        await Assert.That(viaFacade.BackendId).IsEqualTo(TuiBackendRegistry.FallbackBackendId);
        await Assert.That(viaFacade.BackendId == "ansi" || viaFacade.BackendId == "plain").IsTrue();
        await Assert.That(viaFacade.BackendId)
            .IsEqualTo(TuiBackendRegistry.Resolve(TuiBackendRegistry.Build(), "bogus-tui-1525").BackendId);
    }

    [Test]
    public async Task Facade_PluginBackend_ResolvesThroughThePluginDoor()
    {
        var pluginBackends = new Dictionary<string, PluginTuiBackend>(StringComparer.OrdinalIgnoreCase)
        {
            ["web"] = new PluginTuiBackend(["webui"], () => new Harbor.Tui.AnsiPlain.PlainTuiRenderer()),
        };

        ITuiRendererFactory viaFacade = TuiBackendRegistry.ResolveBackend("web", pluginBackends);

        await Assert.That(viaFacade.BackendId).IsEqualTo("web");
        await Assert.That(viaFacade).IsTypeOf<PluginTuiRendererFactory>();

        // Aliases ride the same facade.
        await Assert.That(TuiBackendRegistry.ResolveBackend("webui", pluginBackends).BackendId)
            .IsEqualTo("web");

        // Compiled-in backends survive the merge, unknowns still hit the fallback.
        await Assert.That(TuiBackendRegistry.ResolveBackend("plain", pluginBackends).BackendId)
            .IsEqualTo("plain");
        await Assert.That(TuiBackendRegistry.ResolveBackend("bogus-tui-1525", pluginBackends).BackendId)
            .IsEqualTo(TuiBackendRegistry.FallbackBackendId);
    }
}
