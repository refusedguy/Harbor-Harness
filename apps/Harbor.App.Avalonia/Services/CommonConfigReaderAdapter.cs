using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Configuration;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Adapter that bridges the Desktop.Abstractions
///     <see cref="ICommonConfigStore" /> to the Ui.Framework
///     <see cref="ICommonConfigReader" /> contract. Without this adapter,
///     <c>SessionFactory</c> (in Ui.Framework) couldn't read the persisted
///     provider/model from the on-disk config because Ui.Framework can't
///     reference Desktop.Abstractions (circular project dependency via
///     Terminal.Abstractions).
/// </summary>
/// <remarks>
///     <para>
///         Registered as a singleton in <c>ConfigRegistration</c>, which hands
///         it the <see cref="ICommonConfigStore" /> it forwards to (that store
///         is registered a few lines above). It forwards each
///         <see cref="TryReadProviderModelAsync" /> call to
///         <see cref="ICommonConfigStore.LoadAsync" />.
///     </para>
///     <para>
///         <b>No service locator (#470):</b> the adapter used to hold the whole
///         <c>IServiceProvider</c> and call <c>GetService</c> on every read,
///         which is how <c>SessionFactory</c> ended up doing a container lookup
///         per session creation. The bridge now depends on the one interface it
///         forwards to.
///     </para>
/// </remarks>
public sealed class CommonConfigReaderAdapter : ICommonConfigReader
{
    private readonly ICommonConfigStore _store;

    /// <summary>Construct the adapter.</summary>
    /// <param name="store">The shared-config store this reader forwards to.</param>
    public CommonConfigReaderAdapter(ICommonConfigStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public async Task<(string? ProviderId, string? ModelId)?> TryReadProviderModelAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _store.LoadAsync().ConfigureAwait(false);
        if (!result.IsSuccess) return null;

        var cfg = result.Value;
        if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))
            return null;

        return (cfg.DefaultProvider, cfg.DefaultModel);
    }
}
