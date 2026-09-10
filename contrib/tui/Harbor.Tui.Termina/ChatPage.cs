using Harbor.Tui.Termina.Views;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.Logging;
using R3;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;
namespace Harbor.Tui.Termina;

/// <summary>
///     Reactive view model for the Termina chat page. Projects <see cref="UiState" />
///     changes from the TEA store into an observable stream of display lines that
///     the page appends to its <c>StreamingTextNode</c>.
/// </summary>
public sealed class ChatViewModel : ReactiveViewModel
{
    private readonly TerminaTeaBridge _bridge;
    private readonly ILogger? _logger;
    private readonly Subject<string> _output = new();
    private readonly Subject<string> _stream = new();
    private bool _streamOpen;
    private readonly ChatView _chatView = new();
    private readonly DefaultUiProjector _projector = new();
    private readonly StatusBarView _statusBarView = new();
    private int _lastLineCount;
    private int _lastTextLen;
    private int _lastThinkLen;

    public ChatViewModel(TerminaTeaBridge bridge, ILogger? logger = null)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public Observable<string> Output => _output;

    /// <summary>
    ///     Raw streaming deltas (no trailing newline) — appended inline to the
    ///     StreamingTextNode. Kept separate from <see cref="Output" /> so line
    ///     events can keep their newline semantics without splitting tokens.
    /// </summary>
    public Observable<string> Stream => _stream;

    public TerminaTeaBridge Bridge => _bridge;

    public override void OnActivated()
    {
        _logger?.LogInformation("ChatViewModel.OnActivated called");
        var state = _bridge.Store.State;
        _output.OnNext($"Harbor: connected to {state.Provider}/{state.Model} (agent: {state.AgentName}). Type a message, or /help. Esc to quit.");

        _bridge.Store.Changed += OnStoreChanged;
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        var state = _bridge.Store.State;
        var screen = _projector.Project(state);
        var lines = _chatView.Build(screen);

        if (lines.Count > _lastLineCount)
        {
            CloseStreamBlock();
            for (int i = _lastLineCount; i < lines.Count; i++)
                _output.OnNext(lines[i]);
            _lastLineCount = lines.Count;
            _lastTextLen = 0;
            _lastThinkLen = 0;
        }

        if (state.IsStreaming)
        {
            if (state.Active.ThinkBuffer.Length > _lastThinkLen)
            {
                _streamOpen = true;
                _stream.OnNext(state.Active.ThinkBuffer[_lastThinkLen..]);
                _lastThinkLen = state.Active.ThinkBuffer.Length;
            }
            if (state.Active.TextBuffer.Length > _lastTextLen)
            {
                _streamOpen = true;
                _stream.OnNext(state.Active.TextBuffer[_lastTextLen..]);
                _lastTextLen = state.Active.TextBuffer.Length;
            }
        }
        else
        {
            CloseStreamBlock();
            _lastTextLen = 0;
            _lastThinkLen = 0;
        }

        if (state.ShouldQuit)
            this.RequestShutdown();
    }

    /// <summary>
    ///     Terminates an open streamed block by emitting a newline through the
    ///     line channel, so the next transcript/status line starts on its own row.
    /// </summary>
    private void CloseStreamBlock()
    {
        if (!_streamOpen)
            return;
        _streamOpen = false;
        _output.OnNext(string.Empty);
    }

    public void HandleSubmit(string prompt)
    {
        _logger?.LogInformation("HandleSubmit called with prompt: {Prompt}", prompt);
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (prompt is "exit" or "quit" or ":q")
        {
            _logger?.LogInformation("Shutdown requested");
            this.RequestShutdown();
            return;
        }

        _bridge.Submit(prompt);
    }

    public override void Dispose()
    {
        _bridge.Store.Changed -= OnStoreChanged;
        _output.Dispose();
        base.Dispose();
    }
}

/// <summary>
///     Full-screen interactive chat page. Builds a header, a scrolling streaming text panel,
///     a status bar, and a text input field. User input is forwarded to the view model;
///     state projections arrive through the TEA store.
/// </summary>
public sealed class ChatPage : ReactivePage<ChatViewModel>
{
    private readonly TerminaTeaBridge _bridge;
    private readonly ILogger? _logger;
    private TextInputNode? _input;
    private StreamingTextNode? _output;
    private StreamingTextNode? _statusNode;
    private readonly StatusBarView _statusBarView = new();
    private readonly DefaultUiProjector _projector = new();

    public ChatPage(TerminaTeaBridge bridge, ILogger? logger = null)
    {
        _bridge = bridge;
        _logger = logger;
    }

    protected override void OnBound()
    {
        _logger?.LogInformation("ChatPage.OnBound called");

        _output = StreamingTextNode.Create()
            .WithPrefix("  ", Color.DarkGray)
            .WithScrollbar();

        _statusNode = StreamingTextNode.Create();

        _input = new TextInputNode()
            .WithPlaceholder("Type a message… (Esc to quit)")
            .WithForeground(Color.Cyan)
            .WithHistory();

        this.ViewModel.Output
            .Subscribe(line => _output?.Append(line + "\n", Color.Default))
            .DisposeWith(this.Subscriptions);

        this.ViewModel.Stream
            .Subscribe(chunk => _output?.Append(chunk, Color.Default))
            .DisposeWith(this.Subscriptions);

        _bridge.Store.Changed += (_, _) =>
        {
            if (_statusNode is not null)
            {
                _statusNode.Buffer.Clear();
                var screen = _projector.Project(_bridge.Store.State);
                // Append native-colored segments instead of a pre-ANSI-escaped
                // string — StreamingTextNode's inline-SGR handling is unreliable.
                foreach (var (text, style) in _statusBarView.BuildSegments(screen))
                    _statusNode.Append(text, StatusBarView.MapColor(style));
            }
        };

        _input.Submitted
            .Subscribe(text =>
            {
                _logger?.LogInformation("TextInput submitted: {Text}", text);
                this.ViewModel.HandleSubmit(text);
                _input.Clear();
            })
            .DisposeWith(this.Subscriptions);

        this.ViewModel.Input
            .OfType<IInputEvent, KeyPressed>()
            .Subscribe(key =>
            {
                if (key.KeyInfo.Key == ConsoleKey.Escape)
                {
                    this.ViewModel.RequestShutdown();
                    return;
                }
                if (key.KeyInfo.Key == ConsoleKey.F12)
                {
                    _bridge.DumpDiagnostics();
                    return;
                }

                _input.HandleInput(key.KeyInfo);
            })
            .DisposeWith(this.Subscriptions);

        _logger?.LogInformation("ChatPage.OnBound completed — subscriptions wired");
    }

    public override void OnNavigatedTo()
    {
        _logger?.LogInformation("ChatPage.OnNavigatedTo called");
        base.OnNavigatedTo();
        this.FocusPolicy = FocusPolicy.FirstFocusable;
    }

    public override ILayoutNode BuildLayout()
    {
        _logger?.LogInformation("ChatPage.BuildLayout called");
        return Layouts.Vertical()
            .WithChild(new TextNode("💬 Harbor").WithForeground(Color.Cyan).Bold().Height(1))
            .WithChild(_statusNode!.Height(1))
            .WithChild(new PanelNode().WithTitle("Messages").WithContent(_output!.Fill()).Fill())
            .WithChild(new EmptyNode().Height(1))
            .WithChild(_input!.Height(1));
    }
}
