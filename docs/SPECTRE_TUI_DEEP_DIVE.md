# SpectreTUI Deep Dive — Harbor

> ⚠️ **CONTRIB RENDERER, NOT THE DEFAULT SHELL.** `Harbor.Tui.SpectreTui` с sprint-2
> живёт в contrib ([`contrib/tui/Harbor.Tui.SpectreTui/`](../contrib/tui/Harbor.Tui.SpectreTui/),
> собирается через `contrib/Contrib.slnx`), но при этом дефолтная CLI-сборка всё ещё
> компилирует его в via флага `HarborWithSpectreTui` (включён по умолчанию;
> `HARBOR_MINIMAL=true` исключает). Вторая интерактивная оболочка — **`src/Harbor.Tui.ConsoleEx/`**
> (raw-mode вход, cell-diff вывод, виртуализированный таймлайн; включение —
> `HARBOR_TUI=consoleex`, см. README проекта). Этот документ полезен и как источник
> рецептов (diff-view, slash-completion, file-tree) для переноса в ConsoleEx.

**Цель документа:** дать полное понимание того, как устроен SpectreTUI-рендерер, чтобы вы могли:
1. Добавлять новые виджеты (например, diff-view, todo-list, file-tree, lsp-diagnostics).
2. Переносить UX-фичи из opencode (полscreen scroll, slash-completion popup) или kilocode (token-usage breakdown).
3. Не наступить на грабли, на которые уже наступили авторы.

---

## 1. Слойная архитектура

```
┌────────────────────────────────────────────────────────────────────┐
│ SpectreTuiRenderer : BaseTuiRenderer, IInteractiveTuiRenderer      │
│   ── инициализируется в Program.cs через HARBOR_TUI=spectre-tui     │
│   ── RunInteractiveAsync() — точка входа в full-screen mode          │
│                                                                    │
│   ┌──────────────────────────────────────────────────────────────┐ │
│   │ ChatScreen : Spectre.Tui.Screen                                │ │
│   │   ── OnMessage(key)   →  key dispatch + scroll                │ │
│   │   ── Render(ctx)      →  layout → widgets                     │ │
│   │   ── SyncLayout       →  копирует UiState в ChatViewProjector │ │
│   └──────────────────────────────────────────────────────────────┘ │
│                              │                                     │
│                              ▼                                     │
│   ┌──────────────────────────────────────────────────────────────┐ │
│   │ ChatViewProjector (facade)                                    │ │
│   │   ── chrome     : ChatChromeView (header / stream / footer)   │ │
│   │   ── history    : ChatHistoryView (scroll window over cache)  │ │
│   │   ── shell      : ChatLayoutShell (Spectre Layout tree)       │ │
│   │   ── cache      : ChatTranscriptCache (committed rows)        │ │
│   │   ── BuildWidgets(height) → Dictionary<string, IWidget>       │ │
│   └──────────────────────────────────────────────────────────────┘ │
│                              │                                     │
│                              ▼                                     │
│   Static helpers:                                                 │
│   ── ChatMessageFormatter  (role → TextLine rows)                  │
│   ── ChatMarkdown          (inline md → TextSpan, cached)          │
│   ── ChatTableRenderer     (GFM table → TextLine grid)             │
│   ── ChatMarkup            (escape / truncate / status pill)       │
│   ── ChatRoleColor         (enum → Color)                          │
└────────────────────────────────────────────────────────────────────┘
                              ▲
                              │ reads
                              │
┌────────────────────────────────────────────────────────────────────┐
│ UiStore + UiReducer  (from Harbor.Tui.Abstractions)               │
│   ── UiState (immutable record)                                   │
│   ── Dispatch(AgentEvent | UiMsg) → (UiState, TuiEffect)          │
│   ── TuiEffectHost.Run(effect) → IAgent.PromptAsync / Abort / Quit │
└────────────────────────────────────────────────────────────────────┘
```

**Правило:** SpectreTUI **никогда** не обращается к `Harbor.Core` напрямую. Все agent-события приходят через `IEventBus` → `UiStore.Dispatch(AgentEvent)` → `UiReducer.Reduce` → `UiState`. Все side-effects идут через `TuiEffect`.

---

## 2. Поток данных на один frame

Рассмотрим, что происходит, когда LLM прислал текстовый delta:

```
LLM provider
  └─ OpenAiCompatibleLlmClient.StreamAsync
       └─ TextDeltaEvent { Delta = "Hello" }
            └─ AgentLoop.RunAsync → _eventBus.PublishAsync(MessageUpdateEvent)
                 └─ InMemoryEventBus.PublishAsync → fan-out to subscribers
                      └─ SpectreTuiRenderer.RenderAsync(event)
                           └─ _store.Dispatch(new UiMsg.Agent(event))
                                └─ UiReducer.Reduce(state, event)
                                     └─ state with { Active.TextBuffer += "Hello" }
                                          └─ UiStore.Changed event (но Spectre.TUI сам redraw'ит)
                                               └─ (next Spectre.Tui frame)
                                                    └─ ChatScreen.Render(ctx)
                                                         └─ SyncLayout(state, width)
                                                              └─ _layout.StreamBuffer = state.Active.TextBuffer
                                                                   └─ ChatViewProjector.BuildWidgets(viewport)
                                                                        └─ ChatHistoryView.Build()
                                                                             └─ if (isStreaming && pinned):
                                                                                    ChatMessageFormatter.AppendRole(streamRows, Assistant, StreamBuffer)
                                                                                         └─ TextLine { "  Hello" }
                                                                                              └─ Paragraph widget
                                                                                                   └─ context.Render(widget, area)
```

**Важно:** Spectre.TUI сам дёргает `Render` по своему таймеру (60 FPS по умолчанию). `SpectreTuiRenderer.RenderAsync` **не вызывает** Invalidate/Redraw напрямую — он только обновляет `UiStore`, а `ChatScreen.Render` читает state на следующем frame'е.

---

## 3. Layout tree (Spectre.Tui.Layout)

`ChatLayoutShell` строит Spectre `Layout` дерево:

### Idle (не стримим):
```
Root (rows)
├── Header    (1 row)
├── History   (flex)
├── Input     (3 rows)
└── Footer    (1 row)
```

### Streaming:
```
Root (rows)
├── Header    (1 row)
├── History   (flex)
├── StreamBar (1 row)   ← extra row, toggled by _shell.Ensure(streaming)
├── Input     (3 rows)
└── Footer    (1 row)
```

`Ensure(bool streaming)` пересоздаёт Layout только при смене состояния — иначе no-op.

**Где расширять:** если хотите добавить (например) `DiffPanel` внизу — добавить `new Layout("Diff").Size(10)` в `Create()`, и в `BuildWidgets()` возвращать виджет по ключу `"Diff"`.

---

## 4. ChatViewProjector — facade

```csharp
internal class ChatViewProjector
{
    private readonly ChatTranscriptCache _cache;
    private readonly ChatChromeView _chrome;
    private readonly ChatHistoryView _history;
    private readonly ChatLayoutShell _shell;

    // Pass-through properties — копирует значения из ChatScreen.SyncLayout в подобъекты
    public string Status { get => _chrome.Status; set => _chrome.Status = value; }
    public string Model { get => _chrome.Model; set => _chrome.Model = value; }
    // ... ещё ~10 таких

    public int ScrollOffset { get => _history.ScrollOffset; set => _history.ScrollOffset = value; }
    public string StreamBuffer { get => _history.StreamBuffer; set => _history.StreamBuffer = value; }
    public string ThinkBuffer { get => _history.ThinkBuffer; set => _history.ThinkBuffer = value; }

    // Measured outputs (после Build)
    public int SourceCount => _history.SourceCount;
    public int TotalLines => _history.TotalLines;
    public int ViewportLines => _history.ViewportLines;
    public int EffectiveScroll => _history.EffectiveScroll;
    public int MaxScroll => _history.MaxScroll;
    public int HistoryTopRow => _history.HistoryTopRow;

    public Layout Layout => _shell.Layout;

    public void SetLines(ImmutableArray<ChatLine> lines, bool isStreaming, ActiveMessage active, int historyWidth = 0) { ... }
    public void InvalidateHistoryCache() => _cache.Clear();

    public IReadOnlyDictionary<string, IWidget> BuildWidgets(int historyHeight) { ... }
}
```

**Что делает `BuildWidgets(int historyHeight)`:**

```csharp
_shell.Ensure(IsStreaming);                                    // toggle StreamBar row
var historyWidget = _history.Build(historyHeight, IsStreaming); // build Paragraph
var map = new Dictionary<string, IWidget>(8) {
    ["Header"]  = _chrome.BuildHeader(),
    ["History"] = historyWidget,
    ["Input"]   = _chrome.BuildInput(),
    ["Footer"]  = _chrome.BuildFooter(MaxScroll, EffectiveScroll),
};
if (IsStreaming)
    map["StreamBar"] = _chrome.BuildStreamBar(StreamBuffer, ThinkBuffer);
return map;
```

**Важно:** `BuildWidgets` не делает layout — он только строит widget per region. Layout (позиции регионов) управляется Spectre.TUI через `_shell.Layout`.

---

## 5. ChatHistoryView — scroll window

```csharp
public IWidget Build(int viewportRows, bool isStreaming) {
    // 1. Если стримим и pinned на tail — добавить streamRows в конец
    bool pinned = ScrollOffset <= 0;
    _streamRows.Clear();
    if (isStreaming && pinned) {
        if (!string.IsNullOrWhiteSpace(ThinkBuffer))
            ChatMessageFormatter.AppendRole(_streamRows, Thinking, ThinkBuffer.Trim(), false);
        if (!string.IsNullOrWhiteSpace(StreamBuffer))
            ChatMessageFormatter.AppendRole(_streamRows, Assistant, StreamBuffer.Trim(), false);
    }

    // 2. Total = committed + stream
    int committed = _cache.Rows.Count;
    int stream = _streamRows.Count;
    int total = committed + stream;

    // 3. Clamp scroll
    MaxScroll = Math.Max(0, total - viewportRows);
    EffectiveScroll = Math.Clamp(ScrollOffset, 0, MaxScroll);

    // 4. Window [top, end)
    int top = Math.Max(0, total - viewportRows - EffectiveScroll);
    int end = Math.Min(total, top + viewportRows);

    // 5. Build Paragraph from rows in [top, end)
    var paragraph = new Paragraph().Alignment(Justify.Left);
    int cEnd = Math.Min(end, committed);
    for (int i = Math.Min(top, committed); i < cEnd; i++)
        paragraph.Lines.Add(_cache.Rows[i]);
    if (stream > 0 && end > committed) {
        int s0 = Math.Max(0, top - committed);
        int s1 = Math.Min(stream, end - committed);
        for (int i = s0; i < s1; i++)
            paragraph.Lines.Add(_streamRows[i]);
    }
    return paragraph;
}
```

**Scroll convention:** `ScrollOffset = 0` = pinned to tail (live). `ScrollOffset = MaxScroll` = scrolled to top. Это противоположно привычному "scrollTop" — лучше держать в голове.

---

## 6. ChatTranscriptCache — append-only кеш

```csharp
internal sealed class ChatTranscriptCache {
    private readonly List<TextLine> _rows = new(256);
    private ImmutableArray<ChatLine> _source = ImmutableArray<ChatLine>.Empty;
    private int _width = -1;

    public void Sync(ImmutableArray<ChatLine> lines, int width) {
        if (lines == _source && width == _width) return;  // no-op

        // Width change → full rebuild (tables depend on width)
        if (width != _width) {
            _rows.Clear();
            _source = ImmutableArray<ChatLine>.Empty;
            _width = width;
        }

        // Prefix-check: если первые N элементов совпадают — append-only
        if (_source.Length > 0 && lines.Length >= _source.Length) {
            bool prefixOk = true;
            for (int i = 0; i < _source.Length; i++) {
                if (!lines[i].Equals(_source[i])) { prefixOk = false; break; }
            }
            if (prefixOk) {
                for (int i = _source.Length; i < lines.Length; i++) {
                    ChatMessageFormatter.AppendRole(_rows, lines[i].Role, lines[i].Text, ChatMarkdown.Enabled, _width);
                }
                _source = lines;
                return;
            }
        }

        // Full rebuild
        _rows.Clear();
        for (int i = 0; i < lines.Length; i++) {
            ChatMessageFormatter.AppendRole(_rows, lines[i].Role, lines[i].Text, ChatMarkdown.Enabled, _width);
        }
        _source = lines;
        _width = width;
    }
}
```

**Ключевая оптимизация:** prefix-check O(n). Если в transcript добавили 1 строку — render только 1 строку, не 1000. Ширина терминала изменилась — full rebuild (так как таблицы GFM width-dependent).

---

## 7. ChatMessageFormatter — role → TextLine

```csharp
public static void AppendRole(List<TextLine> target, ChatRole role, string content, bool markdown, int maxWidth = 0) {
    if (role is not ChatRole.ToolResult)
        target.Add(RoleHeader(role));  // "─ user ─" / "─ assistant ─" / etc
    foreach (var line in BodyLines(role, content, markdown, maxWidth))
        target.Add(line);
    target.Add(Gap());  // пустая строка
}

public static TextLine RoleHeader(ChatRole role) {
    (string label, var style) = role switch {
        ChatRole.User       => ("you",       bold green),
        ChatRole.Assistant  => ("assistant", bold aqua),
        ChatRole.Thinking   => ("thinking",  italic grey),
        ChatRole.Tool       => ("tool",      bold blue),
        ChatRole.ToolResult => ("result",    grey),
        ChatRole.System     => ("system",    grey),
        ChatRole.Error      => ("error",     bold red),
        _                   => ("msg",       white),
    };
    return new TextLine()
        + "─ " (grey) + label (style) + " ─" (grey);
}
```

**Цветовая палитра:**

| Role | Color | Style |
|---|---|---|
| User | Green | Bold |
| Assistant | White | (default) |
| Thinking | Grey | Italic |
| Tool | Blue | Bold |
| ToolResult | Grey | (default) |
| System | Grey | (default) |
| Error | Red | Bold |

---

## 8. ChatMarkdown — inline markdown

Поддерживает:
- `# Heading 1`, `## Heading 2`, `### Heading 3`
- `**bold**`, `__bold__`
- `*italic*`, `_italic_`
- `` `code` ``
- `- bullet`, `* bullet`
- `1. numbered`
- `---` horizontal rule (→ `────────────`)

**Cache:** `Dictionary<string, List<TextSpan>>` на 512 entries, при переполнении 2048 — `Cache.Clear()`. Это problematic (см. аудит §FP-004 / §PERF-009).

**Disabled для streaming path** — markdown применяется только к committed transcript. Во время стрима — plain text.

**Политика:** `ChatMarkdown.Enabled` — global toggle. Включён по умолчанию.

---

## 9. ChatChromeView — header / stream / input / footer

### Header (1 row):
```
⚓ provider/model · agent    142↑ 87↓ · $0.0003    [RUN]
```

### StreamBar (1 row, только во время стрима):
```
▌ thinking…  1234 chars
```
или
```
▌ generating…  567 chars
```

### Input (3 rows, boxed):
```
╭───────────────────────────────────────────╮
│ › [invert] [/invert]                       │
╰───────────────────────────────────────────╯
```
- Зелёная рамка когда focused, серая когда нет
- `› ` — prompt
- invert-block — caret когда focused + reading input
- placeholder text когда пусто

### Footer (1 row):
```
esc quit  F2 IN  ↑↓ scroll  PgUp/Dn page  [/]42%
```

`[/]42%` — scroll percent, динамический.

---

## 10. ChatScreen — key dispatch

```csharp
private void OnKeyMessage(KeyMessage key) {
    // Multi-line paste guard: '\n' as character must not submit
    if (key.Key == Key.Enter && key.Character is '\n') return;

    var uiKey = ToUiKey(key);
    var action = _keyMap.Resolve(uiKey);

    // Framework reports these as characters, not key codes
    if (key.Character == 'l' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
        action = ChatAction.Clear;
    else if (key.Character == 'c' && key.Modifiers.HasFlag(KeyModifier.Ctrl))
        action = ChatAction.Abort;

    if (action == ChatAction.None) return;

    // Local scroll (не проходит через reducer — см. аудит §FP-005)
    if (HandleLocalScroll(action)) return;

    // Dispatcher через reducer
    var effect = _store.Dispatch(new UiMsg.KeyInput(action, uiKey));
    if (effect is not TuiEffect.None) _effects.Run(effect);
    if (effect is TuiEffect.QuitApp) _app?.Quit();
}
```

### Default keymap (ChatKeyMap):

| Key | Action |
|---|---|
| Enter | Submit |
| Esc | Quit (idle) / Abort (running) |
| F2 | ToggleFocus (Input ↔ Chat) |
| ↑ / ↓ | ScrollUpLine / ScrollDownLine (chat focus) или InputHistoryPrev/Next (input focus) |
| PgUp / PgDn | ScrollUpPage / ScrollDownPage |
| Home / End | ScrollTop / ScrollBottom |
| Alt+↑ / Alt+↓ | InputHistoryPrev / InputHistoryNext |
| Tab | Autocomplete (slash commands) |
| Backspace | Backspace (input focus) |
| Ctrl+L | Clear |
| Ctrl+C | Abort |
| Any char | Char input (input focus) |

---

## 11. Где и как добавлять фичи из opencode / kilocode / pi-agent

### Фича: diff-view при Edit tool (как в kilocode)

**Где:** `contrib/tui/Harbor.Tui.SpectreTui/View/DiffPreviewView.cs` (новый) + регистрация в `BuildWidgets`.

1. Создать `DiffPreviewView`:
   ```csharp
   internal sealed class DiffPreviewView {
       public DiffPreview? Current { get; set; }
       public IWidget Build(int height) {
           if (Current is null) return new Paragraph(TextLine.FromMarkup("[dim]no diff[/]"));
           // ... build diff widget
       }
   }
   ```

2. Добавить в `ChatViewProjector`:
   ```csharp
   private readonly DiffPreviewView _diff = new();
   public DiffPreview? Diff { get => _diff.Current; set => _diff.Current = value; }
   ```

3. В `ChatLayoutShell.Create()` добавить:
   ```csharp
   new Layout("Diff").Size(10)
   ```

4. В `BuildWidgets`:
   ```csharp
   map["Diff"] = _diff.Build(10);
   ```

5. В `ChatScreen.SyncLayout` — копировать из `UiState`:
   ```csharp
   _layout.Diff = s.PendingDiff;  // добавить в UiState новое поле
   ```

6. В `UiReducer.OnMessageUpdate` — ловить `ToolExecutionStartEvent` для `edit` tool:
   ```csharp
   case ToolExecutionStartEvent tes when tes.ToolName == "edit":
       return state with { PendingDiff = ExtractDiff(tes.Args) };
   ```

### Фича: slash-completion popup (как в opencode)

**Где:** новый `CompletionPopupView` + overlay в `Render`.

1. Создать виджет popup:
   ```csharp
   internal sealed class CompletionPopupView {
       public IReadOnlyList<string>? Items { get; set; }
       public int SelectedIndex { get; set; }
       public IWidget Build() { ... }
   }
   ```

2. Overlay рисуется поверх Input area. В `ChatScreen.Render`:
   ```csharp
   if (state.Completion is { } completion) {
       var popupArea = new LayoutArea(inputArea.X, inputArea.Y - completion.Items.Count, 30, completion.Items.Count);
       context.Render(_popup.Build(), popupArea);
   }
   ```

3. В `UiReducer.UpdateKey` — обрабатывать Tab/Arrow/Enter когда `state.Completion != null`.

### Фича: token-usage breakdown (как в pi-agent)

**Где:** расширить `ChatChromeView.BuildHeader`.

В `UiState` добавить:
```csharp
public TokenBreakdown Breakdown { get; init; }
```

В `UiReducer.OnStepFinish`:
```csharp
return state with {
    Breakdown = state.Breakdown.Add(usage),
    Cost = state.Cost with { ... }
};
```

В `BuildHeader`:
```
⚓ provider/model · agent    142↑ 87↓ (cache: 23↑) · $0.0003    [RUN]
```

### Фича: file-tree sidebar (как в crush)

**Где:** новый `FileTreeView` + Layout column.

В `ChatLayoutShell.Create()`:
```csharp
return new Layout("Root").SplitColumns(
    new Layout("Tree").Size(30),
    new Layout("Main").SplitRows(
        new Layout("Header").Size(1),
        new Layout("History"),
        // ...
    )
);
```

### Фича: LSP diagnostics inline (как в kilocode)

**Где:** подписка на `ToolExecutionEndEvent` от `bash` tool с pattern'ом `error CS####`.

В `UiReducer.OnMessageUpdate`:
```csharp
case ToolExecutionEndEvent tee when tee.Result.Output.Contains("error CS"):
    return state.AddLine(ChatRole.Error, ParseCompilerErrors(tee.Result.Output));
```

---

## 12. Грабли, на которые наступили

### Грабли #1: Spectre Layout rebuild per frame

Если в `Render` вызывать `_shell.Ensure(streaming)` каждый раз — это OK (no-op при том же состоянии). Но если вы создаёте **новый** `Layout` каждый frame — Spectre.TUI будет делать full redraw, мерцание.

**Fix:** всегда хранить Layout как поле, мутировать через `Ensure`.

### Грабли #2: Footer text update после BuildWidgets

В `ChatScreen.RenderCore` есть:
```csharp
var widgets = _layout.BuildWidgets(_viewport);
// ... footer text обновляется после Build
_layout.FooterText = BuildFooter();
// ... приходится пересобирать footer widget отдельно:
var footerWidget = ParagraphFromFooter(_layout.FooterText);
```

Это потому что `BuildFooter()` зависит от `MaxScroll`/`EffectiveScroll`, которые **вычисляются внутри** `BuildWidgets`. Чтобы не пересобирать всё дерево — пересобирается только footer.

Если будете добавлять widget с такой же зависимостью — делайте так же.

### Грабли #3: `\n` как Character vs Key.Enter

При paste многострочного текста Spectre.TUI присылает `Key.Enter` с `Character = '\n'`. Это надо фильтровать:
```csharp
if (key.Key == Key.Enter && key.Character is '\n') return;
```
Иначе каждая вставленная строка будет сабмититься.

### Грабли #4: Width change → cache invalidation

`ChatTranscriptCache.Sync` проверяет `_width == width`. Если width изменился (resize терминала) — **полный rebuild** всех TextLine. Это правильно, потому что GFM-таблицы width-dependent.

Если добавляете свой cache — тоже проверяйте width.

### Грабли #5: Streaming path — без markdown

`ChatMessageFormatter.AppendRole(streamRows, role, text, markdown: false)` — намеренно `false` для streaming. Markdown-парсинг на partial text глючит (незакрытый `**` сделает половину текста bold). Только после `MessageEndEvent` текст попадает в committed transcript, где markdown включается.

### Грабли #6: Local scroll vs reducer scroll — RESOLVED (TEA restoration)

> **Status:** ✅ RESOLVED — subagent T (tea-restorer) убрал `_scroll` / `_viewport` /
> `_wasRunning` из `ChatScreen` и удалил `HandleLocalScroll`. Теперь **единственный**
> scroll-mechanism — `UiState.ScrollOffset`, обновляемый через `UiReducer.Update`
> по `UiMsg.KeyInput(ScrollUpLine/...)`. Render только читает состояние и диспатчит
> measurement-сообщения (`UiMsg.Viewport`, `UiMsg.HistoryMeasured`,
> `UiMsg.ScrollClamp`, `UiMsg.ScrollResetToTail`). См. §14 "TEA compliance" ниже.

Раньше в `ChatScreen` было **два** scroll-mechanism:
1. `_scroll` — локальное поле, обновлялось через `HandleLocalScroll` (мимо reducer'а)
2. `UiState.ScrollOffset` — через reducer (но фактически не использовалось в SpectreTUI)

Это было нарушение TEA: state жил в двух местах. См. аудит §FP-005.

### Грабли #7: Cancellation между Spectre.TUI и AgentLoop

`Application.RunAsync(_screen)` блокирует UI thread. `_effects.Run(TuiEffect.PromptAgent)` запускает `Task.Run(() => agent.PromptAsync(...))` — agent работает в thread pool, UI не блокируется.

Но! `_appCt` (CancellationToken) передаётся в `TuiEffectHost` — когда application quit, токен отменяется, agent aborts. **Не** используйте `CancellationToken.None` в `RunInteractiveAsync`.

---

## 13. Чек-лист для PR с новой фичей в SpectreTUI

- [ ] Фича — это **view** или **effect**? Если view — не трогает `Harbor.Core`. Если effect — добавляй в `TuiEffect` discriminated union.
- [ ] State для фичи — в `UiState` (immutable record, `with` expressions). Не в renderer.
- [ ] Transitions — в `UiReducer.Update` (pattern match на `UiMsg`). Не в renderer.
- [ ] Виджет — в `View/` папке, `internal sealed class`, не более 100 строк.
- [ ] Если виджет нужно инжектить в layout — в `ChatLayoutShell.Create()` + `BuildWidgets()`.
- [ ] Если виджет зависит от measured outputs (scroll, viewport) — обновлять **после** `BuildWidgets`, как `FooterText`.
- [ ] Markdown для виджета — через `ChatMarkdown.ToSpans(text, baseColor)` (с кешем).
- [ ] Hot path — `for (int i = ...)` вместо `foreach` + `LINQ`, `StringBuilderPool.Rent()` вместо `new StringBuilder()`.
- [ ] Тесты в `tests/Harbor.Tui.Tests/` — добавить `*Tests.cs`, проверить что widget строится без исключений.
- [ ] E2E в `tests/Harbor.Tui.E2E.Tests/` — если меняется output в `Render`, обновить capture-diff.

---

## 13.5. Panel System — динамические панели (dockable, toggleable, tabbed)

> Subagent #2 (panel-system) добавил полноценную систему панелей — как в VSCode / opencode.
> Панели можно скрывать, показывать, переключать между ними фокус, менять размер, добавлять через плагины.
> Этот раздел описывает архитектуру, hotkeys, layout-алгоритм и процесс написания новых панелей.

### 13.5.1. Обзор

Раньше SpectreTUI имел hardcoded 5-region layout: Header / History / StreamBar / Input / Footer. Теперь центральная область (chat chrome) окружена опциональными панелями, которые пользователь может включать/выключать:

```
┌──────────────────────────────────────────────────────────────────────┐
│ Header (1 row)                                                       │
├──────────────┬───────────────────────────────────────┬───────────────┤
│ Left panels  │  Top panels (optional)                │ Right panels  │
│ (tabs +      │  Header                               │ (tabs +       │
│  content)    │  History (flex)                       │  content)     │
│              │  StreamBar (only when streaming)      │               │
│              │  Bottom panels (optional)             │               │
│              │  Input (3 rows)                       │               │
├──────────────┴───────────────────────────────────────┴───────────────┤
│ Footer (1 row)                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

Когда ни одна панель не зарегистрирована, layout **идентичен** старому `ChatLayoutShell` — пользователь не видит разницы.

### 13.5.2. Три способа зарегистрировать панель

#### (a) Builtin — пишете в `contrib/tui/Harbor.Tui.SpectreTui/Panels/Builtin/`

```csharp
// contrib/tui/Harbor.Tui.SpectreTui/Panels/Builtin/MyPanel.cs
public sealed class MyPanel : IPanelProvider
{
    public string Id => "my-panel";
    public string Title => "My Panel";
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;
    public int DefaultSize => 40;

    public object? Build(PanelContext ctx)
    {
        var p = new Paragraph().Alignment(Justify.Left);
        p.Lines.Add(TextLine.FromMarkup("[bold]Hello from MyPanel[/]"));
        return p;
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
```

Регистрируется в `SpectreTuiRenderer.RunInteractiveAsync`:
```csharp
Panels.Register(new Builtin.MyPanel());
```

#### (b) Plugin (CS-source) — через `ITuiPanelPlugin`

Плагин лежит в `~/.harbor/plugins/my-plugin.cs`:

```csharp
using Harbor.Abstractions.Plugins;
using Harbor.Tui.Abstractions.Panels;
using Harbor.Tui.Abstractions.State;

public sealed class ClockPanelPlugin : ITuiPanelPlugin
{
    public string Name => "clock";
    public Version Version => new(1, 0, 0);
    public string Description => "Adds a live clock panel";

    public void Initialize(PluginContext context) { }

    public void RegisterPanels(IPanelRegistry registry)
    {
        registry.Register(new ClockPanel());
    }
}

public sealed class ClockPanel : IPanelProvider
{
    public string Id => "clock";
    public string Title => "Clock";
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Top;
    public int DefaultSize => 1;

    public object? Build(PanelContext ctx)
    {
        var p = new Spectre.Tui.Paragraph().Alignment(Spectre.Console.Justify.Left);
        p.Lines.Add(Spectre.Tui.TextLine.FromMarkup(
            $"[cyan]{DateTime.Now:HH:mm:ss}[/]"));
        return p;
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;
}
```

`CsPluginLoader` автоматически обнаруживает `ITuiPanelPlugin` и вызывает `RegisterPanels(adapter)`. Адаптер (`PanelRegistryPluginAdapter`) маршрутизирует вызовы `Register` в `IPluginLoadHost.RegisterPanelProvider`, который складывает их в host-owned `PanelRegistry`. SpectreTuiRenderer при старте читает тот же `PanelRegistry` через DI.

#### (c) Runtime — из другого кода host'а

```csharp
var registry = serviceProvider.GetRequiredService<PanelRegistry>();
registry.Register(new MyCustomPanel());
```

`PanelRegistry` зарегистрирован как singleton в DI (`HostBuilder.RegisterRegistries`). Любой компонент может разрешить его и добавить панель в рантайме — она появится на следующем frame'е.

### 13.5.3. Hotkeys

| Key | Action | Описание |
|---|---|---|
| `Alt+1`..`Alt+9` | `TogglePanelSlot` | Включить/выключить N-ю зарегистрированную панель |
| `Ctrl+Tab` | `CyclePanelFocus` | Переключить фокус между панелями и чатом |
| `Ctrl+↑` / `Ctrl+→` | `ResizePanelGrow` | Увеличить сфокусированную панель |
| `Ctrl+↓` / `Ctrl+←` | `ResizePanelShrink` | Уменьшить сфокусированную панель |
| `q` (when panel focused) | `ClosePanel` | Вернуть фокус чату (панель остаётся видимой) |
| `Esc` (when panel focused) | `ClosePanel` | То же, что и `q` |
| `?` | `HelpPanel` | Открыть/закрыть help-панель (Alt+1 если help — первая) |

Когда панель владеет фокусом, остальные keys идут в `IPanelProvider.OnKey(UiKey, PanelContext)`. Панель возвращает `true` → key consumed, `false` → fall-through к default keymap.

### 13.5.4. Layout algorithm

`PanelLayoutShell.Ensure(streaming)` перестраивает Spectre `Layout` только если изменилась сигнатура (видимый набор панелей + их sizes + streaming flag). Сигнатура — это строка вида `"S1|help:Right:Visible:48|todo-list:Right:Hidden:40|"`.

**Алгоритм:**

1. Query `registry.GetVisibleByPlacement(Left/Right/Top/Bottom)`.
2. Если все 4 коллекции пустые → вернуть legacy layout (Header/History/StreamBar/Input/Footer).
3. Иначе:
   - Build center column rows: optional `TopPanels` stack → Header → History → optional StreamBar → optional `BottomPanels` stack → Input → Footer.
   - Если нет Left/Right panels → вернуть rows-only layout.
   - Иначе обернуть center в `Body.SplitColumns(Left?, Centre, Right?)` и обернуть Body в `Root.SplitRows(Header, Body, Input, Footer)`.
4. Stack для каждого placement: 1-row tabs region + N content regions (по одному на видимую панель этого placement). Left/Right — горизонтальные stacks; Top/Bottom — вертикальные.

**ASCII для каждой комбинации:**

Только Left panel visible:
```
Root (rows)
├── Header (1)
├── Body (cols)
│   ├── LeftPanels
│   │   ├── LeftPanels.Tabs (1 col)
│   │   └── LeftPanels.<id> (size cols)
│   └── Centre (rows: Header/History/StreamBar/Input/Footer)
├── Input (3)
└── Footer (1)
```

Left + Bottom panels visible:
```
Root (rows)
├── Header (1)
├── Body (cols)
│   ├── LeftPanels (cols)
│   │   ├── Tabs (1)
│   │   └── <id> (size)
│   └── Centre (rows)
│       ├── History (flex)
│       ├── StreamBar (1, only when streaming)
│       └── BottomPanels (rows)
│           ├── Tabs (1)
│           └── <id> (size)
├── Input (3)
└── Footer (1)
```

Left + Right panels, no Bottom:
```
Root (rows)
├── Header (1)
├── Body (cols)
│   ├── LeftPanels (cols)
│   ├── Centre (rows: History/StreamBar/Input/Footer)
│   └── RightPanels (cols)
├── Input (3)
└── Footer (1)
```

### 13.5.5. State model (immutable, single source of truth — TEA-compliant)

> **Status:** ✅ TEA-restored (subagent T) — `PanelRegistry` теперь
> **registration-only**: `Register` / `Unregister` / `All` / `Get`. Никаких
> `SetState` / `SetSize` / `FocusedPanelId` setter / `ApplySnapshot` /
> `SnapshotStates` / `SnapshotSizes` / `CycleFocus`. Весь state (visibility, focus,
> size) живёт в `UiState` и мутируется **только** через `UiReducer.Update` по
> `UiMsg.TogglePanel` / `FocusPanel` / `CyclePanelsFocus` / `ResizePanel`. Renderer
> читает state через read-only `PanelRegistryView` snapshot.

`UiState` хранит panel state:

```csharp
public ImmutableDictionary<string, TuiPanelState> PanelStates { get; init; }
    = ImmutableDictionary<string, TuiPanelState>.Empty;
public ImmutableDictionary<string, int> PanelSizes { get; init; }
    = ImmutableDictionary<string, int>.Empty;
public string? FocusedPanelId { get; init; }
public ImmutableArray<string> RegisteredPanelIds { get; init; }
    = ImmutableArray<string>.Empty;
```

`SpectreTuiRenderer.SeedPanelRegistryIntoState()` вызывается один раз на старте
host'а (и при reload плагинов) — сеет registered ids + default Hidden states +
default sizes в `UiState`. После этого reducer — единственный source of truth.
`PanelRegistry` хранит только `IPanelProvider` instances.

Reducer читает `PanelStates` / `RegisteredPanelIds` и **никогда** не зависит от
`IPanelRegistry` (pure function). Renderer читает state через read-only snapshot:

```csharp
// В PanelLayoutShell / PanelViewProjector (read-only):
PanelRegistryView view = _registry.View(state);
IReadOnlyList<IPanelProvider> leftVisible = view.GetVisibleByPlacement(TuiPanelPlacement.Left);
TuiPanelState s = view.GetState(panel.Id);
int size = view.GetSize(panel.Id);
```

```csharp
// Pure reducer (Harbor.Tui.Abstractions):
public static UiState TogglePanel(UiState state, string id) => ...
public static UiState FocusPanel(UiState state, string? id) => ...
public static UiState CycleFocus(UiState state) => ...
public static UiState ResizePanel(UiState state, string id, int delta) => ...
```

### 13.5.6. Performance

- **Lazy build:** `IPanelProvider.Build` вызывается только если панель в состоянии Visible / Focused / Pinned. Hidden панели — нулевая работа.
- **Layout signature caching:** `PanelLayoutShell.Ensure` перестраивает Spectre Layout только при изменении signature; в steady state это O(visible-panels) string compare.
- **Tab strip rendering:** один `Paragraph` widget на stack; не перестраивается, если не изменился набор.
- **No event subscriptions in panels:** панели читают `ctx.State` каждый frame. Reducer уже добавил все `ToolExecutionEndEvent` в `UiState.Lines`, поэтому `TodoListPanel` / `DiagnosticsPanel` / `DiffPreviewPanel` автоматически видят обновления без своих подписок на `IEventBus`.

### 13.5.7. Writing a panel plugin — full walkthrough (`TodoListPanel`)

`TodoListPanel` показывает todo list, который опциональный `TodoWritePlugin` поддерживает через свой `todo` tool.

**Шаг 1: Дизайн контракта**

- Id: `"todo-list"`
- Placement: `Right` (компактная колонка справа от чата)
- Size: `40` cols
- Refresh trigger: `ToolExecutionEndEvent` для tool `todo`. Reducer уже appendит результат в `UiState.Lines` с role `ToolResult` — панели достаточно отсканировать transcript.

**Шаг 2: Реализация**

```csharp
public sealed class TodoListPanel : IPanelProvider
{
    public string Id => "todo-list";
    public string Title => "Todo List";
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;
    public int DefaultSize => 40;

    public object? Build(PanelContext ctx)
    {
        var todos = ExtractTodos(ctx.State);
        var p = new Paragraph().Alignment(Justify.Left);
        // ... render header + todos + footer with counts
        return p;
    }

    public bool OnKey(UiKey key, PanelContext ctx) => false;

    private static List<(string Marker, string Content)> ExtractTodos(UiState state)
    {
        // Walk state.Lines backward; find the most recent ToolResult line
        // that looks like todo output (matches \[ xX~?\]); collect consecutive
        // lines until a non-todo-looking line appears.
    }
}
```

**Шаг 3: Декаплинг**

`TodoListPanel` **не ссылается** на `Harbor.Plugin.TodoWrite` (которая может быть не загружена). Вместо этого парсит текст вывода `todo` tool, который всегда в формате `[ ]/[~]/[x] <id> — <content>`. Это работает для любой реализации `todo` tool, не только конкретного плагина.

**Шаг 4: Тестирование**

Юнит-тесты для парсера:
```csharp
var state = new UiState { Lines = ImmutableArray.Create(
    new ChatLine(ChatRole.Tool, "→ todo  {\"action\":\"add\",\"content\":\"write docs\"}"),
    new ChatLine(ChatRole.ToolResult, "✓ Added todo: abc123 — write docs")
) };
var todos = ExtractTodos(state);
Assert.That(todos.Count).IsEqualTo(0); // no [ ]/[x] line yet

state = state.AddLine(ChatRole.ToolResult,
    "✓ Todos (1):\n  [~] abc123 — write docs");
todos = ExtractTodos(state);
Assert.That(todos.Count).IsEqualTo(1);
```

См. `tests/Harbor.Tui.Tests/PanelRegistryTests.cs` — 9 тестов покрывают регистрацию, toggle, cycle focus, resize clamping, read-only view snapshot.

**Шаг 5: Hotkey mapping**

`TodoListPanel` занимает slot 2 (Alt+2) в стандартном builtin ordering (`HelpPanel` → slot 1). Пользователь жмёт Alt+2 → reducer делает `TogglePanel("todo-list")` → `PanelStates["todo-list"]: Hidden → Visible` → layout signature меняется → следующий frame перестраивает Layout и панель видна.

---

## 14. TEA compliance — The Elm Architecture в SpectreTUI

> Subagent T (tea-restorer) восстановил TEA-инварианты, нарушённые `ChatScreen`'овскими
> mutable fields (`_scroll`, `_viewport`, `_wasRunning`) и dual-source-of-truth
> scroll (`HandleLocalScroll` ↔ `UiReducer`). Этот раздел фиксирует, как SpectreTUI
> устроен теперь.

### 14.1. State diagram — UiState как single source of truth

```
                       ┌──────────────────────────┐
                       │         UiState          │
                       │  (immutable record)      │
                       │                          │
                       │  • Lines / Active        │
                       │  • IsAgentRunning        │
                       │  • WasRunning  ◄── NEW   │
                       │  • ScrollOffset          │
                       │  • ViewportLines         │
                       │  • TotalLines            │
                       │  • Input / Focus         │
                       │  • PanelStates / Sizes   │
                       │  • FocusedPanelId        │
                       │  • RegisteredPanelIds    │
                       └────────────┬─────────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              │ read-only           │ read-only           │ read-only
              ▼                     ▼                     ▼
     ┌────────────────┐   ┌──────────────────┐   ┌──────────────────┐
     │  ChatScreen    │   │ PanelLayoutShell │   │ PanelViewProjector│
     │  (Render loop) │   │ (Layout tree)    │   │ (Widgets)        │
     └────────┬───────┘   └──────────────────┘   └──────────────────┘
              │
              │ dispatch UiMsg (Viewport / HistoryMeasured /
              │ ScrollClamp / ScrollResetToTail / KeyInput)
              ▼
     ┌──────────────────────────────────────────────┐
     │           UiReducer.Update (PURE)            │
     │   (UiState, UiMsg) → (UiState, TuiEffect)    │
     └────────────────────┬─────────────────────────┘
                          │ new UiState
                          ▼
                    (back to UiState)
```

**Single source of truth:** `UiState` — единственное место, где живёт UI state.
Мутируется **только** через `UiReducer.Update` по `UiMsg`. Никакой renderer не
мутирует state напрямую.

### 14.2. Flow diagram — scroll action

```
User presses ↓  →  KeyMessage(Down)
       │
       ▼
ChatScreen.OnKeyMessage
       │
       │ var effect = _store.Dispatch(new UiMsg.KeyInput(
       │                                    ChatAction.ScrollDownLine, uiKey));
       ▼
UiReducer.Update(state, UiMsg.KeyInput(ScrollDownLine, _))
       │
       │ return (state.SetScroll(state.ScrollOffset - 1), TuiEffect.None);
       │  └── SetScroll clamps to [0 .. TotalLines - ViewportLines]
       ▼
UiStore applies new state via CAS loop, raises Changed event
       │
       ▼
Next Render frame: ChatScreen reads state.ScrollOffset
                   → ChatViewProjector.ScrollOffset = state.ScrollOffset
                   → BuildWidgets(viewport, state)
                   → if scroll > MaxScroll: Dispatch(UiMsg.ScrollClamp(MaxScroll))
                   → re-read state
                   → render widgets
```

**Note:** `HandleLocalScroll` — удалён. Все 6 scroll actions (Line Up/Down,
Page Up/Down, Top, Bottom) идут через reducer.

### 14.3. Flow diagram — panel toggle (Alt+1)

```
User presses Alt+1  →  KeyMessage('1' + Alt)
       │
       ▼
ChatScreen.OnKeyMessage
       │
       │ var action = _keyMap.Resolve(uiKey);  // → ChatAction.TogglePanelSlot
       │ if (HandlePanelAction(action, uiKey)) return;
       │   └── slot = '1' - '1' = 0
       │   └── id = _registry.All[0].Id  // "help"
       │   └── _store.Dispatch(new UiMsg.TogglePanel("help"));
       ▼
UiReducer.Update(state, UiMsg.TogglePanel("help"))
       │
       │ return (TogglePanel(state, "help"), TuiEffect.None());
       │  └── PanelStates["help"]: Hidden → Visible
       ▼
Next Render frame: PanelLayoutShell.Ensure(state, streaming)
                   → signature changed ("S0|help:Right:Visible:48|")
                   → rebuilds Spectre Layout
                   → PanelViewProjector.BuildWidgets adds "RightPanels.help"
                   → context.Render(widget, area)
```

**Note:** `PanelRegistry.SetState` / `ApplySnapshot` — удалены. State живёт только
в `UiState.PanelStates`. Registry — registration-only.

### 14.4. Flow diagram — agent event (new run started)

```
AgentLoop fires AgentStartEvent
       │
       ▼
SpectreTuiRenderer.RenderAsync(@event)
       │
       │ _store.Dispatch(new UiMsg.Agent(@event));
       ▼
UiReducer.Reduce(state, AgentStartEvent)
       │
       │ return state with {
       │   Status = "running",
       │   IsAgentRunning = true,
       │   WasRunning = state.IsAgentRunning,  // prior value (false on first run)
       │   ScrollOffset = 0                     // pin to live tail (§FP-005 fix)
       │ };
       ▼
Next Render frame: ChatScreen.RenderCore
       │
       │ var state = _store.State;
       │ _panelShell.Ensure(state, state.IsStreaming);
       │ var historyArea = _panelShell.Layout.GetArea(context, "History");
       │ if (state.ViewportLines != historyArea.Height)
       │     _store.Dispatch(new UiMsg.Viewport(historyArea.Height));
       │
       │ // Rising-edge belt-and-braces (reducer already reset scroll on AgentStart).
       │ // Handles case where IsAgentRunning was flipped by TuiEffectHost.Transition
       │ // (PromptAgent effect) instead of an AgentStartEvent.
       │ if (state.IsAgentRunning && !state.WasRunning) {
       │     _store.Dispatch(new UiMsg.ScrollResetToTail());
       │     state = _store.State;  // WasRunning now true → won't fire again
       │ }
       │
       │ _layout.ScrollOffset = state.ScrollOffset;
       │ ... build + render widgets ...
```

**Note:** `_wasRunning` field — удалён. `WasRunning` живёт в `UiState`,
обновляется reducer'ом на AgentStart/AgentEnd.

### 14.5. What render is allowed to do

```
✅ DO in ChatScreen.Render:
   • Read _store.State (immutable snapshot)
   • Measure geometry (historyArea.Height, _layout.TotalLines, _layout.MaxScroll)
   • Dispatch UiMsg.Viewport / HistoryMeasured / ScrollClamp / ScrollResetToTail
     (this is the "subscription" pattern from Elm — measurement msgs flow back
     through the reducer, never mutate state directly)
   • Re-read _store.State after a dispatch (cheap — CAS loop)
   • Build widgets from state (pure projection)
   • Call context.Render(widget, area) (framework-owned side effect, allowed)

❌ DON'T in ChatScreen.Render:
   • Mutate any local field (_scroll, _viewport, _wasRunning — REMOVED)
   • Mutate _store.State directly (use _store.Dispatch(UiMsg))
   • Mutate PanelRegistry (no state methods on it anymore)
   • Call IAgent / IToolRegistry / IProviderRegistry (use TuiEffect)
   • Run async work (Render is sync — frame-budgeted)
```

### 14.6. What reducer is allowed to do

```
✅ DO in UiReducer.Update:
   • Pattern-match on UiMsg → return new immutable UiState (with expression)
   • Return a TuiEffect for the host to run (PromptAgent, RunSlash, etc.)
   • Read state.* fields (no I/O)
   • Pure math (Math.Clamp, +, -, etc.)

❌ DON'T in UiReducer.Update:
   • Call IAgent / IToolRegistry / IProviderRegistry (no I/O)
   • Call ILogger (no side effects)
   • Mutate state in place (always return new record via `with`)
   • Touch PanelRegistry (pure — no dependency on IPanelRegistry)
   • Spawn tasks / fire-and-forget
```

### 14.7. Common antipatterns (the things we just fixed)

**Antipattern 1: Local mutable scroll field.**

```csharp
// ❌ BAD — bypasses reducer, dual source of truth
private int _scroll;  // removed

private bool HandleLocalScroll(ChatAction action)  // removed
{
    int page = Math.Max(1, _viewport - 2);
    switch (action) {
        case ChatAction.ScrollUpLine: _scroll++; break;
        ...
    }
    return true;
}

// ✅ GOOD — single source of truth in UiState, mutated only by reducer
private void OnKeyMessage(KeyMessage key) {
    var action = _keyMap.Resolve(ToUiKey(key));
    if (action == ChatAction.None) return;
    var effect = _store.Dispatch(new UiMsg.KeyInput(action, ToUiKey(key)));
    if (effect is not TuiEffect.None) _effects.Run(effect);
}
```

**Antipattern 2: Registry holds panel state.**

```csharp
// ❌ BAD — double source of truth (registry ↔ UiState)
public interface IPanelRegistry {
    Result SetState(string id, TuiPanelState state);  // removed
    Result SetSize(string id, int size);              // removed
    string? FocusedPanelId { get; set; }              // removed (no setter)
}

// Sync bridge — REMOVED
private void SyncRegistryFromState() {
    var s = _store.State;
    _registry.ApplySnapshot(s.PanelStates, s.FocusedPanelId);
}

// ✅ GOOD — registry is registration-only; state lives in UiState
public interface IPanelRegistry {
    Result Register(IPanelProvider panel);
    Result Unregister(string id);
    IReadOnlyList<IPanelProvider> All { get; }
    IPanelProvider? Get(string id);
}

// Read-only view over (registry, UiState) — pure snapshot, no mutations
public readonly record struct PanelRegistryView(
    IReadOnlyList<IPanelProvider> Providers,
    UiState State)
{
    public IReadOnlyList<IPanelProvider> GetVisibleByPlacement(...) { ... }
    public TuiPanelState GetState(string id) => State.PanelStates[...];
    public int GetSize(string id) => State.PanelSizes[...];
}
```

**Antipattern 3: Render mutates state.**

```csharp
// ❌ BAD — render mutates _scroll directly
public override void Render(RenderContext context) {
    if (_layout.TotalLines > 0) {
        int maxPrev = Math.Max(0, _layout.TotalLines - _viewport);
        _scroll = Math.Clamp(_scroll, 0, maxPrev);  // direct mutation!
    }
    ...
    _scroll = _layout.EffectiveScroll;  // direct mutation!
    _viewport = _layout.ViewportLines;  // direct mutation!
    _wasRunning = state.IsAgentRunning; // direct mutation!
}

// ✅ GOOD — render reads state, dispatches measurement msgs
public override void Render(RenderContext context) {
    var state = _store.State;  // READ ONLY
    ...
    if (state.ViewportLines != viewport) {
        _store.Dispatch(new UiMsg.Viewport(viewport));  // measurement msg
        state = _store.State;  // re-read after dispatch
    }
    if (state.ScrollOffset > _layout.MaxScroll) {
        _store.Dispatch(new UiMsg.ScrollClamp(_layout.MaxScroll));
        state = _store.State;
    }
    ...
}
```

### 14.8. Tests

`tests/Harbor.Tui.Tests/PanelRegistryTests.cs` содержит `TeaComplianceTests` (13
тестов) — рефлексивные проверки того, что TEA-инварианты держатся:

- `IPanelRegistry_HasOnlyRegistrationMethods` — нет `SetState`/`SetSize`/etc.
- `PanelRegistry_HasNoStateMutationMethods` — concrete impl тоже чистый.
- `UiState_HasScrollViewportTotalLinesWasRunning` — все state fields на месте.
- `UiMsg_HasScrollResetToTailAndScrollClamp` — measurement msgs существуют.
- `Reducer_ScrollResetToTail_SetsScrollZeroAndWasRunningTrue` — reducer обрабатывает.
- `Reducer_ScrollClamp_ClampsScrollOffset` — reducer обрабатывает.
- `Reducer_HandlesAllScrollActions_ThroughUpdate` — все 6 scroll actions идут через reducer.
- `Reducer_AgentStart_SnapshotsWasRunningAndResetsScroll` — rising edge detection.
- `Reducer_AgentEnd_SnapshotsWasRunning` — falling edge snapshot.
- `ChatScreen_HasNoLocalScrollViewportWasRunningFields` — приватные поля удалены.
- `ChatScreen_HasNoHandleLocalScrollMethod` — bypass-метод удалён.
- `ChatScreen_HasNoSyncRegistryFromStateMethod` — bridge удалён.
- `SpectreTuiRenderer_HasNoSyncPanelRegistryToState` — bridge переименован в `SeedPanelRegistryIntoState`.

Запуск: `dotnet test tests/Harbor.Tui.Tests/Harbor.Tui.Tests.csproj`.

---

## 15. Ссылки

- [Spectre.TUI исходники](https://github.com/spectreconsole/spectre.console/tree/main/src/Spectre.Tui) — фреймворк, на котором построен.
- [Spectre.Console docs](https://spectreconsole.net/) — markdown, tables, borders.
- [specs/07-tui.md](../specs/07-tui.md) — изначальная спецификация TUI.
- [docs/CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — §FP-005 и §FP-007 теперь RESOLVED.
- [CLAUDE.md §Full-screen TUI development](../CLAUDE.md) — hotkey table для `FullscreenTuiRenderer` (другой renderer, но паттерны те же).
