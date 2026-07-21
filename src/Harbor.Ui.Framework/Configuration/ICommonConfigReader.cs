namespace Harbor.Ui.Framework.Configuration;
/// <summary>
///     Cross-platform contract for reading the per-user Harbor
///     "common config" (provider, model, theme, etc.). Mirrors the
///     <c>Harbor.Desktop.Abstractions.Configuration.ICommonConfigStore</c>
///     interface but lives in <c>Harbor.Ui.Framework</c> so that
///     <c>SessionFactory</c> (also in Ui.Framework) can depend on it
///     without creating a circular project reference (Ui.Framework ->
///     Desktop.Abstractions -> Terminal.Abstractions -> Ui.Framework).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a separate interface?</b> Harbor.Desktop.Abstractions
///         transitively references Harbor.Ui.Framework (via
///         Harbor.Terminal.Abstractions), so Ui.Framework cannot reference
///         Desktop.Abstractions back. The Desktop.Abstractions
///         implementation (<c>JsonCommonConfigStore</c>) implements BOTH
///         <c>Harbor.Desktop.Abstractions.Configuration.ICommonConfigStore</c>
///         and this interface - the dual implementation is registered in
///         each platform's DI container.
///     </para>
///     <para>
///         This interface exposes only the subset of the config that
///         Ui.Framework / SessionFactory actually needs: the provider id
///         and model id. Other config fields (theme, log level, recent
///         sessions) stay on the Desktop.Abstractions type.
///     </para>
/// </remarks>
public interface ICommonConfigReader
{
    /// <summary>
    ///     Read the persisted provider/model choice, or null if the
    ///     config file doesn't exist yet (first-launch / pre-onboarding).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A (<c>ProviderId</c>, <c>ModelId</c>) tuple, or null if no
    ///     config has been written yet.
    /// </returns>
    public Task<(string? ProviderId, string? ModelId)?> TryReadProviderModelAsync(
        CancellationToken cancellationToken = default);
}
