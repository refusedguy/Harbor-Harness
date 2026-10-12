# SG3: спайк генератора ITool-glue (Gobie vs свой) + AutoCtor

Датированная запись спайка по issue 1244. Статус: normative для объёма спайка,
не для дерева. Вердикт в §6 предварительный, финальный — по зелёному CI.

## 1. Постановка

20+ builtin tools несут ~40 строк glue на тул (Name, DisplayName, Glyph,
Description, ExecutionMode, PromptSnippet, PromptGuidelines, ParameterSchema,
ValidateArguments) — порядка 800 строк ручного кода без готового off-shelf
покрытия. Плюс ~30 сайтов ручных `ctor(ILogger<T>) { _logger = logger; }`.

## 2. Кандидаты

### 2.1. Gobie — шаблоны без Roslyn API

Метаданные пакета на момент спайка (октябрь 2026):

- Последний релиз: 0.5.0-alpha от мая 2022. Четыре года без релизов.
- Суммарные загрузки: порядка 2 тысяч. Для сравнения у AutoCtor — 262 тысячи.
- README репозитория GobieGenerator/Gobie: "in early development", dev log
  в блоге автора. Проект не вышел из ранней альфы.
- Целевой фреймворк генератора: netstandard2.0 эпохи Roslyn 3.x/4.0.
  В дереве Roslyn 5.6, SDK .NET 10, TreatWarningsAsErrors.

Вывод desk-стадии: тянуть заброшенную альфу 2022 года в сборку с
TreatWarningsAsErrors и Roslyn 5.6 — неприемлемый риск. Кодовая рука спайка
для Gobie не выполняется сознательно: спайк не обязан компилировать
заведомо мёртвый вариант, достаточно зафиксировать доказательства.
Gobie — REJECT.

### 2.2. Свой инкрементальный генератор в Harbor.CodeGen

Прецедент в дереве: EscapeCodeGenerator, MoodFrameGenerator,
RendererAdapterGenerator, ResourceKeyGenerator — все IIncrementalGenerator,
матчинг атрибутов по metadata name (без проектных ссылок из генератора),
Microsoft.CodeAnalysis.CSharp 5.6.0 из дерева, PrivateAssets=all на стороне
потребления не нужен вовсе (потребление — ProjectReference
с OutputItemType=Analyzer, рантайм-сборки нет).

Архитектурно чисто: analyzer-ребро проверяется только по цели
(SourceGeneratorProjects содержит Harbor.CodeGen), нового потребителя
декларировать нигде не надо — проверено чтением EnforcerIntegrityTests.
Ноль внешних зависимостей. Стратегически свой — TAKE при зелёном CI.

### 2.3. AutoCtor (distantcam) для ctor(ILogger)

Метаданные на момент спайка:

- Живой: 4.0.0 от 7 октября 2026, ветка 3.1.x стабильна с августа 2026,
  суммарно 262 тысячи загрузок. Модель использования для спайка:
  `[AutoConstruct]` на partial-классе, ручной ctor удаляется.
- Диагностика ACTR007 (ручной ctor, сворачиваемый в AutoConstruct) —
  severity Info по AnalyzerReleases.Shipped.md, под TreatWarningsAsErrors
  не красный. Ошибки ACTR010+ относятся только к [ServiceProvider],
  который спайк не использует.
- Для спайка фиксируется 3.1.1, а не свежий 4.0.0: мажор четырёхдневной
  давности с новым ServiceProvider-генератором не нужен для проверки
  гипотезы про ctor. Переоценка 4.x — follow-up.
- Потребление строго по правилу: PackageVersion в Directory.Packages.props,
  PackageReference с PrivateAssets=all на проекте-потребителе.

## 3. Руки спайка в коде

Рука A (свой генератор), тулы Bash, Glob, Grep:

- Новый `ToolInfoAttribute` в Harbor.Abstractions (дом ITool):
  позиционные name/displayName/description, именованные Glyph,
  ExecutionMode, PromptSnippet, PromptGuidelines (string[]).
- Новый `ToolGlueGenerator` в Harbor.CodeGen: матчинг по имени атрибута,
  эмиссия partial-членов Name/DisplayName/Glyph/Description/
  ExecutionMode/PromptSnippet/PromptGuidelines в файл
  `ToolGlue_<Class>.g.cs`. Schema и Validate остаются ручными —
  это follow-up, не спайк.
- Три тула становятся partial, получают атрибут, ручной скалярный glue
  удаляется. SafetyProfile, ParameterSchema, Validate, Execute — без изменений.

Рука B (AutoCtor), те же три тула:

- Пакет AutoCtor 3.1.1, PrivateAssets=all, в Harbor.Tools.Builtin.
- `[AutoConstruct]` на классах, ручные `ctor(ILogger<T>)` удаляются.
- Руки A и B намеренно пересекаются на одних классах: два генератора
  (ToolGlue + AutoCtor) на одном partial-классе, доказательство
  отсутствия конфликтов — зелёный CI.

## 4. Что НЕ входит в спайк

- Перенос ParameterSchema/ValidateArguments в генерацию (отдельная работа).
- Конвертация остальных ~26 ctor-сайтов под AutoCtor.
- Переоценка AutoCtor 4.x и ServiceProvider-генератора.
- Любые изменения архитектурных тестов: не требуются (см. §2.2).

## 5. Критерии успеха

- CI зелёный на ветке спайка (сборка + Harbor.Tools.Builtin.Tests
  + Harbor.Architecture.Tests как минимум).
- Существующие тесты тулов проходят без правок: сгенерированный ctor
  принимает NullLogger, сгенерированный glue отдаёт те же значения
  (покрыто новым ToolGlueSpikeTests).
- Диагностика ACTR007 не краснит остальные ручные ctor (severity Info).

## 6. Вердикт (предварительный, до CI)

- Gobie: REJECT — заброшенная альфа 2022 года (§2.1).
- Свой генератор: TAKE при зелёном CI — ноль внешних зависимостей,
  прецедент и arch-конвенции в дереве.
- AutoCtor 3.1.1: TAKE при зелёном CI — живой мейнтейн, Info-диагностика,
  PrivateAssets=all, композиция с рукой A на одних классах.
