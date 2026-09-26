using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

public interface IUiProjector
{
    UiScreenModel Project(UiState state);
}