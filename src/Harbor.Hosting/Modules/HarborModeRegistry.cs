using System.Collections.Frozen;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting;

// Issue #175 (OCP — string switch instead of Strategy): HARBOR_MODE
// dispatch moved out of IpcModule into one strategy per mode, resolved
// via a FrozenDictionary. Unknown modes keep failing fast with the same
// ArgumentException as before.
//
// Issue #581 — `IHarborModeStrategy` was `internal` (less accessible than
// the wiring it applies), and this registry carried a second hand-written copy
// of its own key set (`internal const string KnownIds`) that nothing compared
// to `Build().Keys`. The strategy, its context and the registry are public, and
// the id list the error message prints is derived from the built dictionary at
// the call site (`IpcModule`). The array below is the only declaration.

/// <summary>
///     Wiring context for one <c>HARBOR_MODE</c> strategy: the service
///     collection under construction, the shared composition context,
///     and the pipe name from <c>HARBOR_IPC_PIPE</c>.
/// </summary>
public sealed record HarborModeContext(
    IServiceCollection Services,
    HarborCompositionContext Context,
    string PipeName);

/// <summary>
///     Wiring strategy for one <c>HARBOR_MODE</c> id.
/// </summary>
public interface IHarborModeStrategy
{
    /// <summary>Canonical mode id (lowercase), as spelled in <c>HARBOR_MODE</c>.</summary>
    string ModeId { get; }

    /// <summary>Apply the mode's service registrations.</summary>
    void Apply(HarborModeContext context);
}

/// <summary>In-process mode: everything runs inside this process.</summary>
internal sealed class InProcessHarborModeStrategy : IHarborModeStrategy
{
    public string ModeId => "inprocess";

    public void Apply(HarborModeContext context) =>
        context.Services.UseInProcessHarborClient();
}

/// <summary>Daemon mode: serve IPC on top of the in-process client.</summary>
internal sealed class IpcServerHarborModeStrategy : IHarborModeStrategy
{
    public string ModeId => "ipc-server";

    public void Apply(HarborModeContext context)
    {
        context.Services.UseInProcessHarborClient();
        context.Services.UseHarborIpcServer(context.PipeName);
        IpcModule.AddNetworkedListenerIfConfigured(context.Services, context.Context);
    }
}

/// <summary>Remote-client mode: talk to a daemon over IPC.</summary>
internal sealed class IpcClientHarborModeStrategy : IHarborModeStrategy
{
    public string ModeId => "ipc-client";

    public void Apply(HarborModeContext context) =>
        context.Services.UseIpcHarborClient(context.PipeName);
}

/// <summary>
///     Lookup registry over the <see cref="IHarborModeStrategy"/>
///     strategies. A new mode = a new strategy class + one list entry —
///     the unknown-id failure stays in one place.
/// </summary>
public static class HarborModeRegistry
{
    /// <summary>Build the id → strategy index.</summary>
    public static FrozenDictionary<string, IHarborModeStrategy> Build()
    {
        IHarborModeStrategy[] strategies =
        [
            new InProcessHarborModeStrategy(),
            new IpcServerHarborModeStrategy(),
            new IpcClientHarborModeStrategy(),
        ];
        return strategies.ToFrozenDictionary(s => s.ModeId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolve a raw <c>HARBOR_MODE</c> value to its strategy
    ///     (trim + case-insensitive). Unknown modes come back as
    ///     <see cref="Maybe{T}.None" /> — the caller throws the documented
    ///     ArgumentException. Absence is a value, so it travels in the
    ///     signature instead of in a nullable out-parameter.
    /// </summary>
    public static Maybe<IHarborModeStrategy> Resolve(
        FrozenDictionary<string, IHarborModeStrategy> registry,
        string rawMode)
    {
        return registry.TryFind(rawMode.Trim());
    }
}
