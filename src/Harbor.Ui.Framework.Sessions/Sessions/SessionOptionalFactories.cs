using Harbor.Abstractions.Permissions;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.ViewModels;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Optional (host-only) session collaborators as explicit Func-factories.
///     Replaces the <see cref="IServiceProvider" /> Service Locator previously
///     held by <see cref="SessionManager" /> (issue #189): every optional
///     dependency is now a declared constructor input, so headless/test hosts
///     simply pass factories returning null instead of a service provider.
/// </summary>
/// <remarks>
///     <para>
///         #63 legitimate: each factory may return null — the caller treats a
///         missing registration as a no-op, never an error.
///     </para>
/// </remarks>
public sealed class SessionOptionalFactories
{
    /// <summary>Construct the optional-factory bundle.</summary>
    /// <param name="gitService">Resolves the host-only <see cref="GitService" /> (null on headless/test hosts).</param>
    /// <param name="approvalCoordinator">Resolves the <see cref="IApprovalCoordinator" /> (null when unregistered).</param>
    /// <param name="clearTokenUsage">Clears the UI-only token-usage view (no-op when unregistered).</param>
    public SessionOptionalFactories(
        Func<GitService?> gitService,
        Func<IApprovalCoordinator?> approvalCoordinator,
        Action clearTokenUsage)
    {
        GitService = gitService;
        ApprovalCoordinator = approvalCoordinator;
        ClearTokenUsage = clearTokenUsage;
    }

    /// <summary>Resolves the host-only git service.</summary>
    public Func<GitService?> GitService { get; }

    /// <summary>Resolves the approval coordinator for agent abort.</summary>
    public Func<IApprovalCoordinator?> ApprovalCoordinator { get; }

    /// <summary>Clears the UI-only token-usage view.</summary>
    public Action ClearTokenUsage { get; }
}
