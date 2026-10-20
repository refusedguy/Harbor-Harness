using Harbor.Ui.Framework.State;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// Generic-screen projection (epic #1195, issue #1202): the neutral
/// <see cref="IProjector{UiState}"/> for <see cref="GenericScreen"/>.
/// The generic half owns no chat slices — no transcript, no costs, no agents —
/// so the only mirror is store focus into the screen marker (the screen owns
/// the marker, the store owns the truth). In-tree only, NOT a plugin axis — the
/// renderer owns the registry and no plugin can reach it — like its chat
/// sibling <see cref="ChatScreenProjector"/>.
/// </summary>
public sealed class GenericScreenProjector : IProjector<UiState>
{
    /// <summary>Registry key generic hosts project under.</summary>
    public const string GenericScreenId = "generic";

    private readonly Func<GenericScreen> _screen;

    public GenericScreenProjector(Func<GenericScreen> screen)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    /// <inheritdoc/>
    public string ScreenId => GenericScreenId;

    /// <inheritdoc/>
    public void Project(UiState state)
    {
        Ensure.NotNull(state);
        string? id = state.Ui.FocusedPanelId;
        if (id is null)
            return;
        _screen().SetFocus(id);
    }
}
