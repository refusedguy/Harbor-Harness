using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Terminal.Abstractions;
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
///         <c>VirtualizedChatTimeline</c>, <c>StreamCoalescer</c>,
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
        // StdoutBackend serves both contracts off ONE instance: ITerminalBackend
        // for the async REPL writes, ISyncTerminalBackend for ScreenSession's
        // sync flush and the CellForgeRenderContext adapter (issue #468).
        services.AddSingleton<StdoutBackend>();
        services.AddSingleton<ITerminalBackend>(sp => sp.GetRequiredService<StdoutBackend>());
        services.AddSingleton<ISyncTerminalBackend>(sp => sp.GetRequiredService<StdoutBackend>());
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

        services.AddSingleton(sp =>
        {
            var screen = ChatScreen.Build(
                sp.GetRequiredService<ComposerController>(),
                sp.GetRequiredService<StatusViewModel>());

            // Panel chrome (zone separation: feed⇄input⇄status borders, panel
            // titles, chrome surfaces, inter-message separators). Enabled here
            // — the interactive composition root — so block/panel goldens that
            // build their own screens keep painting byte-identically.
            screen.Timeline.Timeline.ShowSeparators = true;
            screen.Composer.ShowChrome = true;
            screen.Status.ShowChrome = true;
            return screen;
        });

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
            autoSubscribe: false, // events pumped through the frame loop thread
            coordinator: sp.GetRequiredService<IApprovalCoordinator>()));

        // Permission asks вместо молчаливого fail-closed deny: карточка
        // ApprovalGateView в таймлайне + ожидание y/n/a. Ленивое замыкание на
        // мост — резолв в момент первого запроса разрешений, не при сборке DI.
        // Последняя регистрация IPermissionService выигрывает (AddHarbor уже отработал),
        // поэтому оверрайд виден и ToolDispatcher'у внутри агента.
        //
        // #52: one-shot verbs (ask, run task) и headless-контексты никогда не
        // крутят ChatScreen frame loop — решение гейта не придёт никогда и
        // Ask-вызовы висят вечно. Без доступного аппрувера оверрайд не
        // регистрируем: остаётся fail-closed Deny из IntelligenceModule.
        // #49 PR1: внутри гейта asker идёт через IApprovalCoordinator.
        if (IsApprovalPromptAvailable())
        {
            services.AddSingleton(sp => new CellForgePermissionAsker(
                () => sp.GetRequiredService<ChatScreenBridge>(),
                sp.GetRequiredService<IApprovalCoordinator>()));
            services.AddSingleton(sp => new Repl.PlainPermissionAsker(
                sp.GetRequiredService<ITuiRenderer>()));
            // #1759: the console surface for the model-facing question channel.
            // It sits next to the permission askers because it answers the same
            // kind of question to the same kind of person. The host attaches it
            // (rather than the module registering it as the presenter) because
            // only the host knows whether the fullscreen frame loop owns stdin.
            services.AddSingleton(sp => new Repl.PlainUserQuestionAsker(
                sp.GetRequiredService<ITuiRenderer>()));
            services.AddSingleton<IPermissionService>(sp => CreatePermissionService(sp));
        }

        return services;
    }

    /// <summary>
    ///     CellForge-вариант <see cref="IPermissionService" />: тот же
    ///     <see cref="PermissionService" />, что регистрирует
    ///     <c>IntelligenceModule</c>, плюс интерактивный asker через
    ///     approval-карточку. #1125: оверрайд обязан прокидывать
    ///     <see cref="IConfigStore" /> — иначе <c>LoadPersistedAsync</c>
    ///     no-op и секция <c>permissions</c> из <c>config.json</c> в
    ///     интерактивном пути никогда не читается.
    ///     Отдельным методом (а не инлайн-лямбдой), чтобы регрессионный
    ///     тест дотягивался до фабрики напрямую: env-гейт
    ///     <see cref="IsApprovalPromptAvailable" /> в CI недоступен.
    /// </summary>
    internal static PermissionService CreatePermissionService(IServiceProvider sp)
    {
        // [steal/opencode] #1182 (epic #1155): density dispatch, snapshotted
        // once — env/config are process-static, and per-ask resolution would
        // do config-file IO on the tool-execution thread. The gate card needs
        // the CellForge frame loop (key routing + render-thread drain); under
        // any other renderer the posted gate would wait forever, so ask the
        // same machine's mini one-liner through the active renderer instead.
        // No PlainPermissionAsker registered (direct factory tests) keeps the
        // card path whatever the env says.
        bool mini = !TuiMode.IsCellForgeSelected()
            && sp.GetService<Repl.PlainPermissionAsker>() is not null;
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> asker = mini
            ? sp.GetRequiredService<Repl.PlainPermissionAsker>().AskAsync
            : sp.GetRequiredService<Repl.CellForgePermissionAsker>().AskAsync;
        return new(
            sp.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                Harbor.Application.Permissions.PermissionService>>(),
            asker,
            workspaceRoot: Directory.GetCurrentDirectory(),
            configStore: sp.GetService<IPermissionConfigStore>());
    }

    /// <summary>
    ///     Есть ли кому отвечать на approval-карточку (#52). Аппрувер — живой
    ///     человек за TTY в интерактивном REPL: stdin не перенаправлен (есть
    ///     клавиатура) и процесс не помечен как one-shot
    ///     (<c>HARBOR_NO_APPROVER=1</c> ставят <c>ask</c>/<c>run task</c> до
    ///     сборки хоста — frame loop там не крутится при любом TTY).
    /// </summary>
    internal static bool IsApprovalPromptAvailable() =>
        Environment.GetEnvironmentVariable("HARBOR_NO_APPROVER") != "1"
        && Environment.UserInteractive
        && !Console.IsInputRedirected;

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
