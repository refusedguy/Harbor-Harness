using Harbor.Abstractions.Events;
using Harbor.Application.Configuration;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Hosting;

/// <summary>
///     CE-4 DI-модуль ConsoleEx-рендера. Регистрирует граф второго пути
///     рендера интерактивного REPL: экранная сессия, композер, статус,
///     мост на живого агента. Всё разрешается лениво — резолв только в
///     ConsoleEx-режиме (<c>tui: "consoleex"</c> / <c>HARBOR_TUI=consoleex</c>),
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
///         <b>Что НЕ регистрируется отдельно:</b> <see cref="Harbor.Tui.ConsoleEx.Rendering.DiffEngine"/>,
///         TimelineRing, CommitTickPacer, SpinnerStrip — внутренние владельцы
///         уже перечисленных корней (<c>ScreenSession.Engine</c>,
///         <c>VirtualizedChatTimeline</c>, <c>ChatScreenBridge._pacer</c>,
///         статический виджет). Вторая регистрация означала бы два состояния
///         одного кадра — запрещено контрактом владения.
///     </para>
/// </remarks>
internal static class ConsoleExModule
{
    /// <summary>Fallback geometry when no tty size is available (pipes, CI).</summary>
    public const int FallbackCols = 80;
    public const int FallbackRows = 24;

    /// <summary>Resize poll cadence for the input thread (SIGWINCH replaces it in a later sprint).</summary>
    private static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(250);

    public static IServiceCollection AddConsoleEx(this IServiceCollection services, ConsoleExUiConfig ui)
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
