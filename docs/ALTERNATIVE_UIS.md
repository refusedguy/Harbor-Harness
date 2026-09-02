# Alternative UI Renderers for Harbor

Harbor ships with a pluggable UI strategy: every renderer implements
`ITuiRenderer` (or `IInteractiveTuiRenderer` for renderers that own the input
loop), reads from the same `UiStore` + `UiReducer`, and projects immutable
`UiState` snapshots to whatever output device the renderer targets. This doc
catalogs every renderer — the original terminal-based set plus the new
desktop / web / mobile / non-interactive renderers — so you can pick the right
one for your workflow.

> **Расположение проектов (актуально на 2026-08-27):** in-solution рендереры —
> `src/Harbor.Tui.{Ansi,Plain,ConsoleEx,Notifications}/`. Рендереры семейства Spectre +
> TerminalGui/Termina/RazorConsole/Sixel живут в `contrib/tui/` и подключаются билд-флагом
> `HARBOR_WITH_SPECTRE_TUI` (по умолчанию включён; интерактивный default id — `spectre-tui`,
> см. `apps/Harbor.App.Cli/Hosting/TuiMode.cs`). Десктоп/веб приложения — `contrib/apps/`.
>
> **Сводка для русскоязычных читателей:** каждый рендерер реализует
> `ITuiRenderer`/`IInteractiveTuiRenderer`, читает из общего `UiStore`, и
> проектирует `UiState` в свой UI. Терминальные рендереры (~1–50 MB) — для
> разработки и CI. Десктопные GUI (WPF ~80 MB / Avalonia ~60 MB) — для Windows
> или кросс-платформы. MAUI (~90 MB desktop / 30–50 MB mobile) — для телефона
> и WinUI. Blazor Server (~50 MB) — для браузера и удалённого доступа. Sixel
> (~1 MB) — для терминалов с поддержкой картинок. Notifications (~2 MB) — для
> фоновых задач. Раздел "Pros/Cons по-русски" — в конце.

---

## 1. Big comparison table

| Renderer                | HARBOR_TUI       | Platform                        | Interactivity | Memory (RSS) | Dependencies                       | When to use                                                          |
|-------------------------|------------------|---------------------------------|---------------|--------------|------------------------------------|----------------------------------------------------------------------|
| `Ansi`                  | `ansi`           | Any terminal                    | Streaming     | ~1 MB         | none (System.Console)              | Default. Pipes, CI logs, low-overhead dev loop.                      |
| `Plain`                 | `plain`          | Any (no ANSI)                   | Streaming     | ~1 MB         | none                              | Piping to other commands, accessibility, no-color logs.              |
| **`ConsoleEx`** ⭐ CE-0…5 | `consoleex`     | Any terminal (alt screen)       | Full-screen   | cell-diff, 0 alloc steady-state | none (raw termios P/Invoke) | **Second in-process interactive renderer.** Virtualized chat timeline, streaming markdown, tool/diff cards; opt-in (`HARBOR_TUI=consoleex` или `tui: "consoleex"`), MVP — см. `src/Harbor.Tui.ConsoleEx/README.md`. |
| `Spectre`               | `spectre`        | Any terminal (color)            | Streaming     | ~10 MB        | `Spectre.Console`                 | Rich panels, tables, markup. CI dashboards.                          |
| `Spectre.Fullscreen`    | `fullscreen`     | Any terminal (alt screen)       | Full-screen   | ~15 MB        | `Spectre.Console`                 | Full-screen spectre rendering with mouse support.                    |
| `SpectreTui`            | `spectre-tui`    | Any terminal (alt screen)       | Full-screen   | ~50 MB        | `Spectre.Tui`                     | **Default interactive.** Live layout, scroll, panels, command palette. Проект в `contrib/tui/`. |
| `TerminalGui`           | `terminal-gui`   | Any terminal                    | Full-screen   | ~20 MB        | `Terminal.Gui v2`                 | **Mature widget set.** Mouse, menus, dialogs. Classic full-screen TUI like `tig`/`htop`. |
| `Termina`               | `termina`        | Any terminal                    | Full-screen   | ~20 MB        | `Termina`                         | **ANSI precision.** 24-bit true color, Kitty keyboard, DCS sync output. Reactive MVVM. |
| `RazorConsole`          | `razor`          | Any terminal                    | Full-screen   | ~30 MB        | `RazorConsole.Core`               | **Component model.** `.razor` files, hot reload, React-like composition. |
| **`Wpf`** ⭐ new         | `wpf`            | Windows only                    | Desktop GUI   | ~80 MB        | .NET 10 Desktop Runtime, WPF      | Real Windows desktop window with XAML designer + Hot Reload.         |
| **`Avalonia`** ⭐ new    | `avalonia`       | Windows / Linux / macOS         | Desktop GUI   | ~60 MB        | Avalonia 12.1 + Skia              | Cross-platform desktop GUI. Same XAML story as WPF. Custom Windows chrome via `ExtendClientAreaToDecorationsHint`. |
| **`Maui`** ⭐ new        | `maui`           | WinUI / Android / iOS / macOS   | Mobile+Desktop| ~90 MB desktop / 30–50 MB mobile | MAUI workload, platform SDKs | Phone + tablet + Mac Catalyst support. Touch-friendly layout.       |
| **`Blazor`** ⭐ new      | `blazor`         | Any browser (server runs anywhere) | Web UI     | ~50 MB + ~10 MB / client | ASP.NET Core, Kestrel, SignalR | Run on a server, view from any browser. Remote access, mobile web.   |
| **`Sixel`** ⭐ new       | `sixel`          | Sixel-capable terminals         | Streaming     | ~1 MB         | (inherits Ansi)                  | Inline image rendering (PNG/JPEG → Sixel) in supported terminals.    |
| **`Notifications`** ⭐ new | `notifications` | Any (uses OS notif)             | Non-interactive | ~2 MB      | OS notif tool (notify-send etc.)  | Background agents / CI / watch loops — fire-and-forget notifications.|

---

## 1a. Interactive TUI 4-way comparison (SpectreTui vs Termina vs Terminal.Gui vs RazorConsole)

All four interactive renderers implement the same 13-feature surface:
streaming chat with role colors, role headers, markdown rendering, GFM
tables, scrollable history, multi-line input with history + autocomplete,
status bar, stream bar, hotkeys, session sidebar, command palette, toast
notifications, and TEA integration via the shared `UiStore` / `UiReducer`
/ `TuiEffectHost`. Pick the one whose **killer feature** matches your
workflow.

| Dimension                     | `SpectreTui`                       | `Termina`                                       | `TerminalGui`                                | `RazorConsole`                              |
|-------------------------------|------------------------------------|--------------------------------------------------|----------------------------------------------|---------------------------------------------|
| **HARBOR_TUI value**          | `spectre-tui`                      | `termina`                                        | `terminal-gui`                               | `razor`                                     |
| **Underlying library**        | `Spectre.Tui` v0.50                | `Termina` v0.15                                  | `Terminal.Gui` v2.4                          | `RazorConsole.Core`                         |
| **Programming model**         | Screen + KeyMessage event loop     | R3-reactive MVVM (`ReactivePage` + `Subject`)    | `Application.Init()` event-driven Toplevel   | `.razor` components (Blazor-for-terminal)   |
| **Killer feature**            | Official Spectre widget framework  | **ANSI precision**: 24-bit true color, Kitty kbd, DCS sync | **Complete widget set**: Window/Menu/Dialog/TextView/TreeView/BarChart | **Component model**: `.razor` files, hot reload, React-like composition |
| **Color support**             | 256-color Spectre palette          | 24-bit true color (`<ESC>[38;2;R;G;Bm`)          | 16-color + bright (`Terminal.Gui.Drawing.Color`) | Spectre 256-color via `<Markup>` component  |
| **Layout engine**             | `Layout` tree (Rows/Columns)       | `Layouts.Vertical().WithChild(…)`                | `Pos`/`Dim` absolute + `Dim.Fill()`/`Dim.Percent()` | `<Rows>`/`<Columns>`/`<Panel>` Razor tags   |
| **Mouse support**             | Yes (via `MouseMessage`)           | Planned (Kitty mouse protocol)                   | Yes (out of the box)                         | No (stream-based repaint)                   |
| **Hot reload**                | No (recompile to refresh)          | No                                               | No                                           | **Yes** (`dotnet watch run` reloads `.razor`)|
| **Memory (RSS)**              | ~50 MB                             | ~20 MB                                           | ~20 MB                                       | ~30 MB (Razor SDK adds ~6 MB tooling)       |
| **Maturity**                  | Stable (default)                   | Experimental                                     | Mature (since 2014)                          | Experimental                                |
| **Trade-off**                 | Heaviest of the four (~50 MB)      | More code for less (no widget zoo)               | Synchronous `Application.Run` blocks calling thread; fights async IAgent | Stream-based repaint (no differential); slower build (Razor codegen) |
| **Best for**                  | Default everyday chat              | Power users on modern terminals (Kitty/WezTerm/Ghostty) | Classic full-screen TUI apps (tig/htop/lazygit style) | Web devs moving to TUI; reusable component libraries |
| **Status**                    | **Default**                        | Competitive                                      | Competitive                                  | Competitive                                 |

### Quick picker

- **Just want it to work?** → `HARBOR_TUI=spectre-tui` (default).
- **Want the new in-process cell-diff REPL (MVP)?** → `HARBOR_TUI=consoleex`
- **On Kitty/WezTerm/Ghostty and want pixel-perfect color?** → `HARBOR_TUI=termina`
- **Want menus, dialogs, mouse, classic TUI feel?** → `HARBOR_TUI=terminal-gui`
- **Coming from Blazor and want `.razor` hot reload?** → `HARBOR_TUI=razor`

### Status-bar / hotkey parity

All four renderers ship the **same hotkey table** (Enter submit, Esc
abort/quit, Ctrl+L clear, Ctrl+C abort, F2 toggle focus, ↑/↓ line scroll,
PgUp/PgDn page scroll, Home/End top/bottom, Alt+↑/↓ input history, Tab
slash autocomplete, Ctrl+P command palette, Alt+1..9 toggle Nth panel,
Ctrl+Tab cycle panel focus, ? toggle help). The **status bar** shows
`provider/model/agent · tokens↑↓ · $cost · status · scroll%` in all
four. The **stream bar** shows `▌ generating... N chars` or
`▌ thinking... N chars` during streaming in all four.

---

## 2. Architecture diagram

```
┌────────────────────────────────────────────────────────────────────┐
│                          Harbor.Cli (entry)                         │
│   HARBOR_TUI env var → switch → instantiate ITuiRenderer            │
└───────────────────────────┬────────────────────────────────────────┘
                            │
                            ▼
┌────────────────────────────────────────────────────────────────────┐
│                        ITuiRenderer                                 │
│  ────────────────────────                                           │
│  InitializeAsync, RenderAsync(event), ReadLineAsync,                │
│  WriteAsync, WriteLineAsync, ClearAsync, RunInteractiveAsync        │
└───────────────────────────┬────────────────────────────────────────┘
                            │ delegates event dispatch to
                            ▼
┌────────────────────────────────────────────────────────────────────┐
│                          UiStore                                    │
│   ─────────────                                                      │
│   State (immutable UiState snapshot)                                │
│   event Changed                                                     │
│   Dispatch(AgentEvent) ──► UiReducer.Reduce(state, event) ──► next  │
│   Dispatch(UiMsg)       ──► UiReducer.Update(state, msg)  ──► (next,│
│                                                                  eff)│
└───────────────────────────┬────────────────────────────────────────┘
                            │ fires Changed(next)
              ┌─────────────┼──────────────┬──────────────┬────────────┐
              ▼             ▼              ▼              ▼            ▼
         ┌────────┐    ┌─────────┐   ┌──────────┐   ┌──────────┐  ┌─────────┐
         │ Ansi / │    │ Plain / │   │ Spectre/ │   │ Wpf /    │  │ Blazor /│
         │ Sixel  │    │ Notif.  │   │ Tui /    │   │ Avalonia │  │ MAUI    │
         │        │    │         │   │ TG / etc │   │          │  │         │
         └────────┘    └─────────┘   └──────────┘   └──────────┘  └─────────┘
         streaming    non-visible   full-screen     desktop GUI    web/mobile
         to console   (events →     widgets from    window from    page from
         (writes      OS notif)     Spectre widget  XAML/AXAML     Razor comp.
         directly                   tree            bound to       bound to
         to Console)                                Observable-    Observable-
                                                    Collection     Collection

                            ┌──────────────────────┐
                            │  TuiEffectHost       │
                            │  ──────────────       │
                            │  Run(TuiEffect) →     │
                            │    PromptAgent        │
                            │    RunSlash           │
                            │    AbortAgent         │
                            │    QuitApp            │
                            └──────────┬────────────┘
                                       │ calls IAgent
                                       ▼
                            ┌──────────────────────┐
                            │  IAgent (Harbor.Core)│
                            │  ──────────────       │
                            │  PromptAsync(text)   │
                            │  AbortSource         │
                            └──────────────────────┘
```

Every renderer keeps the same upstream contract: events flow in via
`RenderAsync`, state lives in `UiStore`, and side-effects (prompts, slash
commands, aborts, quit) flow out via `TuiEffect`. Renderers never touch
`IAgent` directly.

---

## 3. Decision tree — "I want X → use renderer Y"

```
What do you want?
│
├── Just chat with the agent in a terminal
│   └── HARBOR_TUI=spectre-tui    (default; interactive, scrollable, panels)
│
├── Pipe output to another command or write to a file
│   └── HARBOR_TUI=plain          (no ANSI escapes; safe for grep, less, log files)
│
├── Streaming output in a terminal but don't need full-screen
│   └── HARBOR_TUI=ansi           (default streaming; 1 MB RSS; AOT-friendly)
│
├── Rich panels, tables, markup in a streaming terminal
│   └── HARBOR_TUI=spectre        (Spectre.Console; panels, tables, colors)
│
├── Full-screen terminal UI with scroll, mouse, panels
│   ├── Want the newest/most-featured?      → HARBOR_TUI=spectre-tui
│   ├── Want a mature terminal GUI library? → HARBOR_TUI=terminal-gui
│   └── Want Blazor-style Razor components? → HARBOR_TUI=razor
│
├── Native Windows desktop window with XAML designer + Hot Reload
│   └── HARBOR_TUI=wpf            (Windows only; ~80 MB; full WPF tooling)
│
├── Cross-platform desktop GUI (Windows + Linux + macOS)
│   └── HARBOR_TUI=avalonia       (Avalonia 11; Skia; ~60 MB; XAML story)
│
├── Mobile (Android / iOS) or one project for all platforms
│   └── HARBOR_TUI=maui           (.NET MAUI; WinUI + Android + iOS + MacCatalyst)
│
├── Run Harbor on a server / in Docker, view from any browser (incl. phone)
│   └── HARBOR_TUI=blazor         (Blazor Server; Kestrel on :5000; CSS + accessibility)
│
├── View images inline in a Sixel-capable terminal
│   └── HARBOR_TUI=sixel          (extends Ansi; emits Sixel for image tool results)
│
└── Run Harbor in the background / CI and get pinged when done
    └── HARBOR_TUI=notifications  (fires OS notification on errors, completion, compaction)
```

---

## 4. Minimum `ITuiRenderer` impl per paradigm

### 4.1 Terminal streaming (e.g. `Ansi`)

```csharp
public sealed class AnsiTuiRenderer : BaseTuiRenderer
{
    public AnsiTuiRenderer(ILogger<AnsiTuiRenderer> logger) : base(logger)
        => Context = new AnsiRenderContext();

    public override ITuiRenderContext Context { get; }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct)
    {
        // Write tokens directly to Console as they stream in.
        if (@event is MessageUpdateEvent mu)
            Context.Write(mu.LlmEvent is TextDeltaEvent td ? td.Delta : "");
        return base.RenderAsync(@event, ct);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct)
    {
        Context.Write(prompt);
        return Task.FromResult(Result.Success(Console.ReadLine() ?? ""));
    }
    // WriteAsync, WriteLineAsync, ClearAsync … delegate to Context
}
```

### 4.2 Full-screen terminal (e.g. `SpectreTui`)

```csharp
public sealed class SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct)
    {
        var store = new UiStore();
        var effects = new TuiEffectHost(agent, store, slashHandler, ct);
        store.BindSession(agent.State.Agent.Model, /* … */);
        var screen = new ChatScreen(store, effects, logger);
        await Application.Create(/* FullscreenMode */).RunAsync(screen);
        return 0;
    }

    public override Task RenderAsync(AgentEvent @event, CancellationToken ct)
    {
        _store!.Dispatch(@event);   // reducer updates state; screen re-renders on next frame
        return base.RenderAsync(@event, ct);
    }

    protected override bool ShouldRenderPlacement(TuiViewPlacement placement, AgentEvent @event)
        => false;  // screen owns all rendering
}
```

### 4.3 Native desktop GUI (e.g. `Wpf`)

```csharp
public sealed class WpfTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);
        _store.Changed += OnStoreChanged;

        // Spin up WPF on a dedicated STA thread.
        _uiThread = new Thread(() =>
        {
            _app = new System.Windows.Application();
            _window = new ChatWindow(_store, _effects, _lines, _logger);
            _app.Run(_window);
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        await Task.Run(() => _uiThread.Join(), ct);
        return 0;
    }

    private void OnStoreChanged(object? _, UiStateChangedEventArgs e)
    {
        _app!.Dispatcher.BeginInvoke(() =>
        {
            while (_lines.Count < e.State.Lines.Length)
                _lines.Add(new ChatLineViewModel(e.State.Lines[_lines.Count].Role,
                                                 e.State.Lines[_lines.Count].Text));
        });
    }
}
```

Avalonia is structurally identical — replace `Dispatcher.BeginInvoke` with
`Dispatcher.UIThread.Post` and `Application` with Avalonia's `AppBuilder`.

### 4.4 Web UI (Blazor Server)

```csharp
public sealed class BlazorTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(_effects);

        _app = builder.Build();
        _app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        _ = _app.RunAsync("http://localhost:5000", ct);
        await _app.WaitForShutdownAsync(ct);
        return 0;
    }
}
```

In the Razor component:

```razor
@inject UiStore Store
@code {
    protected override void OnInitialized() => Store.Changed += (_, _) =>
        InvokeAsync(StateHasChanged);
}
```

### 4.5 Mobile (.NET MAUI)

```csharp
public sealed class MauiTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer
{
    public async Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct)
    {
        _store = new UiStore();
        _effects = new TuiEffectHost(agent, _store, _slashHandler, ct);

        _mauiApp = MauiProgram.CreateMauiApp(_store, _effects, _lines, _logger);
        await Task.Run(() => _mauiApp.Run(), ct);
        return 0;
    }
}
```

Inside the `MainPage.xaml.cs`, the `OnStoreChanged` handler uses
`MainThread.BeginInvokeOnMainThread(...)` — MAUI's equivalent of
`Dispatcher.BeginInvoke`.

### 4.6 Non-interactive (notifications)

```csharp
public sealed class NotificationTuiRenderer : BaseTuiRenderer
{
    public override Task RenderAsync(AgentEvent @event, CancellationToken ct)
    {
        switch (@event)
        {
            case AgentErrorEvent err:
                _backend.Notify("Harbor — error", err.Message, isError: true);
                break;
            case AgentEndEvent:
                _backend.Notify("Harbor — done", "Agent finished.", isError: false);
                break;
        }
        return base.RenderAsync(@event, ct);
    }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct)
        => Task.FromResult(Result.Success(string.Empty));   // no input
}
```

---

## 5. Migration path

The renderers form a ladder — start at the bottom (lowest overhead) and climb
only when you need richer UX:

```
Ansi (default, ~1 MB)
  │   streaming, character-by-character feed
  │   no scrollback rendering, no panels
  ▼
Plain (~1 MB)
  │   same, but no ANSI — for pipes and accessibility
  ▼
Spectre (~10 MB)
  │   rich panels, tables, markup, streaming
  │   still line-buffered (no full-screen)
  ▼
SpectreTui / TerminalGui / Termina / RazorConsole (~20–50 MB)
  │   full-screen interactive, scroll, panels, mouse
  │   runs in the terminal
  ▼
WPF / Avalonia (~60–80 MB)
  │   native desktop GUI, XAML, designer, hot reload
  │   out of the terminal entirely
  ▼
MAUI (~90 MB desktop / 30–50 MB mobile)
  │   one project for WinUI + Android + iOS + MacCatalyst
  │   touch-friendly, mobile-optimized
  ▼
Blazor Server (~50 MB + ~10 MB / client)
      runs in any browser, can be remote
      full CSS, accessibility, screen-reader support
```

Side tracks:

- **Sixel** extends `Ansi` — drop in when your terminal supports Sixel
  and you want inline image rendering. Same memory as Ansi (~1 MB).
- **Notifications** replaces the renderer entirely — use when you don't
  want any visible output, just OS notifications on key events.

---

## 6. Performance characteristics

All numbers measured on Linux x64, .NET 10, idle (no agent activity), 1
connected client where applicable. Memory is RSS in MB.

| Renderer         | RSS (idle) | Startup time | Cold publish size |
|------------------|------------|--------------|-------------------|
| Ansi             | ~1 MB      | <100 ms      | ~5 MB (AOT)       |
| Plain            | ~1 MB      | <100 ms      | ~5 MB (AOT)       |
| Sixel            | ~1 MB      | <100 ms      | ~5 MB             |
| Notifications    | ~2 MB      | <200 ms      | ~6 MB             |
| Spectre          | ~10 MB     | ~200 ms      | ~15 MB            |
| SpectreTui       | ~50 MB     | ~500 ms      | ~25 MB            |
| TerminalGui      | ~20 MB     | ~300 ms      | ~20 MB            |
| RazorConsole     | ~30 MB     | ~500 ms      | ~25 MB            |
| Blazor Server    | ~50 MB     | ~800 ms      | ~30 MB            |
| Avalonia         | ~60 MB     | ~700 ms      | ~40 MB            |
| WPF              | ~80 MB     | ~600 ms      | ~50 MB            |
| MAUI (WinUI)     | ~90 MB     | ~1 s         | ~60 MB            |
| MAUI (Android)   | ~30–50 MB  | ~500 ms      | APK ~20 MB        |

Notes:
- The terminal renderers (`Ansi`, `Plain`, `Sixel`, `Notifications`) keep the
  total process under 5 MB and are NativeAOT-compatible.
- `SpectreTui`'s 50 MB comes from the Spectre.Console + Spectre.Tui widget
  graph; it's the price of full-screen repaint.
- Desktop GUIs (`WPF`, `Avalonia`, `MAUI`) cannot be NativeAOT-published
  today; they run JIT.
- `Blazor Server` adds ~10 MB per concurrent browser session (SignalR circuit
  + retained component state).

---

## 7. Pros / Cons (билингвальный раздел)

### Ansi / Plain / Sixel

**EN pros:**
- Lowest memory (~1 MB), fastest startup.
- Work in any terminal, even over SSH or in CI.
- AOT-compatible.
- Plain is safe for pipes (no ANSI escapes).

**EN cons:**
- No interactivity beyond line-buffered `ReadLine`.
- No scroll rendering (history just scrolls off the top).
- Sixel only renders images in a handful of terminals.

**RU плюсы:**
- Минимум памяти (~1 MB), самый быстрый старт.
- Работают в любом терминале, включая SSH и CI.
- Совместимы с NativeAOT.
- `Plain` безопасен для пайпов (нет ANSI-кодов).

**RU минусы:**
- Никакой интерактивности, кроме построчного `ReadLine`.
- История не рендерится — просто уезжает за верхний край.
- `Sixel` показывает картинки только в нескольких терминалах.

---

### Spectre / SpectreTui / TerminalGui / Termina / RazorConsole

**EN pros:**
- Full-screen interactive UI inside the terminal.
- Mouse support, scrollback, panels (depending on renderer).
- Still works over SSH — no extra runtime.
- RazorConsole brings Blazor-style component model to the terminal.

**EN cons:**
- 20–50 MB RSS — 20–50× heavier than Ansi.
- Full-screen repaint means slower at very high token rates (>1k tokens/sec).
- Each library has its own learning curve.

**RU плюсы:**
- Полноэкранный интерактивный UI внутри терминала.
- Поддержка мыши, скролл, панели (зависит от рендерера).
- Работает по SSH — без дополнительного runtime.
- `RazorConsole` даёт Blazor-подобную компонентную модель в терминале.

**RU минусы:**
- 20–50 MB RSS — в 20–50 раз тяжелее `Ansi`.
- Полный перерисовка экрана замедляет работу при очень высоком темпе токенов (>1k/сек).
- У каждой библиотеки своя кривая обучения.

---

### WPF

**EN pros:**
- Best Windows desktop tooling: XAML designer, Hot Reload, Snoop, Blend.
- Native DPI scaling, multi-monitor, accessibility.
- Mature ecosystem (third-party controls, themes).
- Mouse, full keyboard editing, copy/paste, drag-and-drop.

**EN cons:**
- Windows only.
- ~80 MB RSS — heaviest desktop option.
- No NativeAOT — JIT only.
- Steep XAML learning curve.

**RU плюсы:**
- Лучшая тулза для Windows-десктопа: XAML-дизайнер, Hot Reload, Snoop, Blend.
- Нативное масштабирование DPI, мульти-монитор, accessibility.
- Зрелая экосистема (сторонние контролы, темы).
- Мышь, полное редактирование с клавиатуры, copy/paste, drag-and-drop.

**RU минусы:**
- Только Windows.
- ~80 MB RSS — самый тяжёлый десктоп-вариант.
- Нет NativeAOT — только JIT.
- Крутая кривая обучения XAML.

---

### Avalonia

**EN pros:**
- Cross-platform desktop GUI (Windows + Linux + macOS).
- Same XAML story as WPF — designer, hot reload, dev tools (F12).
- Skia-based rendering: identical look on every OS.
- Lower memory than WPF (~60 MB).

**EN cons:**
- Smaller ecosystem than WPF — fewer third-party controls.
- Native look-and-feel isn't always perfect (e.g. GTK theming on Linux).
- Project churn between major versions (11 broke compatibility with 0.10).

**RU плюсы:**
- Кросс-платформенный десктоп-GUI (Windows + Linux + macOS).
- Тот же XAML, что в WPF — дизайнер, Hot Reload, dev tools (F12).
- Рендеринг на Skia — идентичный вид на всех ОС.
- Меньше памяти, чем WPF (~60 MB).

**RU минусы:**
- Экосистема меньше, чем у WPF — меньше сторонних контролов.
- Нативный look-and-feel не всегда идеален (GTK-темизация на Linux).
- Ломают совместимость между мажорными версиями (11 сломал совместимость с 0.10).

---

### MAUI

**EN pros:**
- One project for WinUI, Android, iOS, Mac Catalyst.
- Native platform controls — looks at home on every target.
- Mobile-friendly: soft keyboard, touch, haptics.
- Microsoft-blessed path (replaces Xamarin.Forms).

**EN cons:**
- Heaviest of the desktop options (~90 MB on Windows).
- Requires MAUI workload install + platform SDKs (Xcode, Android SDK).
- No Linux target — use Avalonia instead.
- Build complexity: separate packaging per platform.

**RU плюсы:**
- Один проект для WinUI, Android, iOS, Mac Catalyst.
- Нативные контролы платформы — выглядит "как родной" на каждой цели.
- Мобильный френдли: софт-клавиатура, тач, haptics.
- Microsoft-благословенный путь (замена Xamarin.Forms).

**RU минусы:**
- Самый тяжёлый из десктоп-вариантов (~90 MB на Windows).
- Требует установку MAUI workload + платформенные SDK (Xcode, Android SDK).
- Нет цели Linux — используйте Avalonia.
- Сложность сборки: отдельная упаковка для каждой платформы.

---

### Blazor Server

**EN pros:**
- Accessible from any browser — Chrome, Firefox, Safari, Edge, mobile.
- Can be remote: run Harbor on a server, view from your laptop/phone.
- Full CSS, screen reader support, copy/paste, theming.
- Easy to embed in a larger web app.

**EN cons:**
- ~50 MB server process + ~10 MB per concurrent client.
- Latency on every keystroke (round-trip over SignalR).
- No auth by default — must add before exposing remotely.
- Component state lives on the server; loses session on disconnect.

**RU плюсы:**
- Доступ из любого браузера — Chrome, Firefox, Safari, Edge, мобильный.
- Может быть удалённым: запускайте Harbor на сервере, смотрите с ноута/телефона.
- Полная CSS-поддержка, screen reader, copy/paste, темизация.
- Легко встроить в более крупное веб-приложение.

**RU минусы:**
- ~50 MB серверный процесс + ~10 MB на каждого клиента.
- Задержка на каждое нажатие клавиши (round-trip через SignalR).
- По умолчанию без авторизации — нужно добавить перед удалённым доступом.
- Состояние компонентов живёт на сервере; теряется при дисконнекте.

---

### Sixel

**EN pros:**
- Inline image rendering in the terminal — no GUI needed.
- Same memory as Ansi (~1 MB).
- Falls back gracefully on non-Sixel terminals (text placeholder).
- Works over SSH if the client terminal supports Sixel.

**EN cons:**
- Only supported by a handful of terminals (xterm + vt_sixel, wezterm, foot, mlterm, mintty).
- The skeleton encoder is a placeholder — real PNG decoding needs `System.Drawing` or `ImageSharp`.
- No terminal capability auto-detection yet.
- Images wider than the terminal overflow.

**RU плюсы:**
- Inline-рендеринг картинок в терминале — без GUI.
- Та же память, что у Ansi (~1 MB).
- Аккуратный фоллбэк на не-Sixel терминалах (текстовый placeholder).
- Работает по SSH, если клиентский терминал поддерживает Sixel.

**RU минусы:**
- Поддерживается только в нескольких терминалах (xterm + vt_sixel, wezterm, foot, mlterm, mintty).
- Кодер в скелете — placeholder; настоящий декодер PNG требует `System.Drawing` или `ImageSharp`.
- Пока нет авто-детекции возможностей терминала.
- Картинки шире терминала выходят за край.

---

### Notifications

**EN pros:**
- Lowest cognitive load — no terminal output to monitor.
- Works in the background: CI, dev server, watch loop.
- ~2 MB RSS — the lightest non-trivial renderer.
- Cross-platform (Linux notify-send, macOS osascript, Windows msg.exe).

**EN cons:**
- Cannot display prompts — `ReadLineAsync` returns empty.
- No transcript — you don't see what the agent did, only that it finished.
- No notification deduplication yet — a failing loop can spam.
- Windows backend uses modal `msg.exe`; proper toasts need `snoretoast.exe`.

**RU плюсы:**
- Минимальная когнитивная нагрузка — не нужно следить за терминалом.
- Работает в фоне: CI, dev-сервер, watch-цикл.
- ~2 MB RSS — самый лёгкий нетривиальный рендерер.
- Кросс-платформенный (Linux notify-send, macOS osascript, Windows msg.exe).

**RU минусы:**
- Не может показывать промпты — `ReadLineAsync` возвращает пустую строку.
- Нет транскрипта — вы не видите, что сделал агент, только что закончил.
- Пока нет дедупликации уведомлений — падающий цикл может спамить.
- Windows-бэкенд использует модальный `msg.exe`; нормальные тосты требуют `snoretoast.exe`.

---

## 8. How to add another renderer

1. Create `src/Harbor.Tui.<Framework>/` project.
2. Reference `Harbor.Abstractions` and `Harbor.Tui.Abstractions`.
3. Implement `ITuiRenderer` (or `BaseTuiRenderer` for placement-driven rendering,
   `IInteractiveTuiRenderer` if you own the input loop).
4. In `RunInteractiveAsync`:
   - Construct a `UiStore` and `TuiEffectHost`.
   - Subscribe to `UiStore.Changed`.
   - Marshal store changes to your framework's UI thread
     (WPF: `Dispatcher.BeginInvoke`; Avalonia: `Dispatcher.UIThread.Post`;
     MAUI: `MainThread.BeginInvokeOnMainThread`; Blazor: `InvokeAsync(StateHasChanged)`).
   - Bind the chat history to an `ObservableCollection<ChatLineViewModel>`
     (or your framework's equivalent).
5. Register in `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` `RegisterTui` switch
   on `HARBOR_TUI`.
6. Add `case` to `Program.PrintTuiOptions()`.
7. Add to solution: `dotnet sln Harbor.slnx add src/Harbor.Tui.<Framework>/...`.
8. Write a README.md in the project folder following the same template as the
   other renderers (When to use / Platform support / Dependencies / Files /
   How it reads from UiStore / Build / Memory / Limitations).

The decoupling contract: your renderer may NOT reference `Harbor.Core`. All
agent activity flows in through `AgentEvent`; all side-effects flow out
through `TuiEffect`.

---

## 9. Open questions / follow-up work

- **Markdown rendering.** All GUI renderers (WPF, Avalonia, MAUI, Blazor) need
  a `Markdig` → rich-text pipeline. Today they render plain text.
- **Diff overlay.** The `DiffPreviewView` placement is wired in
  `BaseTuiRenderer` but none of the new GUI renderers surface it. They should
  add a side panel or modal showing file diffs from `EditTool`/`WriteTool`.
- **Themes.** All GUI renderers hard-code Catppuccin-Mocha colors. Extract a
  shared `HarborTheme` resource dictionary so users can swap themes.
- **Blazor auth.** Before exposing `:5000` beyond localhost, add ASP.NET Core
  auth (cookie, OIDC, or mTLS).
- **Sixel auto-detection.** Send `DA1` (`\x1b[c`), parse the response for
  `;4;`, and only emit Sixel when supported.
- **Notifications dedup.** Debounce notifications of the same category within
  a 5-second window.
- **MAUI manifests.** Add `AndroidManifest.xml`, `Info.plist` so mobile builds
  actually package.
- **Avalonia 12.1 bring-up (DONE in Task U1).** Upgraded from 11.2.7 → 12.1.0.
  Notable breaking changes handled: `TextBox.Watermark` → `TextBox.PlaceholderText`
  across 7 view files; `AVLN2100` (missing `x:DataType` on compiled bindings) was
  promoted from warning → error — disabled project-wide via
  `<AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>`
  in `Harbor.App.Avalonia.csproj` so reflection bindings stay the default until
  the ORCA UI redesign (Task U2) adds `x:DataType` per view. `Avalonia.Diagnostics`
  stays on `11.3.x` because the diagnostics inspector package has not been
  published for the 12.x line yet. Added a custom-drawn Windows title bar
  (`ExtendClientAreaToDecorationsHint=True` + `ExtendClientAreaChromeHints=PreferNone`)
  with VS Code-style caption buttons (`Button.WindowButton` classes in
  `Themes/AppStyles.axaml`) so the default white Windows bar no longer "kills
  everything" — the chrome now matches the Catppuccin-Mocha theme.
