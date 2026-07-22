# Базовые правила проекта Harbor

> Автоматически определенные конвенции и соглашения на основе анализа кодовой базы. Редактируйте при необходимости.

## Соглашения об именовании

- Файлы: PascalCase (`AgentLoop.cs`, `SessionId.cs`, `ILlmClient.cs`)
- Переменные и параметры: camelCase (`sessionStore`, `cancellationToken`)
- Приватные поля: `_camelCase` (`private readonly ILogger _logger`)
- Свойства и методы: PascalCase (`RunAsync`, `GetClient`, `IsSuccess`)
- Классы и интерфейсы: PascalCase (`Result<T>`, `ITool`, `BaseTuiRenderer`)

## Модульная структура

- `src/Harbor.Abstractions/` — интерфейсы, доменные модели, события, ValueObjects (ноль зависимостей)
- `src/Harbor.Core/` — AgentLoop, EventBus, реестры инструментов и провайдеров, конфигурация
- `src/Harbor.Storage.*/` — реализация хранилищ (JSONL по умолчанию, SQLite, In-Memory)
- `src/Harbor.Providers.*/` — клиенты LLM провайдеров (Anthropic, OpenAI, Ollama, OpenAI-Compatible)
- `src/Harbor.Tools.Builtin/` — встроенные инструменты LLM (14 инструментов)
- `src/Harbor.Tui.*/` — TUI компоненты: ViewModels, Views, рендереры (Ansi, Plain, Spectre)
- `src/Harbor.Cli/` — точка входа CLI, сборка DI контейнера, консольные команды
- `src/Harbor.Plugins.Runtime/` — динамический плагинный движок на базе Roslyn

## Обработка ошибок

- Использование `Result<T>` для функциональной обработки ожидаемых ошибок без проброса исключений
- Исключения использовать только для непредвиденных системных сбоев
- Возврат подробных текстовых сообщений об ошибках в `Result.Failure(error)`

## Управление потоком выполнения (Control Flow)

- Предпочтите плоский читаемый поток выполнения с защитными условиями (guard clauses) и ранними возвратами (`return`/`continue`)
- Использование pattern matching для обработки событий `AgentEvent` и сообщений TUI
- Не допускайте глубокой вложенности условных операторов (`if`/`else`)

## Логирование

- Логирование через `Microsoft.Extensions.Logging.ILogger<T>`
- Структурированное логирование аргументов и событий
- `FileLogger` в `Harbor.Cli` для записи логов работы агента

## Тестирование

- Фреймворк тестирования `TUnit` (`[Test]`, `Assert.That(...)`)
- Архитектурные тесты в `tests/Harbor.Architecture.Tests/` (проверка Clean/Hexagonal слоев)
- E2E тесты с поддержкой скрытия под `HARBOR_E2E`
