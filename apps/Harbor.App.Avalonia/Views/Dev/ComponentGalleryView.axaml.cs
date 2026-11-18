using Avalonia.Controls;
using Harbor.App.Avalonia.Views.Components;

namespace Harbor.App.Avalonia.Views.Dev;

public partial class ComponentGalleryView : UserControl
{
    public ComponentGalleryView()
    {
        InitializeComponent();

        // K25: the gallery is the living reference for the HDS Accordion.
        // Static sample sections keep the demo independent of app state.
        SingleAccordion.Sections =
        [
            new AccordionSection("single-appearance", "Appearance", "Theme, palette and font settings."),
            new AccordionSection("single-provider", "Provider", "Default provider and model."),
            new AccordionSection("single-storage", "Storage (disabled)", "Read-only in the gallery.", IsDisabled: true),
        ];
        MultipleAccordion.Sections =
        [
            new AccordionSection("multi-session", "Sessions", "Recent sessions and their status."),
            new AccordionSection("multi-tools", "Tools", "Builtin and plugin tools."),
        ];
    }
}
