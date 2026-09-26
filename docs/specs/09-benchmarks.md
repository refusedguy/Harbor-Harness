# 09 — Бенчмарки и анализ памяти

> Документ: анализ потребления памяти существующими харнессами (kilo, opencode, pi, crush), сравнение с .NET target, конкретные цифры и метрики. Цель — понять, где_node_инструменты теряют память, и как harbor будет лучше.

## 1. Измерения существующих инструментов

### 1.1. kilocode (`kilo serve`)

**Условия измерения**: свежезапущенный `kilo serve` без активной сессии, после первичного listing'а моделей.

**RSS**: ~700 МБ – 1.1 ГБ (по данным пользователя и анализу кода).

**Разбивка по компонентам** (оценка из репо-анализа):

| Component | RSS | Источник |
|---|---|---|
| Bun runtime baseline | 50–80 МБ | Bun сам по себе + V8/JSC heap |
| SQLite page cache (`cache_size=-64000`) | ~64 МБ | `db.run("PRAGMA cache_size = -64000")` в `storage/db.ts` |
| tree-sitter WASM + grammars | 30–60 МБ | `web-tree-sitter`, `tree-sitter-bash`, `tree-sitter-powershell`, `tree-sitter-wasms` |
| `@silvia-odwyer/photon-node` (WASM) | 20–40 МБ | Image processing |
| `@lydell/node-pty` + `bun-pty` (native) | 10–20 МБ | PTY для bash tool |
| `@parcel/watcher` (native) | 10–20 МБ | File watching, 8 платформ-вариантов |
| `@aws-sdk/credential-providers` | 30–50 МБ | Bedrock auth chain (большой JS deps) |
| Vercel AI SDK + 20 providers (после listing) | 100–200 МБ | `@ai-sdk/*` lazy-imported, но грузятся |
| Effect-TS runtime + OpenTelemetry | 30–60 МБ | `effect`, `@effect/platform-node`, `@effect/opentelemetry`, `@opentelemetry/*` |
| SolidJS TUI + shiki + marked | 50–100 МБ | `@opentui/solid`, `solid-js`, `shiki`, `marked`, `marked-shiki` |
| LanceDB (vector DB) | 50–150 МБ | `@kilocode/kilo-memory` Rust bindings |
| Hono HTTP server + mDNS | 10–30 МБ | `hono`, `bonjour-service`, `ws` |
| Session history caching (active session) | 10–100 МБ | `InstanceState` cache, in-memory message list |
| **TOTAL** | **~450 МБ – 1.1 ГБ** | |

**Подтверждение 1 ГБ**: heap-snapshot auto-trigger в opencode (форк base kilocode) стоит на **2 ГБ** — `LIMIT = 2 * 1024 * 1024 * 1024` в `cli/heap.ts`. То есть разработчики явно ожидают 1+ ГБ в normal operation.

### 1.2. opencode (anomalyco/opencode)

Аналогично kilocode — это форк opencode, ~80% кода общее. RSS ~500 МБ – 1 ГБ.

Дополнительно к kilocode:
- LSP integration (`vscode-jsonrpc`, custom `lsp/server.ts` — 30+ language servers).
- Каждый запущенный LSP server — отдельный subprocess 30–150 МБ.

### 1.3. pi-agent (earendil-works/pi)

**Легче**, чем kilo/opencode — нет Effect-TS, нет SolidJS TUI (свой pi-tui), нет LanceDB.

**Stack**:
- TypeScript + Node.js 22.19+ (или Bun binary).
- `@earendil-works/pi-tui` — собственный TUI (differential rendering).
- `typebox` — schema validation (легче, чем Zod).
- 36 LLM провайдеров через lazy imports.
- JSONL sessions (не SQLite).

**Оценка RSS**: ~150–300 МБ в active session.

**Memory discipline**:
- Lazy imports everywhere.
- JSONL вместо SQLite — нет page cache overhead.
- Bun binary option — single-file executable.
- `partial-json` для streaming JSON.
- Native addons только для win32 console mode и darwin key events.

pi — **эталон** по memory discipline среди TS харнессов. Но даже он не достигает <100 МБ.

### 1.4. crush (charmbracelet/crush)

**Stack**: Go 1.26 + Bubbletea v2 + Lipgloss v2 + Glamour v2.

**Binary**: ~25 МБ (stripped, no CGO — modernc SQLite pure Go).

**RSS idle**: ~50–150 МБ (оценка по коду + типичное поведение Go binaries).

**Активная сессия**: ~100–200 МБ.

**Преимущества**:
- Single static binary, no runtime dependencies.
- Нет V8/JSC overhead.
- Нет Node.js stdlib overhead.
- Нет npm dependencies tree.
- Modernc SQLite — pure Go, no CGO, embedded in binary.

**Почему ~150 МБ, а не 30**:
- Go runtime GC overhead.
- `bubbletea` v2 — TUI framework с собственным state model.
- `charm.land/fantasy` — LLM abstraction.
- `charm.land/catwalk` — provider DB.
- `charm.land/ultraviolet` — screen buffer.
- Каждый MCP subprocess добавляет 20–80 МБ.

## 2. .NET NativeAOT target — ожидаемые метрики

### 2.1. Binary size

| Build variant | Размер |
|---|---|
| Release (AOT, all optimizations, no Spectre) | **~5–7 МБ** |
| Release (AOT, with Spectre.Console rendering) | ~10–12 МБ |
| Release (AOT, no optimizations) | ~15 МБ |
| Debug (JIT, self-contained) | ~80 МБ |

Сравнение:
- kilocode (Bun bundled): ~80 МБ
- opencode (Bun bundled): ~80 МБ
- pi-agent (Bun binary): ~50 МБ
- crush (Go binary): ~25 МБ
- **harbor (AOT, optimized): ~5–7 МБ** ← цель

### 2.2. RSS (Resident Set Size)

| Component | JIT (dev) | NativeAOT (release) |
|---|---|---|
| .NET runtime | ~50 МБ | ~10 МБ |
| GC heap (idle) | ~20 МБ | ~5 МБ |
| BCL (Base Class Library) | ~10 МБ | ~5 МБ |
| Microsoft.Extensions.* | ~5 МБ | ~2 МБ |
| System.Text.Json | ~3 МБ | ~1 МБ |
| HttpClient | ~3 МБ | ~1 МБ |
| Microsoft.Data.Sqlite | ~3 МБ | ~2 МБ |
| e_sqlite3 (native) | ~1 МБ | ~1 МБ |
| ConsoleAppFramework | ~1 МБ | ~0.5 МБ |
| Spectre.Console (optional) | ~5 МБ | ~2 МБ |
| Harbor code | ~5 МБ | ~2 МБ |
| **TOTAL (no Spectre)** | **~100 МБ** | **~30 МБ** |
| **TOTAL (with Spectre)** | ~110 МБ | ~35 МБ |

Сравнение idle RSS:
- kilocode: ~700 МБ
- opencode: ~500 МБ
- pi-agent: ~150 МБ
- crush: ~100 МБ
- **harbor (AOT, optimized): ~30 МБ** ← цель

### 2.3. Cold start

| Tool | Cold start |
|---|---|
| kilocode | ~500–1500 ms |
| opencode | ~500–1500 ms |
| pi-agent | ~300–600 ms |
| crush | ~30–80 ms |
| **harbor (AOT)** | **~30–50 ms** ← цель |

### 2.4. Active session RSS (10K messages)

| Tool | RSS |
|---|---|
| kilocode | ~1 ГБ |
| opencode | ~800 МБ |
| pi-agent | ~300 МБ |
| crush | ~200 МБ |
| **harbor (AOT, SQLite paged)** | **~80 МБ** ← цель |

## 3. Метрики runtime operations

### 3.1. Streaming latency (token-to-screen)

| Component | kilo/opencode | harbor (target) |
|---|---|---|
| SSE byte → parsed event | ~5 ms | ~1 ms |
| Event → AgentMessage partial | ~3 ms | <1 ms |
| Partial → TUI render | ~10 ms | <5 ms |
| Render → terminal flush | ~5 ms | <2 ms |
| **Total** | **~23 ms** | **<10 ms** |

### 3.2. Tool execution overhead

| Operation | kilo (Bun/Node) | harbor (target) |
|---|---|---|
| Parse tool args | ~2 ms | <0.5 ms (source-gen JSON) |
| Permission check | ~5 ms | <1 ms |
| `read` (10 KB file) | ~5 ms | <1 ms |
| `bash` (simple command) | ~50 ms (process spawn) | ~5 ms (persistent shell) |
| `edit` (single replacement) | ~10 ms | <2 ms |
| `grep` (ripgrep, 1000 files) | ~20 ms | ~20 ms (same — subprocess) |
| Persist to DB | ~10 ms | <5 ms (Dapper.AOT) |

### 3.3. Compaction latency

| Operation | kilo (Effect-TS) | harbor (target) |
|---|---|---|
| Find cut point | ~20 ms | <5 ms |
| Generate summary (10K tokens) | ~3 s (LLM call) | ~3 s (LLM call — same) |
| Mark messages compacted (1000 parts) | ~100 ms | <50 ms (batch UPDATE) |
| Reload session from DB | ~200 ms | <50 ms (paged) |

LLM call — bottleneck, не можем оптимизировать. Остальное — ускорение 2–10×.

## 4. Microbenchmarks (planned)

### 4.1. Startup time

```bash
hyperfine --warmup 3 \
  'harbor --version' \
  'kilo --version' \
  'opencode --version' \
  'crush --version'
```

Target: harbor <50ms.

### 4.2. Memory

```bash
# Linux
/usr/bin/time -v harbor --version 2>&1 | grep "Maximum resident set size"

# macOS
ps -o rss,vsz -p $(pgrep harbor)

# Windows
Get-Process harbor | Select-Object WorkingSet64,PrivateMemorySize64
```

Target: <30 MB RSS.

### 4.3. Streaming throughput

```
Generate 10K tokens from a fake LLM stream (file replay), measure end-to-end time.
```

Target: 1000+ tokens/sec sustained without visible lag.

### 4.4. Tool execution

```
Run `read` on 1000 files, measure total time.
```

Target: <1 s total.

### 4.5. Session operations

```
- Create 1000 sessions, measure
- Load session with 10K messages, measure
- Compaction on 10K tokens, measure
```

## 5. Источники overhead в Node.js tools (детальный разбор)

### 5.1. V8/JSC heap baseline

- Node.js V8: ~30–50 МБ на старте (Bootstrap, built-in modules).
- Bun (JavaScriptCore): ~50–80 МБ (включая bundler, sqlite, fetch).

### 5.2. Module loading

- `require()` / `import` парсит и компилирует JS source.
- Большой dependency tree = много compiled code в heap.
- kilocode: ~600+ транзитивных dependencies.
- opencode: ~700+ dependencies.
- pi-agent: ~200 dependencies (меньше).

### 5.3. Native modules

- `node-pty`, `parcel/watcher`, `tree-sitter` (native bindings), `better-sqlite3` — каждый грузит `.node` файл, мапит native memory.
- WASM modules (`photon-node`, `tree-sitter-wasms`) — heap for compiled WASM + linear memory.

### 5.4. JIT-compiled code cache

V8/JSC компилирует hot functions в native code, хранит в code cache. Может занимать 20–50 МБ для большого JS-приложения.

### 5.5. SQLite page cache

- Default `cache_size=-2000` (2 МБ) — но kilocode ставит `-64000` (64 МБ).
- WAL file mmap'ed в память.

### 5.6. String duplication

- JS strings immutable, но operations создают copies.
- `JSON.stringify` + `JSON.parse` круглый путь создаёт много временных строк.
- LLM streaming: каждый token — временная string, garbage collector напрягается.

### 5.7. Promise / async overhead

- Каждое `async` function создаёт state machine, promise chain.
- V8 Promise pool — десятки МБ для активно async приложения.

### 5.8. Effect-TS overhead

- Effect-TS runtime — свой scheduler, fiber pool, context map.
- Layer composition создаёт граф зависимостей.
- Для маленьких приложений — overhead минимальный, для больших (как kilocode) — 30–60 МБ.

### 5.9. SolidJS reactivity

- SolidJS создаёт signals для каждого reactive value.
- TUI rendering через SolidJS — десятки МБ для сложного UI.

### 5.10. OpenTelemetry / Sentry

- `@opentelemetry/sdk-trace-node` — свой thread pool, buffer for spans.
- `@sentry/node` — exception capture, scope management.
- 10–30 МБ combined.

## 6. .NET NativeAOT — почему меньше

### 6.1. Нет runtime compilation

- AOT — весь код уже в native machine code.
- Нет V8/JSC, нет code cache.
- JIT только для fallback (отключён в AOT-mode).

### 6.2. Trimming

- Trimmer удаляет unused code.
- Если в `System.Text.Json` используется только 10% — остальное trimmed out.
- В Node.js — весь V8 + BCL loaded.

### 6.3. Static linking

- Все зависимости статически линкуются в binary.
- Нет shared library overhead.
- Нет DLL hell.

### 6.4. Source generators заменяют reflection

- JSON schema generation — compile-time.
- Configuration binding — compile-time.
- CLI dispatch — compile-time.
- Нет reflection metadata в heap.

### 6.5. Value types (structs)

- C# structs — stack-allocated, не heap.
- `record struct`, `readonly struct` — эффективные data carriers.
- В JS — все objects в heap.

### 6.6. GC efficiency

- .NET GC — server GC для throughput, workstation GC для low latency.
- Gen 0 collection — <1 ms.
- NativeAOT использует тот же GC, но меньше heap pressure (нет JS strings).

### 6.7. Memory-mapped files

- SQLite через `Microsoft.Data.Sqlite` — mmap I/O.
- File reads через `MemoryMappedFile` — zero-copy.
- В Node.js — `Buffer` allocates new memory.

## 7. Memory discipline rules для harbor

Чтобы реально достичь <30 МБ RSS, нужно соблюдать:

### 7.1. Lazy loading

- Провайдеры грузятся только при первом использовании.
- MCP servers connect on-demand (если `lazyConnect: true`).
- LSP servers spawn on first file open.
- Plugins register但不 initialize до первого вызова.

### 7.2. Streaming everywhere

- `IAsyncEnumerable<T>` для всех больших коллекций.
- LLM streaming через `IAsyncEnumerable<LLMEvent>`.
- Session messages — paged, не вся в memory.
- File reads — streaming (не `ReadAllText` для больших).

### 7.3. Object pooling

```csharp
public sealed class StringBuilderPool
{
    private readonly ConcurrentBag<StringBuilder> _pool = new();
    
    public StringBuilder Rent() => _pool.TryTake(out var sb) ? sb : new StringBuilder(1024);
    
    public void Return(StringBuilder sb)
    {
        sb.Clear();
        if (sb.Capacity > 16 * 1024) return;  // don't pool huge
        _pool.Add(sb);
    }
}
```

Используем для: log messages, JSON serialization, diff generation.

### 7.4. Span-based APIs

```csharp
// ✅ BAD: allocates string
public string ProcessJson(string json) { /* ... */ }

// ✅ GOOD: span-based, zero alloc
public void ProcessJson(ReadOnlySpan<char> json, ref Utf8JsonWriter writer) { /* ... */ }
```

### 7.5. Configuration objects — record structs

```csharp
// ✅ Stack-allocated when possible
public readonly record struct ToolCallId(string Value);
public readonly record struct SessionId(string Value);

// ✅ Heap-allocated but small
public sealed record Session(/* ... */);
```

### 7.6. Avoid `List<T>` for hot paths

```csharp
// ✅ For small fixed-size collections:
ImmutableArray<T> — stack-allocated when <8 elements.

// ✅ For streaming:
IAsyncEnumerable<T> — no materialization.

// ⚠️ List<T> — only for materialized collections.
```

### 7.7. Channel<T> for queues

- `Channel<T>` с `BoundedChannelOptions` — bounded memory.
- Не накапливает million events в `_events: List<T>`.

### 7.8. Logging with structured data

```csharp
// ✅ BAD: string allocation
logger.LogInformation($"User {userId} did {action}");

// ✅ GOOD: structured, no allocation if log level disabled
logger.LogInformation("User {UserId} did {Action}", userId, action);
```

### 7.9. CancellationToken everywhere

- Если не пробрасывать `CancellationToken` — операции не отменяются, держат память.

### 7.10. Dispose everywhere

- `IDisposable` / `IAsyncDisposable` для всех ресурсов.
- `using` / `await using` для всех disposable объектов.
- Особенно: `HttpClient`, `SqliteConnection`, `Process`, `FileStream`.

## 8. Profile-guided optimization

.NET 10 поддерживает PGO — Profile-Guided Optimization.

```bash
# 1. Instrumented build
dotnet publish -c Release -r linux-x64 /p:ProfileGuidedOptimization=Instrumented

# 2. Run with representative workload
./harbor run --prompt "test" --pgo-train

# 3. Rebuild with profile data
dotnet publish -c Release -r linux-x64 /p:ProfileGuidedOptimization=Optimized
```

В JIT-mode — dynamic PGO (runtime profiling). В NativeAOT — static PGO (training run).

Экономит 5–10% на hot paths. Для CLI/TUI — modest improvement.

## 9. Continuous benchmarking в CI

```yaml
# .github/workflows/benchmarks.yml
name: Benchmarks
on: [push]
jobs:
  benchmark:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
      - name: Build AOT
        run: dotnet publish src/Harbor.Cli -c Release -r linux-x64
      - name: Run benchmarks
        run: |
          hyperfine --warmup 3 --export-json results.json \
            './bin/publish/harbor --version'
          /usr/bin/time -v ./bin/publish/harbor --version 2>&1 | grep "Maximum resident"
      - name: Compare with main
        uses: benchmark-action/github-action-benchmark@v1
        with:
          tool: 'customBiggerIsBetter'
          output-file-path: results.json
          alert-threshold: '120%'
          comment-on-alert: true
```

Если regression >20% — alert в PR.

## 10. Real-world expectations

### 10.1. Worst case (heavy session)

| Сценарий | RSS |
|---|---|
| 1K messages session + Anthropic streaming | ~50 МБ |
| 10K messages + 2 MCP servers + LSP | ~150 МБ |
| 50K messages + 5 MCP servers + LSP + LanceDB (future) | ~300 МБ |

Даже worst case — лучше, чем kilocode в idle.

### 10.2. Best case (light usage)

| Сценарий | RSS |
|---|---|
| `harbor --version` | ~15 МБ |
| `harbor ask "hello"` (one-shot) | ~25 МБ peak |
| Idle TUI, no session | ~30 МБ |
| Short conversation (<100 messages) | ~40 МБ |

### 10.3. Comparison table (realistic)

| Tool | Idle | Short conv (100 msg) | Long conv (10K msg) | Cold start |
|---|---|---|---|---|
| kilocode | ~700 МБ | ~800 МБ | ~1.1 ГБ | ~1000 ms |
| opencode | ~500 МБ | ~600 МБ | ~900 МБ | ~800 ms |
| pi-agent | ~150 МБ | ~200 МБ | ~400 МБ | ~400 ms |
| crush | ~100 МБ | ~150 МБ | ~250 МБ | ~50 ms |
| **harbor (target)** | **~30 МБ** | **~40 МБ** | **~80 МБ** | **~30 ms** |
| **harbor (worst case, plugins)** | ~60 МБ | ~80 МБ | ~150 МБ | ~80 ms |

**Итог**: harbor должен быть **в 10–20 раз легче**, чем kilocode/opencode, и **в 3–5 раз легче**, чем crush.

## 11. Memory profiling tools

### 11.1. dotnet-counters

```bash
dotnet tool install -g dotnet-counters
dotnet-counters monitor -n harbor --counters System.Runtime,System.Net.Http,System.Runtime.InteropServices
```

Real-time: GC heap, gen 0/1/2 sizes, allocations, exceptions.

### 11.2. dotnet-dump

```bash
dotnet-dump collect -n harbor
dotnet-dump analyze harbor.dump
> dumpheap -stat
> dumpheap -type System.String
```

Heap analysis post-mortem.

### 11.3. dotnet-gcdump

```bash
dotnet-gcdump collect -n harbor
# Opens in PerfView or dotnet-gcdump dump viewer
```

Live GC heap dump.

### 11.4. perfview (Windows)

Полный profiling — CPU, memory, GC, JIT.

### 11.5. valgrind massif (Linux)

```bash
valgrind --tool=massif --stacks=yes ./harbor
ms_print massif.out.*
```

Native heap profiling (для SQLite native code).

### 11.6. Custom Harbor metrics

Встроенный `harbor metrics`:

```bash
harbor metrics --live
```

```
=== Harbor Live Metrics ===
RSS: 32.5 MB
GC Heap: 8.2 MB (Gen0: 1.1 MB, Gen1: 0.8 MB, Gen2: 5.3 MB, LOH: 1.0 MB)
Active Sessions: 1
Messages in memory: 234 (cached)
LLM streams: 0 active
Tool executions: 12 total (avg 8ms)
DB connections: 1 (pool: 1)
HTTP connections: 0 (pool: 0)
Plugins loaded: 2 (WebSearch, TodoWrite)
MCP clients: 0
LSP servers: 0
```

---

**Next**: `10-repo-analysis.md` — глубокий разбор 5 репозиториев, что позаимствовать.
