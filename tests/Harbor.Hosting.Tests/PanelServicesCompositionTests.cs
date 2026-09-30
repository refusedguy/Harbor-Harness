using Harbor.Abstractions.Git;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Tui.CellForge;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #470 acceptance #2 — every consumer of a dependency that used to be resolved
///     through a service locator must get a service the composition root actually
///     registered, resolved at startup, not on a painted frame.
/// </summary>
/// <remarks>
///     <para>
///         Two locators died with this change. <c>ToolContext.Services</c> was
///         declared non-null but passed as <c>null!</c> by both production call
///         sites (<c>ToolDispatcher</c>, <c>McpStdioServer</c>), so seven tools
///         resolved dependencies from a container that never existed.
///         <c>PanelContext.Services</c> was a per-frame <c>IServiceProvider?</c>
///         that the CellForge dock never even filled. Both are gone; this file
///         proves the replacement wiring holds against a real composed container.
///     </para>
/// </remarks>
[NotInParallel("hosting")]
public class PanelServicesCompositionTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider ComposeDefault() => Compose(new HarborComposeOptions
    {
        HarborDir = TempHarborDir(),
        DefaultStorageBackend = "memory",
        IncludeMcpTools = true,
    });

    // ── the panel bag ────────────────────────────────────────────────────

    /// <summary>
    ///     Every <see cref="PanelServices" /> field whose service <c>AddHarbor</c> is
    ///     responsible for is actually resolvable — so the panels that read them
    ///     (help → PanelRegistry, subagents → SessionStore, any panel → UiStore)
    ///     get a value instead of degrading forever.
    /// </summary>
    [Test]
    public async Task AddHarbor_RegistersEveryServiceThePanelBagProjects()
    {
        using var sp = ComposeDefault();

        await Assert.That(sp.GetService<UiStore>()).IsNotNull();
        await Assert.That(sp.GetService<IPanelRegistry>()).IsNotNull();
        await Assert.That(sp.GetService<ISessionStore>()).IsNotNull();

        PanelServices deps = PanelServices.FromContainer(sp);

        await Assert.That(deps.Store).IsNotNull();
        await Assert.That(deps.PanelRegistry).IsNotNull();
        await Assert.That(deps.SessionStore).IsNotNull();

        // #666: the jump palette's worktree query. This is the assertion that
        // catches the mistake the change was most likely to make — registering
        // `IGitQuery` in the Avalonia app, beside the branch badge that already
        // had one, and leaving the CLI with a null query. The panel would then
        // list sessions only, quietly, on the renderer that is the CLI default,
        // and every gate in this repository would still be green.
        await Assert.That(sp.GetService<IGitQuery>()).IsNotNull();
        await Assert.That(deps.Git).IsNotNull();
    }

    /// <summary>
    ///     The services the CLI root does <i>not</i> register are null in the bag —
    ///     and the panels that want them degrade instead of throwing. Before #470
    ///     these lookups happened per frame and silently returned null anyway
    ///     (<c>ISessionManager</c> is a desktop-only registration, and
    ///     <c>IDiagnosticsPanel</c> is added by the app-level HostBuilder, not by
    ///     <c>AddHarbor</c>), so this pins the honest outcome.
    /// </summary>
    [Test]
    public async Task AddHarbor_UnregisteredPanelServices_DegradeToNull()
    {
        using var sp = ComposeDefault();

        PanelServices deps = PanelServices.FromContainer(sp);

        await Assert.That(deps.Sessions).IsNull();
        await Assert.That(deps.Diagnostics).IsNull();
    }

    /// <summary>
    ///     The CellForge backend hands the dock/overlay panels a bag built from the
    ///     live container, so a panel that reaches for the store or the registry
    ///     through <c>ctx.Deps</c> is wired in the interactive CLI too — not only in
    ///     the SpectreTUI shell.
    /// </summary>
    [Test]
    public async Task CellForgeBackend_ReceivesAProjectedPanelBag()
    {
        using var sp = ComposeDefault();

        using var renderer = new CellForgeTuiRenderer(
            sp.GetRequiredService<ILogger<CellForgeTuiRenderer>>(),
            store: sp.GetRequiredService<UiStore>(),
            panelServices: PanelServices.FromContainer(sp));

        await Assert.That(renderer.PanelDeps).IsNotNull();
        await Assert.That(renderer.PanelDeps.Store).IsNotNull();
        await Assert.That(renderer.PanelDeps.PanelRegistry).IsNotNull();
        await Assert.That(renderer.PanelDeps.SessionStore).IsNotNull();
    }

    /// <summary>
    ///     Without an explicit bag the renderer still builds one from the store and
    ///     session manager it was given — a host can never accidentally hand the
    ///     panels an empty bag while wiring the rest of the renderer.
    /// </summary>
    [Test]
    public async Task CellForgeBackend_DerivesTheBagFromItsOwnInjectedDependencies()
    {
        using var sp = ComposeDefault();
        var store = sp.GetRequiredService<UiStore>();

        using var renderer = new CellForgeTuiRenderer(
            sp.GetRequiredService<ILogger<CellForgeTuiRenderer>>(), store: store);

        await Assert.That(renderer.PanelDeps.Store).IsSameReferenceAs(store);
    }

    // ── the tool bag ─────────────────────────────────────────────────────

    /// <summary>
    ///     The services the seven former <c>ToolContext.Services</c> consumers now
    ///     take through their constructors are all resolvable in the root that builds
    ///     the registry — a missing one would have been a silent "No IMcpRegistry is
    ///     registered" style failure at call time instead of a startup fact.
    /// </summary>
    [Test]
    public async Task AddHarbor_RegistersEveryServiceTheToolCatalogInjects()
    {
        using var sp = ComposeDefault();

        await Assert.That(sp.GetService<ILspService>()).IsNotNull();
        await Assert.That(sp.GetService<IMcpRegistry>()).IsNotNull();
        await Assert.That(sp.GetService<ISessionStore>()).IsNotNull();
    }

    /// <summary>
    ///     The <c>read</c> / <c>edit</c> tools are always registered even when no
    ///     language server is wired — their LSP enrichment is optional, and the
    ///     composition root passes the (possibly null) service in rather than
    ///     letting the tool look one up per call.
    /// </summary>
    [Test]
    public async Task AddHarbor_RegistersReadAndEdit_EvenWithoutAnLspService()
    {
        using var sp = Compose(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
        });

        string[] names = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools().Select(t => t.Name.Value).ToArray();

        await Assert.That(names).Contains("read");
        await Assert.That(names).Contains("edit");
    }
}
