# ConsoleEx — дизайн терминального рендерера для Harbor

> **СТАТУС (2026-08-27): реализовано (CE-0…CE-4, PTY-хардненинг CE-5).** См. ADR-003 в
> [DECISIONS.md](../DECISIONS.md); включение и актуальные ограничения MVP —
> [src/Harbor.Tui.ConsoleEx/README.md](../src/Harbor.Tui.ConsoleEx/README.md).
> Этот файл сохранён как design rationale привязки фаз к спринтам.

> Спринт-фундамент: raw stdin парсер, kitty keyboard protocol, SGR mouse,
> bracketed paste, NativeAOT-safe. READ ONLY анализ репо `/mnt/projects/Harbor-Harness`
> (dev). v1.0 — финал: вычитка, фазовая оценка трудозатрат, привязка к спринту-5.

---

## 0. TL;DR

**Что делаем:** новый пакет `src/Harbor.Tui.ConsoleEx/` — первый полноэкранный
интерактивный рендерер Harbor, который сам владеет stdin/stdout: raw mode,
байтовый парсер входа (единый для Linux/Windows через VT-режим), kitty
keyboard protocol с graceful fallback, SGR mouse, bracketed paste с защитой
от paste-инъекций, буферизованный вывод. Всё — BCL-only, без unsafe, без
reflection → AOT-safe.

**Ядро дизайна:** один байтовый поток stdin → state-machine парсер escape-последовательностей
(ECMA-48 + kitty + SGR mouse + paste) → `UiMsg` → существующий `UiStore`/reducer.
Рендерер наследует `BaseTuiRenderer`, реализует `IInteractiveTuiRenderer`
(паттерн уже отработан `SpectreTuiRenderer.RunInteractiveAsync`).

**MVP (один спринт):** raw mode драйверы + парсер legacy-ключей + bracketed paste +
SGR mouse (wheel/click) + kitty probe&push с фолбэком + простая буферизация вывода.
Полноэкранный cell-diff рендер — следующий спринт.

---

## 1. Анализ текущего состояния

### 1.1. Что есть сегодня

**`Harbor.Terminal.Abstractions`**
- `ITuiRenderer` (`InitializeAsync/RenderAsync/ReadLineAsync/Write*/Clear*`,
  опциональный `RunInteractiveAsync`) + маркер `IInteractiveTuiRenderer`
  (`SetSlashHandler`). Program.cs делегирует REPL рендереру, если тот
  интерактивный.
- `ITuiRenderContext`: `Write/WriteColored/WriteStyled/SetCursorPosition/
  ClearLine/Clear/HideCursor/ShowCursor/EnterAlternateScreen/Flush`. Контракт
  thread-safe для Write*, курсорные методы могут быть no-op.
- `BaseTuiRenderer`: реестры Views/ViewModels, 4 builtin view, fan-out
  `AgentEvent` → VM/views, placement-driven рендер (`ShouldRenderPlacement`),
  `Context.Flush()` после каждой placement-порции.

**`Harbor.Tui.Ansi` (`AnsiTuiRenderer`, 213 строк)**
- Line-buffered стриминговый REPL: токены пишутся напрямую (`ctx.Write(td.Delta)`),
  статус-бар и overlay — через builtin views.
- `AnsiRenderContext` (internal): каждый вызов = отдельный `Console.Write`;
  `Width/Height` = живые `Console.WindowWidth/Height`; `Flush()` = `Console.Out.Flush()` —
  **буферизации кадра нет вообще**.
- `ReadLineAsync` = `ShowCursor → prompt → Console.ReadLine() → HideCursor`.
  Блокирующий, cooked mode, без отмены, без inline-редактирования, без истории.
- `InitializeAsync`: только `Console.OutputEncoding = UTF8` + `HideCursor`.
  Alternate screen объявлен в контексте, но **не используется**.
- Статический `Ansi` дублирует коды и пишет в `Console` **мимо контекста** (не тестируется).

### 1.2. Чего не хватает (gap list → цели ConsoleEx)

| # | Gap | Следствие |
|---|-----|-----------|
| G1 | Нет raw mode (termios/Win32) | Нет keypress-событий, только целые строки; Ctrl+C убивает процесс без восстановления терминала |
| G2 | Нет асинхронного байтового чтения stdin | `Console.ReadLine/ReadKey` непригодны: `Console.KeyAvailable` гонка на escape-последовательностях, нет UTF-8 мультибайтовых границ, нет таймаутов |
| G3 | Нет парсера escape-последовательностей | Стрелки/F-клавиши/mouse/paste невозможно различить |
| G4 | Нет пути входа в `UiMsg` | `UiMsg.KeyInput` существует, но ANSI-рендерер его никогда не создаёт; mouse-сообщений нет вовсе |
| G5 | Нет буферизации/диффа вывода | Каждый токен = отдельная запись; мигания, разрывы кадров |
| G6 | Нет capability probing | Невозможно узнать, поддержаны ли kitty/mouse/paste |
| G7 | Нет resize-реакции | Ширина читается на лету, но перерисовки по изменению нет |
| G8 | Нет гарантированного teardown | Крэш в raw mode = сломанный терминал пользователя |

**Важные ограничения репо:** `Directory.Build.props` — `AllowUnsafeBlocks=false`
(«NEVER»), `PublishAot=false` по умолчанию (opt-in per project), `net10.0`,
`TreatWarningsAsErrors`. Спека `specs/07-tui.md` уже фиксирует perf-таргеты:
key echo < 5 ms, token-to-screen < 20 ms, память TUI < 20 MB. Её набросок
raw mode через `stty`-процесс **отвергаем**: spawn процесса на каждый toggle,
зависимость от coreutils, race при ресторе — заменяем на прямой P/Invoke termios.

---

## 2. Kitty keyboard protocol

Спецификация: https://sw.kovidgoyal.net/kitty/keyboard-protocol/

### 2.1. Зачем

В legacy-кодировке неразличимы: `Shift+Enter` ≡ `Enter`, `Ctrl+I` ≡ `Tab`,
`Ctrl+H` ≡ `Backspace`, `Esc` vs начало escape-последовательности (эвристика
по таймеру!). Kitty protocol (flag disambiguate) решает всё это — критично
для многострочного ввода промпта и нормального `Esc`.

### 2.2. Флаги и включение

```
1   Disambiguate escape codes      ← MVP
2   Report event types (press/repeat/release)
4   Report alternate keys
8   Report all keys as escape codes
16  Report associated text
```

- Push: `CSI > flags u` = `\x1b[>{flags}u` (стековый, вложенный push допустим).
- Pop: `CSI < u` = `\x1b[<u` (pop 1 уровень).
- Запрос текущих флагов: `CSI ? u`, ответ терминала — `CSI ? {flags} u`.

MVP-политика: push `flags=1` (только disambiguate; текст идёт обычными байтами,
не через associated text). Release-события (flag 2) — out of MVP. Если позже
понадобятся IME/текстовые раскладки — поднять до `1|16=17`.

### 2.3. Формат события

```
CSI unicode-key-code ; modifiers ; text-as-codepoints u
CSI unicode-key-code : shifted : base-layout-key ;
    modifiers : text-as-codepoints u        (sub-params через ':')
```

- modifiers = `1 + (shift=1, ctrl=2, alt=4, super=8, hyper=16, meta=32,
  caps_lock=64, num_lock=128)` → `Shift+Enter` = `CSI 13;2u`.
- key-codes — unicode codepoints функциональных клавиш (13=Enter, 27=Esc,
  127=Backspace, 57344+ private use для медиа-клавиш).
- Repeat-события приходят как press, если flag 2 не ставили — нам подходит.

Парсинг: тот же CSI-парсер, что и для всего остального; распознаём финальный
байт `u` + наличие первого параметра ≥ 27 (или известный функциональный
codepoint). Sub-params `:` разбираются в фиксированном бюджете (см. §5).

### 2.4. Детект и fallback

1. После входа в raw mode отправить `CSI ? u` и ждать ответ **с таймаутом**
   (по умолчанию 150 мс; env `HARBOR_TUI_KITTY_TIMEOUT_MS` — для медленного SSH).
2. Есть ответ → режим kitty активен, запоминаем фактические флаги из ответа.
3. Нет ответа → legacy-режим: SS3/CSI-коды (`\x1b[A`…`\x1b[1;5A` с модификаторами,
   `\x1bOP`…), одиночный `ESC` — это сразу `UiKeyCode.Escape` (мы в raw mode,
   таймер-эвристика не нужна: байты приходят одним chunk'ом для последовательностей).
4. Оба режима живут в одном `InputAdapter` — переключение = поле-enum, не ветка
   на уровне парсера.

> **Уточнение про Esc-vs-последовательность:** «байты одним chunk'ом» —
> эвристика, а не гарантия: escape-последовательность может быть разрезана
> между read'ами (см. риск в §8.3). Политика парсера: по таймауту ~50 мс
> (`HARBOR_TUI_ESC_TIMEOUT_MS`, 0 = отключить) одиночный `ESC` на границе
> чанка эмитится как `UiKeyCode.Escape`; при продолжении последовательности
> уже эмитted Escape откату не подлежит (принимаем редкий ложный Escape как
> плату за отзывчивость — это лучше зависания ввода). Внутри чанка ESC всегда
> разбирается как начало последовательности.

**Teardown-порядок (строго):** pop kitty (`CSI < u`) → mouse off → paste off →
exit alt screen → show cursor → restore termios/console-mode → flush.
Весь teardown в `try/finally` + обработчики `PosixSignalRegistration`
(SIGINT/SIGTERM/SIGHUP) + AppDomain unhandled exception hook.

### 2.5. Поддержка терминалами (2026)

| Терминал | Kitty protocol |
|---|---|
| Kitty, Ghostty, foot, WezTerm, Alacritty (частично) | ✅ |
| iTerm2 | ✅ |
| Windows Terminal ≥ 1.22 | ✅ (частично) |
| ConHost, старые xterm, gnome-terminal (VTE до недавнего) | ❌ → legacy fallback |

Fallback обязателен и является основным путём на Windows-ConHost и в tmux
(detect `$TMUX`/`$STY` → не пушим kitty вовсе; passthrough-обёртки
`\x1bPtmux;` — out of scope).

---

## 3. SGR mouse (1000/1002/1006)

### 3.1. Режимы

| Код | Режим | Решение |
|---|---|---|
| `?9h` | X10 (только press, col>95 баг) | ❌ не используем |
| `?1000h` | Normal tracking: press+release | ✅ база |
| `?1002h` | Button-event tracking: + motion при зажатой кнопке | ✅ для drag |
| `?1003h` | Any-event tracking (все движения) | ❌ поток мусора, flood |
| `?1005h` | UTF-8 координаты | ❌ ломается на CJK-ширине |
| `?1006h` | **SGR** — десятичные параметры, до 223+ и за пределами экрана | ✅ обязателен |

Включение MVP: `CSI ?1000h CSI ?1006h` (click). Опционально `?1002h`, если
нужен drag (resize панелей мышью — v2).

### 3.2. Формат события

```
CSI < button ; column ; row M   (press/motion)
CSI < button ; column ; row m   (release)
```

- Координаты **1-based**, x=column, y=row. Внутри парсера сразу → 0-based.
- Биты кнопки в `button`: 0=left, 1=middle, 2=right; `+4`=shift,
  `+8`=meta(alt), `+16`=ctrl (те же модификаторные биты, что у kitty);
  `+32`=motion; `+64`=wheel.
- Wheel: button 64=up, 65=down (64/65 с модификаторами); некоторые терминалы
  шлют 66/67 (left/right wheel) и 68–71 (горизонтальный скролл) — маппим
  64/65 на scroll, остальные игнорируем с логом на debug-уровне.

### 3.3. Ограничения и грабли

1. **Нет hover без 1003** — подсветка «на которую навели» невозможна в MVP.
2. Release после drag вне окна может приходить с координатами за пределами
   экрана (в SGR они честно > width/height) — clamp'ить перед индексацией.
3. tmux/screen: mouse надо включать и в мультиплексоре (`set -g mouse on`),
   иначе события не дойдут. Detect `$TMUX` → показываем подсказку один раз.
4. ConHost: SGR mouse работает через VT-input mode; legacy Win32
   `ENABLE_MOUSE_INPUT` мы НЕ используем (иначе два источника событий).
5. Двойной клик = эвристика по таймеру на стороне Harbor (< 300 ms между
   release), терминал сам его не репортит.
6. События мыши и клавиатуры могут приходить вперемешку в одном чанке —
   парсер обязан быть чистым state-machine по байтам, без «прочитать всё до \n».

Маппинг в UiMsg: новые записи `MousePress(MouseButton, int Col, int Row, KeyModifierSet)`,
`MouseRelease(...)`, `MouseWheel(WheelDirection, ...)` (см. §6).

---

## 4. Bracketed paste (`CSI ?2004h/l`)

### 4.1. Механика

- Enable: `\x1b[?2004h`, disable: `\x1b[?2004l`.
- Paste приходит как `\x1b[200~ <произвольные байты> \x1b[201~`.
- Поддержка: практически универсальна (xterm ≥ 277, все современные, WT,
  iTerm2, tmux). Если терминал не включил режим — маркеров просто не будет;
  это безопасный no-op fallback.

### 4.2. Защита от paste-инъекций (обязательно)

Paste из буфера обмена — вектор атаки: «скопируй и вставь» текст вида
`rm -rf /⏎ harbor --dangerous …`. Правила ConsoleEx:

1. **Маркеры не проходят наружу**: парсер поглощает `200~/201~`, наружу отдаёт
   только содержимое как единый `PasteMsg`.
2. **Новые строки внутри paste НЕ триггерят submit** — paste складывается в
   input buffer как literal text (с `\n` → видимый перенос), отправка только
   явным Enter пользователем. Это главный анти-инъекционный инвариант.
3. Первая строка большого paste (> N строк или > K байт, напр. 512) вставляется,
   остаток сворачивается в плейсхолдер `[paste 4.2 KiB — Enter чтобы раскрыть]`;
   полный текст уходит в промпт при submit (не держим гигантскую строку в
   рендере).
4. Escape-последовательности **внутри** paste: по спецификации терминал их не
   генерирует, но вредоносный контент может содержать сырые `\x1b[...` байты.
   Парсер внутри paste-блока НЕ интерпретирует escape-коды (кроме закрывающего
   `201~`) — просто копирует байты. Защита от «ESC-in-paste» атак.
   - Известная проблема реальных терминалов: некоторые режут большой paste на
     куски → закрывающий маркер может прийти в следующем chunk'е. Поэтому
     paste-state живёт в парсере (межчанковый), а не в обработчике чанка.
5. Вложенные `200~` без `201~`: считаем повторный `200~` частью текста
   (логируем warning), жёсткий таймаут 10 c на незакрытый paste → аварийный
   выход из paste-состояния.

UiMsg: `sealed record Paste(string Text, bool WasTruncated)`.

---

## 5. Raw stdin парсер — архитектура

### 5.1. Конвейер

```
stdin (byte stream)
   │  TerminalInputSource        — raw mode driver + async read loop
   ▼  ReadOnlySequence<byte>/Memory<byte> chunks
InputAssembler                  — ring buffer, склейка разрезанных seq
   │  ReadOnlySpan<byte>
EscapeSequenceParser            — pure state-machine, zero-alloc на дельту
   │  InputEvent (struct union)
InputAdapter                    — kitty/legacy декодирование → UiKey/Mouse/Paste
   ▼  Channel<UiMsg> (unbounded, SingleReader)
UiStore.Dispatch                — существующий reducer
```

Принципы:
- Парсер — **чистая функция состояния**: `Parse(span) → events + bytes consumed`,
  состояние (ожидание CSI-финалатора, inside-paste, utf8-tail) хранится в
  struct-поле. Никаких аллокаций на путь key/wheel; аллокации только на
  `Char`(string)/`Paste`(string) — неизбежные.
- Тестирование: golden-byte тесты — массивы байт → ожидаемые UiMsg (без
  реального терминала). Это главный тестовый актив спринта.

### 5.2. TerminalInputSource — raw mode драйверы

Две реализации за интерфейсом `ITerminalModeController`:

```csharp
internal interface ITerminalModeController
{
    void Enter();          // сохранить исходное состояние + включить raw
    void Restore();
    bool IsRaw { get; }
}
```

**Linux/macOS — termios P/Invoke** (замена stty из спеки):
- `libc: tcgetattr/tcsetattr` (или `ioctl(TCGETS/TCSETS)`), флаги:
  off: `ICANON | ECHO | ISIG | IEXTEN | OPOST*`; VMIN=0, VTIME=0 → read
  возвращает сразу что есть (неблокирующее поведение поверх blocking fd).
- `ISIG off` ⇒ Ctrl+C придёт байтом `0x03` → сами маппим в
  `ChatAction.Abort/Quit` (уже есть в ChatAction). Обязателен restore в finally.
- AOT: `[LibraryImport]` (source-generated marshalling), не `[DllImport]`.

**Windows — SetConsoleMode**:
- Input: снять `ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT`,
  поставить `ENABLE_VIRTUAL_TERMINAL_INPUT`.
- Output: поставить `ENABLE_VIRTUAL_TERMINAL_PROCESSING` (+`DISABLE_NEWLINE_AUTO_RETURN`
  опционально).
- В ConHost без VT-input старый ввод недоступен — фиксируем: ConsoleEx требует
  Windows 10+ / WT; на более старых — renderer отказывается инициализироваться
  (Result.Failure), приложение падает обратно на AnsiTuiRenderer.

Чтение: **не** `Console.OpenStandardInput()` поверх StreamReader — берём
`Stream.ReadAsync(Memory<byte>)` напрямую (Console.In кэширует декодер и
ломает границы). На Unix fd 0 блокирующий → читаем в выделенном long-lived
thread (TaskCreationOptions.LongRunning), постим в Channel; отмену — через
закрытие stdin-дескриптора/`dup2(/dev/null)` в teardown, либо
`PosixSignalRegistration` → environment exit path с восстановлением терминала.
Альтернатива на .NET 10: `SafeFileHandle` + `ReadAsync` с pre-read poll через
`select()` — оставляем как оптимизацию v2, MVP: dedicated thread.

### 5.3. EscapeSequenceParser — state machine

Состояния: `Ground → Esc → Csi(param/intermediate) → Finalize`,
плюс `Ss3` (для legacy `ESC O A`), `PastePayload`, `Utf8Tail(n)`.

Правила:
- CSI: параметры `'0'..'9';';' ':'`, промежуточные `' '.."'".."/"`,
  финальный байт `@ .. ~` (диапазоны по ECMA-48). Бюджет длины: параметры
  ≤ 16 байт, intermediate ≤ 2 — переполнение = malformed → сброс в Ground +
  эмит `UnknownSequence` (не вешаем UI на мусоре).
- UTF-8: инкрементальный декодер (`Rune.DecodeFromUtf8` на собранном буфере),
  хвост мультибайта держим между чанками; invalid sequences → U+FFFD.
- Ground byte `< 0x20`: Ctrl-коды (`\r`,`\t`,`0x03`,`0x04`,...) → UiKey сразу.
- `0x7f` = Backspace (важно: не Delete!).
- DSR/DA ответы (`CSI ? ... c/n/R`) — распознаём и роутим в capability-probe
  (см. §6.3), не в UI.

### 5.4. Zero-allocation бюджет

| Путь | Аллокации |
|---|---|
| Key/wheel/mouse event | 0 (struct InputEvent в буфер канала) |
| Char (обычный символ) | 1 string на символ — приемлемо; v2: батчинг подряд идущих символов в одну string |
| Paste | 1 string на paste |
| Resize | 0 (два int) |
| Read loop | переиспользуемый Memory (long-lived buffer) |

Канал: `Channel<UiMsg>.CreateUnbounded(SingleReader=true)` — уже паттерн
репо (`specs/07-tui.md §2.1`). Запись в канал из reader-thread, чтение в
главном цикле `RunInteractiveAsync`.

## 6. Windows Terminal vs Linux — матрица отличий

| Аспект | Linux/macOS | Windows Terminal | ConHost (legacy) |
|---|---|---|---|
| Raw mode | termios (`tcsetattr`), ISIG off | `SetConsoleMode`, VT-input | частично, без VT-input → **не поддержан** |
| ANSI out | всегда | `ENABLE_VIRTUAL_TERMINAL_PROCESSING` (WT включён по умолчанию) | нужен ручной enable, Win10+ |
| Kitty keyboard | kitty/ghostty/foot/WezTerm/iTerm2 ✅; VTE/gnome-terminal ❌/частично; tmux ❌ (passthrough) | ≥ 1.22 частично ✅ | ❌ |
| SGR mouse 1006 | универсально ✅ | ✅ | ✅ через VT-input |
| Bracketed paste | ✅ почти везде | ✅ | ⚠️ свежие Win10 builds |
| SIGINT | байт 0x03 при ISIG off | Ctrl+C → `CTRL_C_EVENT`, при processed-off приходит как `\x03` | то же |
| Resize | `SIGWINCH` → `PosixSignalRegistration` | событие буфера: polling `GetConsoleScreenBufferInfo` в render-тике (100–250 мс), либо `Console.Size` diff | polling |
| UTF-8 stdin | сырые байты UTF-8 | VT-input отдаёт UTF-8 байты (при `Console.InputEncoding=UTF8`) | code page зависит |
| `$TERM` sniffing | `xterm-kitty`, `foot`, `alacritty`, `wezterm`, ghostty env | `TERM_PROGRAM=vscode/wt`… | — |

Правила:
1. Capability detection **не строим на sniffing** — только активные probe
   (`CSI ? u` для kitty; mouse/paste считаем доступными по умолчанию,
   отключение — только по явному env `HARBOR_TUI_NO_MOUSE/PASTE=1`).
2. Sniffing используем лишь как подсказку/логи (`$TERM`, `$TERM_PROGRAM`,
   `$WT_SESSION`, `$TMUX`, `SSH_CONNECTION`).
3. Единый путь байтов: на Windows тоже разбираем escape-последовательности из
   VT-input, а не Win32 `INPUT_RECORD` — один парсер на все платформы.
   Это ключевое упрощение против Terminal.Gui/Spectre подходов.

---

## 7. Структура `src/Harbor.Tui.ConsoleEx/`

```
Harbor.Tui.ConsoleEx/
├── Harbor.Tui.ConsoleEx.csproj      # refs: Terminal.Abstractions (+Ui.Framework.State transitively),
│                                    # Logging.Abstractions; PublishAot=true (opt-in, первый AOT-proект TUI)
├── ConsoleExTuiRenderer.cs          # : BaseTuiRenderer, IInteractiveTuiRenderer
│                                    #   - RunInteractiveAsync: alt-screen, event loop, teardown ladder
│                                    #   - ReadLineAsync: raw inline editor (история, Ctrl+W/U)
│                                    #   - SetSlashHandler (из маркера)
├── Input/
│   ├── ITerminalModeController.cs   # Enter/Restore/IsRaw
│   ├── TermiosModeController.cs     # Unix, [LibraryImport] libc
│   ├── WindowsConsoleModeController.cs
│   ├── NullModeController.cs        # тесты/piped stdin
│   ├── TerminalInputSource.cs       # reader thread → Channel<RawChunk>
│   └── RawChunk.cs                  # struct: Memory<byte>, length
├── Parsing/
│   ├── EscapeSequenceParser.cs      # pure state-machine (ref struct-friendly)
│   ├── ParserState.cs               # enum состояний + budget константы
│   ├── InputEvent.cs                # struct union: Key/Mouse/Paste/Capability/Resize
│   └── Utf8IncrementalDecoder.cs    # обёртка над Rune.DecodeFromUtf8 c хвостом
├── Adapters/
│   ├── KittyKeyboardAdapter.cs      # CSI u → UiKey (+флаги протокола)
│   ├── LegacyKeyAdapter.cs          # SS3/CSI A..Z, F1-F12, модификаторы 1;2A
│   ├── MouseAdapter.cs              # SGR 1006 decode → MouseButton/Wheel
│   └── PasteAdapter.cs              # 200~/201~ payload assembly
├── Capabilities/
│   ├── CapabilityProber.cs          # CSI ?u / DA1 запросы, таймауты, результат struct
│   └── TerminalCapabilities.cs      # readonly record: Kitty, Mouse, Paste, TrueColor...
├── Rendering/
│   ├── ConsoleExRenderContext.cs    # : ITuiRenderContext, BufferedWriteBuffer (StringBuilder→stdout)
│   └── AnsiOut.cs                   # статические CSI-константы (kitty/mouse/paste/alt screen);
│                                    #   НЕ пишет сам, только форматирует (урок Ansi.cs)
├── Integration/
│   └── ConsoleExServiceCollectionExtensions.cs   # AddHarborConsoleEx()
└── Properties/
    └── AssemblyInfo.cs              # [assembly: InternalsVisibleTo("Harbor.Tui.ConsoleEx.Tests")]
```

### Интеграция с BaseTuiRenderer

- Наследуем `BaseTuiRenderer` → бесплатно получаем builtin views/VMs,
  fan-out `AgentEvent`, placement-логику. Переопределяем
  `ShouldRenderPlacement` как AnsiTuiRenderer (ChatHistory/Input live).
- `InitializeAsync`: register/builtin → bind → freeze → `mode.Enter()` →
  enable paste+mouse → kitty probe → enter alt screen → initial frame.
  Порядок строго такой: probe до первого кадра, чтобы кадр не мигал.
- `RunInteractiveAsync` (по образцу SpectreTuiRenderer): создаёт `UiStore`,
  `TuiEffectHost`, регистрирует панели, запускает input-loop и merge agent-events;
  выход — `QuitApp` effect или Ctrl+D (EOF-байт `0x04`).
- `Dispose()`: полный teardown-ladder из §2.4 (идем в обратном порядке включения),
  идемпотентен (повторный вызов безопасен).

**Зависимости:** только BCL + `Microsoft.Extensions.Logging.Abstractions`.
Никакого Spectre/Terminal.Gui. `UiMsg` уже в `Harbor.Ui.Framework.State`,
на который Terminal.Abstractions ссылается транзитивно.

### Новые UiMsg (добавляются в Harbor.Ui.Framework.State)

```csharp
public sealed record MousePress(MouseButton Button, int Col, int Row,
    KeyModifierSet Mods) : UiMsg;                 // MouseButton: enum Left/Middle/Right
public sealed record MouseRelease(MouseButton Button, int Col, int Row,
    KeyModifierSet Mods) : UiMsg;
public sealed record MouseWheel(WheelDir Dir, int Col, int Row,
    KeyModifierSet Mods) : UiMsg;                 // WheelDir: Up/Down (+Left/Right v2)
public sealed record Paste(string Text, bool WasTruncated) : UiMsg;
public sealed record TerminalResized(int Width, int Height) : UiMsg;
```

Редьюсер: `Paste` → append к input buffer (без submit!), `MouseWheel` →
ScrollUp/DownLine ×3, `MousePress` в MVP — только клик по панели = фокус
(`FocusPanel`), остальное игнорируется.

---

## 8. Оценки, риски, MVP

### 8.1. Размеры компонентов

| Компонент | Size | Чел.*дни | Комментарий |
|---|---|---|---|
| Termios/Win32 mode controllers | M | 2–3 | P/Invoke + restore-path + тесты на skip-платформах |
| TerminalInputSource (reader thread) | M | 1–2 | канал, cancel, teardown |
| EscapeSequenceParser | L | 3–4 | state machine + UTF-8 хвосты + malformed-устойчивость |
| Legacy key adapter | M | 1–2 | таблица кодов xterm/VT |
| Kitty adapter + probe/fallback | M | 2 | push/pop, таймаут, флаги |
| SGR mouse adapter | S | 1 | простые биты |
| Bracketed paste + анти-инъекция | S–M | 1–1.5 | межчанковый state, лимиты |
| ConsoleExRenderContext + буфер | S–M | 1–2 | flush-точки, thread-safety Write* |
| Renderer + RunInteractiveAsync | L | 3–5 | event loop, merge с agent events, resize |
| Golden-byte тесты | M | 2–3 | главный регрессионный актив |
| **MVP итого** | **L** | **~17–25 дн.** | один спринт 2 инженера или 3–4 нед. один |

v2 (следующие спринты): cell-diff полноэкранный рендер, drag-resize панелей
(1002), двойной клик, select-copy, 2026-synced kitty flags (release events).

### 8.2. MVP scope (критерии готовности)

1. Приложение стартует в alt-screen, корректно восстанавливает терминал при
   любом выходе (включая kill -TERM и необработанное исключение) — проверяется
   скриптом-«гробовщиком».
2. Печать Unicode (эмодзи/кириллица) в инпут работает на всех платформах.
3. Стрелки/модификаторы через legacy и kitty дают идентичные UiKey (golden-тесты).
4. Wheel-scroll двигает историю; click по панели даёт фокус.
5. Paste многострочного текста вставляет literal, submit только по Enter;
   >512 Б сворачивается.
6. `dotnet publish -r linux-x64 -p:PublishAot=true` собирается без AOT-warning
   (IL3050/IL2026 = 0 в новом проекте).
7. Piped stdin (`echo hi | harbor`) → renderer отказывается (isatty-check) и
   падение обратно на AnsiTuiRenderer.

### 8.3. Риски

| Риск | Вероятн. | Урон | Митигация |
|---|---|---|---|
| Blocking read на fd 0 не отменяется при shutdown | высокая | виснет процесс при выходе | dedicated thread + закрытие fd/dup2; PosixSignal handlers; финальный Environment.Exit после restore |
| Крэш в raw mode оставляет сломанный терминал | средняя | UX-катастрофа | restore в finally + signal hooks + идемпотентный Dispose; smoke-скрипт |
| Разрезанные escape-последовательности между чанками | высокая | мусорные символы вместо клавиш | InputAssembler + parser state между чанками (обязательный golden-тест «seq split across reads») |
| kitty probe timeout на медленном SSH → ложный fallback | средняя | теряются Shift+Enter и т.п. | настраиваемый таймаут; повторный probe лениво; лог |
| ConHost/старый WT без VT-input | средняя | неработающий ввод на старой Windows | isatty+version gate → Result.Failure → fallback renderer |
| Три источника правды о размерах (Console.Window*, ioctl, buffer info) | низкая | артефакты перерисовки | один TerminalResized msg из одного детектора |
| Paste flood (10 МБ из буфера) | низкая | фриз UI | лимит размера paste (напр. 256 KiB) + truncation |
| TreatWarningsAsErrors + новый P/Invoke шум | средняя | красная сборка CA-правил | LibraryImport source-gen, suppressions локальные, BannedApi.txt проверить |
| Мультиплексоры (tmux/screen/su) ломают probe | высокая | ложные capabilities | detect env → conservative defaults |

### 8.4. Открытые вопросы (на планирование спринта)

1. Полноэкранный ли MVP (alt-screen + свой layout) или «inline-mode»
   (рендер снизу промпта, как crush/claude-code)? Inline проще, но тогда
   cell-diff не нужен сразу. Предложение: inline-mode в MVP.
   > **Решение (финализация):** MVP = **inline-mode без alt-screen**;
   > alt-screen и полноэкранный cell-diff переносятся в фазу 2 (см. §8.5).
   > Соответственно, в `InitializeAsync` шаг «enter alt screen» выполняется
   > только в full-режиме (env `HARBOR_TUI_FULLSCREEN=1`); критерий готовности
   > 8.2.1 читается как «восстановление терминала», а не вход в alt-screen.
2. `ReadLineAsync` в raw режиме: полноценный inline editor (история, Ctrl+W) —
   входит в MVP или заглушка? Влияет на ~3 дня оценки.
3. Нужен ли drag (1002) для resize панелей в первом релизе? (по умолчанию нет)
4. Синхронизация с планами Sixel-рендерера (contrib) — он тоже наследует
   AnsiTuiRenderer; ConsoleEx не должен блокировать его путь.

---

## 8.5. Фазовая оценка трудозатрат (MVP → полный) и спринт-5

> Добавлено при финализации (v1.0). База — таблица 8.1 (~17–25 дн. MVP).

### Фазы

| Фаза | Содержание | Оценка | Выход |
|---|---|---|---|
| **0. Каркас** (2–3 дн.) | Проект `Harbor.Tui.ConsoleEx` (PublishAot=true, InternalsVisibleTo), `ITerminalModeController` + Termios/Win32/Null, isatty-gate → fallback на AnsiTuiRenderer | 2–3 дн. | `harbor` стартует с ConsoleEx в cooked-режиме, AOT-publish зелёный |
| **1. MVP** (§8.1, ~17–25 дн.) | Reader-thread + Channel, EscapeSequenceParser + UTF-8 хвосты, legacy-ключи, kitty probe/fallback, SGR mouse (1000/1006), bracketed paste + анти-инъекция, buffered context, inline-режим, golden-byte тесты, teardown-«гробовщик» | ~17–25 дн. (§8.1) | Критерии §8.2 (с оговоркой 8.4.1: inline, без alt-screen) |
| **2. Full** (полноэкранный, ~10–14 дн.) | Alt-screen + cell-diff буфер (два буфера, diff-флеш), полноэкранный layout панелей, resize-детектор (SIGWINCH/poll), `HARBOR_TUI_FULLSCREEN=1` | ~10–14 дн. | Полноэкранный рендер без мерцания, resize без артефактов |
| **3. v2-улучшения** (~5–8 дн., по потребности) | Drag-resize панелей (1002), двойной клик, select-copy, kitty release-события (flag 2), батчинг Char-событий, `select()`-отмена reader-thread | ~5–8 дн. | — |

**Полный путь MVP → full: ~27–39 чел.*дней** (один инженер: 6–8 недель;
два: 3–4 недели). Фазы 0–2 последовательны; фаза 3 — по мере надобности,
не блокирует full.

### Зависимость от планового спринта-5

- ConsoleEx — кандидат в **спринт-5** (после спринтов 3–4, чья верификация —
  бенчи/пороги — описана в `benchmarks-plan.md` из этого же анализа). Фаза 0
  может быть выполнена параллельно со спринтом-4 как «щуп» (каркас + AOT-сборка
  не трогают существующие рендереры).
- Жёсткие зависимости внутри репо: только `Harbor.Terminal.Abstractions`
  (+ транзитивно `Harbor.Ui.Framework.State`) — новые UiMsg (§7) — единственная
  правка чужого кода, обратно совместима (новые record-наследники).
- Вход в спринт-5 (DoR): решение по 8.4.2 (inline editor в MVP или нет, ±3 дня),
  подтверждение inline-mode (8.4.1 — решено выше), ревью этого дока.
- Риск-буфер спринта: +30% к оценке фазы 1 из-за TreatWarningsAsErrors и
  P/Invoke-шума (риск §8.3, строка «TreatWarningsAsErrors»).

---

## 9. Источники в репо

- `src/Harbor.Terminal.Abstractions/{ITuiRenderer.cs, Renderers/ITuiRenderContext.cs, BaseTuiRenderer.cs}`
- `src/Harbor.Tui.Ansi/{AnsiTuiRenderer.cs (внутри AnsiRenderContext), Ansi.cs}`
- `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs` — образец `RunInteractiveAsync`
- `src/Harbor.Ui.Framework.State/State/{UiMsg.cs, UiKey.cs, ChatAction.cs, UiStore.cs}`
- `specs/07-tui.md` §8 (input), §13 (perf targets), §14 (windows); `specs/08-native-aot.md`
- `Directory.Build.props` (AllowUnsafeBlocks=false, PublishAot opt-in, net10.0)

---

## CONSOLEEX ДИЗАЙН ЗАВЕРШЁН

**Дата:** 23.08.2026. Дизайн покрывает все разделы ТЗ: kitty keyboard protocol
(§2, с fallback-политикой), SGR mouse 1006 (§3), bracketed paste с
анти-инъекцией (§4), Span/Memory raw stdin парсер (§5), MVP scope с
критериями готовности (§8.2), риски с митигациями (§8.3). Фазовая оценка
MVP → full — §8.5; привязка к спринту-5 — §8.5. Ссылки на код репо
проверены по факту 23.08.2026 (SpectreTuiRenderer, State/*.cs,
specs/07-tui.md §8/§13/§14, Directory.Build.props).


