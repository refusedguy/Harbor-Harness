using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Board;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harbor.Ui.Framework.Navigation;
using Harbor.Desktop.Abstractions.ViewModels;

namespace Harbor.App.Avalonia.Navigation;

/// <summary>
///     Avalonia-specific content host. Resolves shell view-models from DI
///     and exposes the currently active one through <see cref="IContentHost.ActiveView"/>.
///     <para>
///     <see cref="CommandPaletteViewModel"/> is intentionally omitted: it holds
///     a direct <see cref="MainViewModel"/> dependency, so it stays lazy-resolved
///     inside <see cref="MainViewModel"/> to avoid a DI cycle.
///     </para>
/// </summary>
public sealed class AvaloniaContentHost : IContentHost
{
    public object? ActiveView { get; private set; }

    public ChatViewModel Chat { get; }
    public SessionListViewModel Sessions { get; }
    public CodeEditorViewModel CodeEditor { get; }
    public Harbor.Desktop.Abstractions.ViewModels.DiffViewModel Diff { get; }
    public TokenUsageViewModel TokenUsage { get; }
    public Harbor.Desktop.Abstractions.ViewModels.ProviderBrowserViewModel ProviderBrowser { get; }
    public Harbor.App.Avalonia.ViewModels.ProviderModelPickerViewModel ProviderModelPicker { get; }
    public SettingsViewModel Settings { get; }
    public Harbor.App.Avalonia.ViewModels.FocusSessionViewModel FocusSession { get; }
    public BoardViewModel Board { get; }

    private readonly ILogger<AvaloniaContentHost> _logger;

    public IReadOnlyList<string> AvailableRoutes { get; } = new[]
    {
        "chat",
        "sessions",
        "code",
        "diff",
        "tokenUsage",
        "settings",
        "board"
    };

    public AvaloniaContentHost(
        ChatViewModel chat,
        SessionListViewModel sessions,
        CodeEditorViewModel codeEditor,
        Harbor.Desktop.Abstractions.ViewModels.DiffViewModel diff,
        TokenUsageViewModel tokenUsage,
        Harbor.Desktop.Abstractions.ViewModels.ProviderBrowserViewModel providerBrowser,
        Harbor.App.Avalonia.ViewModels.ProviderModelPickerViewModel providerModelPicker,
        SettingsViewModel settings,
        Harbor.App.Avalonia.ViewModels.FocusSessionViewModel focusSession,
        BoardViewModel board,
        ILogger<AvaloniaContentHost> logger)
    {
        Chat = chat;
        Sessions = sessions;
        CodeEditor = codeEditor;
        Diff = diff;
        TokenUsage = tokenUsage;
        ProviderBrowser = providerBrowser;
        ProviderModelPicker = providerModelPicker;
        Settings = settings;
        FocusSession = focusSession;
        Board = board;
        _logger = logger;

        ActiveView = chat;
    }

    public bool TryNavigate(string route)
    {
        ObservableObject? target = route switch
        {
            "chat"       => Chat,
            "sessions"   => Sessions,
            "code"       => CodeEditor,
            "diff"       => Diff,
            "tokenUsage" => TokenUsage,
            "settings"   => Settings,
            "board"      => Board,
            _            => null
        };

        if (target is null)
        {
            _logger.LogWarning("Unknown route '{Route}'. Available routes: {Routes}",
                route, string.Join(", ", AvailableRoutes));
            return false;
        }

        ActiveView = target;
        return true;
    }

    public void NavigateTo(string route)
    {
        TryNavigate(route);
    }
}
