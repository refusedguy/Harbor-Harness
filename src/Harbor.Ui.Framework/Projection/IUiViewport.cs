using Harbor.Ui.Framework.Projection;

namespace Harbor.Ui.Framework.Projection;

public interface IUiViewport
{
    void Apply(UiScreenModel screen);
}