using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
///         Registered as a singleton in <c>ServiceRegistration</c>. At
///         construction time it pulls the <see cref="ICommonConfigStore" />
///         from the DI container (which is itself registered by
///         <c>ConfigRegistration</c>) and forwards each
///         <see cref="TryReadProviderModelAsync" /> call to
///         <see cref="ICommonConfigStore.LoadAsync" />.
///     </para>
/// </remarks>
public sealed class CommonConfigReaderAdapter : ICommonConfigReader
{
    private readonly IServiceProvider _services;

    /// <summary>Construct the adapter.</summary>
    public CommonConfigReaderAdapter(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public async Task<(string? ProviderId, string? ModelId)?> TryReadProviderModelAsync(
        CancellationToken cancellationToken = default)
    {
        var store = _services.GetService<ICommonConfigStore>();
        if (store is null) return null;

        var result = await store.LoadAsync().ConfigureAwait(false);
        if (!result.IsSuccess) return null;

        var cfg = result.Value;
        if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))
            return null;

        return (cfg.DefaultProvider, cfg.DefaultModel);
    }
}
