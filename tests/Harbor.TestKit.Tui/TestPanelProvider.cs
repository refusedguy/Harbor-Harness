using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.TestKit;

/// <summary>
///     An <see cref="IPanelProvider" /> that builds a declared widget and
///     consumes no keys.
/// </summary>
/// <remarks>
///     Five hand-written panel providers existed across four files, and they
///     were the same object with two dials: what the panel is called, and
///     what it builds. A panel that builds a string is a panel the decoder
///     renders; a panel that builds a widget object is a panel a decoder
///     under test is handed.
/// </remarks>
public sealed class TestPanelProvider : IPanelProvider
{
    /// <summary>
    ///     Creates a panel provider.
    /// </summary>
    /// <param name="id">The stable panel id.</param>
    /// <param name="title">The panel title.</param>
    /// <param name="widget">
    ///     What <see cref="Build" /> returns. A string, an
    ///     <see cref="IReadOnlyList{T}" /> of strings and an
    ///     <see cref="IEnumerable{T}" /> of strings are the three shapes the
    ///     shipped decoders understand; anything else is flattened to its
    ///     type name.
    /// </param>
    /// <param name="placement">Where the panel docks by default.</param>
    /// <param name="defaultSize">The default size, in rows or columns.</param>
    public TestPanelProvider(
        string id = "stub-panel",
        string title = "Stub Panel",
        object? widget = null,
        TuiPanelPlacement placement = TuiPanelPlacement.Right,
        int defaultSize = 10)
    {
        Id = id;
        Title = title;
        Widget = widget;
        DefaultPlacement = placement;
        DefaultSize = defaultSize;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement { get; }

    /// <inheritdoc />
    public int DefaultSize { get; }

    /// <summary>What <see cref="Build" /> returns.</summary>
    public object? Widget { get; }

    /// <inheritdoc />
    public object? Build(PanelContext ctx) => Widget;

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
