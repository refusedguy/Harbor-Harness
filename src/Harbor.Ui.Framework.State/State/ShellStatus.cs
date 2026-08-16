using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.Ui.Framework.State;

public sealed partial class ShellStatus : ObservableValidator
{
    [ObservableProperty] private string _status = "idle";
    [ObservableProperty] private string _provider = "ollama";
    [ObservableProperty] private string _model = "—";
    [ObservableProperty] private string _agentName = "code";
    [ObservableProperty] private long _tokensIn;
    [ObservableProperty] private long _tokensOut;
    [ObservableProperty] private decimal _costUsd;
    [ObservableProperty] private bool _isAgentRunning;
    [ObservableProperty] private int _activeSessionCount = 1;
    [ObservableProperty] private int _messageCount;
}
