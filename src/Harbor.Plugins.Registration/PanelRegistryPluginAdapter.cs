using Harbor.Plugins.Abstractions;
using Harbor.Tui.Abstractions.Panels;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Registration;

/// <summary>
///     Adapter that lets an <see cref="ITuiPanelPlugin" /> register panels through the
///     host's <see cref="IPluginLoadHost.RegisterPanelProvider" /> sink while only
///     depending on the <see cref="IPanelRegistry" /> interface.
/// </summary>
/// <remarks>
///     <para>
///         Plugin authors see a regular <c>IPanelRegistry</c>; under the hood every
///         <see cref="Register" /> call is forwarded into
///         <see cref="IPluginLoadHost.RegisterPanelProvider" />, which stores the
///         provider on the host. When the interactive renderer is later constructed,
///         it pulls the host's list and copies the providers into its own live
///         <see cref="PanelRegistry" />.
///     </para>
///     <para>
///         <b>Registration-only:</b> this adapter exposes just <see cref="Register" /> /
///         <see cref="Unregister" /> / <see cref="All" /> / <see cref="Get" />. There is
///         no state mutation surface — visibility / focus / size transitions flow
///         through <c>UiStore.Dispatch(UiMsg.*)</c> and live in <c>UiState</c> (TEA
///         compliance, §FP-005).
///     </para>
/// </remarks>
internal sealed class PanelRegistryPluginAdapter : IPanelRegistry
{
    private readonly IPluginLoadHost _host;
    private readonly ILogger _logger;

    internal PanelRegistryPluginAdapter(IPluginLoadHost host, ILogger logger)
    {
        _host = host;
        _logger = logger;
    }

    /// <inheritdoc />
    public Result Register(IPanelProvider panel)
    {
        var result = _host.RegisterPanelProvider(panel);
        if (result.IsFailure)
            _logger.LogWarning("Plugin panel registration failed for {Id}: {Error}", panel.Id, result.Error);
        return result;
    }

    /// <inheritdoc />
    public Result Unregister(string id) => Result.Success();

    /// <inheritdoc />
    public IReadOnlyList<IPanelProvider> All => Array.Empty<IPanelProvider>();

    /// <inheritdoc />
    public IPanelProvider? Get(string id) => null;
}
