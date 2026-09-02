Ты работаешь в репозитории Harbor-Harness (.NET 10). Спринт Performance.

## Context
Harbor позиционируется как быстрый AI harness, но в коде остались 4 "убийцы" производительности,
которые никто из конкурентов не имеет — и которые мы можем убрать.

EventBus scrollback ring buffer и GetStatsAsync cache уже починены (v1.5/v2), но WireCodec всё ещё
аллоцирует new byte[] на каждый фрейм (IPC-001, WireCodec.cs:118-156), AppReducer тратит 19.4 MB
на 1000 TextDelta (O(N²) string concatenation, P0 bottleneck per docs/BENCHMARKS.md),
JsonlSessionStore парсит каждую строку через JsonDocument.Parse (~10k allocations per GetMessagesAsync,
PERF-005), а SpectreTUI перерисовывает весь экран при каждом дельте.

Цель: zero-alloc budget на hot path + streaming diff rendering.

## Tasks (выполняй по порядку)

1. **WireCodec: ArrayPool + PipeReader.**
   Замени new byte[length] на ArrayPool<byte>.Shared.Rent, используй PipeReader для фреймирования
   вместо кастомного Stream.ReadAsync loop.
   Acceptance: WireCodec roundtrip 64B alloc drops from 1.8 KB → 0 B (zero alloc на steady-state).
   См. BENCHMARKS.md P1 EventBroadcaster.

2. **AppReducer: streaming concat zero-alloc.**
   Замени StringBuilder concatenation на List<TextDelta> с materialize-on-MessageEnd.
   Acceptance: 1000 TextDelta → < 100 KB alloc (сейчас 19.4 MB).
   Benchmark: dotnet run -c Release --project tests/Harbor.Benchmarks --filter "*AppReducer*".

3. **JsonlSessionStore: Utf8JsonReader streaming parse.**
   Замени JsonDocument.Parse(line) на pooled-buffer Utf8JsonReader с MemoryPool<byte>.
   За 10k-message session GetMessagesAsync аллоцирует ~50k объектов — цель: < 1 KB alloc на whole-file parse.
   Acceptance: GetMessagesAsync alloc < 1 KB per 10k msgs.

4. **DifferentialRenderer для ConsoleEx.**
   Создай CellBuffer + DiffEngine: project UiState → CellBuffer, diff с previous,
   emit только changed cells как ANSI cursor-move + set-color + write-char.
   Acceptance: 1000 streaming deltas/sec → < 10 KB/s ANSI output (сейчас ~1920 cells × full redraw, §3.10).

## Hard Rules
1. Один коммит = 1–2 файла. Не собирай гигантский дифф.
2. Zero-warning policy: dotnet build -c Release должен проходить с 0 warnings. Любой новый warning — баг.
3. Все изменения в hot path должны быть backed benchmark'ами. Без цифр — без merge.
4. Не трогай AgentLoop / storage schema / UI layout в этом спринте — только IPC codec, reducer, JSONL parse, renderer diff.
5. После каждого коммита: dotnet test tests/Harbor.Benchmarks/ -c Release --no-build.

## Deliverables
- WireCodec ArrayPool + PipeReader rewrite (zero alloc frame path)
- AppReducer streaming concat < 100 KB per 1000 deltas
- JsonlSessionStore Utf8JsonReader parse < 1 KB per 10k msgs
- DifferentialRenderer prototype for ConsoleEx (cell-diff ANSI output)
- benchmark report с before/after цифрами в `.kilo-docs/sprints/performance/benchmark.md`
- status.json + HTML-отчёт `.kilo-docs/sprints/performance/report.html`
