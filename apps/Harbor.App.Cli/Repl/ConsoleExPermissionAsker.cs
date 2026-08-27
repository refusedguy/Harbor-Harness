using System.Text.Json;
using Harbor.Abstractions.Permissions;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Интерактивный permission-prompt ConsoleEx REPL'а: подменяет
///     fail-closed deny у <c>PermissionService</c> на карточку
///     <see cref="ApprovalGateView" /> в таймлайне и ожидание y/n/a.
///     Потоковый контракт: метод зовётся из tool-execution контекста; карточка
///     попадает на таймлайн через очередь моста (рендер-поток), решение
///     приходит событием при обработке клавиш тем же рендер-потоком.
/// </summary>
internal sealed class ConsoleExPermissionAsker(Func<ChatScreenBridge> bridge)
{
    private const int MaxDetailChars = 96;

    public async Task<PermissionResponse> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        var gate = bridge().RequestApprovalGate(request.Permission, Describe(request));

        // RunContinuationsAsynchronously: continuation уходит из render-потока,
        // чтобы await не продолжился синхронно внутри обработки клавиши.
        var tcs = new TaskCompletionSource<ApprovalChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDecision(object? _, EventArgs __) => tcs.TrySetResult(gate.Decision);
        gate.DecisionRecorded += OnDecision;

        try
        {
            var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            try
            {
                return Map(await tcs.Task.ConfigureAwait(false));
            }
            finally
            {
                // Регистрация отмены живёт только в ask-фазе; после решения
                // конвейера она больше не нужна.
                await reg.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.DecisionRecorded -= OnDecision;
        }
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

    private static PermissionResponse Map(ApprovalChoice choice) => choice switch
    {
        ApprovalChoice.AlwaysAllow => new(PermissionAction.Allow, PersistDecision: true),
        ApprovalChoice.Approve => new(PermissionAction.Allow, false),
        _ => new(PermissionAction.Deny, false),
    };
}
