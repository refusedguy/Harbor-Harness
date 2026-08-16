using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

public abstract partial class CodeEditorViewModelBase : StoreSubscriberViewModel
{
    [ObservableProperty]
    private EditorTabViewModelBase? _activeTab;

    [ObservableProperty]
    private bool _isBusy;

    protected CodeEditorViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.IsAgentRunning, v => IsBusy = v);
    }

    public ObservableCollection<EditorTabViewModelBase> Tabs { get; } = new();

    protected void CloseTabCore(EditorTabViewModelBase? tab)
    {
        if (tab is null)
        {
            return;
        }
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.LastOrDefault();
        }
    }

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}

public abstract partial class EditorTabViewModelBase : StoreSubscriberViewModel
{
    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _filePath;

    [ObservableProperty]
    private string _fileName;

    protected EditorTabViewModelBase(
        string filePath,
        string fileName,
        string extension,
        string content,
        IDispatcherAdapter dispatcher,
        ILogger logger)
        : base(dispatcher, logger)
    {
        _filePath = filePath;
        _fileName = fileName;
        Extension = extension;
        _content = content;
    }

    public string Extension { get; }

    partial void OnContentChanged(string value) => IsDirty = true;

    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
    }
}
