using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Panels;
/// <summary>
///     Immutable per-frame context handed to <see cref="IPanelProvider.Build" /> and
///     <see cref="IPanelProvider.OnKey" />. Captures the current <see cref="UiState" />,
///     the available geometry, and the typed panel dependencies.
/// </summary>
/// <param name="State">The current immutable UI snapshot.</param>
/// <param name="Width">Available width in terminal columns for this panel.</param>
/// <param name="Height">Available height in terminal rows for this panel.</param>
/// <param name="Services">
///     Typed panel dependencies. #470: this used to be
///     <c>IServiceProvider? Services</c> plus a separate <c>UiStore? Store</c>,
///     so a panel could resolve an arbitrary service that the host might never
///     have registered — a latent <see cref="NullReferenceException" /> behind a
///     per-frame lookup. It is now a <see cref="PanelServices" /> bag built once by
///     the composition root; every field is honestly nullable and every consumer
///     handles absence. Pass <see langword="null" /> (or omit) for a host that
///     offers no panel dependencies at all.
/// </param>
public sealed record PanelContext(
    UiState State,
    int Width,
    int Height,
    PanelServices? Services = null)
{
    /// <summary>
    ///     Never <see langword="null" />: <see cref="PanelServices.Empty" /> when the
    ///     host passed no dependencies, so a panel can read a field without first
    ///     null-checking the bag itself.
    /// </summary>
    public PanelServices Deps => Services ?? PanelServices.Empty;
}
