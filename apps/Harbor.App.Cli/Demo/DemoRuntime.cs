using Harbor.Abstractions.Agents;
using Harbor.Application.Permissions;
using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Harbor.App.Cli.Demo;

/// <summary>
///     DI wiring for <c>harbor demo</c>. Registered by <c>HostBuilder.Build</c>
///     only when <c>HARBOR_DEMO=1</c>; the last <see cref="IPermissionService" />
///     registration wins (same override mechanism as CellForgeModule), so the
///     demo approval gate replaces the fail-closed default asker.
/// </summary>
internal static class DemoRuntime
{
    /// <summary>Register the demo permission service with the auto-approving gate asker.</summary>
    internal static IServiceCollection AddDemoRuntime(this IServiceCollection services) =>
        services
            .AddSingleton<DemoApprovalGate>()
            .AddSingleton<IPermissionService>(sp => new PermissionService(
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<ILogger<PermissionService>>(),
                (request, ct) => sp.GetRequiredService<DemoApprovalGate>().AskAsync(request, ct),
                workspaceRoot: Directory.GetCurrentDirectory()));
}

/// <summary>
///     Scripted approval-gate asker for demo recordings: renders the gate card
///     through the active renderer, holds it visible for a beat (so GIFs show
///     the interaction), then auto-approves. Deterministic — no keyboard input.
/// </summary>
internal sealed class DemoApprovalGate(IServiceProvider services, ILogger<DemoApprovalGate> logger)
{
    private const int MaxArgsChars = 96;
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(1500);

    public async Task<PermissionResponse> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        var renderer = services.GetRequiredService<ITuiRenderer>();
        string args = request.Args.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? request.Args.GetRawText()
            : string.Empty;
        args = args.Replace("\n", " ", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal);
        if (args.Length > MaxArgsChars)
        {
            args = args[..(MaxArgsChars - 1)] + "…";
        }

        logger.LogInformation("Demo approval gate: {Tool} {Pattern}", request.Permission, request.Pattern);
        await renderer.WriteLineAsync("┌─ approval gate ────────────────────────────────").ConfigureAwait(false);
        await renderer.WriteLineAsync("│ tool: " + request.Permission + "   args: " + args).ConfigureAwait(false);
        await renderer.WriteLineAsync("│ [y] allow   [n] deny   [a] always").ConfigureAwait(false);
        await renderer.WriteLineAsync("│ demo policy: auto-approving…").ConfigureAwait(false);
        await Task.Delay(HoldDelay, ct).ConfigureAwait(false);
        await renderer.WriteLineAsync("└─ approved (demo)").ConfigureAwait(false);

        return new PermissionResponse(PermissionAction.Allow, PersistDecision: false);
    }
}
