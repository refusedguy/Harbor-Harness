using Microsoft.Extensions.Logging;
using R3;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;
namespace Harbor.Tui.Termina;
/// <summary>
///     Reactive view model for the Termina chat page. Holds the streamed output as an
///     <see cref="Observable{T}" /> of text tokens and funnels user-submitted prompts to the agent.
/// </summary>
public sealed class ChatViewModel : ReactiveViewModel
{
    private readonly ChatBridge _bridge;
    private readonly ILogger? _logger;
    private readonly Subject<ChatLine> _stream = new();

    public ChatViewModel(ChatBridge bridge, ILogger? logger = null)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public Observable<ChatLine> Stream => _stream;

    public string Model { get; private set; } = string.Empty;

    public string Provider { get; private set; } = string.Empty;

    public string AgentName { get; private set; } = string.Empty;

    public override void OnActivated()
    {
        _logger?.LogInformation("ChatViewModel.OnActivated called");
        var agent = _bridge.Agent;
        if (agent is not null)
        {
            Model = agent.State.Agent.Model;
            Provider = agent.State.Agent.ProviderId;
            AgentName = agent.State.Agent.Name.Value;
            _stream.OnNext(new ChatLine(
                $"Harbor: connected to {Provider}/{Model} (agent: {AgentName}). Type a message, or /help. Esc to quit.\n",
                Color.Cyan, true));
        }
        else
        {
            _stream.OnNext(new ChatLine("Harbor: ready.\n", Color.Cyan, true));
        }
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

        _stream.OnNext(new ChatLine($"You: {prompt}\n", Color.Green, true));
        _bridge.Submit(prompt);
    }

    public override void Dispose()
    {
        _stream.Dispose();
        base.Dispose();
    }
}

/// <summary>
///     Full-screen interactive chat page. Builds a header, a scrolling streaming text panel, and a
///     text input field. User input is forwarded to the view model; streaming agent output arrives
///     through the shared <see cref="ChatBridge" />.
/// </summary>
public sealed class ChatPage : ReactivePage<ChatViewModel>
{
    private readonly ChatBridge _bridge;
    private readonly ILogger? _logger;
    private TextInputNode? _input;
    private StreamingTextNode? _output;

    public ChatPage(ChatBridge bridge, ILogger? logger = null)
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

        _input = new TextInputNode()
            .WithPlaceholder("Type a message… (Esc to quit)")
            .WithForeground(Color.Cyan)
            .WithHistory();

        this.ViewModel.Stream
            .Subscribe(line => AppendLine(line))
            .DisposeWith(this.Subscriptions);

        _bridge.OutputStream
            .Subscribe(line =>
            {
                _logger?.LogDebug("OutputStream received token: {TokenLength} chars", line.Text.Length);
                AppendLine(line);
            })
            .DisposeWith(this.Subscriptions);

        _input.Submitted
            .Subscribe(text =>
            {
                _logger?.LogInformation("TextInput submitted: {Text}", text);
                this.ViewModel.HandleSubmit(text);
                _input.Clear();
            })
            .DisposeWith(this.Subscriptions);

        // Pattern 2 (always-visible input): route every key from the ViewModel's
        // input observable into the TextInputNode so it can handle character input,
        // backspace, cursor movement and history. Escape is intercepted here to quit.
        this.ViewModel.Input
            .OfType<IInputEvent, KeyPressed>()
            .Subscribe(key =>
            {
                _logger?.LogDebug("Key pressed: {Key}", key.KeyInfo.Key);
                if (key.KeyInfo.Key == ConsoleKey.Escape)
                {
                    this.ViewModel.RequestShutdown();
                    return;
                }

                _input.HandleInput(key.KeyInfo);
            })
            .DisposeWith(this.Subscriptions);

        _logger?.LogInformation("ChatPage.OnBound completed — subscriptions wired");
    }

    private void AppendLine(ChatLine line)
    {
        if (_output is null) return;
        if (line.NewLineBefore)
            _output.Append("\n", Color.Default);
        _output.Append(line.Text, line.Color ?? Color.Default);
    }

    public override void OnNavigatedTo()
    {
        _logger?.LogInformation("ChatPage.OnNavigatedTo called");
        base.OnNavigatedTo();
        this.FocusPolicy = FocusPolicy.FirstFocusable;
        _logger?.LogInformation("FocusPolicy set to FirstFocusable");
    }

    public override ILayoutNode BuildLayout()
    {
        _logger?.LogInformation("ChatPage.BuildLayout called");
        return Layouts.Vertical()
            .WithChild(new TextNode("💬 Harbor").WithForeground(Color.Cyan).Bold().Height(1))
            .WithChild(new EmptyNode().Height(1))
            .WithChild(new PanelNode().WithTitle("Messages").WithContent(_output!.Fill()).Fill())
            .WithChild(new EmptyNode().Height(1))
            .WithChild(_input!.Height(1));
    }
}
