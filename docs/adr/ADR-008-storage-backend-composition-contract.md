# ADR-008: контракт storage-бэкенда при композиции (спринт 5v2, Ф2)

- Статус: принято
- Дата: 2026-08-24
- Решение: спринт 5v2 зона B, шаг верификации Ф2 (зеркала пресетов §6.3 Ф2.7)
- Затрагивает: Harbor.Desktop.Abstractions (CommonConfig), Harbor.Hosting
  (ConfigurationModule, StorageModule), Harbor.App.Cli, Harbor.App.Avalonia

## Контекст

После сведения графа DI в `Registration.AddHarbor` зеркальные тесты пресетов
(`tests/Harbor.Hosting.Tests`) вскрыли, что десктопный пресет
`DefaultStorageBackend = "memory"` мёртв: `StorageModule` выстраивает
приоритет `env HARBOR_STORAGE → CommonConfig.StorageBackend → preset`,
но `CommonConfig.StorageBackend` имел непустой дефолт `"jsonl"` — пресет
не достигался никогда, а значение из конфига невозможно было отличить от
дефолта. Попутно выяснилось, что `ConfigurationModule` грузил CommonConfig из
процессного `~/.harbor` игнорируя `options.HarborDir`: встроенные хосты и
тесты получали конфиг разработчика (на машине верификации в реальном
`~/.harbor/config.json` был явно записан `"storageBackend": "jsonl"`).

## Решение

1. **Empty-unset контракт**: `CommonConfig.StorageBackend` по умолчанию `""`
   («не выбран»). Все потребители уже страхуются `IsNullOrEmpty → "jsonl"`
   (`CompositeConfig`, `OnboardingViewModel`, `SettingsViewModel`) — рябь нулевая.
   Выбор теперь честно распределяется по приоритету:
   `HARBOR_STORAGE` → явное значение из config.json → пресет приложения
   (CLI: `jsonl`, desktop: `memory`).
2. **Hermetic scope**: `ConfigurationModule` создаёт `JsonCommonConfigStore`
   с `new CommonConfig { ConfigDirectory = options.HarborDir }` — CommonConfig
   читается из `<HarborDir>/config.json`, а не из процессного home. Для CLI
   `HarborDir` == реальный home (поведение прежнее); для embedded/E2E/тестов
   граф конфигов замкнут на переданный каталог.

## Последствия

- Десктопный дефолт «memory» из дизайн-дока (§1.2) впервые стал реальным;
  CLI-поведение (jsonl) не изменилось — зафиксировано типовой ассерцией
  `ISessionStore → JsonlSessionStore` в `HostBuilderDiTests`.
- Тесты, полагавшиеся на литеральный дефолт `"jsonl"` у свойства, переведены
  на поведенческие ассерты (тип резолвимого стора).
- Композиционные тесты `tests/Harbor.Hosting.Tests` помечены
  `[NotInParallel]`: пин env-переменных — глобальное состояние процесса.

## Верификация

- `dotnet run --project tests/Harbor.Hosting.Tests` — 8/8.
- `dotnet run --project tests/Harbor.App.Cli.Tests` — 43/43.
- `dotnet run --project tests/Harbor.App.Avalonia.Tests` — 210/211
  (1 предсуществующий headless-сбой ChatView_Inflates, воспроизводится 1:1
  на e6c9128 — к данному решению отношения не имеет).
- `dotnet run --project tests/Harbor.Ui.Framework.Tests` — 53/53.
