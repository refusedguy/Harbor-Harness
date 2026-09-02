using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Wpf.Services;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Root view model for the main window. Coordinates the sidebar, the
///     active main-panel VM, and the global status bar.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly ToastNotificationViewModel _toasts;

    /// <summary>The currently selected panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePanelContent))]
    private PanelTab? _activePanel;

    /// <summary>Cost summary (status bar).</summary>
    [ObservableProperty] private string _costText = "$0.0000";

    /// <summary>Whether the agent is currently running.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>Active model id (status bar).</summary>
    [ObservableProperty] private string _model = "—";

    /// <summary>Active provider id (status bar).</summary>
    [ObservableProperty] private string _provider = "ollama";

    /// <summary>Status text shown in the status bar.</summary>
    [ObservableProperty] private string _statusText = "idle";

    /// <summary>Window title.</summary>
    [ObservableProperty] private string _title = "Harbor";

    /// <summary>Token usage summary (status bar).</summary>
    [ObservableProperty] private string _tokenCount = "0 in / 0 out";

    /// <summary>Construct the <see cref="MainViewModel" />.</summary>
    /// <param name="theme">Theme service.</param>
    /// <param name="dialogs">Dialog service.</param>
    /// <param name="chat">Chat VM (active by default).</param>
    /// <param name="sessions">Session list VM (sidebar).</param>
    /// <param name="tokens">Token usage VM.</param>
    /// <param name="editor">Code editor VM.</param>
    /// <param name="diff">Diff VM.</param>
    /// <param name="toasts">Toast notifications VM.</param>
    public MainViewModel(
        ThemeService theme,
        DialogService dialogs,
        ChatViewModel chat,
        SessionListViewModel sessions,
        TokenUsageViewModel tokens,
        CodeEditorViewModel editor,
        DiffViewModel diff,
        ToastNotificationViewModel toasts)
    {
        _theme = theme;
        _dialogs = dialogs;
        _toasts = toasts;

        Chat = chat;
        Sessions = sessions;
        Tokens = tokens;
        Editor = editor;
        Diff = diff;
        Toasts = toasts;

        Panels = new ObservableCollection<PanelTab>
        {
            new("chat", "Chat", chat),
            new("editor", "Editor", editor),
            new("diff", "Diff", diff),
            new("tokens", "Tokens", tokens)
        };

        ActivePanel = Panels[0];
        Title = "Harbor — AI Coding Agent";
        Provider = "ollama";
        Model = "—";
        StatusText = "idle";
        TokenCount = "0 in / 0 out";
        CostText = "$0.0000";
    }

    /// <summary>Collection of dockable panels.</summary>
    public ObservableCollection<PanelTab> Panels { get; }

    /// <summary>Content for the active panel (bound to ContentPresenter).</summary>
    public ObservableObject? ActivePanelContent => ActivePanel?.Content;

    /// <summary>The chat view model.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>The session list view model.</summary>
    public SessionListViewModel Sessions { get; }

    /// <summary>The token usage view model.</summary>
    public TokenUsageViewModel Tokens { get; }

    /// <summary>The code editor view model.</summary>
    public CodeEditorViewModel Editor { get; }

    /// <summary>The diff view model.</summary>
    public DiffViewModel Diff { get; }

    /// <summary>The toast notifications view model.</summary>
    public ToastNotificationViewModel Toasts { get; }

    /// <summary>Toggle between Dark and Light themes.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.Toggle();
        _toasts.Show($"Theme: {_theme.Current}");
    }

    /// <summary>Open the provider + model browser.</summary>
    [RelayCommand]
    private void BrowseProviders() => _dialogs.ShowProviderBrowser();

    /// <summary>Open the settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    /// <summary>Open the command palette (Ctrl+P).</summary>
    [RelayCommand]
    private void OpenCommandPalette(Window? owner)
    {
        if (owner is null) return;
        _dialogs.ShowCommandPalette(owner);
    }

    /// <summary>Bring a panel to front by id.</summary>
    /// <param name="panelId">Panel id to activate.</param>
    public void ActivatePanel(string panelId)
    {
        for (int i = 0; i < Panels.Count; i++)
        {
            if (Panels[i].Id == panelId)
            {
                ActivePanel = Panels[i];
                return;
            }
        }
    }
}

/// <summary>
///     A dockable panel tab. Wraps a name + a view-model.
/// </summary>
/// <param name="Id">Stable panel id.</param>
/// <param name="Title">Display title shown on the tab.</param>
/// <param name="Content">The panel's view model.</param>
public sealed record PanelTab(string Id, string Title, ObservableObject Content);
