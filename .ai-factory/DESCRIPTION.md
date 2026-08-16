# Harbor — Модульный .NET 10 AI Coding Harness

## Обзор проекта

Harbor — это высокопроизводительный, модульный AI-каркас (coding agent harness) на платформах .NET 10 / C#, созданный с акцентом на NativeAOT, минимизацию расхода оперативной памяти (<30 MB RSS в режиме ожидания) и строгую чистую архитектуру (Clean / Hexagonal Architecture).

## Ключевые возможности и архитектура

- **Модульная архитектура агента**: Каждая подсистема скрыта за интерфейсом (`ILlmClient`, `ITool`, `ISessionStore`, `ITuiRenderer`).
- **Цикл агента (`AgentLoop`)**: Поддержка многошаговых диалогов, вызова инструментов (tool calls), стриминга ответов и автоматической компактификации контекста.
- **Шина событий (`IEventBus`)**: Публикация строго типизированных событий (`AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`, `ToolExecutionStartEvent` и др.) для реактивного обновления TUI и логгеров.
- **Разнообразие TUI рендереров**:
  - `ANSI` (стриминговый консольный рендерер по умолчанию)
  - `Plain` (для CI/CD и пайплайнов)
  - `Spectre` / `Spectre.Fullscreen` (полноэкранный интерактивный интерфейс)
- **Встроенные инструменты (14 tools)**: Чтение/запись/редактирование файлов (`read`, `write`, `edit`), выполнение команд (`bash`), поиск (`glob`, `grep`, `ripgrep`, `ls`, `tree`), мульти-агентные задачи (`task`), веб-интеграция (`webfetch`), подсистема плагинов MCP (`mcp`) и патчей (`patch`, `notebook`).
- **Провайдеры LLM**: Родная поддержка Anthropic Messages API, OpenAI (Chat + Responses API), Ollama и универсальный OpenAI-Compatible адаптер с 13+ готовыми конфигурациями провайдеров.
- **Динамическая подсистема плагинов**: Загрузка C#-исходников плагинов "на лету" с помощью Roslyn (`Harbor.Plugins.Runtime`) и классических DLL-плагинов.
- **Система разрешений (Permissions)**: Гибкие правила выполнения команд и инструментов (Allow, Ask, Deny).

## Стек технологий

- **Язык программирования:** C# 13 / .NET 10 SDK
- **Архитектура:** Clean / Hexagonal Layered Architecture
- **Консольный UI:** Spectre.Console 0.57.2, ANSI streaming engine
- **Хранилище сессий:** JSONL (по умолчанию), SQLite (`Microsoft.Data.Sqlite`), In-Memory
- **Компиляция плагинов:** Microsoft.CodeAnalysis.CSharp (Roslyn runtime compilation)
- **Тестирование:** TUnit (334+ модульных и E2E тестов, архитектурные тесты NetArchTest)

## Архитектурные принципы

- **Строгое разделение слоев:** Иерархия зависимостей `Abstractions` <- `Core` <- `Providers`/`Tools`/`Storage` <- `Cli` / `Tui`. Нарушение слоев контролируется архитектурными тестами `Harbor.Architecture.Tests`.
- **Zero Allocations & Performance:** Использование `FrozenDictionary`, `ArrayPool<T>`, `ReadOnlySpan<char>`, `StringBuilderPool`.
- **Функциональная обработка ошибок:** Использование `Result<T>` вместо выброса исключений для бизнес-ошибок.
- **Типизированные идентификаторы (Value Objects):** `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName`.

## Нефункциональные требования

- **Производительность:** Холодный старт <40 мс, потребление памяти в idle <30 МБ, размер бинарника ~5 МБ.
- **Логирование:** Microsoft.Extensions.Logging (`ILogger`), ротация лог-файлов `FileLogger`.
- **Безопасность:** Проверка прав доступа перед выполнением bash-команд и инструментов, отсутствие `unsafe` кода.
