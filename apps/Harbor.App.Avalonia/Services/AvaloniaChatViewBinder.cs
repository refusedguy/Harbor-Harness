using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
namespace Harbor.App.Avalonia.Services;
/// <summary>
///     Avalonia implementation of <see cref="IChatViewBinder" />. Bridges
///     the framework-layer <c>SessionManager</c> to the Avalonia-specific
///     <see cref="ChatViewModel" /> + <see cref="Dispatcher.UIThread" />.
/// </summary>
/// <remarks>
///     <para>
///         Registered as a singleton in <c>ServiceRegistration</c>. The
///         framework-layer <c>SessionManager</c> resolves this via DI
///         and calls <see cref="Rebind" /> on every session switch.
///     </para>
///     <para>
///         <b>Why this lives in Avalonia:</b> the framework layer cannot
///         reference <c>ChatViewModel</c> (which is Avalonia-specific) or
///         <c>Dispatcher.UIThread</c> (which is an Avalonia static). Both
///         are wrapped behind this interface so the framework layer stays
///         platform-agnostic.
///     </para>
/// </remarks>
public sealed class AvaloniaChatViewBinder : IChatViewBinder
{
    private readonly IServiceProvider _services;

    /// <summary>Construct the binder.</summary>
    public AvaloniaChatViewBinder(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public void Rebind(UiStore store)
    {
        var chatVm = _services.GetService<ChatViewModel>();
        if (chatVm is null) return;
        // Marshal onto the UI thread — RebindToStore mutates
        // ObservableCollection bound to the chat view.
        Dispatcher.UIThread.Post(() => chatVm.RebindToStore(store));
    }
}