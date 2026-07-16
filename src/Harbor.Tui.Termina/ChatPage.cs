using System;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using R3;
using Termina;
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
    private readonly Subject<string> _stream = new();

    public ChatViewModel(ChatBridge bridge)
    {
        _bridge = bridge;
    }

    public Observable<string> Stream => _stream;

    public string Model { get; private set; } = string.Empty;

    public string Provider { get; private set; } = string.Empty;

    public string AgentName { get; private set; } = string.Empty;

    public override void OnActivated()
    {
        var agent = _bridge.Agent;
        if (agent is not null)
        {
            Model = agent.State.Agent.Model;
            Provider = agent.State.Agent.ProviderId;
            AgentName = agent.State.Agent.Name.Value;
            _stream.OnNext($"Assistant: connected to {Provider}/{Model} (agent: {AgentName}). Type a message, or /help. Esc to quit.\n");
        }
        else
        {
            _stream.OnNext("Assistant: ready.\n");
        }
    }

    public void HandleSubmit(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (prompt is "exit" or "quit" or ":q")
        {
            this.RequestShutdown();
            return;
        }

        _stream.OnNext($"You: {prompt}\n");
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
    private StreamingTextNode? _output;
    private TextInputNode? _input;

    public ChatPage(ChatBridge bridge)
    {
        _bridge = bridge;
    }

    protected override void OnBound()
    {
        _output = StreamingTextNode.Create()
            .WithPrefix("  ", Color.Gray)
            .WithScrollbar();

        _input = new TextInputNode()
            .WithPlaceholder("Type a message… (Esc to quit)")
            .WithForeground(Color.Cyan)
            .WithHistory();

        ViewModel.Stream
            .Subscribe(token => _output.Append(token))
            .DisposeWith(Subscriptions);

        _bridge.OutputStream
            .Subscribe(token => _output!.Append(token))
            .DisposeWith(Subscriptions);

        _input.Submitted
            .Subscribe(text =>
            {
                ViewModel.HandleSubmit(text);
                _input.Clear();
            })
            .DisposeWith(Subscriptions);

        ViewModel.Input
            .OfType<IInputEvent, KeyPressed>()
            .Subscribe(key =>
            {
                if (key.KeyInfo.Key == ConsoleKey.Escape)
                    ViewModel.RequestShutdown();
            })
            .DisposeWith(Subscriptions);
    }

    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        FocusPolicy = FocusPolicy.FirstFocusable;
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(new TextNode("💬 Harbor").WithForeground(Color.Cyan).Bold().Height(1))
            .WithChild(new EmptyNode().Height(1))
            .WithChild(new PanelNode().WithTitle("Messages").WithContent(_output!.Fill()).Fill())
            .WithChild(new EmptyNode().Height(1))
            .WithChild(_input!.Height(1));
    }
}
