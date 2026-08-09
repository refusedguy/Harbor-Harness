using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.App.Avalonia.ViewModels;

public sealed partial class FocusSessionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Focus Session";

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _agent = string.Empty;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private long _tokensIn;

    [ObservableProperty]
    private long _tokensOut;
}
