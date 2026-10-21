namespace Harbor.Tools.Builtin;

/// <summary>
///     One axis of plugin extension, as the runtime sees it: the marker a plugin
///     implements, the door the host offers, and what the route does with that
///     axis.
/// </summary>
/// <param name="Marker">
///     The plugin-implementable interface — <c>IToolPlugin</c>,
///     <c>IProviderPlugin</c>, … — as the product names it.
/// </param>
/// <param name="Door">
///     The <c>Register*</c> member of the plugin load host that receives the
///     contribution.
/// </param>
/// <param name="Route">
///     The route the verdict belongs to — the id of the plugin route the product
///     declares.
/// </param>
/// <param name="Carriage">
///     What the route does with the axis: <c>carried</c>, <c>refused</c> or
///     <c>closed</c>. The three words are the whole vocabulary; the reason
///     beside them says why.
/// </param>
/// <param name="Reason">
///     Why the route does what it does, in one sentence. A verdict without a
///     reason is a decision nobody can act on.
/// </param>
/// <remarks>
///     The projection of <c>PluginRouteAxisRegistry</c> and
///     <c>PluginAxisRegistry</c> — the product's own declaration of what a
///     plugin may contribute and what each route does with it. The facts are
///     copied at composition time rather than read here, because the tools
///     assembly sits below the plugin assemblies in the layer matrix and cannot
///     reach them; the composition root owns the copy, and this type is the
///     shape it copies into.
/// </remarks>
public sealed record RuntimeExtensionAxis(
    string Marker,
    string Door,
    string Route,
    string Carriage,
    string Reason);

/// <summary>
///     One provider the runtime knows about, as the provider catalogue declares
///     it.
/// </summary>
/// <param name="Id">Stable lowercase provider id (<c>kilocode</c>, <c>ollama</c>, …).</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="DefaultModel">The model the provider defaults to, without the provider prefix.</param>
/// <param name="RequiresApiKey">Whether the provider needs a credential to be reachable.</param>
/// <param name="SetupHint">Where to get a credential, when the provider says.</param>
/// <remarks>
///     <para>
///         There is no credential field, and that is the point: the catalog
///         carries what a provider IS, never what it is CONFIGURED with. The
///         environment variable a provider reads is a name, not a value, and
///         the value never reaches this type — so a catalog printed into a
///         model's context cannot carry a key out of the process.
///     </para>
/// </remarks>
public sealed record RuntimeProviderEntry(
    string Id,
    string DisplayName,
    string DefaultModel,
    bool RequiresApiKey,
    string? SetupHint);

/// <summary>
///     The facts about the runtime that no registry in the domain exposes: the
///     Harbor version, the plugin manifest schema version, the plugin extension
///     axes and their per-route verdicts, and the provider catalogue.
/// </summary>
/// <param name="HarborVersion">The Harbor version the running plugins are checked against.</param>
/// <param name="PluginSchemaVersion">The plugin manifest schema version this build accepts.</param>
/// <param name="ExtensionAxes">Every extension axis and what each route does with it.</param>
/// <param name="KnownProviders">Every provider the catalogue knows about.</param>
/// <remarks>
///     <para>
///         Everything else the catalog reports — tools, agents, registered
///         providers, skills, MCP servers — is read live from the registries
///         that own it, because those change while the process runs. These four
///         facts do not: they are declared once, in the product, and a snapshot
///         of them cannot go stale.
///     </para>
/// </remarks>
public sealed record RuntimeApiFacts(
    string HarborVersion,
    string PluginSchemaVersion,
    IReadOnlyList<RuntimeExtensionAxis> ExtensionAxes,
    IReadOnlyList<RuntimeProviderEntry> KnownProviders)
{
    /// <summary>
    ///     The empty snapshot — a host that composes the catalog without the
    ///     product's static facts. The sections that read it say so instead of
    ///     inventing a value.
    /// </summary>
    public static RuntimeApiFacts Empty { get; } = new("unknown", "unknown", [], []);
}
