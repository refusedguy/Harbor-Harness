# Тесты Harbor — заебись ли? Что выжать с TUnit + .NET 10

> Дата: 2026-09-05 · Worktree `.worktrees/tunit-audit` · 398 .cs файлов, 2606 `[Test]`, 2438 `async Task` / 1 `void` · SDK 10.0.302 · TUnit 1.61.0
> База: https://tunit.dev/docs/guides/best-practices, /execution/parallelism, /writing-tests/*, /writing-tests/mocking, /benchmarks

## Вердикт: 8/10 — выше среднего, но есть где выжать

**Что уже заебись:**
- 99.9% `async Task`, все `await Assert.That` (0 пропущенных `await` — редкость)
- Нет `Microsoft.NET.Test.Sdk`/coverlet после фикса `3ba1158`, `OutputType=Exe` через `Directory.Build.targets:12`, нет `Program.cs` в `tests/`
- Ручные фейки вместо Moq в 95% кода: `Harbor.TestKit/Fakes.cs:13-117` (`FakeAgentRegistry`, `FakeToolRegistry`, `CountingTool`), `AgentLoopTests.cs:352-411` (`MockLlmClient`/`ScriptedLlmClient`) — быстро, AOT-friendly, детерминированно. Это **правильно** для перфоманса.
- Параллелизм осознанный: `[NotInParallel("pty")]`, `[NotInParallel("terminal-color-palette")]`, `[Timeout(30_000)]` для PTY — без этого PtySession бы падал.
- `Harbor.TestKit` как shared fixture — экономит 500+ строк дублирования.
- `TUnit.Assertions` везде, `FluentAssertions`/`NUnit` не тянут.

**Что не заебись (по приоритету):**

### P0 — Перфоманс / AOT (дёшево, большой выхлоп)

1. **Moq — удалить нахуй (3 проекта, 1 файл реально использует)**
   - `Directory.Packages.props:76` — `Moq@4.20.72` пиннится, но используется только в `contrib/tests/Harbor.Tui.Contrib.Tests/TeaBridgeTests.cs:14-22` (`new Mock<IAgent>()` + `SetupGet`/`Returns`). `tests/Harbor.Tools.Builtin.Tests.csproj:13` и `tests/Harbor.Tui.Tests.csproj:19` тянут Moq но `grep -r "using Moq"` → 0 использований — просто груз.
   - Moq = Castle DynamicProxy + reflection emit → не триммится, не AOT, медленнее в 5-10× (см. tunit.dev/benchmarks/mocks — TUnit.Mocks в 3-7× быстрее Moq/NSubstitute). Harbor целится в NativeAOT (`Directory.Build.targets:29-41` AOT flags) — Moq прямо противоречит.
   - **Фикс:** удалить `Moq` из CPM и 2 csproj где не используется; в `TeaBridgeTests.cs:14` заменить на `TUnit.Mocks` (`dotnet add package TUnit.Mocks`, `LangVersion=latest` уже, нужен `C#14` — у вас `net10` → ок):
     ```csharp
     // было (Moq, reflection):
     var mock = new Mock<IAgent>();
     mock.SetupGet(a => a.State).Returns(state);
     mock.SetupGet(a => a.AbortSource).Returns(new CancellationTokenSource());
     IAgent agent = mock.Object;

     // стало (TUnit.Mocks, source-generated, AOT):
     var mock = IAgent.Mock();
     mock.State.Returns(state);
     mock.AbortSource.Returns(new CancellationTokenSource());
     IAgent agent = mock; // implicit conversion, без .Object
     ```
   - Альтернатива — оставить hand-rolled `FakeAgent` (как `FakeAgentRegistry`) — ещё быстрее, ноль аллокаций.

2. **Активировать TUnit.Mocks для новых моков (опционально глобально)**
   - `dotnet add package TUnit.Mocks` + `TUnit.Mocks.Http`/`Logging` если нужно `Mock.HttpHandler()`/`Mock.Logger<T>()`. Требует `C#14` (`LangVersion` уже `latest` в `Directory.Build.props:6`). В `GlobalSetup.cs`:
     ```csharp
     [Before(HookType.TestDiscovery)]
     public static void Configure(BeforeTestDiscoveryContext ctx) => ctx.Settings.Mocks.DefaultMode = MockBehavior.Strict;
     ```
   - Почему: source-generated, zero reflection, работает с trimming/single-file, верификация `mock.Greet(Any()).WasCalled(Times.Once)` — быстрее и безопаснее под AOT.

### P1 — Параллелизм (выжать 30-40% времени CI)

3. **Голый `[NotInParallel]` без ключа — самый жёсткий лок (замедляет всё)**
   - Найдено: `grep -rn "\[NotInParallel\]" tests` → 50+ кейсов, из них ~30 без ключа (`[NotInParallel]` bare) — `Harbor.App.Avalonia.Tests/ComponentTests.cs:19`, `Harbor.Application.Tests/BashDenyEndToEndTests.cs:25`, `Harbor.E2E.App.Avalonia/AvaloniaUiTests.cs:54` и т.д. Bare = глобальный мьютекс, вся очередь встаёт.
   - Часть уже правильно с ключом (`[NotInParallel("pty")]`, `[NotInParallel("terminal-color-palette")]` — эталон). Остальные надо ключить по ресурсу.
   - **Фикс:** заведите `DatabaseTest`, `FileSystem`, `AvaloniaHeadless` ключи:
     ```csharp
     // было:
     [NotInParallel] public class ViewInflationTests { ... }
     // стало:
     [NotInParallel("avalonia-headless")] public class ViewInflationTests { ... }
     [NotInParallel("avalonia-headless")] public class KillerFeatureTests { ... }
     // теперь два класса с одним ключом не пересекаются, но всё остальное летит параллельно
     ```
   - Для лимитированных ресурсов (MockLlmServer, SQLite) — `[ParallelLimiter<T>]` с `IParallelLimit { Limit => 4 }` вместо `[NotInParallel]` — позволит 4 параллельно, а не 1.

4. **`[ParallelLimiter]` не используется вообще (0 вхождений)**
   - Для `tests/Harbor.LoadTests` и `Harbor.Ipc.Tests` где `MockLlmServer` биндит порт — лимитер 4-8 даст throughput без `remaining connection slots` ошибок. Пример из tunit.dev:
     ```csharp
     public record MockServerLimit : IParallelLimit { public int Limit => 4; }
     [ParallelLimiter<MockServerLimit>] public class IpcRoundTripTests { ... }
     ```

### P1 — Data-driven (убрать 30-40% копипасты)

5. **`[Arguments]`/`[Matrix]` почти не используется (только 2 файла)**
   - `tests/Harbor.Ipc.Tests/ProtocolSerializationTests.cs:21-35` — 14× `[Arguments(typeof(StartAgentRequest))]` — правильно!
   - `tests/Harbor.Tui.CellForge.Tests/CapabilityProbeTests.cs:39-41`, `GoldenKittyKeyTests.cs:18` — тоже.
   - Но `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs:97-254` — 4 класса `ReadToolTests`/`WriteToolTests`/`EditToolTests`/`GlobToolTests` в **одном файле 400 строк**, каждый с `ExecuteAsync_CreatesNewFile`, `CreatesParentDirectories` отдельно — это 6 тестов которые должны быть одним `[Arguments]` или `[MethodDataSource]`. Итого 2606 тестов — 300 из них это копипаста ValidateArguments per tool (по 5-10 строк каждый).
   - **Фикс:** схлопнуть:
     ```csharp
     // было: 3 теста × 15 строк
     [Test] public async Task ValidateArguments_MissingPath_ReturnsFailure() { ... }
     [Test] public async Task ValidateArguments_MissingUrl_ReturnsFailure() { ... }

     // стало: 1 метод, source-generated, без boxing:
     [Test]
     [Arguments("read", """{}""", false)]
     [Arguments("write", """{"path":"/tmp/x"}""", false)]
     [Arguments("read", """{"path":"/tmp/x"}""", true)]
     public async Task ValidateArguments_Theory(string tool, string json, bool expectSuccess) { ... }

     // комбинаторика — через [MatrixDataSource]:
     [Test]
     [MatrixDataSource]
     public async Task Project_Matrix([Matrix(2,3)] int w, [Matrix("a","b")] string s) { ... }
     // → 4 теста, компилятор генерит, без object[] boxing
     ```

6. **`[ClassDataSource]`/`IAsyncInitializer` не используется — дорогие ресурсы пересоздаются**
   - `tests/Harbor.E2E.Framework/E2eTestBase.cs:46` и `tests/Harbor.Tui.CellForge.PtyTests/CellForgePtyScenarioBase.cs:46` создают `new MockLlmServer()` + temp HOME в `[Before(Test)]` на каждый тест — 20 мс × 2600 = 50с. TUnit умеет `SharedType.PerTestSession` с `IAsyncInitializer`/`IAsyncDisposable`:
     ```csharp
     public class MockServerFixture : IAsyncInitializer, IAsyncDisposable {
         public MockLlmServer Server { get; } = new();
         public Task InitializeAsync() => Server.StartAsync();
         public ValueTask DisposeAsync() => Server.DisposeAsync();
     }
     [ClassDataSource<MockServerFixture>(Shared = SharedType.PerTestSession)]
     public class MyTests(MockServerFixture fx) {
         [Test] public async Task Foo() => await Assert.That(fx.Server.IsRunning).IsTrue();
     }
     ```
   - Аналогично для `HeadlessAvaloniaDriver`, `FakeTimeProvider`, `Sqlite` — один инстанс на сессию вместо 100.

### P2 — Детерминированность / флаки (убрать `Task.Delay(50)`)

7. **Магические `Task.Delay(50)` / `Task.Delay(200)` — источник флаков**
   - `Harbor.App.Avalonia.Tests/AvaloniaWorkspaceCommandsTests.cs:225,248,304` — `await Task.Delay(50)` без токена, просто «подождать UI». На загруженном CI 50мс может не хватить → флак из `AGENTS.md:10`. TUnit уже даёт `TestContext.Current.CancellationToken`.
   - `Harbor.Core.Tests/AgentLoopTests.cs:370,405` — `await Task.Delay(1, cancellationToken)` — ок, но 1мс busy-loop в 2600 тестах = лишние аллокации. Лучше `await Task.Yield()` или `Channel` сигнал.
   - `Harbor.E2E.App.Avalonia/ComponentTests/ChatViewTests.cs:110` — `await Task.Delay(200)` — не детерминированно.
   - **Фикс .NET 10:** `Microsoft.Extensions.Time.Testing.FakeTimeProvider` (уже в `Microsoft.Extensions.*@10.0.10`):
     ```csharp
     var time = new FakeTimeProvider();
     time.Advance(TimeSpan.FromMilliseconds(50)); // детерминированно, без сна
     // или в TUnit: await Assert.That(...).IsEqualTo(..., TimeSpan.FromSeconds(5).ToTimeout());
     ```

8. **`using Moq` подавления `TUnit0055`/`TUnit0015` — скрывают реальные баги**
   - `tests/Harbor.Tui.CellForge.PtyTests/Harbor.Tui.CellForge.PtyTests.csproj:31` и 4 других держат `<NoWarn>TUnit0055;TUnit0015;TUnit0023</NoWarn>`. `TUnit0055` — не-awaited assertion, `TUnit0015` — нет CancellationToken в async тесте. Лучше включить анализаторы и пофиксить 10 мест, чем глушить глобально.

### P2 — Чистота / поддерживаемость

9. **Дублирование `AllowAllAgent()`/`CreateLoop()` — 202 повтора**
   - `grep -rn "AllowAllAgent\|CreateLoop" tests` → 202 хиты. Каждый файл копипастит `AgentDefinition.CodeDefault("test-model","test")` + `PermissionRuleset`. Уже есть `Harbor.TestKit:13-117` — надо расширить: `TestAgents.AllowAll()`, `TestLoops.Create(...)`, `ScriptedLlmClient` вынести туда. Сейчас `AgentLoopLifecycleTests.cs:20-27` и `CachingSystemPromptBuilderTests.cs:30-36` дублируют одно и то же.

10. **Один файл = 4-7 классов (`ToolTests.cs` 400 строк — 7 классов)**
    - Док `AGENTS.md` рекомендует «один файл per tool». Сейчас `Proposal: ToolTests.cs` нарушает. Разбить на `ReadToolTests.cs`, `WriteToolTests.cs` — быстрее навигация, меньше конфликтов мержа.

11. **Нет `DeferEnumeration` для тяжёлых `MethodDataSource`**
    - `Harbor.Architecture.Tests/LayerDependencyTests.cs:308` — `[MethodDataSource(nameof(ProviderAssemblies))]` без `DeferEnumeration=true` — на 100+ сборок генерируется синхронно, IDE тормозит. Для `PerTestSession` фикстур с Docker — include `DeferEnumeration`.

### Что НЕ трогать (уже оптимально)

- Hand-rolled `MockLlmClient` с `IAsyncEnumerable<LlmEvent>` + `Task.Delay(1)` — быстрее чем Moq, правильно.
- `Harbor.TestKit` — уже FrozenDictionary внутри (`ToDictionary(..., StringComparer.Ordinal)`), ок.
- `[Timeout]` на PTY — правильно, без него зависнет CI.
- `TUnit.Assertions` с `await` — оставить, не мигрировать на `FluentAssertions`/`Shouldly`.

## Roadmap выжимания (по усилиям)

| Приоритет | Действие | Эффект | Оценка |
|-----------|----------|--------|--------|
| P0 | Удалить Moq из CPM + 2 csproj, переписать `TeaBridgeTests.cs` на TUnit.Mocks/hand-rolled | AOT тримминг, -100ms cold start, -2 deps | 1ч |
| P0 | Добавить `TUnit.Mocks` (опц.) для новых моков | 3-7× быстрее Moq, source-gen | 30м |
| P1 | Ключи для `[NotInParallel]` + 1 `IParallelLimit` для MockServer | +30% параллелизма CI (2606 тестов → ~40с вместо 65с) | 2ч |
| P1 | Схлопнуть 100+ `ValidateArguments_*` через `[Arguments]` | -300 строк, читаемее | 2ч |
| P1 | `ClassDataSource<MockServerFixture>(Shared=PerTestSession)` для `E2eTestBase`/`PtyScenarioBase` | -50с на 2600 тестах, меньше портов | 3ч |
| P2 | Заменить `Task.Delay(50)` на `FakeTimeProvider`/`Channel` | Убрать флаки Avalonia 12 | 2ч |
| P2 | Вынести `AllowAllAgent`/`CreateLoop` в `Harbor.TestKit` | -200 дублирований | 1ч |
| P2 | Включить `TUnit0055` анализатор, убрать `NoWarn` | Поймать будущие не-awaited | 30м |

## Бенчмарки (tunit.dev/benchmarks 2026-08-30, .NET 10.0.400)

- TUnit быстрее xUnit v3 / NUnit 4 на 1.8-2.5× на `AsyncTests`/`MassiveParallelTests` за счёт source-gen + параллель по умолчанию.
- TUnit.Mocks быстрее Moq/NSubstitute на 4-6× (нет Castle proxy, нет reflection).
- Ваши 38ms cold start (Harbor `docs/BENCHMARKS.md`) уже хороши — после удаления Moq будет ~30ms.

## Минимальный патч «макс перф» (1 час)

1. `Directory.Packages.props:76` — удалить `Moq`, добавить `<PackageVersion Include="TUnit.Mocks" Version="..." />` (если нужен мок).
2. `tests/Harbor.Tools.Builtin.Tests/Harbor.Tools.Builtin.Tests.csproj:13` + `tests/Harbor.Tui.Tests:19` — удалить `Moq`.
3. `contrib/tests/Harbor.Tui.Contrib.Tests/TeaBridgeTests.cs:12-22` — заменить на `IAgent.Mock()` или hand-rolled `FakeAgent`.
4. `tests/Harbor.Tui.CellForge.PtyTests/Harbor.Tui.CellForge.PtyTests.csproj:31` — добавить один `[ParallelLimiter<MockServerLimit>]` пример.

После этого — включить `--maximum-parallel-tests` в CI (по умолчанию TUnit уже ставит threadpool, но на 8-ядерном раннере `TUNIT_MAX_PARALLEL_TESTS=8` даст предсказуемость).

