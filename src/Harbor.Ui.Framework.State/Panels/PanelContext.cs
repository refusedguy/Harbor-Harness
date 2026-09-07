using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Immutable per-frame context handed to <see cref="IPanelProvider.Build" /> and
///     <see cref="IPanelProvider.OnKey" />. Captures the current <see cref="UiState" />,
///     the available geometry, and a service provider for DI-based panels.
/// </summary>
/// <param name="State">The current immutable UI snapshot.</param>
/// <param name="Width">Available width in terminal columns for this panel.</param>
/// <param name="Height">Available height in terminal rows for this panel.</param>
/// <param name="Services">DI service provider (may be <see langword="null" /> in tests).</param>
public sealed record PanelContext(
    UiState State,
    int Width,
    int Height,
    IServiceProvider? Services = null);
