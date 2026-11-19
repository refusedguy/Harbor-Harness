using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Themes;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views.Components;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Settings dialog code-behind.
/// </summary>
public partial class SettingsView : UserControl
{
    private INotifyPropertyChanged? _boundContext;

    /// <summary>Construct the settings view.</summary>
    public SettingsView()
    {
        InitializeComponent();
        RebuildSections();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_boundContext is not null) _boundContext.PropertyChanged -= OnBoundContextPropertyChanged;
        _boundContext = DataContext as INotifyPropertyChanged;
        if (_boundContext is not null) _boundContext.PropertyChanged += OnBoundContextPropertyChanged;

        RebuildSections();
    }

    private void OnBoundContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SearchQuery)) RebuildSections();
    }

    /// <summary>
    ///     Filter the declared sections by the current search text, then hand
    ///     the remaining ones to the Accordion. Assigning <see cref="Accordion.Sections"/>
    ///     is what rebuilds its rows; the search never touches a row's IsVisible.
    /// </summary>
    private void RebuildSections()
    {
        string? query = (DataContext as ISettingsSearchContext)?.SearchQuery;
        var sections = new List<AccordionSection>(SettingsSections.All.Count);
        foreach (SettingsSectionDefinition definition in SettingsSections.All)
        {
            if (!SettingsSearch.IsMatch(definition.Aliases, query)) continue;
            if (Resources[definition.BodyKey] is not Control body) continue;
            sections.Add(new AccordionSection(
                definition.Id, definition.Header, definition.Description, Body: body));
        }

        SettingsSectionsAccordion.Sections = sections;

        // The old screen showed every section at once. Open every row the
        // filter kept so the accordion is a container, not a disclosure gate.
        foreach (AccordionSection section in sections)
        {
            if (!SettingsSectionsAccordion.ExpandedIds.Contains(section.Id))
                SettingsSectionsAccordion.Toggle(section.Id);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.CloseOverlay(OverlayIds.Settings);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.CancelCommand.Execute(null);

        CloseModal();
    }

    /// <summary>
    ///     Close the modal when the user clicks the dark backdrop outside
    ///     the card. Only fires when the click landed directly on the
    ///     backdrop, not on a child (the card eats the event).
    /// </summary>
    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            CloseModal();
        }
    }

    private void CloseModal()
    {
        ShellChrome.CloseOverlay(OverlayIds.Settings);
    }

    private void OnThemePreviewClick(object? sender, PointerPressedEventArgs e)
    {
        var current = e.Source as Visual;
        while (current is not null)
        {
            if (current is Border { DataContext: HdsThemePreview preview })
            {
                if (DataContext is ThemeSettingsViewModel vm)
                    vm.ApplyHdsThemeCommand.Execute(preview.Name);
                break;
            }
            current = current.GetVisualParent();
        }
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= ShellLocator.Of(this).Get<IShellChrome>();
}
