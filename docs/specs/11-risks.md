# 11 — Risk Register

> Документ: что может пойти не так. Категории рисков (технические, продуктовые, операционные), вероятности, impact, mitigation.

## 1. Технические риски

### R-T01: NativeAOT несовместимость с критической библиотекой

**Вероятность**: Medium
**Impact**: High
**Описание**: В процессе разработки обнаружится, что какая-то критически важная библиотека (например, `Microsoft.Data.Sqlite` или `Markdig` или `ModelContextProtocol`) имеет reflection-based код, который ломается под AOT.

**Mitigation**:
- Spike-проект на первой неделе: собрать минимальный AOT-binary с SQLite + HttpClient + System.Text.Json + ConsoleAppFramework. Цель — убедиться, что baseline работает.
- Если библиотека не AOT-compatible — ищем альтернативу или пишем свой.
- В .csproj ставим `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — ловим IL2026/IL3053 рано.
- Fallback: JIT-сборка для dev, AOT — для release. Если AOT ломается — release с JIT + trimming (без NativeAOT).

### R-T02: Plugin isolation не работает под AOT

**Вероятность**: High (точно произойдёт — `AssemblyLoadContext` collectible не работает под AOT)
**Impact**: Medium
**Описание**: Заявленная архитектура с collectible `AssemblyLoadContext` не работает в NativeAOT-билдах. Hot-reload плагинов становится невозможным.

**Mitigation**:
- В `02-plugins.md` уже заложены 3 подхода: out-of-process (основной для AOT), build-time dispatch, native libs.
- MVP: только builtin tools + hardcoded plugin list (compile-time).
- v1: out-of-process plugin-host для runtime-loadable плагинов.
- v2: native libs (C ABI) для power users.
- Чёткая документация: JIT-билд поддерживает collectible ALC, AOT-билд — нет.

### R-T03: Spectre.Console рендеринг ломается под AOT в edge-cases

**Вероятность**: Low (для v0.50+)
**Impact**: Medium
**Описание**: Хотя Spectre.Console (рендеринг) официально AOT-compatible с v0.50, edge-cases возможны — особенно с custom renderers, complex objects, dynamic tables.

**Mitigation**:
- MVP: НЕ используем Spectre.Console вообще. Custom ANSI wrapper (50 LOC).
- v1: Spectre.Console как опциональный рендер для rich widgets (tables, panels). Тщательно тестируем под AOT.
- Если Spectre ломается — fallback на custom ANSI.
- TrimmerRootDescriptor для Spectre types если нужно.

### R-T04: System.Text.Json source generator не покрывает все типы

**Вероятность**: Medium
**Impact**: Medium
**Описание**: `JsonSerializerContext` требует явного перечисления всех типов. Если тип забыт — runtime ошибка. Polymorphic types требуют `[JsonPolymorphic]` + `[JsonDerivedType]`.

**Mitigation**:
- Централизованный `HarborJsonContext` со всеми типами.
- Unit-тест: для каждого типа в assembly, проверять что есть `[JsonSerializable]` в context.
- `<TreatWarningsAsErrors>` ловит missing context на этапе компиляции.
- Если тип из external library — пишем свой `JsonConverter<T>`.

### R-T05: Dapper.AOT не поддерживает complex queries

**Вероятность**: Low
**Impact**: Low
**Описание**: Dapper.AOT генерирует typed mapper для queries, но сложные queries (dynamic SQL, multi-mapping) могут не работать.

**Mitigation**:
- Избегать dynamic SQL. Все queries — static strings.
- Для complex result mapping — использовать `SqlMapper.GridReader` (multi-result) или explicit `dynamic` (но это не AOT-friendly).
- Fallback: raw `SqliteCommand` + `SqliteDataReader` + manual mapping.

### R-T06: MCP C# SDK нестабилен

**Вероятность**: Medium (SDK всё ещё 0.1.0-preview)
**Impact**: Low (MCP — опциональная фича)
**Описание**: Официальный `ModelContextProtocol` NuGet может иметь breaking changes, баги, несовместимость с ecosystem.

**Mitigation**:
- MCP — плагин, не core. Если SDK нестабилен — откладываем v1+ до стабилизации.
- Тесты против нескольких известных MCP servers (filesystem, github) — раньше всех ловим regression.
- Fallback: своя impl JSON-RPC over stdio (300-500 LOC).

### R-T07: Производительность TUI ниже ожидаемой

**Вероятность**: Medium
**Impact**: Medium
**Описание**: Streaming rendering может быть медленным, особенно с markdown + syntax highlighting + diff preview. Пользователь будет видеть lag.

**Mitigation**:
- Streaming markdown prefix cache (как у crush) — критическая оптимизация.
- Throttling: 30 fps render rate, не больше.
- Async render: tokens буферизуются, render в отдельной задаче.
- Если lag — fallback на plain text streaming (без markdown).
- Microbenchmarks в CI — ловим regression.

### R-T08: Cross-platform нюансы (Windows console)

**Вероятность**: High
**Impact**: Medium
**Описание**: Windows console имеет исторические баги — ANSI escape support (Win7/8 не поддерживает), code pages, UTF-8 encoding, raw mode differences.

**Mitigation**:
- Минимальные требования: Windows 10 1809+ (VT processing).
- Тесты на Windows 10/11, macOS, Linux (Ubuntu, Alpine).
- `EnableWindowsAnsi()` на startup (через `SetConsoleMode`).
- Fallback: если VT processing не работает — `Console.ForegroundColor`/`Console.BackgroundColor` (basic 16 colors, без rich rendering).
- UTF-8: `Console.OutputEncoding = Encoding.UTF8` на Windows.

### R-T09: SQLite concurrency issues

**Вероятность**: Medium
**Impact**: Medium
**Описание**: SQLite с WAL поддерживает concurrent reads, но writes сериализуются. Если фоновые сервисы (compaction, pruning, snapshotting) конкурируют с main loop за writes — `SQLITE_BUSY` ошибки.

**Mitigation**:
- Single connection с `PRAGMA busy_timeout = 5000` (ждём до 5 секунд при lock).
- Все writes через `SqliteConnection` с одним и тем же `ConnectionString` (shared cache).
- Write serialization: все DB writes в одну очередь (`Channel<DbAction>`), обрабатываются одним background task.
- Если `SQLITE_BUSY` после 5s — fail с понятной ошибкой.

### R-T10: Streaming LLM response теряется при crash

**Вероятность**: Medium
**Impact**: Medium
**Описание**: Если harbor крашится во время streaming LLM ответа — частичный assistant message теряется (не записан в БД).

**Mitigation**:
- В MVP: пишем в БД только на `MessageEnd`. Crash = потеря partial.
- v1: streaming write — каждые 500 токенов или 2 секунды, что раньше. Crash = потеря последних 500 токенов.
- v2: WAL-like лог всех LLM events в отдельный файл, recover при старте.

### R-T11: Compaction quality плохая

**Вероятность**: Medium
**Impact**: High
**Описание**: LLM может сделать плохой summary — упустить важные детали, галлюцинировать, не сохранить structure. Это ломает long-running сессии.

**Mitigation**:
- Structured Markdown template с фиксированными секциями (Goal/Constraints/Progress/...).
- Cumulative file tracking — previous summary всегда передаётся, новое summary обновляет.
- Temperature 0.3 для summarization.
- Тесты: golden tests с разными LLM (Anthropic, OpenAI, Google) — проверяем, что summary содержит expected information.
- Manual fallback: `harbor session compact --manual` — пользователь редактирует summary вручную.

### R-T12: Plugin compatibility breaks

**Вероятность**: High
**Impact**: Medium
**Описание**: При обновлении harbor (new major version) plugin может сломаться — изменился interface, removed method, changed signature.

**Mitigation**:
- SemVer: major version bump на breaking changes.
- `[Obsolete]` deprecation cycle: 1 minor version warning, 1 major version error, 2 major versions removed.
- `RequiredHarborVersion` проверка при загрузке плагина.
- Plugin compatibility tests в CI — прогоняем plugin samples против каждой новой версии.

## 2. Продуктовые риски

### R-P01: Слишком большой scope MVP

**Вероятность**: High
**Impact**: High
**Описание**: Попытка реализовать все фичи kilocode/opencode/pi/crush в MVP — затянется на год, и продукт не выйдет.

**Mitigation**:
- Чёткий MVP scope: 4 builtin tools + 2 LLM провайдера + SQLite sessions + compaction + custom ANSI TUI + ConsoleAppFramework CLI.
- Deferred: MCP, LSP, plugins, client-server, branching, skills.
- Каждая фича — отдельный milestone. См. `12-roadmap.md`.

### R-P02: Пользовательские expectations не совпадают с реальностью

**Вероятность**: Medium
**Impact**: Medium
**Описание**: Пользователь ждёт "kilo-code но быстрее" — а получает tool с меньшим функционалом (без MCP, без LSP, без plugins в MVP).

**Mitigation**:
- Чёткая документация: что в MVP, что в v1, что в v2.
- README: "harbor is in early development. MVP focuses on core agent loop + minimal tools. MCP/LSP/plugins coming in v1."
- Migration guide: "coming from kilocode? Here's what's supported and what's not yet."

### R-P03: Concurrent agent executions ломают session state

**Вероятность**: Medium
**Impact**: High
**Описание**: Пользователь запускает несколько `harbor` процессов на одном проекте. Они могут конфликтовать — оба пишут в одну SQLite DB, оба пытаются fork одну сессию.

**Mitigation**:
- File lock на session: `~/.harbor/sessions/<id>.lock` с PID. Если lock занят — warn user.
- SQLite WAL handles concurrent reads, but writes serialized.
- Для client-server mode (v1) — single server per project, multiple clients.

### R-P04: Cost runaway

**Вероятность**: Medium
**Impact**: High
**Описание**: LLM может зациклиться (doom loop) и потратить $100 на API calls. Особенно если tools возвращают errors, и LLM пытается снова и снова.

**Mitigation**:
- Doom loop detection (3 одинаковых tool calls → ask user).
- Cost limit в config: `maxCostPerSession: 10.0` — при превышении warn или stop.
- Per-session cost tracking в status bar.
- `harbor session stats` — full breakdown.

### R-P05: Security — malicious plugin execution

**Вероятность**: Low (если trust model работает)
**Impact**: High
**Описание**: Злоумышленник подсунул malicious plugin в склонированный репозиторий. Пользователь запускает harbor — плагин выполняет arbitrary code.

**Mitigation**:
- Trust prompt для project-local плагинов (`.harbor/plugins/`).
- `~/.harbor/trusted-repos.json` с hash'ами DLL.
- Если hash изменился — переспрашиваем.
- Global plugins (`~/.harbor/plugins/`) — trusted by default (user сам их ставит).
- Документация: "Never trust plugins from untrusted sources."

### R-P06: API key leak

**Вероятность**: Low
**Impact**: High
**Описание**: API keys могут попасть в логи, в error messages, в session exports.

**Mitigation**:
- Никогда не логировать `Authorization` headers, API keys.
- Mask в error messages: `Authorization: Bearer sk-***...***`.
- `harbor auth set` — хранить в OS keychain (через `keyring` NuGet) или `~/.harbor/credentials.json` (chmod 600).
- Session exports — не включать env vars / config.

## 3. Operational риски

### R-O01: .NET 10 SDK bleeding edge

**Вероятность**: Medium
**Impact**: Low
**Описание**: .NET 10 вышел в ноябре 2025. К маю 2026 (LTS) — могут быть bug fixes, breaking changes в NativeAOT.

**Mitigation**:
- Targeting `net10.0` — но также работаем с `net9.0` если нужно (LTS до мая 2026).
- CI на нескольких версиях SDK (latest stable + LTS).
- Follow dotnet/runtime releases, оперативно обновляемся.

### R-O02: Single maintainer bottleneck

**Вероятность**: High (если один разработчик)
**Impact**: High
**Описание**: Один разработчик не может поддерживать 36 LLM провайдеров, 30+ LSP servers, full MCP integration, etc.

**Mitigation**:
- MVP: 2-3 провайдера (Anthropic, OpenAI, Ollama).
- v1: plugin architecture позволяет community добавлять провайдеры.
- Документация "How to write a provider" — снижает entry barrier для контрибьюторов.
- Plugin marketplace (future) — community распространяет плагины.

### R-O03: Documentation rot

**Вероятность**: High
**Impact**: Medium
**Описание**: Документация устаревает быстрее, чем код. Пользователь читает docs v0.5, ставит v0.7 — API отличается.

**Mitigation**:
- Versioned docs (git tags).
- `harbor docs` команда — открывает docs для текущей версии.
- Examples в `samples/` — всегда тестируются в CI (compile + run).
- Changelog с breaking changes highlighted.

### R-O04: Limited test coverage

**Вероятность**: High
**Impact**: High
**Описание**: Без comprehensive test suite — регрессии неизбежны. Особенно в streaming / async / concurrent коде.

**Mitigation**:
- Unit tests для core logic (agent loop, compaction, message conversion).
- Integration tests с mock LLM (VCR-style — записанные SSE responses).
- E2e tests с real LLM (но expensive — на ночь, на PR merge).
- Property-based tests для permission system, token estimator.
- Mutation testing (Stryker.NET) — проверка quality тестов.

### R-O05: Community adoption slow

**Вероятность**: Medium
**Impact**: Low (для пользователя, для ecosystem — High)
**Описание**: Без community — нет плагинов, нет контрибьюторов, нет bug reports.

**Mitigation**:
- Публикация на nuget.org как `dotnet tool`.
- GitHub README с clear value proposition ("10x lighter than kilocode").
- Demo video / GIF в README.
- Blog post с benchmarks.
- Cross-posting в r/dotnet, r/csharp, HackerNews.

## 4. Risk matrix

| Risk | Probability | Impact | Mitigation cost | Priority |
|---|---|---|---|---|
| R-T01: AOT incompatibility | Medium | High | Low (spike early) | **HIGH** |
| R-T02: Plugin isolation under AOT | High | Medium | Already mitigated | MEDIUM |
| R-T03: Spectre edge-cases | Low | Medium | Low (don't use Spectre) | LOW |
| R-T04: JSON source-gen coverage | Medium | Medium | Low (test) | MEDIUM |
| R-T05: Dapper.AOT complex queries | Low | Low | Low | LOW |
| R-T06: MCP SDK unstable | Medium | Low | Low (defer) | LOW |
| R-T07: TUI perf below target | Medium | Medium | High (prefix cache) | **HIGH** |
| R-T08: Cross-platform Windows | High | Medium | Medium | **HIGH** |
| R-T09: SQLite concurrency | Medium | Medium | Low (busy_timeout) | MEDIUM |
| R-T10: Streaming loss on crash | Medium | Medium | Medium (v1) | MEDIUM |
| R-T11: Compaction quality | Medium | High | Medium (templates+tests) | **HIGH** |
| R-T12: Plugin compatibility | High | Medium | Low (SemVer) | MEDIUM |
| R-P01: Scope creep | High | High | Low (discipline) | **HIGH** |
| R-P02: Expectations mismatch | Medium | Medium | Low (docs) | MEDIUM |
| R-P03: Concurrent sessions | Medium | High | Medium (file lock) | **HIGH** |
| R-P04: Cost runaway | Medium | High | Low (limits) | **HIGH** |
| R-P05: Malicious plugin | Low | High | Low (trust) | MEDIUM |
| R-P06: API key leak | Low | High | Low (masking) | MEDIUM |
| R-O01: .NET 10 bleeding edge | Medium | Low | Low | LOW |
| R-O02: Single maintainer | High | High | Medium (plugin arch) | **HIGH** |
| R-O03: Documentation rot | High | Medium | Low (CI) | MEDIUM |
| R-O04: Test coverage | High | High | High (write tests) | **HIGH** |
| R-O05: Community adoption | Medium | Low | Medium (marketing) | LOW |

## 5. Top-10 priority risks

1. **R-T11: Compaction quality** — тестируем с разными LLM, golden tests.
2. **R-T07: TUI perf** — streaming markdown prefix cache, throttling.
3. **R-P01: Scope creep** — strict MVP scope, deferred features в roadmap.
4. **R-P04: Cost runaway** — doom loop detection, cost limits.
5. **R-T01: AOT incompatibility** — spike project в первой неделе.
6. **R-T08: Cross-platform** — тесты на всех ОС в CI.
7. **R-P03: Concurrent sessions** — file lock, single-writer pattern.
8. **R-O02: Single maintainer** — plugin architecture для community.
9. **R-O04: Test coverage** — comprehensive test suite с самого начала.
10. **R-T09: SQLite concurrency** — busy_timeout, write serialization.

## 6. Risk monitoring

- Monthly review: какие risks сбылись, какие новые появились.
- GitHub issues с `risk` label для отслеживания.
- Post-mortem на каждый инцидент — что пошло не так, как предотвратить.

---

**Next**: `12-roadmap.md` — план разработки, MVP → v1 → v2.
