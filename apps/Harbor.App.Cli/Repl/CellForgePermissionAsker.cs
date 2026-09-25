using System.Text.Json;
using Harbor.Abstractions.Permissions;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Интерактивный permission-prompt CellForge REPL'а: подменяет
///     fail-closed deny у <c>PermissionService</c> на карточку
///     <see cref="ApprovalGateView" /> в таймлайне и ожидание y/n/a.
///     Потоковый контракт: метод зовётся из tool-execution контекста; карточка
///     попадает на таймлайн через очередь моста (рендер-поток), решение
///     приходит событием при обработке клавиш тем же рендер-потоком.
/// </summary>
/// <remarks>
///     #49 PR1: ожидание идёт через <see cref="IApprovalCoordinator" /> —
///     единую точку линеаризации решения vs отмены. Отмена (Ctrl+C/Esc,
///     RPC, session-switch) прилетает как <see langword="null" /> и
///     маппится в fail-closed Deny; view-решение штампует роутер через тот же
///     координатор, повторные/поздние решения гейт не трогают.
/// </remarks>
internal sealed class CellForgePermissionAsker(
    Func<ChatScreenBridge> bridge,
    IApprovalCoordinator coordinator)
{
    private const int MaxDetailChars = 96;

    public async Task<PermissionResponse> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        var gate = bridge().RequestApprovalGate(request.Permission, Describe(request));
        coordinator.RegisterGate(gate.Id);

        var resolution = await coordinator.WaitForDecisionAsync(gate.Id, ct).ConfigureAwait(false);
        if (resolution is null)
        {
            // Cancel won: fail closed. Resolve the card visually so it doesn't
            // linger as pending (best-effort — the router may have pruned it).
            gate.TryDecide(ApprovalChoice.Deny);
            return new(PermissionAction.Deny, false);
        }

        return Map(resolution);
    }

    /// <summary>Одна строка «цель запроса»: правило-паттерн плюс однострочный JSON аргументов.</summary>
    internal static string Describe(PermissionRequest request)
    {
        string args = request.Args.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? request.Args.GetRawText()
            : string.Empty;
        args = args.Replace("\n", " ", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal);
        if (args.Length > MaxDetailChars)
        {
            args = args[..(MaxDetailChars - 1)] + "…";
        }

        return $"{request.Pattern} {args}".Trim();
    }

    private static PermissionResponse Map(ApprovalResolution resolution) => resolution switch
    {
        { Approved: true, PersistDecision: true } => new(PermissionAction.Allow, PersistDecision: true),
        { Approved: true } => new(PermissionAction.Allow, false),
        _ => new(PermissionAction.Deny, false),
    };
}
