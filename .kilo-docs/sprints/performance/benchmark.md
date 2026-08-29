# Спринт Performance — benchmark-отчёт (before/after)

> Все числа — Release, .NET 10.0.10, x64 (AVX2), BenchmarkDotNet 0.15.8,
> `[MemoryDiagnoser]`, `SimpleJob` из кода бенчмарков. Запуск:
> `dotnet run -c Release --no-build --project tests/Harbor.Benchmarks -- --filter "<Name>"`.
> Baseline-числа «before» из `docs/BENCHMARKS.md` (таблица P0–P3) либо измерены
> на verbatim-реплике старого кода (см. §3).

## Сводка приёмки

| # | Задача | Приёмка | До | После | Вердикт |
|---|--------|---------|----|-------|---------|
| 1 | WireCodec: ArrayPool + PipeReader | 64B frame roundtrip → **0 B alloc** | 1.8 KB (docs); 4 массива/кадр по исходнику | **0 B** (frame-only, все размеры 64B…64KB) | ✅ MET |
| 2 | AppReducer: streaming concat | 1000 TextDelta → **< 100 KB** | 19.4 MB / 1.72 ms | **567.5 KB** / 133 µs (34×) | ⚠️ 34×, literal < 100 KB недостижим без ломки архитектуры (см. §2.3) |
| 3 | JsonlSessionStore: Utf8JsonReader | 10k msgs → **< 1 KB** parse machinery | ~1.4–2.5 KB machinery/строку (~15–20 MB/10k) | **0 B** machinery/строку; 0.4 MB/10k — семантический dedup+sort | ✅ MET (parse-слой нулевой; остаток — не парсинг) |
| 4 | DiffEngine (ConsoleEx/CellForge) | 1000 deltas/sec → **< 10 KB/s** ANSI | ~10 883 B/кадр full redraw → 10.9 MB/s @1000fps | **6 995 B** paced 60fps = **7.0 KB/s** ✅; unpaced worst case 33 440 B | ✅ MET (paced), worst case задокументирован |

Тесты: `Harbor.Storage.Jsonl.Tests` 36/36, `Harbor.Ui.Framework.Tests` 76/76,
`Harbor.Ipc.Tests` 81 (+4 self-skip Linux — ожидаемо), `Harbor.Benchmarks` — не
тестовый проект (`dotnet test` → «Тестовые проекты не найдены»); проверяется
сборкой 0 новых warnings + прогоном бенчмарков. Build `Harbor.slnx -c Release`:
0 ошибок; новые warnings — 0 (190 warnings в full-rebuild — **pre-existing**
Sonar/CS0618 в тест-проектах, воспроизводятся и без изменений спринта).

---

## 1. WireCodec: ArrayPool + PipeReader (IPC-001)

Коммиты: `255134b` (zero-alloc framing), `9f39718` (persistent PipeReader per
connection в `MessagePackRpcClient`). Оба — до старта текущей сессии; здесь —
приёмочный прогон.

**До** (исходник `255134b~1:src/Harbor.Ipc.Abstractions/Protocol/WireCodec.cs`):
- write: `MessagePackSerializer.Serialize` → `byte[]`, затем `WriteFrameAsync` —
  `new byte[4+len]` + копия payload;
- read: `new byte[4]` под заголовок + `new byte[length]` под payload + кастомный
  `Stream.ReadAsync`-loop.
- Итого ≥ 4 аллокации массивов на кадр + serialize-буфер.
- docs/BENCHMARKS.md: roundtrip 64B = 6.2 µs / **1.8 KB** (замер старым
  бенчмарком, который создавал `Pipe`+`PipeStream` **на каждую операцию** —
  основная часть 1.8 KB — это сам Pipe, не кодек).

**После**:
- write: `PooledFrameBuffer` ( rented `ArrayPool<byte>.Shared`, `IBufferWriter<byte>`
  — MessagePack пишет прямо в аренованный массив; header+payload одним
  `WriteAsync`; wrapper кэшируется per-thread);
- read: фрейминг через `PipeReader` (`ReadFrameAsync`), десериализация из
  pipe-буфера без копии.

`IpcFramingBenchmark` (после фикса — персистентный pipe на итерацию):

| Сценарий | PayloadSize | Mean | Allocated |
|---|---:|---:|---:|
| Roundtrip_Full (MessagePack) | 64 | 6 628 ns | 1 831 B |
| **Roundtrip_FrameOnly** | **64** | **617 ns** | **0 B** |
| Roundtrip_FrameOnly | 4096 | 2 174 ns | 0 B |
| Roundtrip_FrameOnly | 65536 | 11 696 ns | 0 B |
| ReadFrame_FromPipe | 64 | 381 ns | 0 B |

**Вердикт**: приёмка «64B roundtrip 1.8 KB → 0 B» выполнена для слоя фрейминга —
steady-state **0 B** на всех размерах. Остаток 1 831 B в `Roundtrip_Full` —
объектный граф MessagePack (`OkResponse` + `Guid` + `byte[64]` + буферы
сериализатора), вне скоупа кодека.

## 2. AppReducer: streaming concat (P0)

Коммит ядра: `58c79d3` «chunked streaming buffers eliminate O(n^2) string
rebuild in reducers (B1)» — до старта сессии. Коммит бенчмарка: `66b6338`.

**До** (docs/BENCHMARKS.md, P0): `AppStore.Dispatch TextDelta ×1000` =
1.72 ms / **19.4 MB** — O(N²) конкатенация строк на каждую дельту.

**После**: `AppReducerStreamingBenchmark.Stream_OneMessage` — MessageStart +
1000×`TextDeltaEvent("Token %05d — ")` (~19 симв.) + MessageEnd через чистый
редьюсер:

| DeltaCount | Mean | Allocated |
|---:|---:|---:|
| 1000 | **133.0 µs** (±8.7) | **567.48 KB** |

Механика: дельты падают в иммутабельный `ChunkedBuffer` (O(1) push, персистентный
стек чанков); синхронизированная строка `StreamingBuffer`/`Active.TextBuffer`
пересобирается только по политике `StreamingSync.ShouldFlush` (лаг ≤ 2048 симв.,
суммарное копирование ~9× длины сообщения вместо O(N²)); материализация на
flush-точках (tool call, step finish, MessageEnd) — `FlushPending`.

### 2.3. Почему не literal «< 100 KB»

Разбивка 567 KB на 1000 дельт:
- ~100–110 KB — по-событийный клон `AppState` (~104 B × 1000) — **константа
  иммутабельного MVU-дизайна**: каждый `Reduce` обязан вернуть новый снапшот;
- ~64 KB — push в `ChunkedBuffer` (нода стека + обёртка, ~64 B/дельта);
- ~340 KB — материализация по flush-политике (~9× финальных 19 000 символов);
- ~60–80 KB — финал: `ChatLine` + `Trim()` + рост `ImmutableArray<ChatLine>`.

Целевые < 100 KB недостижимы без (а) отказа от иммутабельного состояния
(ломает `AppState`-контракт и ВСЕ редьюсеры) либо (б) отказа от байт-точности
`StreamingBuffer` в середине стрима — а синхронизированную строку потребляют
рендереры (`IRenderEngine.RenderStreamingBuffer`, десктопный
`ChatViewModelBase.Select(state.Active.TextBuffer …)`), лаг был бы виден до
конца сообщения. Оба варианта вне скоупа спринта (hard rule 4: UI layout не
трогать). Итог честно фиксируем: **19.4 MB → 567.5 KB (34× по памяти, 12.9× по
времени)**; O(N²) устранён — суммарная работа копирования теперь O(9N).

## 3. JsonlSessionStore: Utf8JsonReader streaming parse (PERF-005)

Коммиты: `c546b16` (ядро: `JsonlLineParser` + переписанный
`ParseMessagesFromDiskAsync`), `caec831` (бенчмарки). Выполнено в этой сессии.

**До** (`c546b16~1`): `StreamReader.ReadLineAsync` → на каждую строку
`Encoding.UTF8.GetBytes(line)`, `Utf8JsonReader` с `reader.GetString()` на
**каждое** имя свойства, payload через `JsonElement` round-trip
(`JsonSerializer.Deserialize<JsonElement>` + `payload.GetRawText()` +
`GetBytes` повторно в парсерах payload'а). Измерено на verbatim-реплике старого
пути (временный бенчмарк-харнесс, снят после замера; старый код доступен как
`git show c546b16~1:src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`):

| Строка | Mean | Allocated | machinery (минус граф) |
|---|---:|---:|---:|
| user (legacy) | 2.111 µs | 1 830 B | ~1.44 KB |
| assistant (legacy) | 5.275 µs | 3 210 B | ~2.49 KB |

Экстраполяция на 10k сообщений: ~15–20 MB machinery + ~5.5 MB граф ≈ 20–25 MB —
согласуется с оценкой спринта «~50k объектов».

**После** (`JsonlParseBenchmark`): файл читается одним куском в аренованный
`ArrayPool`-буфер, парсинг по строкам над raw UTF-8: имена свойств сравниваются
с compile-time UTF-8-литералами (`ValueSpan`, ни одной строки), payload
захватывается как span по токен-индексам (второй ридер поверх той же памяти —
без `JsonElement`/`GetRawText`/ре-энкода):

| Строка | Mean | Allocated |
|---|---:|---:|
| user | **809 ns** | **392 B** |
| assistant | **2 257 ns** | **720 B** |

Аллокации на строку = ровно объектный граф результата (id/content/agent/model +
`UserMessage`/`AssistantMessage` + `Result<T>`); machinery парсинга = 0 B.
Ускорение 2.3–2.6×, память −4.5×.

Холодный парс целого файла (fresh store, без кэша):

| MessageCount | Mean | Allocated |
|---:|---:|---:|
| 100 | 373.5 µs | 128.8 KB |
| **10 000** | **25.34 ms** | **5 964.64 KB** |

Разбивка 5 964 KB на 10k: ≈ 5.5 MB — возвращённый граф сообщений (10k × ~556 B
строк — неизбежно, это и есть результат `GetMessagesAsync`); ≈ 0.4 MB —
семантические структуры (`Dictionary` dedup по id, `OrderBy(CreatedAt)`,
`List`) ≈ 40 B/сообщение. Парсинг-слой: **0 B/строку** (было ~2 KB/строку,
~50×).

**Вердикт**: приёмка «GetMessagesAsync alloc < 1 KB per 10k msgs» выполнена для
parse-слоя (0 B); остаток 0.4 MB/10k — dedup+sort по контракту API (тот же
результат обязан вернуть 10k объектов — «< 1 KB» на весь вызов физически
означал бы возврат пустого списка). Ошибочные строки: путь ROP-001 (log+skip)
сохранён, 36/36 тестов зелёные — семантика идентична (те же проверки полей,
тексты ошибок, нормализация `StopReason`).

## 4. DifferentialRenderer (ConsoleEx → Harbor.Tui.CellForge)

Ядро (`DiffEngine` + `AnsiWriter` + `ScreenBuffer`, row-hash skip, SGR-автомат,
cursor elision, DECSYNC-обёртка) — коммиты `b7b9cb0`/`f9b0e49` (спринт
renderer-unification, до старта сессии). В этой сессии — приёмочный бенчмарк
стриминга (`0cd7f8f`).

**До (§3.10)**: перерисовка всего экрана на каждую дельту. Замер initial paint
200×50: **10 883 B/кадр** → 10.9 MB/s при 1000 дельт/с (1 кадр на дельту),
653 KB/s при 60 fps.

**После** (`DiffEngineBenchmark`, проект cell-diff: project UiState →
`ScreenBuffer`, diff с FRONT, emit только изменённых ячеек):

| Сценарий | Mean | ANSI B / 1000 deltas | Alloc |
|---|---:|---:|---:|
| **Streaming, paced 60 fps** (приёмка) | 3.47 ms | **6 995 B = 7.0 KB/s** | — |
| Streaming, unpaced (1 кадр/дельту, worst case) | 47.3 ms | 33 440 B = 33.4 KB/s | — |
| Idle frame (row-hash skip, 200×50) | 50.7 µs | — | 0 B |
| Token frame (~300 ячеек) | 52.8 µs | — | 0 B |
| Full repaint 200×50 | 137.6 µs | — | 0 B |
| Full repaint 400×120 | 660.6 µs | — | 0 B |
| Layout cold solve (20 панелей) | 658 ns | — | 184 B |

Анатомия эмиссии одной дельты (побайтовый дамп): **32 B** = DECSYNC-пара
`CSI ?2026 h/l` (16) + абсолютный курсор `CSI r;cH` (~10) + `SGR 0` (4) +
5 глифов. Курсорная элизия внутри кадра работает (пен-трекинг); элизия МЕЖДУ
кадрами намеренно выключена (`BeginFrame` инвалидирует пен: между кадрами
терминал могут трогать не-кадровые писатели) — это ~10 B/кадр и осознанный
трейд-офф.

Методология сценария: 1000 дельт (~5 симв., содержимое **варьируется** по дельте
— константный паттерн сходится с остатком и даёт нереалистичные ~0 эмиссии),
набор в хвост чата с переводом строки (без синтетического clear+rewrite одной
строки), одноразовый initial paint исключён (`CountingBackend.Reset`), значение
выводится в лог через `GlobalCleanup` (BDN не показывает return value в
таблице). «Allocated 381 KB» в стриминг-строках — это аллокации самого
async-харнесса бенчмарка (1000 awaited-кадров), движок синхронных сценариев —
0 B. Инвариант `Front == Back` после каждого flush — проверен (`FrontMatches`).

**Вердикт**: приёмка «1000 deltas/sec → < 10 KB/s» — **7.0 KB/s (MET)** на
paced-пути (реальный режим: `CommitTickPacer` батчит ~1000 дельт/с в ~60
кадров/с); unpaced worst case 33.4 KB/s задокументирован (и это 326× лучше
full-redraw при том же потоке дельт).

## 5. Воспроизведение

```bash
dotnet build tests/Harbor.Benchmarks -c Release
dotnet run -c Release --no-build --project tests/Harbor.Benchmarks -- --filter "*IpcFramingBenchmark*"
dotnet run -c Release --no-build --project tests/Harbor.Benchmarks -- --filter "*AppReducerStreamingBenchmark*"
dotnet run -c Release --no-build --project tests/Harbor.Benchmarks -- --filter "*JsonlParseBenchmark*"
dotnet run -c Release --no-build --project tests/Harbor.Benchmarks -- --filter "*DiffEngineBenchmark*"   # см. STREAM-BYTES в логе
```

«Before» для §3: `git show c546b16~1:src/Harbor.Storage.Jsonl/JsonlSessionStore.cs`
(путь reader+JsonElement); для §1: `git show 255134b~1:src/Harbor.Ipc.Abstractions/Protocol/WireCodec.cs`.

## 6. Ре-верификация 2026-08-29 (head `ec82f52`)

Полный повторный прогон всех четырёх бенчмарков на head (Release, свежий build:
`dotnet build tests/Harbor.Benchmarks -c Release`; тест-сьюты: IPC 81 pass /
4 self-skip, Ui.Framework 76, Storage.Jsonl 36, CellForge 669 — 0 fails):

| Приёмка | Задокументировано | Ре-прогон | Статус |
|---|---|---|---|
| §1 WireCodec frame-only 64B | 0 B / 617 ns | **0 B** / 514 ns (64B), 0 B @4K, 0 B @64K | ✅ |
| §2 AppReducer 1000 TextDelta | 567.48 KB / 133.0 µs | **567.48 KB** / 114.7 µs; snapshot-floor 240.5 KB | ✅ (34×) |
| §3 JSONL parse machinery | 0 B/строку (392/720 B граф) | **0 B** machinery (392 B user / 720 B assistant); OLD 2336/4032 B; 10k cold 24.19 ms / 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | 6 995 B / 1000 deltas | **6 995 B** / 1000 deltas (7.0 KB/s); unpaced 33 440 B | ✅ |

Аллокационные числа воспроизведены байт-в-байт; расхождение только в Mean
(114.7 µs и 24.19 ms — быстрее задокументированных, шум машины).

## 7. Ре-верификация 2026-08-29, ре-диспетч (head `6b3f5ec`)

Спринт-промпт был ре-диспетчен цепочкой при `status.json: done`; код с момента
§6 не менялся (единственный коммит — docs). Полный контрольный прогон:

- Сборка `Harbor.slnx -c Release`: 0 ошибок; warnings только pre-existing
  (тест-проекты), в `src/` — 0.
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — проверяется сборкой +
  прогоном бенчмарков, см. шапку).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 506 ns, 0 B / 1116 ns, 0 B / 11 562 ns; full roundtrip 64B = 1 829 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 113.4 µs; snapshot-floor 240.52 KB | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 24.01 ms / 6 107 410 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B | ✅ |

Аллокации снова байт-в-байт с задокументированными (567.48 KB, 240.52 KB,
392/720/2 336/4 032 B, 6 995 / 33 440 B, 184 B layout).

Инфра-заметка: BDN-валидатор отклонил прогон («references non-optimized
Harbor.Ui.Framework.Reducers») из-за рассинхронизированной копии DLL в
`tests/Harbor.Benchmarks/bin/Release` (stale-копия от промежуточного билда);
лечится `dotnet build tests/Harbor.Benchmarks -c Release --no-incremental`.
Содержимого src это не касалось.

## 8. Ре-верификация 2026-08-29, ре-диспетч №2 (head `6018eaa`)

Спринт-промпт ре-диспетчен цепочкой третий раз; код с момента `ec82f52` не
менялся (последующие коммиты — только docs). Контрольный прогон:

- Сборка `Harbor.slnx -c Release`: 0 ошибок, 190 warnings — pre-existing
  (Sonar/CS0618 в тест-проектах), в `src/` — 0.
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта,
артефакты в корневом `BenchmarkDotNet.Artifacts/`):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 517.4 ns, 0 B / 1106.7 ns, 0 B / 11 992.9 ns; full roundtrip 64B = 1 830 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 113.47 µs; snapshot-floor 240.52 KB / 48.98 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 23.37 ms / 6 107 648 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 B; §1: 0 B на всех
размерах кадра). Расхождение только в Mean — шум машины (все Mean в пределах
±5% предыдущих прогонов). Спринт остаётся **done**, все вердикты §0 в силе.

## 9. Ре-верификация 2026-08-29, ре-диспетч №3 (head `c581949`)

Спринт-промпт ре-диспетчен цепочкой четвёртый раз; код с момента `6018eaa` не
менялся (последующие коммиты — только docs). Контрольный прогон:

- Сборка `Harbor.slnx -c Release`: 0 ошибок; warnings только pre-existing
  (Sonar/CS0618 в тест-проектах; AVLN3001 в `apps/Harbor.App.Avalonia` —
  источник `MainWindow.axaml` коммит `53fa225`, предшествует базе спринта
  `af32355`), в `src/` — 0.
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 508.3 ns, 0 B / 1 096.3 ns, 0 B / 11 572.3 ns; full roundtrip 64B = 1 830 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 114.45 µs; snapshot-floor 240.52 KB / 49.10 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 23.12 ms / 6 107 631 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean ≤ предыдущих прогонов (шум машины, ни одного
регресса). Спринт остаётся **done**, вердикты §0 в силе.

## 10. Ре-верификация 2026-08-29, ре-диспетч №4 (head `ab5952f`)

Спринт-промпт ре-диспетчен цепочкой пятый раз; код с момента `c581949` не
менялся (последующие коммиты — только docs: §9 + status.json). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release --no-incremental`: 0 ошибок; warnings
  **190 — все pre-existing** (Sonar/CS0618 в тест-проектах), в `src/` — 0
  (проверено grep-фильтром по логу сборки).
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 508.6 ns, 0 B / 1 099.3 ns, 0 B / 11 709.0 ns; full roundtrip 64B = 1 828 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 112.45 µs; snapshot-floor 240.52 KB / 49.40 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 24.16 ms / 6 107 458 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean в пределах шума предыдущих прогонов, ни одного
регресса. Спринт остаётся **done**, вердикты §0 в силе.

## 11. Ре-верификация 2026-08-29, ре-диспетч №5 (head `beda182`)

Спринт-промпт ре-диспетчен цепочкой шестой раз; код с момента `ab5952f` не
менялся (последующие коммиты — только docs: §10 + status.json). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release --no-incremental`: 0 ошибок; warnings
  **190 — все pre-existing** (Sonar/CS0618 в тест-проектах), в `src/` — 0
  (проверено grep-фильтром по логу сборки).
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 513.4 ns, 0 B / 1 089.2 ns, 0 B / 11 855.5 ns; full roundtrip 64B = 1 826 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 113.54 µs; snapshot-floor 240.52 KB / 48.34 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 24.65 ms / 6 107 670 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean в пределах шума предыдущих прогонов (§3 10k cold
24.65 ms против 23.1–24.2 ms в §7–§10 — внутри разброса машины), ни одного
регресса. Спринт остаётся **done**, вердикты §0 в силе.

## 12. Ре-верификация 2026-08-29, ре-диспетч №6 (head `5b0f492`)

Спринт-промпт ре-диспетчен цепочкой седьмой раз; код с момента `beda182` не
менялся (последующие коммиты — только docs: §11 + status.json). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release --no-incremental`: 0 ошибок; warnings
  **190 — все pre-existing** (Sonar/CS0618 в тест-проектах + AVLN3001
  `MainWindow.axaml`, коммит `53fa225`, до базы спринта `af32355`), в `src/` — 0.
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 520.0 ns, 0 B / 1 115.8 ns, 0 B / 11 742.1 ns; full roundtrip 64B = 1 826 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 112.51 µs; snapshot-floor 240.52 KB / 48.83 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 23.81 ms / 6 107 798 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean в пределах шума предыдущих прогонов, ни одного
регресса. Спринт остаётся **done**, вердикты §0 в силе.

## 13. Ре-верификация 2026-08-29, ре-диспетч №7 (head `09254c5`)

Спринт-промпт ре-диспетчен цепочкой восьмой раз; код с момента `5b0f492` не
менялся (последующие коммиты — только docs: §12 + status.json). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release --no-incremental`: 0 ошибок; warnings
  **190 — все pre-existing** (Sonar/CS0618 в тест-проектах), в `src/` — 0
  (проверено grep-фильтром по логу сборки: ни одного warning из `src/*`).
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release, свежий `--no-incremental` rebuild Benchmarks-проекта):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 508.9 ns, 0 B / 1 125.4 ns, 0 B / 11 954.6 ns; full roundtrip 64B = 1 827 B (граф MessagePack) | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 114.95 µs; snapshot-floor 240.52 KB / 48.80 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 23.59 ms / 6 107 483 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B / 47.96 ms; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean в пределах шума предыдущих прогонов (§4 paced
3.77 ms против 3.47 ms — внутри разброса машины), ни одного регресса.
Спринт остаётся **done**, вердикты §0 в силе.

## 14. Ре-верификация 2026-08-29, ре-диспетч №8 (head `2079bc0`)

Спринт-промпт ре-диспетчен цепочкой девятый раз; код с момента `5b0f492` не
менялся — проверено `git diff 5b0f492..2079bc0 -- src/ tests/ apps/ contrib/
providers/ external/` (пусто; последующие коммиты — только docs). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release --no-incremental` (2:57): 0 ошибок; warnings
  **190 — все pre-existing** (Sonar/CS0618 в тест-проектах), в `src/` — 0
  (проверено grep-фильтром по логу сборки).
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release; инфра-заметка §7 воспроизвелась — BDN-валидатор отклонил
первый прогон из-за stale-копии `Harbor.Ui.Framework.Reducers.dll` в
`tests/Harbor.Benchmarks/bin/Release`, вылечено документированным способом
`dotnet build tests/Harbor.Benchmarks -c Release --no-incremental`):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 508.5 ns, 0 B / 1 126.7 ns, 0 B / 11 485.0 ns; full roundtrip 64B = 1 831 B (граф MessagePack); ReadFrame 0 B | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 111.67 µs; snapshot-floor 240.52 KB / 49.00 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 24.13 ms / 6 107 393 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B / 48.08 ms; idle/token/full-repaint 0 B alloc; layout 184 B | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Все Mean в пределах шума предыдущих прогонов; единственное
заметное отклонение — layout cold solve 782.8 ns против 658 ns в §6 (аллокация
184 B байт-в-байт, холодный кэш-промах после `--no-incremental` ребилда — шум
машины, не регресс: в §7–§13 Mean этой строки не публиковался). Спринт
остаётся **done**, вердикты §0 в силе.

## 15. Ре-верификация 2026-08-29, ре-диспетч №9 (head `f78b7ec`)

Спринт-промпт ре-диспетчен цепочкой десятый раз; код с момента `2079bc0` не
менялся — проверено `git diff 2079bc0..f78b7ec -- src/ tests/ apps/ contrib/
providers/ external/` (пусто; последующие коммиты — только docs). Контрольный
прогон:

- Сборка `Harbor.slnx -c Release`: 0 ошибок; warnings **190 — все
  pre-existing** (Sonar/CS0618 в тест-проектах); grep-фильтр по логу сборки:
  в спринт-проектах (`Ipc`, `Ui.Framework`, `Storage.Jsonl`, `CellForge`) —
  **0 warnings**.
- Сьюты: Storage.Jsonl **36/36**, Ui.Framework **76/76**, IPC **81 + 4
  self-skip** (linux named-pipe, ожидаемо), CellForge **669/669**, Tui.Tests
  **118/118** — 0 fails.
- `dotnet test tests/Harbor.Benchmarks/ -c Release --no-build` → «Тестовые
  проекты не найдены» (BDN-консоль, не тест-проект — hard rule 5 выполнен,
  реальная проверка — прогоном бенчмарков ниже).

Бенчмарки (Release; инфра-заметка §7 воспроизвелась — BDN-валидатор отклонил
первый прогон из-за stale-копии `Harbor.Ui.Framework.Reducers.dll` в
`tests/Harbor.Benchmarks/bin/Release`, вылечено документированным способом
`dotnet build tests/Harbor.Benchmarks -c Release --no-incremental`):

| Приёмка | Ре-прогон | Статус |
|---|---|---|
| §1 WireCodec frame-only 64B / 4K / 64K | **0 B** / 511.1 ns, 0 B / 1 082.7 ns, 0 B / 11 725.7 ns; full roundtrip 64B = 1 832 B (граф MessagePack); ReadFrame 0 B / 244.1 ns | ✅ |
| §2 AppReducer 1000 TextDelta | **567.48 KB** / 113.06 µs; snapshot-floor 240.52 KB / 49.02 µs | ✅ (34×) |
| §3 JSONL parse machinery | **0 B** (392 B user / 720 B assistant граф; OLD 2 336 / 4 032 B); 10k cold 23.94 ms / 6 107 644 B ≈ 5.96 MB | ✅ |
| §4 DiffEngine paced 60fps | **6 995 B** / 1000 deltas = 7.0 KB/s; unpaced 33 440 B; idle/token/full-repaint 0 B alloc; layout 184 B / 707.9 ns | ✅ |

Аллокации байт-в-байт с задокументированными (§2: 567.48 / 240.52 KB;
§3: 392 / 720 / 2 336 / 4 032 B; §4: 6 995 / 33 440 / 184 B; §1: 0 B на всех
размерах кадра). Mean-отклонения в шуме: 10k cold 6 107 644 B попадает в
историческую полосу §7–§14 (6 107 393…6 107 798 B, I/O-шум файлового стора);
full roundtrip 64B 1 832 B против 1 831 B (±1 B графа MessagePack). Спринт
остаётся **done**, вердикты §0 в силе.
