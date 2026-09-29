using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Typed, immutable bag of the collaborators framework panels are allowed to
///     reach for (#470) — the explicit replacement for the
///     <c>IServiceProvider</c> that used to ride along in
///     <see cref="PanelContext" /> on every frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this shape:</b> a service locator in a per-frame contract hides
///         two things: what a panel actually needs, and whether the host ever
///         registered it. Both are now visible — the fields are named, typed and
///         nullable, and a <see langword="null" /> means one specific thing:
///         "this host did not register it".
///     </para>
///     <para>
///         <b>How it is filled:</b> exactly once, at composition time, by
///         <see cref="FromContainer(IServiceProvider)" /> (or by hand in tests).
///         The container lookup is a legitimate one: composition roots are the
///         only place allowed to know the whole graph. The per-frame contract
///         carries values, never the container.
///     </para>
///     <para>
///         <b>Panel-side contract:</b> every field is optional and every consumer
///         must handle absence — a panel with no session store shows
///         "(session store unavailable)", never a <c>NullReferenceException</c>.
///     </para>
/// </remarks>
public sealed record PanelServices
{
    /// <summary>The all-absent bag: a host that registered nothing (headless, tests).</summary>
    public static PanelServices Empty { get; } = new();

    /// <summary>UI store owning the state transitions; null when the panel may not mutate state.</summary>
    public UiStore? Store { get; init; }

    /// <summary>Panel registry, so help-style panels can list siblings; null disables the listing.</summary>
    public IPanelRegistry? PanelRegistry { get; init; }

    /// <summary>Log ring buffer behind the logs/diagnostics panels; null shows a placeholder.</summary>
    public IDiagnosticsPanel? Diagnostics { get; init; }

    /// <summary>Session persistence, used by the sub-agents panel; null shows "session store unavailable".</summary>
    public ISessionStore? SessionStore { get; init; }

    /// <summary>Live session facts for the sub-agents / jump-palette panels; null disables the enrichment.</summary>
    public IPanelSessionGateway? Sessions { get; init; }

    /// <summary>
    ///     Project a container into the bag, once, at composition time. Uses
    ///     <see cref="IServiceProvider.GetService(Type)" /> semantics: a service
    ///     the host never registered lands as <see langword="null" /> rather than
    ///     throwing — the same degradation the panels already implemented, now
    ///     decided at startup instead of on every painted frame.
    /// </summary>
    /// <param name="container">The composition root's service provider.</param>
    /// <remarks>
    ///     <para>
    ///         <b>This method takes a container on purpose, and the #470 guard
    ///         deliberately does not flag it</b>
    ///         (<c>ServiceLocatorBoundaryRules</c>, which sweeps this assembly).
    ///         Worth stating plainly, because the guard's scope looks like an
    ///         oversight otherwise and invites one of two wrong "fixes": widening
    ///         the rule until it reddens here, or assuming the rule is full of
    ///         holes.
    ///     </para>
    ///     <para>
    ///         The difference is <em>who holds the container</em>. A service
    ///         locator is a container kept as a field of an object that lives
    ///         longer than the composition, so its dependencies are invisible in
    ///         the signature and it can resolve anything, at any time, from
    ///         anywhere. Here the container is an argument to a static factory,
    ///         called exactly once by a composition root
    ///         (<c>TuiBackendRegistry</c> / <c>TuiModule</c>), and its only
    ///         product is this immutable record of named, typed fields. The
    ///         per-frame contract carries values; the graph is consulted at
    ///         startup, once, where knowing the whole graph is the job.
    ///     </para>
    ///     <para>
    ///         The rule matches fields and constructor parameters — shapes that
    ///         persist a container for the lifetime of an instance — and not
    ///         ordinary method parameters. A per-frame method that accepted a
    ///         live container would be a genuine violation and is the case that
    ///         deserves its own rule with a per-site baseline, not an
    ///         exception smuggled in here.
    ///     </para>
    /// </remarks>
    public static PanelServices FromContainer(IServiceProvider container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return new PanelServices
        {
            Store = container.GetService(typeof(UiStore)) as UiStore,
            PanelRegistry = container.GetService(typeof(IPanelRegistry)) as IPanelRegistry,
            Diagnostics = container.GetService(typeof(IDiagnosticsPanel)) as IDiagnosticsPanel,
            SessionStore = container.GetService(typeof(ISessionStore)) as ISessionStore,
            Sessions = container.GetService(typeof(IPanelSessionGateway)) as IPanelSessionGateway,
        };
    }

    /// <summary>Return a copy with <paramref name="store" /> replaced (per-session store rebinding).</summary>
    /// <param name="store">The store to use.</param>
    public PanelServices WithStore(UiStore? store) => this with { Store = store };
}
