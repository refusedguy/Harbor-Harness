using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Hosting;

/// <summary>
///     CE-4 DI-модуль CellForge-рендера. Регистрирует граф второго пути
///     рендера интерактивного REPL: экранная сессия, композер, статус,
///     мост на живого агента. Всё разрешается лениво — резолв только в
///     CellForge-режиме (<c>tui: "consoleex"</c> / <c>HARBOR_TUI=consoleex</c>),
///     поэтому legacy-путь не платит ни байтом.
/// </summary>
/// <remarks>
///     <para>
///         <b>Lifetime-соглашение (singleton где надо):</b> каждый компонент
///         владеет мутируемым состоянием кадра ровно один раз.
///     </para>
///     <list type="bullet">
///         <item><see cref="ITerminalBackend" /> / <see cref="AnsiWriter" /> /
///         <see cref="ScreenSession" /> — процессные одиночки: один stdout,
///         один BACK/FRONT-буфер, одна точка флаша.</item>
///         <item><see cref="ComposerController" /> / <see cref="StatusViewModel" /> /
///         <see cref="ChatScreen" /> — одно состояние экрана на сессию.</item>
///         <item><see cref="ChatScreenBridge" /> — подписывается на
///         <see cref="IEventBus" /> в конструкторе и живёт до закрытия хоста.</item>
///         <item><see cref="TerminalInputSource" /> — единственный читатель
///         stdin (SingleReader-канал).</item>
///     </list>
///     <para>
///         <b>Что НЕ регистрируется отдельно:</b> <see cref="Harbor.Tui.CellForge.Rendering.DiffEngine"/>,
///         TimelineRing, CommitTickPacer, SpinnerStrip — внутренние владельцы
///         уже перечисленных корней (<c>ScreenSession.Engine</c>,
///         <c>VirtualizedChatTimeline</c>, <c>ChatScreenBridge._pacer</c>,
///         статический виджет). Вторая регистрация означала бы два состояния
///         одного кадра — запрещено контрактом владения.
///     </para>
/// </remarks>
internal static class CellForgeModule
{
    /// <summary>Fallback geometry when no tty size is available (pipes, CI).</summary>
    public const int FallbackCols = 80;
    public const int FallbackRows = 24;

    /// <summary>Resize poll cadence for the input thread (SIGWINCH replaces it in a later sprint).</summary>
    private static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(250);

    public static IServiceCollection AddCellForge(this IServiceCollection services, CellForgeUiConfig ui)
    {
        services.AddSingleton<ITerminalBackend, StdoutBackend>();
        services.AddSingleton(sp => new AnsiWriter(
            sp.GetRequiredService<ITerminalBackend>(),
            syncUpdates: ui.SyncUpdates));

        services.AddSingleton(sp => new ScreenSession(
            sp.GetRequiredService<AnsiWriter>(),
            ProbeInitialCols(),
            ProbeInitialRows(),
            sizeSource: ReadTerminalSize));

        services.AddSingleton<ComposerController>();
        services.AddSingleton<StatusViewModel>();

        services.AddSingleton(sp => ChatScreen.Build(
            sp.GetRequiredService<ComposerController>(),
            sp.GetRequiredService<StatusViewModel>()));

        services.AddSingleton(_ => new TerminalInputSource(
            TerminalInputStream.Open(),
            new TerminalInputSourceOptions
            {
                SizeProvider = ReadTerminalSizeForInput,
                ResizePollInterval = ResizePollInterval,
            }));

        services.AddSingleton(sp => new ChatScreenBridge(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ChatScreen>().Timeline,
            sp.GetRequiredService<StatusViewModel>(),
            autoSubscribe: false)); // events pumped through the frame loop thread

        // Permission asks вместо молчаливого fail-closed deny: карточка
        // ApprovalGateView в таймлайне + ожидание y/n/a. Ленивое замыкание на
        // мост — резолв в момент первого запроса разрешений, не при сборке DI.
        // Последняя регистрация IPermissionService выигрывает (AddHarbor уже отработал),
        // поэтому оверрайд виден и ToolDispatcher'у внутри агента.
        services.AddSingleton(sp => new CellForgePermissionAsker(
            () => sp.GetRequiredService<ChatScreenBridge>()));
        services.AddSingleton<IPermissionService>(sp => new PermissionService(
            sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                Harbor.Application.Permissions.PermissionService>>(),
            sp.GetRequiredService<Repl.CellForgePermissionAsker>().AskAsync,
            workspaceRoot: Directory.GetCurrentDirectory()));

        return services;
    }

    /// <summary>Viewport probe for the frame pipeline. Never throws.</summary>
    private static (int Cols, int Rows) ReadTerminalSize()
    {
        return TryGetTerminalSize(out var cols, out var rows) ? (cols, rows) : (FallbackCols, FallbackRows);
    }

    private static int ProbeInitialCols() => TryGetTerminalSize(out var cols, out _) ? cols : FallbackCols;

    private static int ProbeInitialRows() => TryGetTerminalSize(out _, out var rows) ? rows : FallbackRows;

    /// <summary>Input-pipeline probe shape ((Width, Height)). Never throws.</summary>
    private static (int Width, int Height) ReadTerminalSizeForInput()
    {
        var (cols, rows) = ReadTerminalSize();
        return (cols, rows);
    }

    /// <summary>Best-effort tty size; redirected output reports no window.</summary>
    private static bool TryGetTerminalSize(out int cols, out int rows)
    {
        try
        {
            cols = Console.WindowWidth;
            rows = Console.WindowHeight;
            return cols > 0 && rows > 0;
        }
        catch (IOException)
        {
            (cols, rows) = (FallbackCols, FallbackRows);
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            (cols, rows) = (FallbackCols, FallbackRows);
            return false;
        }
    }
}
