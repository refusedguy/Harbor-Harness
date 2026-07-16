# 02 — Plugin система

> Документ: контракт плагина, discovery, загрузка, изоляция, hot-reload, AOT-совместимость. Самый сложный раздел — plugin loading под NativeAOT требует компромиссов.

## 1. Цели и не-цели

### Цели

1. **Модульность** — пользователь добавляет tool/provider/agent через `~/.harbor/plugins/foo.dll`, без пересборки ядра.
2. **Изоляция** — упавший плагин не валит весь процесс. Утечка памяти в плагине — выгружаемым.
3. **AOT-совместимость** — `harbor` binary должен работать под NativeAOT с подключёнными плагинами.
4. **Hot-reload** — в dev-режиме изменение плагина → перезагрузка без рестарта harbor.
5. **Versioning** — плагин может декларировать совместимый диапазон версий harbor.
6. **Typed contract** — плагин пишется против `Harbor.Abstractions`, без тащения impl.

### Не-цели

1. **Sandboxing** — плагины запускаются с теми же permissions, что и сам harbor. Sandbox = process isolation (отдельный plugin-host процесс), что усложняет UX. MVP — без sandbox.
2. **Remote plugins** — плагины только локальные (DLL или NuGet package, установленный в `~/.harbor/plugins/`). Не загружаем код с URL.
3. **Cross-language plugins** — только .NET. Python/JS plugins — через MCP-протокол (out-of-process), не через in-process loading.
4. **Dynamic code generation** — никаких `Roslyn.CSharp.Scripting` в production (медленно, reflection-heavy). Только compiled DLLs.

## 2. Контракт плагина

### 2.1. `IPlugin` interface

```csharp
// Harbor.Abstractions/Plugins/IPlugin.cs

using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Plugins;

/// <summary>
/// Базовый контракт плагина. Реализуется в plugin DLL.
/// </summary>
public interface IPlugin
{
    /// <summary>Уникальное имя плагина. Используется для логов, конфига, permissions.</summary>
    string Name { get; }
    
    /// <summary>Версия плагина (Semantic Versioning).</summary>
    Version Version { get; }
    
    /// <summary>Минимальная совместимая версия Harbor.Abstractions.</summary>
    Version RequiredHarborVersion { get; }
    
    /// <summary>Описание для /plugin list.</summary>
    string Description { get; }
    
    /// <summary>
    /// Инициализация плагина. Регистрирует services в DI.
    /// Вызывается ОДИН раз при загрузке плагина.
    /// </summary>
    void Initialize(PluginContext context);
    
    /// <summary>
    /// Деинициализация. Освобождает ресурсы.
    /// Вызывается при shutdown или hot-reload.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);
}

/// <summary>
/// Маркерный interface для плагинов, которые регистрируют tools.
/// Harbor ищет все IToolPlugin реализации при загрузке.
/// </summary>
public interface IToolPlugin : IPlugin
{
    /// <summary>Регистрирует tools в реестре.</summary>
    void RegisterTools(IToolRegistryBuilder builder);
}

public interface IProviderPlugin : IPlugin
{
    void RegisterProviders(IProviderRegistryBuilder builder);
}

public interface IAgentPlugin : IPlugin
{
    void RegisterAgents(IAgentRegistryBuilder builder);
}

/// <summary>
/// Кастомные slash-команды.
/// </summary>
public interface ICommandPlugin : IPlugin
{
    void RegisterCommands(ICommandRouterBuilder builder);
}
```

### 2.2. `PluginContext`

```csharp
public sealed class PluginContext
{
    public IServiceCollection Services { get; init; }
    public IConfiguration Configuration { get; init; }
    public IPluginLogger Logger { get; init; }
    public HarborVersion HarborVersion { get; init; }
    public string PluginDirectory { get; init; }  // где лежит DLL + ресурсы
    public string DataDirectory { get; init; }    // ~/.harbor/data/<plugin-name>/
    
    /// <summary>Хук на события ядра (см. §5).</summary>
    public IEventBus Events { get; init; }
}
```

### 2.3. Минимальный пример плагина

```csharp
// MyPlugin.csproj
// <ProjectReference Include="Harbor.Abstractions" />

using Harbor.Plugins;
using Harbor.Tools;

public sealed class MyToolPlugin : IToolPlugin
{
    public string Name => "my-tool-plugin";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 1, 0);
    public string Description => "Custom tools for my workflow";
    
    public void Initialize(PluginContext context)
    {
        context.Logger.LogInformation("MyToolPlugin initializing v{Version}", Version);
    }
    
    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<GreetTool>();
        builder.AddTool<FsTreeTool>();
    }
    
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class GreetTool : ITool
{
    public string Id => "greet";
    public string Description => "Greet someone by name";
    
    public JsonDocument ParameterSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Name to greet" }
          },
          "required": ["name"]
        }
        """);
    
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext ctx,
        CancellationToken ct)
    {
        var name = args.GetProperty("name").GetString() ?? "world";
        return new ToolResult(
            Output: $"Hello, {name}!",
            IsError: false,
            Metadata: new { greeted = name });
    }
}
```

## 3. Discovery

### 3.1. Поиск плагинов

Plugin loader ищет в 3 местах (по порядку, дубликаты игнорируются — побеждает первый найденный):

| Место | Scope | Когда загружается |
|---|---|---|
| `~/.harbor/plugins/*.dll` | Глобальные (пользовательские) | Всегда при старте |
| `.harbor/plugins/*.dll` (в cwd) | Проект-локальные | Только если пользователь подтвердил trust (см. §6) |
| Пути из `config.json:plugins[]` | Явно указанные | Всегда |

Дополнительно поддерживается **NuGet-установка**:

```bash
harbor plugin install Harbor.Plugin.WebSearch
# или
harbor plugin install Harbor.Plugin.WebSearch --version 1.2.0
# или из локального .nupkg
harbor plugin install ./my-plugin.1.0.0.nupkg
```

Команда `harbor plugin install`:
1. Скачивает NuGet-пакет.
2. Распаковывает в `~/.harbor/plugins/<name>/<version>/`.
3. Парсит `.nuspec` — извлекает зависимости.
4. Скачивает зависимости в тот же каталог.
5. Регистрирует в `~/.harbor/plugins/installed.json`.

### 3.2. Manifest

Каждый плагин может опционально иметь `plugin.json` рядом с DLL:

```jsonc
{
  "name": "my-tool-plugin",
  "version": "1.0.0",
  "requiredHarborVersion": "0.1.0",
  "description": "Custom tools for my workflow",
  "authors": ["Jane Doe"],
  "license": "MIT",
  "repository": "https://github.com/jane/harbor-my-tools",
  "entryPoint": "MyToolPlugin.dll",  // DLL с типом, реализующим IPlugin
  "entryType": "MyToolPlugin.MyToolPlugin, MyToolPlugin",  // FQN, опционально
  "dependencies": [
    { "name": "Newtonsoft.Json", "version": "13.0.3" }
  ],
  "loadMode": "isolated",  // isolated | default | shadow-copy
  "permissions": ["read", "bash:ls", "bash:cat"]  // declarative permissions
}
```

Если manifest отсутствует — loader сканирует DLL на типы, реализующие `IPlugin`, и берёт первый.

## 4. Загрузка

### 4.1. AssemblyLoadContext (JIT mode)

В JIT-билдах (dev) каждый плагин грузится в свой **collectible `AssemblyLoadContext`** — для изоляции и hot-reload.

```csharp
// Harbor.Extensions/CollectibleAssemblyLoadContext.cs
public sealed class CollectibleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    
    public CollectibleAssemblyLoadContext(string mainAssemblyPath) 
        : base(name: $"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", 
               isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }
    
    protected override Assembly? Load(AssemblyName name)
    {
        // 1. Сначала пробуем resolver (для зависимостей плагина)
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path != null) return LoadFromAssemblyPath(path);
        
        // 2. Не находим — пусть хост-контекст загрузит (Harbor.Abstractions etc.)
        return null;
    }
    
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
```

Загрузка плагина:

```csharp
public sealed class PluginLoader
{
    public async Task<IPlugin?> LoadAsync(string dllPath, CancellationToken ct)
    {
        var alc = new CollectibleAssemblyLoadContext(dllPath);
        var assembly = alc.LoadFromAssemblyPath(dllPath);
        
        // Ищем тип, реализающий IPlugin
        var pluginType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
        
        if (pluginType == null) return null;
        
        // Создаём instance
        var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
        
        // Version check
        if (plugin.RequiredHarborVersion > HarborVersion.Current)
            throw new PluginCompatibilityException(
                $"Plugin {plugin.Name} requires Harbor >= {plugin.RequiredHarborVersion}");
        
        // Initialize
        var context = new PluginContext { /* ... */ };
        plugin.Initialize(context);
        
        return plugin;
    }
    
    public async Task UnloadAsync(IPlugin plugin, CancellationToken ct)
    {
        await plugin.ShutdownAsync(ct);
        
        // Находим ALC для этого плагина
        var alc = _pluginAlcs[plugin.Name];
        _pluginAlcs.Remove(plugin.Name);
        
        // Выгружаем assembly
        alc.Unload();
        
        // GC должен собрать, но принудительно — для надёжности
        for (int i = 0; i < 5 && alc.Assemblies.Any(); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
```

### 4.2. NativeAOT mode — РАЗНЫЙ путь

**КРИТИЧЕСКОЕ ОГРАНИЧЕНИЕ**: `AssemblyLoadContext.LoadFromAssemblyPath` и collectible ALC **НЕ работают под NativeAOT**. NativeAOT компилирует в нативный код, runtime не умеет загружать и JIT-ить новые сборки.

**Решение**: в NativeAOT-билдах плагины регистрируются **на этапе компиляции** через source generator, который сканирует `~/.harbor/plugins/*.csproj` и генерирует dispatch-код.

#### Подход 1: Pre-compiled dispatch (build-time)

Пользователь собирает harbor с explicitly-указанными плагинами:

```bash
# build-time approach
harbor build --plugins Harbor.Plugin.WebSearch,Harbor.Plugin.TodoWrite
```

Source generator создает:

```csharp
// Harbor.NativeAot/PluginDispatch.Generated.cs (auto-generated)
public static class PluginDispatch
{
    public static void RegisterAll(IPluginHost host)
    {
        host.Register(new Harbor.Plugin.WebSearch.WebSearchPlugin());
        host.Register(new Harbor.Plugin.TodoWrite.TodoWritePlugin());
    }
}
```

Плагины статически линкуются в бинарник. **Минус**: нельзя добавить плагин без пересборки.

#### Подход 2: Out-of-process plugins (process isolation)

Плагин запускается как отдельный процесс, общается с harbor по JSON-RPC over stdio (как MCP-серверы).

```
harbor process                  plugin-host process
─────────────                   ─────────────────────
PluginClient ── JSON-RPC ──→   PluginServer
  ├─ tool.execute                 ├─ ITool.Execute
  ├─ plugin.event                 ├─ events
  └─ ...                          └─ ...
```

**Плюсы**: работает под AOT, plugin crash не валит harbor, plugin может быть написан на любом языке (через MCP-совместимый протокол).

**Минусы**: latency на IPC (~1ms per call), memory overhead на каждый процесс (~20-30 MB baseline).

**Реализация**: `Harbor.Extensions.ProcessIsolation` — `RemotePlugin` реализация `IPlugin`, проксирует все вызовы через stdio.

#### Подход 3: Plugin package как NativeAOT-библиотека

Каждый плагин — это отдельный `.so`/`.dll`/`.dylib` (NativeAOT-compiled library), который exposes C ABI:

```c
// plugin.h
typedef struct {
    const char* name;
    const char* version;
    const char* description;
    void (*initialize)(PluginContext* ctx);
    void (*register_tools)(ToolRegistryBuilder* builder);
    void (*shutdown)(void);
} PluginEntry;

// Каждый плагин экспортирует:
extern "C" PluginEntry* plugin_get_entry(void);
```

Harbor dynamically loads `.so` через `NativeLibrary.Load` + `NativeLibrary.GetExport` (оба AOT-safe).

**Плюсы**: реально динамическая загрузка под AOT, низкий overhead.

**Минусы**: C ABI только для primitive types; для сложных типов (JsonElement, IReadOnlyList<>) нужен C wrapper; разработка плагинов усложняется.

#### Рекомендация

**Для MVP**: Подход 2 (out-of-process) — унифицирует с MCP-транспортом, работает везде, нативно AOT-safe.

**Для v1**: Подход 1 (build-time) как опция `harbor build --plugins` для пользователей, которым нужен maximum footprint optimization.

**Для v2**: Подход 3 (native libraries) — для power users, готовых писать C ABI wrappers.

### 4.3. JIT mode vs AOT mode — summary

| Свойство | JIT mode (`dotnet run`) | AOT mode (`harbor` binary) |
|---|---|---|
| Загрузка плагинов | `AssemblyLoadContext.LoadFromAssemblyPath` | Out-of-process (Approach 2) |
| Collectible (unloadable) | ✅ Да | N/A (plugin = отдельный процесс, kill = unload) |
| Hot-reload | ✅ Да (unload + reload) | ⚠️ Только restart plugin-host process |
| Reflection | ✅ Полная | ❌ Только `[DynamicallyAccessedMembers]` |
| Plugin can use any NuGet | ✅ Да | ✅ Да (плагин-процесс имеет свой dependency tree) |
| Memory overhead per plugin | ~5-10 МБ (shared ALC) | ~20-30 МБ (отдельный процесс) |
| Latency per tool call | <1 мс | ~1-3 мс (IPC overhead) |

## 5. Event bus (для плагинов)

Плагины могут подписываться на события ядра:

```csharp
public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) 
        where TEvent : IEvent;
    
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) 
        where TEvent : IEvent;
}

// Events
public interface IEvent { }

public sealed record SessionStartingEvent(string SessionId, string Directory) : IEvent;
public sealed record SessionStartedEvent(string SessionId) : IEvent;
public sealed record BeforeAgentStartEvent(string SessionId, string AgentName) : IEvent;
public sealed record BeforeToolCallEvent(
    string SessionId, string ToolCallId, string ToolName, JsonElement Args) : IEvent;
public sealed record AfterToolCallEvent(
    string SessionId, string ToolCallId, ToolResult Result) : IEvent;
public sealed record BeforeLlmRequestEvent(
    string SessionId, string Model, IReadOnlyList<LlmMessage> Messages) : IEvent;
public sealed record AfterLlmResponseEvent(
    string SessionId, string Model, Usage Usage) : IEvent;
public sealed record BeforeCompactionEvent(string SessionId, IReadOnlyList<AgentMessage> Messages) : IEvent;
public sealed record AfterCompactionEvent(string SessionId, string Summary, int PrunedMessages) : IEvent;
```

### Пример: плагин, логирующий все bash-команды

```csharp
public sealed class BashAuditPlugin : IPlugin
{
    public string Name => "bash-audit";
    // ...
    
    public void Initialize(PluginContext context)
    {
        context.Events.Subscribe<BeforeToolCallEvent>(async (evt, ct) =>
        {
            if (evt.ToolName != "bash") return;
            
            var command = evt.Args.GetProperty("command").GetString();
            context.Logger.LogInformation(
                "[{SessionId}] bash: {Command}", evt.SessionId, command);
        });
    }
}
```

### Hook-и — модификация данных

Некоторые events позволяют плагину **модифицировать** данные:

```csharp
public sealed class BeforeSystemPromptBuildEvent(
    string SessionId,
    string AgentName,
    List<string> SystemPromptParts,  // плагин может добавить/изменить
    IReadOnlyList<ToolDefinition> Tools) : IEvent;
```

Реализация — mutable fields в event record (нет, records immutable — используем class):

```csharp
public sealed class BeforeSystemPromptBuildEvent
{
    public string SessionId { get; init; }
    public string AgentName { get; init; }
    public List<string> SystemPromptParts { get; set; }  // mutable
    public IReadOnlyList<ToolDefinition> Tools { get; init; }
}
```

## 6. Trust model для project-local плагинов

`.harbor/plugins/` (в cwd) — потенциальная **security risk**. Злоумышленник может подсунуть malicious плагин в склонированный репозиторий, и пользователь, запустивший harbor в этом репо, выполнит arbitrary code.

Решение — **trust prompt** (как у pi):

```
$ cd ~/projects/some-repo
$ harbor
⚠ This project contains Harbor plugins:
   - .harbor/plugins/repo-helper.dll (v1.0.0)
   - .harbor/plugins/repo-extra.dll (v2.3.1)
   
Do you trust these plugins? [y/N/always]
> always  ← добавит hash в ~/.harbor/trusted-repos.json
```

`~/.harbor/trusted-repos.json`:
```json
{
  "trusted": [
    {
      "path": "/home/user/projects/some-repo",
      "pluginHashes": [
        "sha256:abc123...",
        "sha256:def456..."
      ],
      "trustedAt": "2026-07-16T10:30:00Z"
    }
  ]
}
```

Если hash DLL изменился — переспрашиваем.

## 7. Hot-reload (JIT mode только)

```csharp
public sealed class PluginWatcherService : IHostedService, IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly IPluginHost _pluginHost;
    
    public PluginWatcherService(IPluginHost pluginHost)
    {
        _pluginHost = pluginHost;
        _watcher = new FileSystemWatcher("~/.harbor/plugins/", "*.dll")
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        _watcher.Changed += OnPluginChanged;
        _watcher.Created += OnPluginChanged;
        _watcher.Deleted += OnPluginDeleted;
        _watcher.Renamed += OnPluginRenamed;
    }
    
    private async void OnPluginChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce 500ms (FS watcher дёргает несколько раз для одного изменения)
        await Task.Delay(500);
        
        var pluginName = Path.GetFileNameWithoutExtension(e.FullPath);
        if (_pluginHost.IsLoaded(pluginName))
        {
            _logger.LogInformation("Hot-reloading plugin {Name}", pluginName);
            await _pluginHost.ReloadAsync(pluginName);
        }
        else
        {
            _logger.LogInformation("Loading new plugin {Name}", pluginName);
            await _pluginHost.LoadAsync(e.FullPath);
        }
    }
}
```

`ReloadAsync` = unload (collectible ALC) + load.

## 8. Versioning и совместимость

### 8.1. `RequiredHarborVersion` проверка

```csharp
if (plugin.RequiredHarborVersion > HarborVersion.Current)
{
    throw new PluginCompatibilityException(
        $"Plugin '{plugin.Name}' requires Harbor {plugin.RequiredHarborVersion}+, " +
        $"but current version is {HarborVersion.Current}. " +
        $"Please update harbor: `harbor upgrade`");
}
```

### 8.2. ABI stability

`Harbor.Abstractions` — публичный контракт. Breaking changes:
- Удаление или переименование public API → major version bump.
- Добавление нового method в interface → minor version bump (с `default` implementation).
- Изменение signature → major version bump.

Plugin декларирует `RequiredHarborVersion` — major+minor. Patch versions — обратно-совместимы.

### 8.3. Deprecation flow

- API помечается `[Obsolete("...use X instead", error: false)]` — minor version.
- Через 1 major release — `[Obsolete("...", error: true)]` — compile-time error для плагинов.
- Через 2 major release — удаляется.

## 9. Permissions для плагинов

Плагин может декларировать, какие permissions ему нужны:

```jsonc
// plugin.json
{
  "name": "bash-audit",
  "permissions": ["read", "bash:ls", "bash:cat"]
}
```

Или программно:

```csharp
public void Initialize(PluginContext context)
{
    context.RequirePermissions("read", "bash:ls");
    // ...
}
```

При загрузке плагина harbor проверяет, разрешены ли эти permissions в config. Если нет — fail или ask user.

## 10. Logging из плагинов

```csharp
public interface IPluginLogger
{
    void LogDebug(string message, params object[] args);
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(string message, params object[] args);
    void LogCritical(string message, params object[] args);
    
    IDisposable BeginScope(string scopeName);
}
```

Под капотом — `Microsoft.Extensions.Logging.ILogger` с category = `Harbor.Plugin.<PluginName>`.

## 11. Configuration для плагинов

Каждый плагин имеет свой subsection в `config.json`:

```jsonc
{
  "plugins": {
    "my-tool-plugin": {
      "apiKey": "..."  // плагин-specific
    }
  }
}
```

Plugin читает через:

```csharp
public void Initialize(PluginContext context)
{
    var config = context.Configuration
        .GetSection("plugins:my-tool-plugin")
        .Get<MyPluginConfig>();
}
```

Под AOT — через source-gen binder (см. `01-architecture.md` §5.3).

## 12. Marketplace (future)

Долгосрочная цель — `harbor plugin install <name>` тянет с центрального репозитория.

```
harbor plugin search web-search
harbor plugin install harbor-plugin-websearch
harbor plugin list
harbor plugin update --all
harbor plugin uninstall harbor-plugin-websearch
```

Marketplace — простой HTTP endpoint, отдающий `.nupkg` по имени. Можно поднять на GitHub Releases (для community плагинов) или на nuget.org (для широкого распространения).

## 13. Builtin tools как плагины

Все builtin tools (`read`, `write`, `edit`, `bash`, etc.) упакованы в `Harbor.Tools.Builtin.dll` и зарегистрированы как **встроенный плагин**:

```csharp
internal sealed class BuiltinToolsPlugin : IToolPlugin
{
    public string Name => "harbor.builtin.tools";
    public Version Version => HarborVersion.Current;
    public Version RequiredHarborVersion => HarborVersion.Current;
    public string Description => "Built-in file/shell tools";
    
    public void Initialize(PluginContext context) { /* no-op */ }
    
    public void RegisterTools(IToolRegistryBuilder builder)
    {
        builder.AddTool<ReadTool>();
        builder.AddTool<WriteTool>();
        builder.AddTool<EditTool>();
        builder.AddTool<BashTool>();
        builder.AddTool<GlobTool>();
        builder.AddTool<GrepTool>();
        builder.AddTool<LsTool>();
    }
    
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

Это даёт единый механизм — builtin и пользовательские плагины обрабатываются одинаково. Пользователь может `disable plugin harbor.builtin.tools` и зарегистрировать свои реализации `read`/`write`/etc.

## 14. Failure modes и recovery

| Сценарий | Поведение |
|---|---|
| Plugin DLL не найден | Лог + skip, продолжить загрузку остальных |
| Plugin throws в `Initialize` | Лог + deactivate, продолжить |
| Plugin требует harbor version выше текущей | Лог + skip с warning |
| Plugin использует reflection (под AOT) | Compile-time warning от trim analyzer |
| Plugin-hang в `Initialize` (timeout 5s) | Лог + deactivate |
| Plugin-hang в `ShutdownAsync` (timeout 3s) | Force-unload (collectible ALC) или kill process (out-of-process) |
| Plugin crash во время tool execution | Tool result с `isError=true`, "Plugin crashed: ...", продолжить работу |
| Plugin утечка памяти | Периодический reload (JIT) или restart plugin-host (out-of-process) |

## 15. Observability для плагинов

- Каждый tool execution помечается тегом `plugin.name` в OpenTelemetry.
- `harbor plugin list` показывает: загружен, версия, память (RSS plugin-host процесса), количество зарегистрированных tools.
- `harbor plugin logs <name>` — tail логов конкретного плагина.
- `harbor plugin stats <name>` — счётчики: tool calls, errors, avg latency.

---

**Next**: `03-providers.md` — LLM provider abstraction, SSE streaming, Anthropic/OpenAI/Google/Ollama impl.
