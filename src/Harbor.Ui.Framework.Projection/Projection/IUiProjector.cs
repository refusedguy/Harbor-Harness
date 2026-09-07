using Harbor.Ui.Framework.State;
namespace Harbor.Ui.Framework.Projection;

public interface IUiProjector
{
    public UiScreenModel Project(UiState state);
}
