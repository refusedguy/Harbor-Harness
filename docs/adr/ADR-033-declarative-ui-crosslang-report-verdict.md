# ADR-033: отчёт по кросс-языковому declarative UI (#2699) — гибрид уже в дереве

> **Status (2026-11-18):** dated record, not normative. Источник — issue
> [#2699](https://github.com/refusedguy/Harbor-Harness/issues/2699),
> self-contained competitor research по семи фреймворкам. Этот PR меняет только
> два markdown-файла: код, тесты, гейты и пороги не трогаются. Факты проверены
> через `gh pr list` и инвентаризацию `origin/dev` (`09a673bc`); локальные
> `dotnet`, сборка и тесты по условию смены не запускались.

## Status

**Decided:** новый code slice не открывается. Главный вопрос отчёта, sourcegen
или интерпретатор, решён гибридом, и этот гибрид уже влит в DSL-платформу
CellForge. Отчёт остаётся OPEN как источник сравнения и этим PR не закрывается.

## Context

Issue #2699 сравнивает WPF/UWP XAML, Flutter, React, SwiftUI, Jetpack Compose,
Dioxus/Leptos и Textual, а также ImGui как контраст. Вопрос владельца: нужен ли
source generator или хватит интерпретатора разметки в рантайме. Отчёт отвечает
гибридом: разметка компилируется в типизированный builder на сборке, а тот же
парсер работает в dev-режиме как интерпретатор с diff по живому дереву.

Проверка `gh pr list --search declarative` и `gh pr list --state merged --search dsl`
показала, что рекомендация отчёта совпадает с уже принятой архитектурой Harbor:
общий парсер, Roslyn generator, dev-интерпретатор и стабильные element id.

## Сравнение по фреймворкам

| Фреймворк | Что берём | Что не берём | След в Harbor |
|---|---|---|---|
| XAML (WPF/WinUI) | Переприменение фрагмента к живому дереву без потери состояния VM | BAML, дизайнер, зависимость от отладчика | Dev-патч дерева в #2727 |
| Flutter | Неизменяемый дескриптор + постоянный рендерер + diff | Инъекция кода в Dart VM | `SpecArena` (#2489) и cell-diff |
| React | Состояние по позиции и ключу, не по тексту разметки | Подмена функций бандлером | `.id`, keyed children (#2722), element id (#2442) |
| SwiftUI | Превью на sample data вне живого сеанса | Отдельный preview-процесс Xcode | Headless-деревья (#2761, #2524) |
| Compose | Headless-рендер дерева без агента и эмулятора | Layoutlib, multipreview, live edit студии | Automation-протокол (#2761) |
| Dioxus / Leptos | Один парсер на сборку и dev, template diff, переиспользование слотов, fallback на пересборку | Subsecond, dylib, jump table, ASLR | #2489 + #2727, один `MarkupParser` |
| Textual / ImGui | Вотчер файла описания и отдельный канал логов; стабильный ID при смене метки | Интерпретатор как единственный путь; отказ от декларативности | #2760 (`DslDevFileWatcher`, `DevLogChannel`), `.id` |

Dioxus ближе всех к Harbor по механике, но его Subsecond-патчи Rust-функций
требуют JIT и несовместимы с NativeAOT. Leptos предостерегает от одного DSL на
все native GUI; Harbor не строит такой DSL.

## Пять заимствований отчёта и их статус

| # | Заимствование | Статус | Подтверждение |
|---|---|---|---|
| 1 | Template diff Dioxus | влито | #2727 — `DevReloadInterpreter` патчит живое дерево, rejects при изменении логики |
| 2 | Динамические слоты и стабильные ID | влито | #2722 keyed children, #2442 `ElementIdStack` с state по `(GlobalElementId, TypeId)` |
| 3 | Раздельные dev-каналы Textual | влито | #2760 — `DslDevFileWatcher` и `DevLogChannel` вне cell-сетки |
| 4 | Неизменяемый дескриптор и правило позиции | влито | #2489 `SpecArena`, #2531 BuildRoot с sequence numbers как ключами cell-diff |
| 5 | Headless-рендер на sample data | частично влито | #2761 `HeadlessTree` (getTree, clock, testId) и #2524 `CompositionPilot`; отдельной политики «нет сети и файлов» для терминального preview нет |

Пять пунктов таблицы — это не пять новых задач. Это пять описаний того, что
уже работает, и попытка переписать их создала бы второй источник истины.

## Decision

Код не писать. Конкретно:

- Гибрид из отчёта принят как описание существующего пути: `MarkupParser`
  разбирает `.harbor-ui` в `SpecArena`, `MarkupGenerator` эмитирует обычный C#
  (#2443, #2477, #2531, #2534), а dev-сборка использует тот же парсер в
  `DevReloadInterpreter` (#2727). Второго парсера не добавляем.
- Subsecond-патчи, dylib, jump table и любую JIT-инъекцию кода не вводим:
  запрет динамического кода в `src/` и `apps/` и цель NativeAOT это исключают.
- Загрузку разметки из плагинов с полным доверием в prod, DOM- и GPU-зависимые
  абстракции и единый DSL на все рендереры не вводим. Это owner-decision из
  отчёта, а не пропущенный slice.
- Оконный previewer, стили и селекторы, SRE-компиляция остаются за пределами
  терминального DSL; тот же вердикт зафиксирован в
  [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md).
- Отдельный guard для политики «нет сети и файлов» в preview не добавляем:
  реального previewer с sample data в терминале нет, а headless-тесты уже
  изолированы (#2761, #2524). Гейты и пороги не меняются.

## Consequences

- Меняются только этот ADR и [DECISIONS.md](DECISIONS.md). Риск ограничен
  документационными гейтами `docs.yml`: `check-md-links` и `md-lint`
  прогоняются до PR.
- Issue #2699 остаётся OPEN: это отчёт, а не трекер одной задачи.
- Пересмотр — по новому факту: появление требования preview с sample data,
  снятие запрета на динамический код в AOT-сборке или решение владельца
  строить единый DSL для нескольких рендереров. В каждом случае открывается
  новый issue и новый ADR, а не новая реализация внутри этого отчёта.

## References

- Issue [#2699](https://github.com/refusedguy/Harbor-Harness/issues/2699) —
  полный текст кросс-языкового обзора.
- Merged PR: #2442, #2443, #2477, #2489, #2524, #2531, #2534, #2722, #2727,
  #2760, #2761, #2768.
- [ADR-028](ADR-028-avalonia-declarative-xaml-dsl-verdict.md) — вердикт по
  Avalonia AXAML, смежной теме.
- [DECISIONS.md](DECISIONS.md).
