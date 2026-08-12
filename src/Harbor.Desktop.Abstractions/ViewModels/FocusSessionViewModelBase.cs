namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     serves as API for the focus-session panel view-model (upon_layers_featureFlags):
///     a pure projection of the active session's headline stats (title, model,
///     provider, agent, message/token counts) shown in the Focus overlay on
///     every desktop app. Zero behaviour — the shell VM copies values in when
///     the overlay opens.
/// </summary>
public abstract partial class FocusSessionViewModelBase : ViewModelBase
{
    [ObservableProperty]
    private string _agent = string.Empty;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _title = "Focus Session";

    [ObservableProperty]
    private long _tokensIn;

    [ObservableProperty]
    private long _tokensOut;

    /// <summary>Construct a <see cref="FocusSessionViewModelBase" />.</summary>
    protected FocusSessionViewModelBase(ILogger logger) : base(logger)
    {
    }
}
