# TUnit + MTP — полный аудит Harbor (branch chore/tunit-mtp-audit)

> Worktree: `.worktrees/tunit-audit` → ветка `chore/tunit-mtp-audit` (от `dev` @ 13c78cf)
> Дата: 2026-09-05 · SDK: 10.0.302 · TUnit: 1.61.0 · `Microsoft.NET.Test.Sdk`: 18.8.1
> Источники: https://tunit.dev/docs/intro, /getting-started/installation, /getting-started/running-your-tests, /troubleshooting

## TL;DR — главный проёб

**`Microsoft.NET.Test.Sdk` конфликтует с TUnit/MTP.** Док прямо пишет:

> `danger If you're used to other testing frameworks, you're probably used to the package Microsoft.NET.Test.Sdk. This should NOT be used with TUnit. It'll stop test discovery from working properly.` — https://tunit.dev/docs/getting-started/installation

В Harbor он **всё ещё зашит** в 11 `.csproj` + в `Directory.Packages.props:71`. Именно поэтому `AGENTS.md:10` честно признаётся:

> `dotnet test` discovers ZERO tests under the Microsoft.Testing.Platform (MTP) bridge (host exits 5 with a silent discovery error)

Это не «особенность репо» — это баг конфигурации. Лечится удалением пакета.

---

## 1. Что проверено

### 1.1 Структура конфигурации

- `Directory.Build.targets:11-19` — централизованно ставит `IsTestProject=true` для `*.Tests` и `OutputType=Exe` + `PackageReference Include="TUnit"` (без версии, берётся из CPM). **Правильно.**
- `tests/Directory.Build.props:7` — дублирует `IsTestProject=true`. **OK, избыточно но не ломает.**
- `Directory.Packages.props:71-73` — пинит `Microsoft.NET.Test.Sdk@18.8.1` и `TUnit@1.61.0` (+ `TUnit.Assertions`). **Sdk нужно удалить.**
- `Directory.Build.props:5` — `TargetFramework=net10.0` глобально. **OK для TUnit.**

### 1.2 Где сломано

| Файл | Проблема | Тяжесть |
|------|----------|---------|
| `Directory.Packages.props:71` | `Microsoft.NET.Test.Sdk` pinned | **CRITICAL** — удалять |
| `tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj:15` | явный `PackageReference Include="Microsoft.NET.Test.Sdk"` | CRITICAL |
| `tests/Harbor.App.Avalonia.Tests/Harbor.App.Avalonia.Tests.csproj:21` | — // — | CRITICAL |
| `tests/Harbor.App.Cli.Tests/Harbor.App.Cli.Tests.csproj:46` | — // — | CRITICAL |
| `tests/Harbor.E2E.App.Avalonia/Harbor.E2E.App.Avalonia.csproj:60` | — // — | CRITICAL |
| `tests/Harbor.E2E.Cli/Harbor.E2E.Cli.csproj:23` | — // — | CRITICAL |
| `tests/Harbor.E2E.Framework/Harbor.E2E.Framework.csproj:49` | — // — | CRITICAL |
| `tests/Harbor.Ipc.Tests/Harbor.Ipc.Tests.csproj:29` | — // — | CRITICAL |
| `tests/Harbor.Ui.Framework.Tests/Harbor.Ui.Framework.Tests.csproj:15` | — // — | CRITICAL |
| `tests/Harbor.Tui.CellForge.Tests/Harbor.Tui.CellForge.Tests.csproj:16` | — // — | CRITICAL |
| `tests/Harbor.Tui.CellForge.PtyTests/Harbor.Tui.CellForge.PtyTests.csproj:39` | — // — | CRITICAL |
| `tests/Harbor.LoadTests/Harbor.LoadTests.csproj:18` | — // — | CRITICAL |
| `contrib/tests/*/Harbor.E2E.*.csproj` (5 шт) | — // — | CRITICAL |
| `external/ConsoleEx/...` | `coverlet.collector` + `Microsoft.NET.Test.Sdk` 17.x/18.6 | OK — external, не трогать |

Всего: **11 прямых упоминаний** в `tests/` + 5 в `contrib/tests` + 1 pin в CPM = 17 точек.

**Что правильно (не трогать):**
- `OutputType=Exe` — уже ставится автоматом из `Directory.Build.targets:12`. Явных `<OutputType>Exe</OutputType>` в тестах нет, кроме `Harbor.Benchmarks` (не тест). Это **верно** — дублировать не нужно.
- `coverlet.collector` — в наших проектах **отсутствует** (только в `external/ConsoleEx`). Правильно, TUnit уже тащит `Microsoft.Testing.Extensions.CodeCoverage` транзитивно.
- `Program.cs` — в `tests/` **нет** `Program.cs` (только `tests/Harbor.Benchmarks/Program.cs` — не тест, ок). TUnit генерит `Main` через source generator, ручной `Program.cs` ломает сборку `CS0017`.
- `await Assert.That` — выборочная проверка `tests/Harbor.Abstractions.Tests/*.cs` показала 100% `await`. Нужно прогнать линтер на все `Assert.That` без `await` (см. скрипт ниже).

### 1.3 Мифы из твоего промпта (что НЕ нужно делать)

| Совет из «жёсткого» промпта | Reality в Harbor на .NET 10 |
|-----------------------------|------------------------------|
| Ставить `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` | На .NET 10 SDK + `IsTestProject=true` — **не нужно**. Свойство нужно было для .NET 8/9 чтобы `dotnet test` понял MTP. С .NET 10 достаточно `IsTestProject=true` (см. tunit.dev Troubleshooting → dotnet test vs dotnet run). Если оставишь — не сломает, но шум. |
| Удалять все `Microsoft.NET.Test.Sdk` | **Да, это единственный реальный фикс.** |
| Ставить `<OutputType>Exe</OutputType>` руками | Уже в `Directory.Build.targets:12`. Дублировать не надо, но если пропишешь явно — не сломает. |
| Ставить `<TestingPlatformShowTestsFailure>` | Опционально, для CI. |

### 1.4 Как должно выглядеть идеально (по доке)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Harbor.Core\Harbor.Core.csproj"/>
  </ItemGroup>
  <!-- Ничего про Microsoft.NET.Test.Sdk, ни про TUnit — всё инжектится из Directory.Build.targets -->
</Project>
```

И CPM:

```xml
<!-- Directory.Packages.props — оставить только TUnit -->
<PackageVersion Include="TUnit" Version="1.61.0" />
<PackageVersion Include="TUnit.Assertions" Version="1.61.0" />
<!-- УДАЛИТЬ: <PackageVersion Include="Microsoft.NET.Test.Sdk" .../> -->
```

Если нужен `dotnet test` (а не только `dotnet run`) — он уже работает через `Microsoft.Testing.Platform` без `Microsoft.NET.Test.Sdk` при `IsTestProject=true`. Проверено в шаблоне `dotnet new TUnit`.

---

## 2. Best practices / типичные проёбы (из tunit.dev)

1. **Забыл `await` на `Assert.That`** — тест всегда зелёный. Док: https://tunit.dev/docs/troubleshooting#why-do-i-have-to-await-assertions. Фикс: `await Assert.That(x).IsEqualTo(y);`. Включи анализатор TUnit (он ругается на не-awaited assertion).
2. **Нет `[Test]`** — класс не нуждается в `[TestClass]`/`[TestFixture]`. Только `[Test]` на методе. Док: troubleshooting → Missing `[Test]` attribute.
3. **Не-public / static тест-метод** — должны быть `public` instance.
4. **`coverlet.collector` + TUnit** — несовместимо, используй `--coverage` (встроено в TUnit meta). Док: installation → What's Included.
5. **Свой `Program.cs`/`Main`** — TUnit генерит точку входа. Удали.
6. **Запуск:** `dotnet run -c Release` (предпочтительно) или `dotnet test -c Release` (на .NET 10 флаги без `--`). На .NET 8/9: `dotnet test -- --report-trx --coverage`. Док: running-your-tests.
7. **Фильтры:** не VSTest syntax, а `--treenode-filter "/*/*/MyClass/*"` или `"/*/*/*/*[Category=Integration]"`. Док: troubleshooting → Test Filtering.
8. **Параллелизм по умолчанию** — все тесты параллельны. Для shared ресурса: `[NotInParallel]` или `[ParallelLimiter<T>] where T : IParallelLimit`. Док: things-to-know → Parallelisation.
9. **Новый инстанс класса на каждый тест** — не храни state в полях (сбросится). Используй `static` или `[ClassDataSource]`.

---

## 3. Что отдать агенту (copy-paste промпт)

Скопируй `AGENT_PROMPT.md` в этом же worktree — там готовый системный промпт для Cursor/Claude/OpenCode с чек-листом, командами верификации и Definition of Done.

Короткая версия для быстрой проверки:

```
Ты — эксперт по TUnit + Microsoft.Testing.Platform на .NET 10.
Проверь Harbor на соответствие https://tunit.dev/docs/getting-started/installation и /troubleshooting:

1. Найди все <PackageReference Include="Microsoft.NET.Test.Sdk"> и <PackageVersion Include="Microsoft.NET.Test.Sdk"> — их быть НЕ должно.
2. Убедись что каждый tests/**/*.csproj либо наследует IsTestProject=true из Directory.Build.targets/props, либо имеет его явно, и что OutputType=Exe резолвится (dotnet msbuild -getProperty:OutputType == Exe).
3. Убедись что нет tests/**/Program.cs.
4. Убедись что нет coverlet.collector.
5. Проверь что все Assert.That await'ятся (grep -rn "Assert\.That" --include="*.cs" | grep -v "await Assert").
6. Проверь что тесты имеют [Test] и public instance методы.
7. Собери и запусти: dotnet build -c Release && dotnet run --project tests/Harbor.Core.Tests -c Release -- --minimum-expected-tests 1  и  dotnet test -c Release -- --treenode-filter "/*/*/*/*"
```

---

## 4. План фикса (для этого worktree)

### Step 1 — удалить Microsoft.NET.Test.Sdk (CRITICAL)

- Удалить `<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />` из `Directory.Packages.props:71`
- Удалить `<PackageReference Include="Microsoft.NET.Test.Sdk"/>` из 11 файлов в `tests/` и 5 в `contrib/tests/` (список выше)
- Удалить комментарии про «test host adapter — required for dotnet test» — они ложные для TUnit (оставь только «TUnit is auto-included via IsTestProject»)

### Step 2 — верификация (без изменения `dotnet test` поведения в AGENTS.md до фикса)

```bash
# 1. Проверить что OutputType всё ещё Exe
dotnet msbuild tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj -getProperty:OutputType
# → Exe

# 2. Сборка
dotnet build -c Release

# 3. Запуск через MTP напрямую (как в AGENTS.md)
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1
dotnet run --project tests/Harbor.Abstractions.Tests -c Release --no-build -- --minimum-expected-tests 1

# 4. Теперь dotnet test должен перестать падать с exit 5
dotnet test tests/Harbor.Core.Tests -c Release -- --minimum-expected-tests 1
dotnet test tests/Harbor.Core.Tests -c Release -- --treenode-filter "/*/*/IdentifiersTests/*"

# 5. Проверить отсутствие забытых await
grep -rn "Assert\.That" tests --include="*.cs" | grep -v "await Assert" | grep -v "//"

# 6. Проверить что нет Program.cs и coverlet
find tests -name "Program.cs" | grep -v Benchmarks
grep -r "coverlet" tests --include="*.csproj" --include="*.props"
```

### Step 3 — обновить документацию

- `AGENTS.md` и `CLAUDE.md`: заменить «dotnet test discovers ZERO tests — never use it» на актуальные команды после фикса
- `tests/Harbor.App.Cli.Tests/Harbor.App.Cli.Tests.csproj` комментарий: убрать ложь про «Microsoft.NET.Test.Sdk is required»

### Step 4 — IDE

- Rider: Settings → Build, Execution, Deployment → Unit Testing → Testing Platform → Enable Testing Platform support
- VS: Tools → Options → Preview Features → Use testing platform server mode
- VS Code: C# Dev Kit → Use Testing Platform Protocol

---

## 5. Скрипт для автопроверки

См. `scripts/verify-tunit-mtp.sh` в этом worktree — гоняет все проверки из Step 2 одним запуском.

---

## 6. Почему Harbor так оказался (контекст)

- `Microsoft.NET.Test.Sdk` был добавлен до миграции на TUnit/MTP, когда тесты ещё были на VSTest. При переходе на TUnit его забыли вычистить, а `Directory.Build.targets:12` уже обеспечивал `OutputType=Exe` и `TUnit` — получилось гибридное состояние: сборка идёт, но `dotnet test` идёт через VSTest-хост и падает с `host exits 5`.
- Комментарии в `Harbor.App.Cli.Tests.csproj:41` прямо вводят в заблуждение: «Microsoft.NET.Test.Sdk is the test host adapter — required for dotnet test to discover and run the TUnit engine» — это неверно, TUnit discovery идёт через source generator + MTP, без VSTest.

## 7. Ссылки

- https://tunit.dev/docs/getting-started/installation — главный источник про запрет Microsoft.NET.Test.Sdk и coverlet
- https://tunit.dev/docs/getting-started/running-your-tests — dotnet run vs dotnet test vs dotnet exec
- https://tunit.dev/docs/troubleshooting — Tests Not Discovered, IDE Setup, Test Filtering, Code Coverage
- https://tunit.dev/docs/writing-tests/things-to-know — Parallelisation, Instance Data
- `Directory.Build.targets:11-19`, `tests/Directory.Build.props:1-13`, `Directory.Packages.props:71-77`
