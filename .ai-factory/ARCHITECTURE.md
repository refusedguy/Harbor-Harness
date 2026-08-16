# Архитектура Harbor (Clean / Hexagonal Layered Architecture)

> Архитектурное руководство для проекта Harbor. Определяет разделение слоев, правила зависимостей и потоки данных.

## 1. Обзор архитектурного паттерна

Harbor построен на основе **Чистой / Гексагональной (Clean / Hexagonal) архитектуры**.
Главный принцип — **Правило Зависимостей (Dependency Rule)**: внешние слои зависят от внутренних, но внутренние слои никогда не знают о внешних.

```
       [ Harbor.Cli / Harbor.Tui.* ]  (Презентация / Точка входа)
                      │
                      ▼
 [ Harbor.Providers.* / Harbor.Tools.* / Harbor.Storage.* ]  (Инфраструктура)
                      │
                      ▼
               [ Harbor.Core ]  (Бизнес-логика агента)
                      │
                      ▼
           [ Harbor.Abstractions ]  (Ядро домена / Контракты)
```

---

## 2. Структура слоев и правило зависимостей

| Слой | Проект / Модуль | Назначение | Зависимости |
|---|---|---|---|
| **Domain & Abstractions** | `Harbor.Abstractions` | Интерфейсы (`ILlmClient`, `ITool`, `ISessionStore`), модели, типизированные Value Objects (`SessionId`, `ProviderId`), события (`AgentEvent`). | **Ноль внешних зависимостей** |
| **Application & Core** | `Harbor.Core` | Исполнительный цикл агента (`AgentLoop`), шина событий (`EventBus`), реестры инструментов и провайдеров, компактификация контекста. | Зависает ТОЛЬКО от `Harbor.Abstractions` |
| **Infrastructure & Adapters** | `Harbor.Storage.*`<br/>`Harbor.Providers.*`<br/>`Harbor.Tools.Builtin`<br/>`Harbor.Plugins.Runtime` | Реализация провайдеров LLM (Anthropic, OpenAI, Ollama), хранилищ сессий (JSONL, SQLite) и 14 встроенных инструментов. | Зависят от `Harbor.Abstractions`. Некоторые адаптеры могут зависеть от `Harbor.Core` при необходимости. |
| **Presentation & Host** | `Harbor.Cli`<br/>`Harbor.Tui.*` | Сборка DI-контейнера (`HostBuilder`), консольный интерфейс, рендереры TUI (Ansi, Plain, Spectre), обработка команд CLI. | Зависят от `Core`, `Abstractions` и необходимых инфраструктурных модулей. |

---

## 3. Матрица запрещенных зависимостей

1. **`Harbor.Abstractions`** НЕ ДОЛЖЕН иметь ссылок ни на какие другие проекты solution (`<ProjectReference>` запрещены).
2. **`Harbor.Core`** НЕ ДОЛЖЕН зависеть от TUI (`Harbor.Tui.*`), CLI (`Harbor.Cli`) или конкретных провайдеров (`Harbor.Providers.*`).
3. **`Harbor.Tui.Abstractions`** НЕ ДОЛЖЕН зависеть от `Harbor.Core`. Все состояние TUI поступает исключительно через события `AgentEvent`.
4. Соблюдение матрицы зависимостей механически проверяется в CI с помощью NetArchTest и отражательных тестов в `tests/Harbor.Architecture.Tests`.

---

## 4. Основные потоки данных (Data Flows)

### A. Поток выполнения команды пользователя (`AgentLoop`)

```
User Message → SystemPromptBuilder → ILlmClient.StreamAsync
                                            │
                                  (MessageUpdateEvent)
                                            │
                                            ▼
                                     ToolCallEvent
                                            │
                                 ToolRegistry.GetTool
                                            │
                                     ITool.ExecuteAsync
                                            │
                                     ToolResult
                                            │
                             (Следующий шаг диалога...)
```

### B. Шина событий (`IEventBus`)

Все компоненты общаются в асинхронном режиме без жесткой связности:
- `AgentStartEvent`, `AgentEndEvent`
- `TurnStartEvent`, `TurnEndEvent`
- `MessageStartEvent`, `MessageUpdateEvent`, `MessageEndEvent`
- `ToolExecutionStartEvent`, `ToolExecutionEndEvent`

---

## 5. Конвенции разработки и код

### Создание Value Objects (Result Pattern)

```csharp
// Использование встроенных фабричных методов с возвратом Result<T>
var providerIdResult = ProviderId.TryCreate("anthropic");
if (providerIdResult.IsFailure)
{
    return Result.Failure<ILlmClient>(providerIdResult.Error);
}
```

### Регистрация инструментов в DI (`HostBuilder.cs`)

```csharp
// Регистрация инструмента с синглтон-зависимостями
builder.AddTool(sp => new MyCustomTool(
    sp.GetRequiredService<IMyService>(),
    sp.GetRequiredService<ILogger<MyCustomTool>>()));
```

---

## 6. Проверка архитектуры

Для проверки соответствия архитектуре и слоям выполните:

```bash
dotnet test tests/Harbor.Architecture.Tests
```
