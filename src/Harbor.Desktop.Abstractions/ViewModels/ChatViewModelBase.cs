using Harbor.Desktop.Abstractions.DesignSystem;
using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the chat view-model shared by every desktop app. Holds the
///     observable chat-line collection and the streaming state; platform VMs
///     derive from this and add dispatcher wiring + command implementations.
/// </summary>
/// <remarks>
///     <para>
///         Stays abstract — the platform VM must supply the
///         <see cref="IDispatcherAdapter"/> and <see cref="IToastService"/>
///         implementations and implement <see cref="OnSend"/>,
///         <see cref="OnStop"/>, <see cref="OnClear"/> to forward to the
///         platform-specific <c>UiStore</c> + <c>TuiEffectHost</c>.
///     </para>
///     <para>
///         The <see cref="RoleBrushKey"/> lookup is framework-agnostic and
///         lives here so all platforms use the same resource-key names. Each
///         platform's theme dictionary (e.g. Avalonia <c>Dark.axaml</c>) must
///         define those keys.
///     </para>
/// </remarks>
public abstract partial class ChatViewModelBase : ViewModelBase
{
    /// <summary>Construct a <see cref="ChatViewModelBase"/>.</summary>
    protected ChatViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible chat lines, projected for the view layer.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

    /// <summary>User input text bound to the chat input box.</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>True when the agent is actively streaming a response.</summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>Active streaming buffer (partial assistant text).</summary>
    [ObservableProperty]
    private string _streamingBuffer = string.Empty;

    /// <summary>True when the agent is running but not yet streaming (thinking / tool-use).</summary>
    [ObservableProperty]
    private bool _isThinking;

    /// <summary>Reset the chat-line collection (called by derived Clear()).</summary>
    protected void ResetLines()
    {
        Lines.Clear();
    }

    /// <summary>Append a chat line. Called by the derived class from the dispatcher.</summary>
    protected void AppendLine(ChatRole role, string text)
    {
        Lines.Add(new ChatLineViewModel(role, text));
    }

    /// <summary>Resource-key lookup for the role's accent color.</summary>
    /// <param name="role">Chat role.</param>
    /// <returns>A resource key like <c>"ChatUserBrush"</c> resolved by the platform theme.</returns>
    public static string RoleBrushKey(ChatRole role) => role switch
    {
        ChatRole.User => "ChatUserBrush",
        ChatRole.Assistant => "ChatAssistantBrush",
        ChatRole.Thinking => "ChatThinkingBrush",
        ChatRole.Tool => "ChatToolBrush",
        ChatRole.ToolResult => "ChatToolResultBrush",
        ChatRole.System => "ChatSystemBrush",
        ChatRole.Error => "ChatErrorBrush",
        _ => "ChatAssistantBrush",
    };

    /// <summary>Catppuccin accent color for the given role, used by Blazor CSS / non-XAML platforms.</summary>
    public static RgbColor RoleColor(ChatRole role) => role switch
    {
        ChatRole.User => ColorPalette.MochaSky,
        ChatRole.Assistant => ColorPalette.MochaText,
        ChatRole.Thinking => ColorPalette.MochaSurface2,
        ChatRole.Tool => ColorPalette.MochaBlue,
        ChatRole.ToolResult => ColorPalette.MochaGreen,
        ChatRole.System => ColorPalette.MochaYellow,
        ChatRole.Error => ColorPalette.MochaRed,
        _ => ColorPalette.MochaText,
    };
}

/// <summary>
///     One chat line projected for the UI. Role + text + brush key for binding.
/// </summary>
/// <param name="Role">Chat role — drives color.</param>
/// <param name="Text">Line text.</param>
public sealed record ChatLineViewModel(ChatRole Role, string Text)
{
    /// <summary>The brush resource key (resolved by <see cref="ChatViewModelBase.RoleBrushKey"/>).</summary>
    public string BrushKey => ChatViewModelBase.RoleBrushKey(Role);

    /// <summary>The lowercase role label for the gutter.</summary>
    public string RoleLabel => Role.ToString().ToLowerInvariant();
}
