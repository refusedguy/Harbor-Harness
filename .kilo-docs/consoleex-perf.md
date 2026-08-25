# ConsoleEx — перф-аудит рендеринга и ввода (волна спринта-7)

> READ-ONLY аудит. Донор анти-паттернов: `AnsiTuiRenderer` + `AnsiRenderContext`
> (`src/Harbor.Tui.Ansi/`, 629 строк: AnsiTuiRenderer.cs 213 + Ansi.cs 70 +
> TerminalQrRenderer.cs 334 + GlobalUsings 12). База дизайна:
> `/tmp/harbor-analysis/consoleex-design.md` §5 (raw stdin конвейер), §8.3 (риски).
> Таргеты из `specs/07-tui.md:1136-1145`: key echo < 5 ms, token-to-screen < 20 ms,
> память TUI < 20 MB, 1000 tok/sec sustained, cold start < 50 ms.
>
> Все «до» — статический анализ с file:line; все «после» — оценки из известных
> констант (syscall ≈ 1–2 µs, стоимость аллокаций, размеры таблиц) и подлежат
> подтверждению бенчами из §2.4. Ничего не запускалось и не правилось.

---

## 0. Вердикт (TL;DR)

1. **Убийца №1 — не рендерер, а ViewModel**: `StreamingText += td.Delta`
   (`src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs:153`) — O(n²)
   конкатенация строк на каждый токен. Один ответ на 10k токенов (~40 KB текста)
   ≈ **~200 МБ мусора** суммарно. Это мгновенно хоронит таргет 20 MB и даёт
   gen0-шторм. ConsoleEx обязан НЕ копить стриминг-текст строкой вообще.
2. **Убийца №2 — реестры аллоцируют на каждый токен**: `Views.GetAll()` /
   `GetByPlacement()` делают `.ToList()` под lock'ом на КАЖДОЕ событие
   (`ViewRegistry.cs:77-93`) — при 1000 tok/sec это ~2000 лишних List-аллокаций/сек.
3. **Стратегия вывода**: per-token `Console.Write` (сегодня) проигрывает
   буферизованному кадру по syscall'ам (AutoFlush у Console.Out = write(2) на каждый
   Write) и атомарности кадра. Для inline-MVP правильная стратегия —
   **append-only passthrough + throttled flush**; DiffWrite (cell-diff) — только фаза 2.
4. **Ширины Unicode**: сегодня их нет нигде (grep по репо — 0 попаданий wcwidth/wide).
   Хорошая новость: passthrough-стриминг ширин НЕ требует — они нужны только там,
   где МЫ считаем курсор (inline-editor, будущий diff). Таблица ~2 KB, lookup
   O(log n) ≈ 7 сравнений ≈ 10 ns.
5. **Память**: бюджет 20 MB достижим ТОЛЬКО с PublishAot (JIT-рантайм CLR один
   стоит 15–25 MB RSS). Плюс ring-buffer истории: в inline-mode терминал сам держит
   scrollback — нам хранить напечатанное НЕ нужно.
6. **AOT/P-Invoke**: `[LibraryImport]` (source-gen) обязателен, `[DllImport]`
   сломает сборку с TreatWarningsAsErrors; главная мина — раскладка struct termios
   различается между glibc/musl/macOS → отдельные layouts per-OS.

Оценки трудозатрат на перф-фиксы: ~3–5 дней сверх плана §8.1 дизайн-дока.

---

## 1. АЛЛОКАЦИИ HOT-PATH: инвентаризация AnsiTuiRenderer на токен

### 1.1. Путь одного TextDeltaEvent сегодня

```
RenderAsync(event)                                  ← AnsiTuiRenderer.cs:58
├─ RenderLiveToken → ctx.Write(td.Delta)            ← :116 → Console.Write (:172)
└─ base.RenderAsync(event)                          ← BaseTuiRenderer.cs:84 (async!)
   ├─ foreach vm in ViewModels.GetAll()             ← :88 → ToList()+lock (ALLOC)
   │  └─ ChatHistoryVM.UpdateFromEventAsync         ← TuiViewModels.cs:140
   │     └─ StreamingText += td.Delta               ← :152 O(n²) ALLOC (KILLER)
   ├─ foreach view in Views.GetAll()                ← :102 → ToList()+lock (ALLOC)
   └─ ShouldRenderPlacement ×4 (дёшево, switch)     ← :117-135
```

| # | Аллокация на токен | Где | Размер/токен | Суровость |
|---|---|---|---|---|
| A1 | `StreamingText += delta` — новая строка длиной i+len | TuiViewModels.cs:153 | растёт со стримом: Σ ≈ n·avg_len/2 | 🔴 критично |
| A2 | То же для `ThinkingText += thd.Delta` | TuiViewModels.cs:157 | то же в thinking-фазе | 🔴 критично |
| A3 | `ViewModels.GetAll()` → `_views.Values.ToList()` под lock | ViewRegistry.cs:171+ | ~56 B List + элементы | 🟠 высоко |
| A4 | `Views.GetAll()` → `.ToList()` под lock | ViewRegistry.cs:88-93 | ~56 B | 🟠 высоко |
| A5 | async state-machine box + Task от base.RenderAsync | BaseTuiRenderer.cs:84 | ~72-120 B | 🟡 средне |
| A6 | WriteStyled: `new List<string>()` + `string.Join(';')` + интерполяция | AnsiTuiRenderer.cs:187-201 | ~4-6 строк (~300 B) на thinking/tool-call дельту | 🟠 высоко |
| A7 | WriteColored: 2-3 интерполированные строки | AnsiTuiRenderer.cs:176-183 | ~250 B, но только на границах сообщений | 🟡 низко |
| A8 | PropertyChanged от `[ObservableProperty]` StreamingText на каждый токен | TuiViewModels.cs:124-126 (генератор) | делегаты/подписки без слушателей | 🟡 низко |
| A9 | GetByPlacement → `list.ToList()` на каждом placement-render | ViewRegistry.cs:77-82 | ~56 B, редко | 🟢 ок |

Отдельно, вне токенного пути: `ChatHistoryView.RenderAsync` перерисовывает ВСЕ
записи истории на каждом MessageEndEvent / ToolExecutionEndEvent
(`src/Harbor.Terminal.Abstractions/Views/ChatHistoryView.cs:40-43`, foreach по
vm.Entries) — O(история) на конец сообщения = O(n²) за сессию. Для Ansi-REPL это
маскируется тем, что вывод просто дописывается вниз, но в полноэкранном ConsoleEx
это прямой шаблон для отказа.

### 1.2. Бюджет аллокаций на кадр (цель — 0)

Целевая модель ConsoleEx (из дизайн-дока §5.4): парсер и путь key/wheel/mouse —
0 alloc; Char — 1 string на символ; Paste — 1 string. Рендерный путь обязан
ложиться в тот же бюджет:

- **Steady-state токен-стрим: 0 B/op.** Дельта пишется в переиспользуемый
  байтовый буфер кадра (pooled `byte[]` или `ArrayBufferWriter<byte>`), flush по
  кадру. Никаких строк не создаётся — дельта приходит строкой из провайдера,
  кодируется энкодером прямо в буфер кадра.
- **Кадровый бюджет при 30 fps**: ≤ 30 flush/сек фиксированно; каждый flush —
  1 write(2) + 0 alloc (буфер переиспользуется, capacity держится).
- Допустимые аллокации: только события-границы (MessageStart/End — единицы в сек),
  resize, paste. Итого цель: **< 64 B/сек средних аллокаций во время стрима**,
  gen0 < 1 collection за 30-секундный стрим.

### 1.3. Фиксы (sketches)

**F1 — не копить стриминг-текст (A1/A2).** Рендерер пишет дельту напрямую;
VM хранит только счётчик и хвост строки для префикс-кэша markdown (см. §4):

```csharp
// ChatHistoryViewModel: вместо StreamingText += td.Delta
public void AppendDelta(string delta) {
    _tokenCount++; _charCount += delta.Length;
    _tail = TruncateHead(_tail + delta, MaxTail); // хвост ≤ 512 символов для md-кэша
}
public string ConsumeFullTextOnEnd(); // собирается из источника, не из конкатенаций
```

До: 40 KB ответ → ~200 МБ мусора, ~10 000 промежуточных строк.
После: 0 строк в пути, ~40 KB итоговый текст живёт один раз (в истории сессии).

**F2 — заморозить реестры после InitializeAsync (A3/A4/A9).**

```csharp
// BaseTuiRenderer.RenderAsync: после Freeze() читаем snapshot без lock/ToList
private ITuiViewModel[] _vmSnapshot = Array.Empty<ITuiViewModel>();
// InitializeAsync: _vmSnapshot = ViewModels.GetAll().ToArray();
foreach (var vm in _vmSnapshot) await vm.UpdateFromEventAsync(@event, ct);
```

До: 2 × ToList + lock на каждый токен (~112 B + lock-контеншн).
После: 0 alloc, 0 lock в steady-state (реестр иммутабелен после Freeze — он уже
зовёт `Freeze()` в InitializeAsync, BaseTuiRenderer.cs:76, но GetAll об этом не знает).

**F3 — WriteStyled без List/Join (A6).** Кодовые строки стилей — const, склейка
в стековый буфер:

```csharp
public void WriteStyled(string text, TuiStyle style) {
    Span<char> buf = stackalloc char[16]; var pos = 0;
    buf[pos++] = '\x1b'; buf[pos++] = '[';
    if ((style & TuiStyle.Bold) != 0) { AppendDigit(ref buf, ref pos, '1'); }
    // ... ';' между кодами ...
    buf[pos++] = 'm';
    WriteRaw(buf[..pos]); WriteRaw(text); WriteRaw(Ansi.Reset); // в буфер кадра
}
```

До: 4-6 строк/дельта. После: 0 alloc (HasFlag заменён на битовые `&` —
Enum.HasFlag в современных рантаймах интринзится, но явный `&` дешевле и честнее).

**F4 — убрать async-бокс на токен (A5).** Разделить путь: синхронный
`RenderTokenDelta(...)` для MessageUpdateEvent, async остаётся для редких событий.
ValueTask + `MethodImplOptions.AggressiveInlining` на проверке быстрого пути.

---

## 2. СТРАТЕГИИ ВЫВОДА: per-token Write vs buffered frame vs DiffWrite

### 2.1. Сравнение (по существу кода и системным константам)

| Стратегия | Syscalls | Аллокации | Атомарность кадра | Latency p99 | Когда |
|---|---|---|---|---|---|
| **A. Console.Write per-token** (сегодня, AnsiRenderContext.cs:172 + AutoFlush) | 1 write(2)/токен = 1000/с @ 1000 tok/s | энкодер-чанкинг, ~0-2 мелких | ❌ статус-бар может вклиниться между токеном и \n | ~0.1–1 ms, спайки на GC | донор-антипаттерн |
| **B. Buffered frame + throttled flush** (StringBuilder/ArrayBufferWriter → 1 write на тик 33 ms) | ≤ 30/с фикс | 0 после прогрева (переиспользуемый буфер) | ✅ кадр = одна запись | < 0.3 ms + до 33 ms кадровой задержки | **MVP inline** |
| **C. DiffWrite (cell-diff)** (2 экранных буфера, пишем только изменённые клетки) | пропорционально диффу | 2 × w×h×sizeof(Cell) ≈ 200×60×8 B ≈ 96 KB | ✅ | < 1 ms | фаза 2 (fullscreen) |

Факты из кода:
- `Console.Out` создаётся с `AutoFlush = true` → каждый `ctx.Write` = отдельный
  write(2) через SyncTextWriter-lock. Плюс `Flush()` контекста (:212) — второй
  syscall впустую.
- `Width => Console.WindowWidth` на лету (:168) — каждый вызов может ходить в
  ioctl(TIOCGWINSZ); в дифф-математике фазы 2 это надо кэшировать и обновлять по
  TerminalResized (риск «трёх источников правды» из дизайн-дока §8.3).
- Спековский скетч ThrottledRenderer (`specs/07-tui.md:1104-1128`) сам содержит
  анти-паттерн: `_pendingText += text` — та же O(n²)-конкатенация, что A1, только
  теперь ещё и в троттлере. В ConsoleEx брать идею (троттлинг + обязательные точки
  flush на MessageEnd/ToolCallStart/AgentEnd), но реализацию — на
  переиспользуемом `StringBuilder`/`ArrayBufferWriter`.

Решение: **B для MVP** (inline-mode по решению 8.4.1 дизайн-дока), C — фаза 2.
Append-only passthrough стрима внутри B: пока идёт чистый текст — просто пишем
байты в буфер кадра, терминал сам скроллит; позиционирование нужно только
инлайн-редактору ввода и статус-бару (нижняя закреплённая строка).

### 2.2. Sketch буферизованного кадра

```csharp
internal sealed class ConsoleExRenderContext : ITuiRenderContext {
    private readonly ArrayBufferWriter<byte> _frame = new(64 * 1024);
    private System.Text.Encodings.Web.Utf8? _enc; // свой энкодер без BOM-faff
    public void Write(string s) {
        var w = _enc.EncodeToUtf8(s, _frame.GetSpan(s.Length * 4)); // ASCII fast-path внутри
        _frame.Advance(w.Length);            // 0 alloc: span уже выделен
    }
    public void Flush() {
        var written = _frame.WrittenSpan;
        if (written.Length == 0) return;
        StdoutHolder.Write(written);          // ОДИН write(2) на кадр
        _frame.Clear();                       // capacity сохранена → 0 alloc
    }
}
```

### 2.3. Ожидаемые числа (оценка)

| Метрика | A (сегодня) | B (MVP-цель) | C (фаза-2) |
|---|---|---|---|
| write(2) при 1000 tok/s | ~1000/с | ≤ 30/с | ~30/с (только дифф-байты) |
| Байт в секунду на 40 tok/s стриме | = полезной нагрузке | = полезной нагрузке | ~в 5-10 раз больше служебных CSI, но меньше на перерисовках |
| Аллокации/токен | ~6-8 объектов (§1.1) | 0 | 0 (после прогрева двух буферов) |
| p99 token-to-screen | 0.1–1 ms + GC-спайки | < 1 ms (+≤33 ms кадра — всё равно << 20 ms таргета) | < 1 ms |
| Разрывы кадра (статус-бар вклинился) | возможны | исключены | исключены |

Примечание к таргету 20 ms: кадровая задержка троттлера 33 ms формально больше
20 ms — поэтому flush-точки должны включать немедленный flush на «тихих»
событиях (конец мысли → начало текста) И адаптивный таймер: если токены идут
реже 30/с — flush сразу по приходу (idle-flush по 5-10 ms таймеру). Тогда
p99 = min(33 ms, время ожидания следующего токена) и таргет выполняется.

### 2.4. Бенчмарк-план (что именно мерить)

Каркас: BenchmarkDotNet (`[MemoryDiagnoser]`, `[ThreadingDiagnoser]`) + pty-harness
для e2e. Проект `Harbor.Tui.ConsoleEx.Benchmarks`, три группы:

1. **TokenStream**: `RenderAsync(MessageUpdateEvent(TextDelta))` × N в заглушку
   Stream (null-sink с подсчётом байт). Params: N = 1k / 10k; режимы A/B/C.
   Меряем: alloc/op (цель B/C: 0 B/op), tokens/sec (цель ≥ 1000), gen0/sec.
2. **KeyEcho**: байты `\x1b[A`, `a`, `\x03`, paste-chunk → парсер → echo-write →
   flush. Меряем: mean/p99 (таргет < 5 ms — с запасом на порядок; красная зона >
   1 ms), alloc/event (цель 0 на key/wheel).
3. **FrameFlush**: кадр 80×24 + статус-бар, 30 fps, 5 минут. Меряем:
   RSS-рост (таргет: плато < 20 MB), gen0 collections/min, p99 flush-latency.
4. **E2E-pty**: python-pty-харнесс (или `script -q`): реальный терминал,
   штампы времени прибытия байт; token-to-screen p50/p99 (таргет < 20 ms),
   key-echo p99 (таргет < 5 ms). Это единственный честный замер — всё остальное
   ловит только CPU-side.

Regression-gate (по образцу benchmarks-plan.md репо): > 20% И > 3σ к базлайну
JSON-репорта = красный билд; абсолютные полы: alloc/token > 32 B, key-echo p99 >
1 ms, RSS > 24 MB.

---

## 3. UTF-8/UNICODE ШИРИНЫ: эмодзи/CJK = 2 клетки

### 3.1. Проблема

В репо сегодня НЕТ обработки ширин вообще (grep по src/ на
`wcwidth|Width|Rune|EastAsian` в TUI-коде — только Console.Window*).
Последствия для ConsoleEx:

1. **Inline-editor ввода** (ReadLineAsync в raw mode): курсорная математика
   «колонка = длина строки» ломается на `привет 👍` — .Length = 8 UTF-16 юнитов
   (эмодзи = surrogate pair), Rune-ов = 8, а клеток терминал займёт 9.
   Backspace посреди CJK сотрёт полклетки → визуальный мусор.
2. **Будущий diff/cell-diff (фаза 2)**: без таблицы ширин невозможно замапить
   клетку буфера на колонку экрана — расползание всей сетки.
3. **Passthrough-стриминг текста ширин НЕ требует** — терминал сам делает
   layout. Это важно: в MVP hot-path ширин нет вовсе.

Кто владелец: один статический класс `UnicodeWidth` в
`src/Harbor.Tui.ConsoleEx/Rendering/`, BCL-only, данные — сгенерированные
из EastAsianWidth.txt + DerivedGeneralCategory (Mn/Me/Cf) диапазоны,
зашитые константами (никаких данных в рантайм-ресурсах — AOT-friendly).

### 3.2. Решение и стоимость lookup

Два отсортированных массива диапазонов: Wide/F (~120 диапазонов EAW=W+F),
Zero-width (~30 диапазонов Mn/Me/Cf + U+200B..). Поиск бинпоиском:
O(log2(120)) ≈ 7 сравнений ≈ 10–15 ns. Память ~2 KB. Вызов — только при
редактировании строки ввода (per keystroke ≤ длина строки lookups ≈ 80 × 15 ns
= 1.2 µs — ничто против таргета 5 ms) и при расчёте диффа в фазе 2.

```csharp
internal static class UnicodeWidth {
    private static readonly (int Lo, int Hi)[] Wide = LoadWide();   // EAW W+F
    private static readonly (int Lo, int Hi)[] Zero = LoadZero();   // Mn|Me|Cf
    internal static int Width(Rune r) {
        int v = r.Value;
        if (In(Zero, v)) return 0;          // combining, ZWJ, VS16-хвосты
        if (In(Wide, v)) return 2;          // CJK, эмодзи-база
        return 1;                           // дефолт: new/unassigned = 1
    }
    private static bool In((int, int)[] ranges, int v) {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi) { int m = (lo + hi) >> 1;
            if (v < ranges[m].Item1) hi = m - 1;
            else if (v > ranges[m].Item2) lo = m + 1; else return true; }
        return false;
    }
}
```

Грабли (обязательные golden-тесты): ZWJ-последовательности (семейные эмодзи =
несколько рун = ОДНА клетка → нужен grapheme-cluster шаг над Width);
U+FE0F меняет презентацию; tab/CR/LF — не клетки. Для MVP inline-editor
достаточно Width по рунам + правило «не разрывать кластер» на границе backspace.

До/после: до — позиция курсора уходит на N×(лишние клетки) после первого эмодзи
(воспроизводимый баг), после — точная математика за ~10 ns/rune; вклад в
key-echo < 0.1% бюджета.

---

## 4. СТРИМИНГ 10k ТОКЕНОВ ЗА 30 СЕК

### 4.1. Что не перерисовывать и почему

Профиль нагрузки: 10k токенов × ~4 символа = ~40 KB текста, темп ~333 tok/s
(средний), пики до 1000+ tok/s. Таргет token-to-screen < 20 ms.

Три стратегии хвоста:

| Стратегия | Байт всего | CPU | p99 latency |
|---|---|---|---|
| Полный redraw хвоста на каждый токен (markdown re-render целиком) | O(n²): Σ re-render ≈ 400 МБ эмитнутых байт | секунды стагнации к концу | взрыв > 20 ms задолго до конца |
| Incremental line update (prefix-cache + переписать последнюю частичную строку) | ~900 flush × ≤ 300 B ≈ 270 KB | тривиален | < 1 ms |
| Append-only passthrough (чистый текст без markdown-подсветки) | ровно 40 KB | ноль | < 1 ms |

Базовое правило: **пока не нужен markdown/layout — passthrough** (это уже
делает AnsiTuiRenderer, AnsiTuiRenderer.cs:116 — единственное, что он делает
правильно). Перерисовка появляется только когда мы подсвечиваем markdown на
лету: тогда prefix-cache из спеки (`specs/07-tui.md:310-343`, «инновация crush»)
— stable prefix не трогаем, переписываем только trailing partial:

```csharp
// TailRenderer: держит окно последних K видимых строк текущего параграфа
public void OnDelta(string delta) {
    _pending.Append(delta);                    // переиспользуемый StringBuilder
}
public void RenderFrame() {                    // тик кадра, 33 ms
    if (_pending.Length == 0) return;
    int boundary = FindSafeBoundary(_tail + _pending); // \n вне инлайн-разметки
    EraseTailLines(_tailWindowLines);          // \x1b[{K}A\x1b[J — K ≤ 3 строк
    Write(_tailPrefix + _pending);             // переписываем только хвост
    _tail = TruncateToWindow(_tail + _pending); _pending.Clear();
}
```

Обязательные flush-точки (уже в спеке §12): MessageEnd, ToolCallStart, AgentEnd +
немедленный flush при простое (адаптивный таймер, см. §2.3) — иначе хвост
зависает до 33 ms и ломает ощущение живого набора.

Resize во время стрима: единственный случай, когда нужен redraw всего экрана —
по TerminalResized пересчитать wrap заново (один раз, не per-token; риск-строка
«три источника правды о размерах» дизайн-дока §8.3).

Ожидаемые числа (10k токенов): полный-redraw — ~400 МБ записи + рост latency до
сотен ms к концу (O(n²)); incremental — ~270 KB записи, flat < 1 ms весь стрим;
passthrough — 40 KB, 0 CPU. GC: с фиксом F1 генерации почти не churn'ятся
(< 1 gen0 за 30 с против оценочных десятков сейчас).

---

## 5. ПАМЯТЬ: бюджет 20 MB

### 5.1. Кто жрёт (по коду)

| Пожиратель | Где | Оценка |
|---|---|---|
| O(n²)-мусор StreamingText/ThinkingText | TuiViewModels.cs:153,157 | ~200 МБ транзиентного мусора на большой ответ → всплески RSS |
| Неограниченная `_entries: ObservableCollection<ChatEntry>` — полные тексты сообщений навсегда | TuiViewModels.cs:116 | 100 сообщений × 4 KB avg = ~0.4 MB; сессия-марафон с большими ответами → десятки МБ |
| ChatHistoryView перерисовывает все Entries строками | Views/ChatHistoryView.cs:40-43 | транзиентные строки при каждом repaint истории |
| Unbounded Channel<UiMsg> | дизайн §5.4 | paste-flood уже лимитирован 256 KiB, но очередь событий без bound |
| JIT-рантайм CLR | сам факт framework-dependent запуска | 15–25 MB RSS базой — **съедает бюджет ещё до нашего кода** |

Плюс консольная история: в inline-mode всё напечатанное УЖЕ хранится в
scrollback самого терминала — дублировать его в наших структурах незачем.
Это ключ к бюджету: our-state ≠ scrollback.

### 5.2. Стратегия: ring-buffer истории + bounded channel

```csharp
// HistoryRing: фиксированный бюджет символов, вытеснение старейших
private readonly Queue<ChatEntry> _entries = new();
private long _chars;
public void AddEntry(ChatEntry e) {
    _entries.Enqueue(e); _chars += e.Content.Length;
    while (_chars > MaxChars /* 2 MB */ || _entries.Count > MaxEntries /* 500 */)
        _chars -= _entries.Dequeue().Content.Length;
    // в inline-mode вытесненное НЕ теряется для юзера: оно в scrollback терминала
}
```

- Channel<UiMsg>: `CreateBounded(1024, BoundedChannelFullMode.Wait)` — ввод
  НЕ дропаем (клавиши дороже задержки), backpressure на reader-thread.
- Буфера: кадровый ArrayBufferWriter 64 KB (переиспользуемый), input chunk
  4 KB, parser state — struct в поле. Итого наши структуры < 3 MB даже в худшем
  легальном случае (2 MB история + буфера + VM).
- Логи: запретить Info+ логирование per-token (grep-правило из аудитов репо:
  негардированные LogDebug в циклах — уже находили такие); TUI-логи — только
  переходы состояний, в файл, не в stdout (stdout = протокол терминала!).

### 5.3. Ожидаемые числа

| Профиль | Сегодня (JIT + A1) | После (AOT + ring + F1-F4) |
|---|---|---|
| Стрим 40 KB ответа | RSS-всплеск +100–300 МБ, gen0 шторм | всплеск < 1 MB, gen0 ≈ 0 |
| Steady после 1 ч сессии | рост с историей без предела | плато ~12–18 MB (история упёрлась в 2 MB cap) |
| Худший легальный случай | n/a | < 20 MB с запасом на runtime |

Жёсткое условие: `PublishAot=true` для harbor-TUI-процесса (дизайн §7 это уже
фиксирует — первый AOT-проект TUI). Без AOT таргет 20 MB недостижим в принципе:
один CLR стоит как весь бюджет.

---

## 6. AOT ПОДВОДНЫЕ КАМНИ P/INVOKE TERMIOS

### 6.1. LibraryInterfaceGenerator (LibraryImport) vs DllImport

- `[DllImport]` маршалинг — рефлексия времени исполнения: под NativeAOT это
  IL3050/IL2026 предупреждения → с TreatWarningsAsErrors сборка красная
  (ровно риск-строка дизайн-дока §8.3 «TreatWarningsAsErrors + P/Invoke шум»).
- `[LibraryImport]` (source-gen интероп) генерирует маршалеры на компиляции:
  0 рефлексии, чистый AOT, минус первый-call overhead DllImport (нет
  инициализации маршевых стабов на вызове Enter()).
- Perf самого вызова tcgetattr/tcsetattr (~1 µs, зовётся 2 раза за сессию на
  Enter/Restore) — неважен; важна собираемость и отсутствие скрытых фолбэков.

```csharp
[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = false)]
private static partial int tcgetattr(int fd, ref TermiosLinux termios);
[LibraryImport("libc")]
private static partial int tcsetattr(int fd, int optionalActions, ref TermiosLinux termios);
// struct blittable: [StructLayout(LayoutKind.Sequential)], c_cc = fixed buf NCCS
```

### 6.2. Конкретные мины

1. **Раскладка struct termios РАЗНАЯ**: glibc/musl Linux — NCCS=32 и один
   порядок полей; macOS (BSD) — NCCS=20, другой порядок и u_long c_iflag.
   Один struct на всех = тихая порча памяти/флагов на macOS. Решение: per-OS
   layouts (`TermiosLinux`, `TermiosMacos`) + два частичных класса контроллера
   или рантайм-ветка по `OperatingSystem.IsMacOS()`; golden-тест размеров:
   `sizeof(TermiosLinux)==60`, `sizeof(TermiosMacos)==...` (по заголовкам).
2. **Blittable-only**: LibraryImport требует blittable аргументов либо явного
   маршалинга; `fixed byte c_cc[NCCS]` внутри struct — ок (unsafe запрещён
   Directory.Build.props! → заменить на `[MarshalAs(UnmanagedType.ByValArray,
   SizeConst=32)] byte[]` — source-gen сгенерирует копирующий маршалер; либо
   развернуть 32 полей — быстрее и без маршалера).
3. **AllowUnsafeBlocks=false глобально** — значит никаких fixed-буферов в
   structs; развёрнутые поля или ByValArray.
4. `PosixSignalRegistration` (SIGWINCH/SIGINT/SIGTERM) — BCL, AOT-safe, брать
   его, а не signal() P/Invoke.
5. ioctl(TCGETS) как альтернатива: один P/Invoke вместо двух, но тот же struct —
   выигрыш нулевой, читаемость хуже. Оставить tcgetattr/tcsetattr.
6. Windows-сторона: SetConsoleMode через [LibraryImport("kernel32")] — там всё
   blittable из коробки; спековский DllImport-скетч (specs/07-tui.md:1166-1168)
   переписать на LibraryImport.

Чеклист выхода: `dotnet publish -r linux-x64 -p:PublishAot=true` — 0 AOT-warning
в новом проекте (критерий 8.2.6 дизайн-дока); sizeof-тесты обоих структур;
smoke-скрипт «гробовщик» на restore termios после kill -9/-TERM.

---

## 7. Сводный приоритет внедрения (перф-часть спринта)

| # | Фикс | Часов | Эффект |
|---|---|---|---|
| 1 | F1: убить StreamingText+= (VM) | 2–4 ч | −~200 МБ мусора/ответ; блокер бюджета памяти |
| 2 | F2: snapshot реестров после Freeze | 1–2 ч | −2 alloc+2 lock/токен |
| 3 | Кадровый буфер + throttled flush (§2.2) + адаптивный idle-flush | 4–6 ч | −97% syscall, атомарный кадр |
| 4 | F3: WriteStyled без аллокаций | 1–2 ч | 0 alloc на styled-дельту |
| 5 | HistoryRing + bounded Channel | 2–3 ч | плато памяти < 20 MB |
| 6 | UnicodeWidth таблица + golden-тесты | 3–4 ч | корректный inline-editor |
| 7 | Prefix-cache tail-renderer (если markdown-подсветка в MVP) | 4–8 ч | иначе O(n²) вернётся через подсветку |
| 8 | Бенчи §2.4 + regression-gate | 4–6 ч | закрыть верификацию таргетов |
| 9 | LibraryImport termios + per-OS structs + sizeof-тесты | входит в фазу 0 плана | AOT-зелёный |

Итого перф-надбавка поверх §8.1 дизайн-дока: ~3–5 дней.

## Источники (проверено по факту 25.08.2026)

- `src/Harbor.Tui.Ansi/AnsiTuiRenderer.cs` (:58-127 токенный путь, :166-213 контекст) — прочитан полностью
- `src/Harbor.Tui.Ansi/Ansi.cs` — полностью (статический писатель мимо контекста)
- `src/Harbor.Terminal.Abstractions/BaseTuiRenderer.cs` :84-136 fan-out, :192-219 placement-switch
- `src/Harbor.Terminal.Abstractions/ViewRegistry.cs` :77-93 ToList-under-lock
- `src/Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs` :140-190 (+= дельты, AddEntry, preview-cap 200)
- `src/Harbor.Terminal.Abstractions/Views/ChatHistoryView.cs` :32-50 full repaint
- `specs/07-tui.md` §13 (:1136-1145 таргеты), §12 (:1099-1134 throttle-скетч с анти-паттерном +=), §4 (:310-343 prefix-cache)
- `/tmp/harbor-analysis/consoleex-design.md` §5, §7, §8.3 — прочитан полностью
