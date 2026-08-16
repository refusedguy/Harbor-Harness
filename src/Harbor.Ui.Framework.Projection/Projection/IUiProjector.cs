using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

public interface IUiProjector
{
    UiScreenModel Project(UiState state);
}