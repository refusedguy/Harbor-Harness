# ADR-007: полное отделение Harbor.Abstractions от Domain и Extensions (спринт 5v2, Ф1)

- Статус: принято
- Дата: 2026-08-24
- Решение: спринт 5v2 зоны B, «без компромиссов»
- Затрагивает: src/Harbor.Abstractions, src/Harbor.Abstractions.Contracts,
  src/Harbor.Extensions, tests/Harbor.Architecture.Tests

## Контекст

После split'а round-6 (A1) `Harbor.Abstractions` был тонким фасадом с двумя
ProjectReference: `Harbor.Domain` (модели) и `Harbor.Extensions` (пул-хелперы).
Потребители (~78 проектов) получали оба слоя транзитивно через фасад:

```
Harbor.Domain       → 0
Harbor.Extensions   → Harbor.Domain   (мёртвая ссылка: хелперы генерик)
Harbor.Abstractions → Domain + Extensions (фасад)
```

Проблемы:

1. Фасад тянет инфраструктурные хелперы всем потребителям абстракций — лишняя
   trim/AOT-поверхность для встраиваемых сценариев.
2. Любая правка Domain/Extensions пересобирала весь граф транзитивно.
3. Слойing был фиктивным: «чистые абстракции» физически не существовали —
   любой, кто ссылается на интерфейсы, тащит и модели, и пулы.

## Решение

**Harbor.Abstractions.Contracts** — новый чистый контрактный проект; весь состав
бывшего `Harbor.Domain` переехал туда целиком, `Harbor.Domain` удалён.
Namespace'ы сохранены дословно (`Harbor.Abstractions.{Models,Events,Permissions,
Models.Identifiers}`) — ни один `using` в репозитории не изменён.

Итоговая топология (механически проверяется
`AbstractionsSplitLayerRules.cs`):

```
Harbor.Abstractions.Contracts → 0          (контракты: данные + правила)
Harbor.Extensions              → 0          (BCL/NuGet-хелперы)
Harbor.Abstractions            → Contracts  (подписи интерфейсов)
```

Пул-хелперы больше не ре-экспортируются фасадом: прямые потребители
(`Harbor.Application`, `Harbor.Tools.Builtin`, `tests/Harbor.Architecture.Tests`)
ссылаются на `Harbor.Extensions` явно.

## Почему закрытие ПОЛНОЕ (без остаточного Harbor.Domain)

Чеклист §6 дизайн-дока допускал компромисс: оставить в Domain типы, не нужные
подписям интерфейсов. Инвентаризация (Ф1.1) показала, что замыкание на
интерфейсно-типизированные типы покрывает **весь** Domain:

| Тип / связь | Кто требует |
|---|---|
| `PermissionRuleset`, `PermissionRule`, `PermissionAction` | подписи `AgentDefinition.Permission`, `ITool`, `IToolSource`, `IToolRegistry` |
| `BashArgMatcher` | жёсткий вызов из `PermissionRuleset.Evaluate` (строки 192–198) — не оторвать без переписывания логики прав |
| `Pricing` | свойство `ModelInfo.Pricing`; `ModelInfo` в подписях `ILlmClient`/`IProviderRegistry` |
| `FileAttachment`, `IdentifierValidation`, `JsonElementMemoryPackFormatter` | внутренний граф Messages/Identifiers + `ToolCallPart.StaticConstructor()` |

Остаточный Domain был бы пуст. Оставлять проект-пустышку ради буквы метрики
отказались.

## Последствия для rebuild-set (честная фиксация)

Метод замера: `touch` всех `.cs` слоя → `dotnet build Harbor.slnx --no-restore`
→ счёт строк ` -> ` (артефакт-строки = пересобранные проекты). Подробности и
числа — `docs/BUILD.md`.

- Правка **Extensions**: 78 → **4** проекта (Extensions + 3 прямых потребителя).
  Главный численный выигрыш Ф1.
- Правка **моделей** (бывший Domain, ныне Contracts): 78 → 78. Размер каскада
  определяется множеством проектов, реально потребляющих типы, а не формой рёбер:
  модели юзают все 78. Цель «≤35 при правке Domain» численно недостижима без
  разрыва реальных цепочек потребления (см. таблицу выше) — зафиксировано как
  осознанный отказ, а не недоработка.
- Правка **Abstractions**: каскад прежний по размеру, но перестал тащить
  Extensions-поверхность.

## Жертвы и компромиссы

1. `[TypeForwardedTo]` сознательно не применяется (вернул бы зависимость
   Contracts→старый Domain либо сломал чистоту). Все потребители пересобираются
   монолитно в одном репо — бинарная совместимость со старыми сборками не нужна.
2. `tests/Harbor.Domain.Tests` сохраняет имя (тестирует доменные модели), но
   перепрофилирован на ссылку Contracts. Переименование сочтено шумом.
3. Транзитивная видимость моделей через фасад сохранена (Contracts за фасадом),
   поэтому ~40 потребителей НЕ потребовали правок csproj — чеклист §6.2 шаг Ф1.7
   («~40 механических правок») выродился в 6 точечных репоинтов
   (Plugins.Host, Plugins.Compilation, Benchmarks, Domain.Tests, 2 slnx).

## Верификация

- `dotnet build Harbor.slnx` — 0 ошибок (warn-профиль не хуже baseline 159→144).
- Architecture.Tests 40/40 (обновлённые правила слоя).
- HostBuilderDiTests 28/28, AppHostDiTests 28/28 (DI обоих приложений).
- Abstractions.Tests 35/35.
- Harbor.Domain.Tests 16/22; 6 падений (`BashArgMatcherTests`, OOM в StringBuilder)
  воспроизводятся 1:1 на чистом e6c9128 — предсуществующий сбой песочницы,
  к переносу отношения не имеет.
