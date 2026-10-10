using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Configuration;
using Harbor.Abstractions.Events;
using Harbor.Registries.Events;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Panels;
using Microsoft.Extensions.Logging;
using Harbor.Registries.Agents;
using Harbor.Registries.Providers;
using Harbor.Registries.Tools;

namespace Harbor.Hosting;

/// <summary>
///     Eagerly-constructed composition state shared by the AddHarbor modules.
///     Replaces the four temporary <c>BuildServiceProvider()</c> calls and the
///     static logger fields of the old CLI HostBuilder: the bootstrap logger
///     factory lives for the process lifetime, configs are loaded exactly once
///     and stored here, the event bus is constructed explicitly and published
///     as an instance, and the registries are built before being frozen.
/// </summary>
/// <remarks>
///     <para>
///         <b>#562 — the two members a later module assigns</b> (<see cref="EventBus" />
///         and <see cref="Registries" />) were <c>= null!</c> with an internal
///         setter. Nothing recorded that they are non-null only because an
///         EARLIER <c>AddHarbor*</c> module assigned them, so a module reordered in
///         <see cref="Registration.AddHarbor" /> did not fail: it read a null and
///         produced a <c>NullReferenceException</c> three frames deeper, or — worse
///         — a silently empty registry (<c>StorageModule</c> reads
///         <c>ctx.Registries.SessionStores</c> and, running first, composed a
///         container that could never see a plugin backend).
///     </para>
///     <para>
///         The C# <c>non-nullable</c> annotation is a contract the compiler checks
///         in one file and not at all once the value arrives through DI. So the
///         two members are now assigned exactly once, through
///         <see cref="SetEventBus" /> / <see cref="SetRegistries" />, and a read
///         before the assignment THROWS — naming the member and the module that
///         owns it — instead of handing out a null.
///     </para>
///     <para>
///         Not <c>Maybe&lt;T&gt;</c> (the shape the #613 wave applied elsewhere in
///         this project): every consumer of these two members requires them, so
///         absence is not a state to model but a composition bug to report at the
///         call site. A <c>Maybe</c> would have moved the failure further down and
///         given every reader a branch that can only ever take one arm.
///     </para>
/// </remarks>
public sealed class HarborCompositionContext
{
    private IEventBus? _eventBus;
    private HarborRegistries? _registries;

    public HarborCompositionContext(HarborComposeOptions options, ILoggerFactory loggerFactory)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger("Harbor.Hosting");
    }

    public HarborComposeOptions Options { get; }

    /// <summary>Process-lifetime bootstrap logger factory (Avalonia pattern).</summary>
    public ILoggerFactory LoggerFactory { get; }

    public ILogger Logger { get; }

    /// <summary>Eagerly loaded common config (loaded once, §3.4 of di-design). Apps may override via AfterConfiguration.</summary>
    public CommonConfig Common { get; set; } = new();

    /// <summary>config.json → HarborConfig (model/tools overrides), env-overridden.</summary>
    public HarborConfig Harbor { get; internal set; } = new();

    /// <summary>
    ///     Explicitly constructed event bus; registered as an instance. Assigned
    ///     exactly once by <c>AddHarborConfiguration</c>, which runs first in
    ///     <see cref="Registration.AddHarbor" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Read before <c>AddHarborConfiguration</c> assigned it — the DI modules
    ///     ran out of order. The message names both.
    /// </exception>
    public IEventBus EventBus => _eventBus ?? throw Unassigned(nameof(EventBus), "AddHarborConfiguration(options)");

    /// <summary>
    ///     Eager registries (agents / tools / providers / panels), frozen before
    ///     publication. Assigned exactly once by <c>AddHarborRegistries</c>; the
    ///     modules that read it (<c>StorageModule</c>, <c>TuiModule</c>) run after it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Read before <c>AddHarborRegistries</c> assigned it — the DI modules ran
    ///     out of order. The message names both.
    /// </exception>
    public HarborRegistries Registries =>
        _registries ?? throw Unassigned(nameof(Registries), "AddHarborRegistries(ctx)");

    /// <summary>Assigns the process event bus. Once per context.</summary>
    internal void SetEventBus(IEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        if (_eventBus is not null)
        {
            throw AlreadyAssigned(nameof(EventBus), "AddHarborConfiguration(options)", "split the process over two buses");
        }

        _eventBus = eventBus;
    }

    /// <summary>Publishes the eager registry bundle. Once per context.</summary>
    internal void SetRegistries(HarborRegistries registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        if (_registries is not null)
        {
            throw AlreadyAssigned(
                nameof(Registries), "AddHarborRegistries(ctx)", "split the process over two registry sets");
        }

        _registries = registries;
    }

    /// <summary>
    ///     The end-of-composition backstop, called by <see cref="Registration.AddHarbor" />.
    ///     The throwing getters above catch a module that reads state before its
    ///     owner assigned it; this catches the other half — a module that was
    ///     supposed to assign never ran at all, so nothing read the hole during
    ///     composition and the process only discovered it on first use.
    /// </summary>
    internal void AssertFullyInitialized()
    {
        List<string> missing = [];
        if (_eventBus is null)
        {
            missing.Add($"{nameof(EventBus)} (assigned by AddHarborConfiguration)");
        }

        if (_registries is null)
        {
            missing.Add($"{nameof(Registries)} (assigned by AddHarborRegistries)");
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Harbor composition finished with unassigned state: "
            + string.Join("; ", missing)
            + ". Registration.AddHarbor runs the modules in a fixed order (di-design §3.5): configuration → "
            + "telemetry/core → registries → intelligence → http-clients → storage → TUI → IPC. A member that is "
            + "still missing at the end was never assigned: either its module was dropped from that chain, or it "
            + "was moved after every reader of it and the readers therefore composed against a hole.");
    }

    private static InvalidOperationException Unassigned(string member, string owner) =>
        new(
            $"Harbor composition state '{member}' was read before it was assigned. {owner} owns that assignment "
            + "and must run first: the module order in Registration.AddHarbor is fixed (di-design §3.5). A read "
            + "here means the AddHarbor modules were invoked out of order, or without the module that owns "
            + $"'{member}'.");

    private static InvalidOperationException AlreadyAssigned(string member, string owner, string consequence) =>
        new(
            $"Harbor composition state '{member}' is already assigned. It is assigned exactly once, by {owner}. "
            + $"A second assignment means that module ran twice, which would {consequence}.");
}

/// <summary>
///     The eager registry bundle built by <c>AddHarborRegistries</c>.
/// </summary>
/// <remarks>
///     #562: the four registries used to be <c>= null!</c> properties with an
///     internal setter, filled in one by one at the end of
///     <c>AddHarborRegistries</c> — so a bundle that existed was a bundle that
///     lied about four of its members. They are constructor arguments now, which
///     makes "all four or none" a fact the compiler enforces at the one place
///     that builds them. Only the two plugin-contributed maps remain assignable,
///     and they default to empty rather than to null (#581).
/// </remarks>
public sealed class HarborRegistries
{
    public HarborRegistries(
        AgentRegistry agents,
        ToolRegistry tools,
        ProviderRegistry providers,
        PanelRegistry panels)
    {
        Agents = agents ?? throw new ArgumentNullException(nameof(agents));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Providers = providers ?? throw new ArgumentNullException(nameof(providers));
        Panels = panels ?? throw new ArgumentNullException(nameof(panels));
    }

    public AgentRegistry Agents { get; }

    public ToolRegistry Tools { get; }

    public ProviderRegistry Providers { get; }

    public PanelRegistry Panels { get; }

    /// <summary>
    ///     Session-store backends a plugin registered through
    ///     <c>IPluginLoadHost.RegisterSessionStore</c> during the startup plugin load in
    ///     <c>AddHarborRegistries</c>, read by <c>StorageModule</c> when it builds the
    ///     storage registry (#581). Empty when the plugin stack is compiled out.
    /// </summary>
    /// <remarks>
    ///     Startup only. Out-of-process (#1055s3) no plugin backend reaches
    ///     these maps — CS-plugin session stores and TUI backends are not
    ///     served over the MCP route — so they stay at their empty defaults
    ///     and StorageModule/TuiModule snapshot emptiness. Known limitation,
    ///     not a silent gap.
    /// </remarks>
    public IReadOnlyDictionary<string, Func<ISessionStore>> SessionStores { get; internal set; } =
        new Dictionary<string, Func<ISessionStore>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     TUI backends a plugin registered through
    ///     <c>IPluginLoadHost.RegisterTuiBackend</c> during the startup plugin load, read
    ///     by <c>TuiModule</c> when it builds the backend registry — the same registry
    ///     the runtime-swap table is derived from, so a plugin backend is listed by
    ///     <c>/renderer</c> and can be swapped to too (#581/#584). Startup-only, like
    ///     <see cref="SessionStores" />.
    /// </summary>
    public IReadOnlyDictionary<string, PluginTuiBackend> TuiBackends { get; internal set; } =
        new Dictionary<string, PluginTuiBackend>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Freezes the two registries that support it. Safe by construction since
    ///     #562: the members are constructor arguments, so there is no window in
    ///     which this can be called on a half-built bundle.
    /// </summary>
    internal void Freeze()
    {
        Tools.Freeze();
        Providers.Freeze();
    }
}
