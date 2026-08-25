# ConsoleEx фаза 2 — дизайн ядра cell-diff рендера

> Спринт-7 волна аудитов Harbor. Компаньон к `consoleex-design.md` §8.5 (фаза 2
> «Full»: alt-screen + cell-diff буфер, полноэкранный layout, resize-детектор,
> ~10–14 дн.). READ ONLY — код репо не менялся. Ограничения неизменны:
> .NET 10, BCL-only, AllowUnsafeBlocks=false, PublishAot=true, TreatWarningsAsErrors.
> Perf-таргеты specs/07-tui.md: token-to-screen < 20 ms, память TUI < 20 MB;
> бюджет кадра здесь — **< 16 ms**.
>
> Референсы изучены по исходникам (25.08.2026): ratatui main (`buffer.rs`,
> `buffer/diff.rs`, `terminal/{render,buffers,resize}.rs`), tcell v3 (`cell.go`,
> `tscreen.go`, `vt/mode.go`) — на нём теперь работает lazygit (go.mod: gdamore/tcell/v3),
> btop++ `btop_draw.cpp`, Spectre.Console `Live/*`. Выводы — §6.

---

## 0. Конвейер кадра

```
Channel<UiMsg> ──drain-to-empty──▶ reducer/VM (существующий BaseTuiRenderer fan-out)
                                      │ состояние приложения (единственный источник контента)
                                      ▼
                    Paint: панели пишут клетки в BACK ScreenBuffer (layout из §5)
                                      │
                                      ▼
                    DiffEngine: fused-проход back vs FRONT → ANSI прямо в Writer (§2)
                                      │
                                      ▼
                    AnsiWriter: SGR-автомат + cursor-elision + sync-обёртка (§3)
                                      │  один WriteAsync на stdout
                                      ▼
                    Swap: BACK↔FRONT (swap индекса, не копирование)
```

Принципы:
1. **Два буфера, своп указателя** — как ratatui/tcell. FRONT = что показано
   терминалом, BACK = что намалевали в этом кадре.
2. **Fused diff→encode** — никаких промежуточных списков изменённых клеток
   (ratatui материализует `Vec<(x,y,&Cell)>` — мы это убираем, ноль аллокаций).
3. **Рендер — чистая функция от состояния**: контент чата живёт в VM, буферы
   производны ⇒ resize/повреждение буфера лечатся полным repaint без потери данных.
4. Один flush на итерацию цикла; кадр = атомарная порция байт (sync-режим §3).

---

## 1. Cell-buffer модель

### 1.1. Ячейка — readonly struct 16 байт

```csharp
public readonly struct Cell : IEquatable<Cell>
{
    public const byte WSkip = 0;               // хвост wide-char: клетка невидима
    public readonly int   Rune;                // codepoint (System.Rune-decoded, без суррогатов)
    public readonly uint  Fg, Bg;              // упаковка цвета ниже
    public readonly ushort Flags;              // Bold|Dim|Italic|Underline|Blink|Reverse|
                                               // Hidden|Strike (8b) + UnderlineStyle (3b)
    public readonly byte   Width;              // 1 | 2 | WSkip
    public bool Equals(Cell o) => Rune == o.Rune & Fg == o.Fg & Bg == o.Bg
                               & Flags == o.Flags & Width == o.Width;
}
```

Упаковка цвета (uint): бит 31 = «default» (эмитить `39/49`), бит 30 = «RGB24»
(биты 23..0), иначе — индекс палитры 0..255 в битах 7..0. Одна ветка на эмит,
без иерархии классов-цветов.

**Размер/память:** 16 Б/клетку. Полный экран 200×50 = 160 КБ на буфер, два
буфера = 320 КБ; 400×120 = 750 КБ на буфер (1.5 MB пара). Против таргета 20 MB —
шум даже на 4K-терминале. Для сравнения:
tcell держит на клетку две Go-строки (currStr/lastStr) — десятки байт + GC-давление;
наш вариант плотнее и сравним за 5 int-сравнений (~1–2 нс).

### 1.2. ScreenBuffer — capacity-reuse вместо пула

```csharp
sealed class ScreenBuffer
{
    Cell[] _cells;                       // индекс y*Cols + x
    public int Cols, Rows;               // видимая геометрия
    int _capacityCols, _capacityRows;    // выделенный объём
    public ulong[] RowHash;              // кэш хэшей строк (sentinel = invalid)
    public ref Cell At(int x, int y) => ref _cells[y * Cols + x];
    public void Resize(int cols, int rows)          // см. §4
    public void Fill(Rect r, in Cell c);
    public void SetText(int x, int y, ReadOnlySpan<char> s, in Style st); // wide-aware
}
```

**Пул/аренда:** `ArrayPool<Cell>.Shared` отвергнут — бакеты по степени двойки
дают до 2× перерасхода и чужие обрезанные массивы. Вместо этого: массивы
растут геометрически (×1.25) и **никогда не сжимаются** до явного `Trim()`.
Resize «вниз»/«вбок» переиспользует тот же массив (меняются только Cols/Rows) —
**ноль аллокаций** на типичном resize; рост — редкая LOH-аллокация (>85 КБ),
дебаунсится (§4). Отдельный transient-пул нужен только AnsiWriter'у (§3, byte[]).

### 1.3. Wide-char инварианты

- Широкая руна занимает `Width=2` + хвостовая клетка `Width=WSkip`;
  diff эмитит только ведущую (терминал сам продвигает курсор на 2).
- Перезапись ЛЕВОЙ половины wide одиночным символом ⇒ принудительная
  перерисовка и правой половины (иначе призрак глифа — правило из ratatui BufferDiff).
- Перезапись правой половины ⇒ форс-перерисовка левой. Проверяется golden-тестами (§8).

---

## 2. Diff-алгоритм: три стратегии и когда что быстрее

### 2.1. Базовая: fused full-scan prev/next (по умолчанию)

```csharp
public void Flush(ScreenBuffer next, AnsiWriter w)
{
    for (int y = 0; y < next.Rows; y++)
    {
        if (RowsEqual(y)) continue;                    // hash fast-path (§2.3)
        for (int x = 0; x < next.Cols; )
        {
            ref Cell n = ref next.At(x, y), f = ref front.At(x, y);
            byte wdt = n.Width;
            if (wdt == Cell.WSkip || n == f) { x += Math.Max(wdt, 1); continue; }
            w.MoveTo(x, y);                            // элит только при смене позиции
            w.SetStyle(in n);                          // дельта-SGR
            w.PutRune(n.Rune);                         // + пробел-затирание хвоста при need
            f = n;                                     // FRONT догоняет BACK
            if (n.NeedsWideCleanup) front.ForceRepaintNeighbors(x, y); // §1.3
            x += wdt;
        }
    }
}
```

O(cells) сравнение 16-байтовых структур. Честная математика: 5 нс/клетку →
80×25 ≈ 0.03 ms, 200×50 ≈ 0.16 ms, 400×120 ≈ 0.92 ms. **Полный скан никогда не
нарушает бюджет 16 ms** — поэтому он и дефолт (так же живут tcell `draw()` и ratatui).
Отличие от ratatui: без материализации списка изменений — emit сразу в Writer.

### 2.2. Dirty-регионы (FrameHint API) — точечные апдейты

```csharp
// Панель/виджет знает ГДЕ изменилось (caret blink, spinner, часы статуса):
void FrameHint(Rect damage);          // копится в DamageTracker (List<Rect>, union)
// Flush: если hints есть и покрывают < 25% клеток — сканируем только их;
// иначе fallback на полный скан (защита от «забытых» инвалидаций).
```
Стоимость O(damage): spinner 10×1 ≈ микросекунды. Риск пропущенной инвалидации
= классический источник «застывшего экрана» ⇒ включается ТОЛЬКО как явный hint
сверху, базовая корректность остаётся на full-scan; в Debug-сборке — параноид-
проверка (full-scan поверх, assert эквивалентности).

### 2.3. Строковый hash — ускорение «тихих» кадров

`RowHash[y]` — FNV-1a(64) по полям клеток строки; пишется лениво: любое SetX
помечает строку invalid-sentinel, пересчёт — один раз перед flush. Кадр без
изменений стоит **O(rows)** вместо O(cells). Для стриминга токенов (апдейт раз в
~50 мс, меняется 1–3 строки) это главный выигрыш на больших экранах.

### 2.4. Матрица выбора

| Стратегия | Сложность/кадр | 80×25 | 200×50 | 400×120 | Когда выигрывает |
|---|---|---|---|---|---|
| Full-scan prev/next | O(cells) | ~0.03 ms | ~0.16 ms | ~0.9 ms | всегда предсказуемо, база |
| + row-hash skip | O(rows)+O(changed) | ~0.01 ms | ~0.05 ms | ~0.2 ms¹ | редкие локальные апдейты |
| Dirty-rects (hints) | O(damage) | µs | µs | µs | spinner/caret/статус |

¹ оценка; финальные цифры фиксируются BenchmarkDotNet-бенчем фазы 2
(`ScreenBufferBench`: scan/hash/dirty × {80×25, 200×50, 400×120}).

**Решение:** full-scan + row-hash сразу; dirty-rects — опциональный FrameHint для
анимаций. Все три ≤ ~1 ms CPU даже worst-case: реальный бюджет кадра съедает не
сравнение, а объём байт к терминалу → §3.

Аллокации кадра (steady state): **0** — BACK переиспользуется, Writer-буфер
pooled, списков изменений нет.

---

## 3. Flush-стратегия: байты важнее тактов

### 3.1. AnsiWriter — один буфер, один syscall на кадр

```csharp
sealed class AnsiWriter
{
    byte[] _buf; int _len;                    // ArrayPool<byte>, растёт до max frame
    SgrState _cur; (int X,int Y) _pos = (-1,-1);
    static readonly byte[][] PalFg = Precompute("38;5;{0}"), PalBg = ...; // interning

    public void BeginFrame()  { Append(ESC, "[?2026h"); _cur = Invalid; }   // sync on
    public void MoveTo(int x, int y) { if (_pos == (x,y)) return;
                                      Append($"\x1b[{y+1};{x+1}H"); _pos = (x,y); }
    public void SetStyle(in Cell c) { /* дельта-SGR, см. 3.2 */ }
    public void PutRune(int r)      { _pos.X += width; }   // неявный advance
    public void EndFrame()    { Append("[?2026l"); Stream.WriteAsync(_buf.AsMemory(0,_len)); }
}
```

Весь кадр собирается в один `byte[]` и уходит **одним WriteAsync** в
`Console.OpenStandardOutput()` (взят один раз; `Console.Write` не используем —
локи + свой кодировщик поверх). Ровно так устроен tcell: `t.buf.WriteTo(t.tty)`
один раз на `Show()`, с 2026-обёрткой (`startBuffering/endBuffering`).

### 3.2. Минимальные SGR-переходы

- Автомат держит текущие fg/bg/флаги **внутри кадра**; в начале кадра — сброс.
- Дельта-эмиссия: атрибуты включаются/снимаются попарно (`1`/`22`, `3`/`23`,
  `4`/`24`, `7`/`27`, `9`/`29`); если меняется >3 групп сразу — дешевле
  `SGR 0` + переприменить (эвристика по счётчику).
- Цвета: палитровые — из **precomputed byte[]** (`\x1b[38;5;Nm` × 256, интернинг
  как у btop), RGB форматируется вручную в стековый span. Даунгрейд по
  capability-probe MVP (truecolor→256→16) остаётся.
- Подряд идущие клетки одного стиля не повторяют SGR вообще.

### 3.3. Курсорная адресация vs \r\n-прокрутка

- Полноэкранный режим: только абсолютный `CUP` с элизией избыточных переходов +
  неявное продвижение курсора рунами (wide = +2). `\n` внутри кадра запрещён
  (на нижней строке вызовет скролл alt-screen'а).
- Прокрутка чата: v1 фазы 2 — честная перерисовка изменившихся клеток
  (терминал справляется); DECSTBM scroll-regions (`CSI Ps;Ps r`) — v2 opt-in:
  экономит байты на «прилипающих» логах, но тянет origin-mode-кворки терминалов
  (ratatui держит это за feature-flag `scrolling-regions` — поступаем так же).
- Курсор ввода: НЕ hide/show каждый кадр (как tcell) — в конце кадра паркуем
  курсор в логическую позицию каретки инпута; DECTCEM трогаем только когда
  позиция вне видимой области. Экономия ~12 байт/кадр и мерцания на SSH.

### 3.4. Sync-string (kitty/iTerm2 synchronized output)

Кадр оборачивается `CSI ?2026 h … CSI ?2026 l` (DECSET 2026) — терминал
применяет всё содержимое атомарно, исчезают разрывы/телевизор на середине
кадра. Поддержка: kitty, ghostty, foot, WezTerm, iTerm2 ≥3.5, WT ≥1.18,
Alacritty ≥0.11. **Гейт — не sniffing, а DECRQM-проба** `CSI ?2026$py`
(ответ `CSI ?2026;1$y`) → поле `SyncUpdates` в `TerminalCapabilities`;
без поддержки просто пропускаем обёртку (батчинг и так один write).
tmux: passthrough не эмулируем (политика §2.5 consoleex-design.md) — tmux сам
буферизует достаточно.

### 3.5. Темп кадров и бюджет байт

| Сценарий | Изменено клеток | Байт на кадр | Время encode+write |
|---|---|---|---|
| Тишина (hash-skip) | 0 | 8 (sync on/off) | <0.05 ms |
| Токен стрима (~50 мс) | 100–300 | 0.4–1.2 KB | 0.2–0.6 ms |
| Spinner-тик | 10–20 | 80–200 B | ~0.1 ms |
| Полный repaint 200×50 | 10 000 | 30–60 KB | 2–5 ms |

Worst-case полный кадр ≈ 6 ms ≪ 16 ms. Локально можно 60 fps; профиль SSH
(`SSH_CONNECTION` + RTT>50 мс → cap 15 fps, обязательный row-hash) — иначе
3 MB/s трафика на спиннере убьют линк. Планировщик: drain-to-empty канала UiMsg
→ максимум один flush на пачку событий; анимации будят таймером 30 fps
(HARBOR_TUI_MAX_FPS переопределяет).

---

## 4. Resize без потери контента

### 4.1. Детект

- Unix: `PosixSignalRegistration` на SIGWINCH — обработчик делает ТОЛЬКО
  `Channel<UiMsg>.TryWrite(new TerminalResized(...))` (в сигнале нельзя async/локи);
  размер запрашивается ioctl(TIOCGWINSZ) в главном цикле — сигнал лишь «пинок».
- Windows: poll `GetConsoleScreenBufferInfo` в render-тике каждые 150 мс
  (матрица §6 consoleex-design.md). v2: kitty in-band resize reports (mode 2048)
  как probe-опция — консистентно с kitty-политикой MVP.
- Дедуп: tmux шлёт пачку WINCH'ей → сравнение с текущими Cols/Rows + debounce
  50 мс (drag уголка = шторм событий).

### 4.2. Алгоритм (policy ratatui + наши нулевые аллокации)

```csharp
void ApplyResize(int w, int h)
{
    if (w == front.Cols && h == front.Rows) return;      // дедубль
    bool shrinkW = w < front.Cols;
    if (w < MinCols || h < MinRows) { PaintTooSmallSplash(w, h); return; }

    layout.Solve(w, h);                                  // §5: панели пересчитаны
    front.Resize(w, h); back.Resize(w, h);               // realloc только при росте cap;
                                                         // иначе переиспользование массива
    back.InvalidateAll();                                // все строки dirty ⇒ полный кадр
    if (shrinkW) writer.EmitEd(2);                       // Erase-in-display ДО кадра:
                                                         // policy ratatui «clear on
                                                         // horizontal shrink» против
                                                         // артефактов переноса строк
    PaintPanels(); Flush();                              // в одном тике — нет пустого кадра
    inputView.RestoreCaretClamped();                     // каретка в пределах новой ширины
}
```

**Почему контент не теряется:** буферы — производные; текст чата хранится в VM
развёрнутыми логическими строками и re-wrap'ится при paint под новую ширину.
Поэтому копирование overlap-областей старого буфера не нужно вовсе —
перерисовываем всё из состояния. Resize-кадр = обычный full repaint.

Аллокации: 0 при `w*h ≤ capacity`; геометрический рост — единичные LOH-аллокации,
дебаунсится до ≤1 на 50 мс. Время: solve + paint + encode ≈ 3–6 ms на 200×50 —
в бюджете. Артефакты: sync-обёртка скрывает промежуточное состояние от глаза.

---

## 5. Панельный layout

### 5.1. Дерево сплитов

```csharp
readonly record struct Rect(int X, int Y, int W, int H);
enum SplitDir { Horiz, Vert }
sealed class SplitNode {                       // struct в пуле при >16 узлов
    SplitDir Dir; float Ratio;                 // доля A от usable
    SplitNode? A, B; Panel? Leaf;              // лист или пара
    byte GapSize = 1;                          // колонка/строка разделителя
}
abstract class Panel {
    Rect Rect; Size Min; int Priority;         // min-enforced; меньше Priority = жертва
    bool Focused;                              // читается при paint рамки
    public abstract void Paint(ScreenBuffer b);   // clip строго по Rect (урок gocui views)
}
```

Solver — DFS сверху вниз, water-filling c учётом минимумов:

```csharp
Rect Solve(SplitNode n, Rect avail) {
    int total  = n.Dir == Horiz ? avail.W : avail.H;
    int usable = total - n.GapSize;
    int a = Clamp(Round(usable * n.Ratio), n.A.MinAlong(n.Dir), usable - n.B.MinAlong(n.Dir));
    return Merge(Solve(n.A, Take(avail, n.Dir, a)),
                 Solve(n.B, TakeFromEnd(avail, n.Dir, usable - a)));
}
```

Если `usable < ΣMin` детей — коллапс панели с меньшим `Priority`
(statusbar имеет бесконечный priority — не коллапсирует никогда).
Операции API: `Split(panel, dir, ratio)`, `Close(panel)`, `FocusIn(dir)`.

### 5.2. Focus traversal

- Плоский список видимых листьев в Tab-порядке: `Tab/Shift+Tab` = next/prev;
  `Alt+1..9` — прямой прыжок (стиль lazygit); клик мышью — hit-test по `Rect`
  (SGR mouse уже включён MVP-фазой), drag-resize границ — фаза 3 (mode 1002).
- Фокус визуализируется рамкой (accent fg + Bold) — это просто другой Style при
  paint, ноль отдельных кадров/проходов.
- Клавиатура роутится ТОЛЬКО focused-панели; глобальные хоткеи — до роутинга.

Стоимость: solver для ≤20 панелей — чистая int-математика по struct-нодам:
< 0.01 ms, 0 аллокаций. Рамки: T-стыки считаются post-pass'ом маской соседей
(4 бита → глифы ┌┐└┘├┤┬┴┼│─) поверх покраски панелей — O(perimeter).

---

## 6. Сравнение с зрелыми проектами: что украсть, что не брать

| Аспект | lazygit (tcell v3) | btop++ (C++) | Spectre.Console Live | ratatui (rust) | **ConsoleEx фаза 2** |
|---|---|---|---|---|---|
| Буфер | `[]cell`: currStr/lastStr строки на клетку + dirty-флаги | сетки НЕТ: кэш готовых string-фрагментов по элементам | segment-line модель (не grid) | два `Buffer`, `Vec<Cell>` | два ScreenBuffer, Cell=16Б, capacity-reuse |
| Diff | full-scan `drawCell` per `Show()` | redraw-flags по элементам/боксам | нет — переписывание блока целиком | линейный BufferDiff-итератор (+Vec аллокация) | fused full-scan + row-hash skip + FrameHint |
| Flush | один WriteTo; **2026-обёртка каждого кадра**; hideCursor; SGR при смене стиля; cursor-elision | конкатенация строк с вшитыми адресами; interned цветовые префиксы темы | `\r`+`CursorUp` танец; overflow crop/ellipsis | backend.draw(diff); autoresize перед кадром | один WriteAsync; 2026 по DECRQM-пробе; cursor-парковка вместо hide/show |
| Resize | `cells.Resize+Invalidate()` ⇒ полный redraw | пересчёт боксов + инвалидация кэшей | Ed(2)+ClearScrollback+Home при shrink | autoresize каждый кадр; **clear on horizontal shrink** | та же policy + нулевые аллокации на переиспользовании |
| Тесты | — | — | — | TestBackend + golden diff-тесты wide/V16 | golden-byte ANSI (актив MVP) + fuzz-invariant |

### Что крадём (конкретно)

1. **У tcell:** sync-обёртку 2026 вокруг всего кадра (`PmSyncOutput` в
   `startBuffering/endBuffering`); cursor-elision state-machine; пометку соседа
   dirty для wide-char; `Invalidate()` всех клеток при resize.
2. **У ratatui:** policy «horizontal shrink ⇒ Erase-in-display до кадра»;
   autoresize-per-frame как единую точку правды о размере; краевые случаи diff
   (правая половина wide, VS16) — переносим их golden-тесты; TestBackend-подход.
3. **У btop:** интернинг SGR-префиксов палитры (precomputed byte[] ×256) и
   элементные кэши тяжёлых виджетов (графики/спарклайны) с явными
   инвалидационными флагами.
4. **У Spectre:** ellipsis-crop политику для панелей, чей контент выше окна;
   `\r`+CursorUp-приём остаётся в inline-режиме фазы 1.

### Что НЕ берём

- Полноэкранное переписывание блока как у Spectre Live — трафик и мерцание.
- Строковые кэши btop как ОСНОВУ рендера — корректность держится на дисциплине,
  unit-гарантий нет (у нас — инвариант front==next, проверяемый fuzz'ем).
- gocui-наследие lazygit — проект уже ушёл на tcell; брать свежий путь.
- Материализованный список изменений ratatui — лишняя аллокация на кадр.

---

## 7. Сводка перфа/аллокаций ядра

| Путь | Время | Аллокации/кадр |
|---|---|---|
| Idle-кадр (hash-skip, 200×50) | ~0.05 ms | 0 (8 байт sync-парных CSI в буфер) |
| Типичный токен-кадр (300 клеток) | 0.5–1 ms | 0 |
| Полный repaint 400×120 | ~6 ms worst-case | 0–1 (рост Writer-буфера) |
| Layout solve ≤20 панелей | <0.01 ms | 0 |
| Resize 200×50 (без роста cap) | 3–6 ms | 0 |
| Память ядра steady-state (200×50) | ~0.39 MB: 2×160 КБ буферы + хэши + Writer 64 КБ; 400×120 → ~1.6 MB | — |

Оценка трудозатрат ядра: Cell/ScreenBuffer 2 дн., DiffEngine+AnsiWriter 2.5,
планировщик кадров/интеграция с RunInteractiveAsync 1.5, layout solver 2,
focus/mouse routing 1, resize 1, golden+fuzz тесты 2 → **~12 дн.** — внутри
оценки фазы 2 из §8.5 (10–14 дн.).

## 8. Риски и тесты

| Риск | Митигация |
|---|---|
| FrameHint пропустил инвалидацию → застывшие клетки | hints только поверх полного скана; Debug-paranoid assert; fallback ≥25% площади |
| 2026 не поддержан (ConHost/старый VTE) | DECRQM-проба → просто без обёртки; атомарность даёт одиночный write |
| LOH-фрагментация от роста capacity | геометрический рост + debounce resize; Trim() по таймеру простоя |
| Wide-char артефакты (CJK/эмодзи) | правила §1.3 + перенесённые golden-тесты BufferDiff + fuzz-invariant `front==next` после flush |
| Шторм WINCH от tmux | debounce 50 мс + дедуп по размеру |

Тесты: (1) golden-byte — состояние VM → точная ANSI-последовательность кадра
(расширение существующего актива MVP); (2) fuzz — случайные мутации BACK,
инвариант «после flush FRONT == BACK»; (3) resize-матрица grow/shrink/same;
(4) bench-гейт BenchmarkDotNet: scan ≤1 ms @400×120.

## 9. Источники (проверено 25.08.2026)

- ratatui main: `ratatui-core/src/buffer/buffer.rs`, `buffer/diff.rs`,
  `terminal/{render,buffers,resize}.rs`; док «Rendering under the hood»
  (ratatui.rs/concepts/rendering/under-the-hood)
- tcell v3 (рендерер lazygit; go.mod lazygit: gdamore/tcell/v3 v3.4.1):
  `cell.go`, `tscreen.go` (draw/drawCell/resize/Show), `vt/mode.go` (PmSyncOutput=2026)
- Spectre.Console: `src/Spectre.Console/Live/{LiveRenderable,LiveDisplayRenderer}.cs`
- btop++: `src/btop_draw.cpp` (element-cache + redraw flags)
- Локальная база: consoleex-design.md §8.5 (фаза 2), specs/07-tui.md §13

---
CONSOLEEX CELL-DIFF ЯДРО: ДИЗАЙН ГОТОВ
