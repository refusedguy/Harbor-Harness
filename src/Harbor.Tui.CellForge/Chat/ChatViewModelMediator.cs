using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge;

/// <summary>
///     Mediator seam (issue #1556): the one place that names the chat screen's
///     colleague view-models and knows how they dock into
///     <see cref="ChatScreenProjector" />. Until this seam the renderer was the
///     <i>implicit</i> mediator — it held the status / chat / input VMs plus
///     the composer and quick-switch slots as fields and assembled the
///     projector by hand in <b>both</b> constructors, so every new colleague or
///     projector argument meant editing fields and duplicated constructor
///     bodies. The colleagues still know nothing of each other; the
///     coordination is centralized exactly as before, but under an explicit
///     role: the renderer depends on this seam instead of naming every VM.
///     In-tree only — not a plugin axis: no plugin can reach it, so
///     <c>PluginAxisRegistry</c> does not name it.
/// </summary>
public interface IChatViewModelMediator
{
    /// <summary>
    ///     The chat-screen projector the mediator assembled from its colleagues.
    /// </summary>
    ChatScreenProjector ChatProjector { get; }

    /// <summary>
    ///     The composer buffer the colleagues' text/cursor edits flow through
    ///     (renderer test seam, see <c>CellForgeTuiRenderer.PromptBuffer</c>).
    /// </summary>
    PromptBuffer PromptBuffer { get; }
}

/// <summary>
///     Concrete mediator: resolves each colleague (an explicit instance or the
///     shared registry default), keeps the input VM registry-coherent, and
///     builds <see cref="ChatScreenProjector" /> — the single assembly point
///     the renderer's two constructors used to duplicate. Adding a colleague
///     edits this class and no longer the renderer's constructors.
/// </summary>
public sealed class ChatViewModelMediator : IChatViewModelMediator
{
    private readonly ComposerController _composer = new();
    private readonly QuickSwitchSlots _quickSwitchSlots = new();
    private readonly ChatScreenProjector _chatProjector;

    /// <summary>
    ///     Assembles the chat screen's colleagues. Null VMs fall back to the
    ///     registry defaults the base renderer seeded ("status-bar",
    ///     "chat-history"); an explicit input VM is (re)registered so
    ///     field/registry/bound-view stay coherent — the previous
    ///     <c>ResolveInputVm</c> behaviour, moved verbatim.
    /// </summary>
    /// <param name="viewModels">The renderer's shared view-model registry.</param>
    /// <param name="screen">Host screen accessor the projector reads per frame.</param>
    /// <param name="idlePlaceholder">Input placeholder while the agent is idle.</param>
    /// <param name="busyPlaceholder">Input placeholder while the agent is running.</param>
    /// <param name="statusVm">Explicit status-bar VM, or null for the registry default.</param>
    /// <param name="chatVm">Explicit chat-history VM, or null for the registry default.</param>
    /// <param name="inputVm">Explicit input VM, or null for the registry default.</param>
    public ChatViewModelMediator(
        ViewModelRegistry viewModels,
        Func<ChatScreen?> screen,
        string idlePlaceholder,
        string busyPlaceholder,
        StatusBarViewModel? statusVm = null,
        ChatHistoryViewModel? chatVm = null,
        InputViewModel? inputVm = null)
    {
        var resolvedStatus = statusVm ?? viewModels.Get<StatusBarViewModel>("status-bar")!;
        var resolvedChat = chatVm ?? viewModels.Get<ChatHistoryViewModel>("chat-history")!;
        var resolvedInput = ResolveInputVm(inputVm, viewModels);
        _chatProjector = new ChatScreenProjector(
            resolvedStatus, resolvedChat, resolvedInput, _composer, _quickSwitchSlots,
            screen, idlePlaceholder, busyPlaceholder);
    }

    /// <inheritdoc/>
    public ChatScreenProjector ChatProjector => _chatProjector;

    /// <inheritdoc/>
    public PromptBuffer PromptBuffer => _composer.Buffer;

    /// <summary>
    ///     Resolves the input view model (explicit instance or the registry
    ///     default) and keeps field/registry coherent by registering the
    ///     resolved instance in the shared registry.
    /// </summary>
    private static InputViewModel ResolveInputVm(InputViewModel? inputVm, ViewModelRegistry viewModels)
    {
        var resolved = inputVm ?? viewModels.Get<InputViewModel>("input")!;
        viewModels.Register(resolved);
        return resolved;
    }
}
