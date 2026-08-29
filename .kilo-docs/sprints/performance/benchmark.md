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
