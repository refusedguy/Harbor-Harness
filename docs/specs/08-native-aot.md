# 08 — NativeAOT

> Документ: ограничения NativeAOT в .NET 10, что работает, что НЕ работает, source generators, trimming, экспериментальные фичи. Самый критический раздел — определяет, насколько реально построить TUI-харнесс под AOT.

## 1. Что такое NativeAOT

NativeAOT — компиляция .NET-кода в нативный машинный код (через ILC — Intermediate Language Compiler) на этапе публикации. Получается standalone-бинарник без .NET runtime.

**Плюсы**:
- **Cold start**: 10–30 мс (vs 200+ мс JIT).
- **RSS idle**: 15–25 МБ (vs 50–100 МБ JIT).
- **Binary size**: 1–15 МБ (vs 100+ МБ self-contained JIT).
- **No runtime dependency** — пользователь не ставит .NET SDK.
- **Memory predictability** — нет JIT-пауз, нет GC background compilation.

**Минусы**:
- **No dynamic code generation** — `System.Reflection.Emit`, `Expression.Compile()`, `dynamic` — НЕ работают.
- **Trimming** — неиспользуемый код удаляется. Reflection на trimmed types падает в runtime.
- **Build time**: 60+ секунд (vs 5 секунд JIT).
- **Peak throughput**: -5..15% vs JIT (после warmup) — irrelevant для CLI/TUI.
- **Reflection limitations** — `[DynamicallyAccessedMembers]` annotations обязательны.

## 2. Совместимость библиотек — критический анализ

### 2.1. Spectre.Console (рендеринг — OK, CLI — НЕ ОК)

| Компонент | Версия | AOT-статус | Рекомендация |
|---|---|---|---|
| `Spectre.Console` (рендеринг) | ≥ 0.50.0 (Nov 2024) | ✅ Чистый (PR #1690) | Можно использовать для tables/panels |
| `Spectre.Console.Cli` | любая | ❌ TypeConverter quagmire (IL2026/IL2072) | **НЕ ИСПОЛЬЗОВАТЬ** |

**Подробности**:
- В v0.50 добавлены `[DynamicallyAccessedMembers]` аннотации в рендеринг.
- `Enum.GetValues<Decoration>()` (generic) вместо reflection-based.
- Убран `Assembly.Location` → `AppContext.BaseDirectory`.
- CLI-часть НЕ починят до 1.0 + source generator rewrite (см. issue #955).

**Что НЕ работает в Spectre.Console.Cli под AOT**:
- `CommandApp<T>` — `Type.GetConstructors()` без DAM → IL2070.
- `Activator.CreateInstance(Type)` для settings → IL2072.
- `TypeDescriptor.GetConverter(Type)` → IL2026 (помечен `[RequiresUnreferencedCode]`).
- `EnumConverter` падает в runtime для enum settings.
- Default values отображаются как int.

**Workaround (если очень нужен)**:
```xml
<TrimmerRootDescriptor Include="SpectreCliRoots.xml" />
```
```xml
<linker>
  <assembly fullname="Spectre.Console.Cli" preserve="all" />
  <assembly fullname="MyApp" preserve="all" />
</linker>
```
**НО**: это фактически отключает trimming → binary ~15+ МБ, побеждает смысл AOT.

### 2.2. Terminal.Gui v2

| Версия | AOT-статус |
|---|---|
| v2.0.0-rc.7 | ❌ 27 IL2026/IL3053 warnings, runtime bugs (enum config, theme cloning) |
| v2.1.0+ (May 2026) | ✅ Без warnings для normal usage |

**Вердикт**: для streaming AI CLI — overkill. Terminal.Gui v2 — это full-screen TUI toolkit (окна, мышь, виджеты). Для streaming-ответов и slash-команд слишком тяжёлый.

### 2.3. ConsoleAppFramework v5 (Cysharp/neuecc)

✅ **РЕКОМЕНДУЕТСЯ** для CLI parsing.

- Zero reflection, zero allocation, AOT safe.
- Source generator-based.
- Built-in `CancellationToken` через `PosixSignalRegistration`.
- XML doc comments → help text.
- Filter pipeline (middleware).
- Managed function pointers (`delegate* managed<>`).

```csharp
// Пример
await ConsoleApp.RunAsync(args, App.Run);

static partial class App
{
    /// <summary>Run a prompt.</summary>
    /// <param name="prompt">-p, The prompt</param>
    /// <param name="model">-m, Model name</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task Run(string prompt, string model = "gpt-4", CancellationToken ct = default)
    {
        // ...
    }
}
```

### 2.4. System.CommandLine 2.0

✅ AOT-compatible, стабилен в .NET 10 (target release — Nov 2025).

- Microsoft official, будет в .NET 10 по умолчанию.
- Performance хуже ConsoleAppFramework (Dictionary dispatch + reflection binding).
- Используется если не нужен max perf.

### 2.5. System.Text.Json

✅ AOT-compatible через **source generators**:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(LLMEvent))]
[JsonSerializable(typeof(AgentEvent))]
// ... все типы
public partial class HarborJsonContext : JsonSerializerContext { }

// Использование:
var json = JsonSerializer.Serialize(request, HarborJsonContext.Default.ChatRequest);
var response = JsonSerializer.Deserialize(json, HarborJsonContext.Default.ChatResponse);
```

**Reflection-based режим** (без source-gen) — выдаёт IL2026 warnings.

### 2.6. EF Core

❌ **НЕ AOT-compatible**.

- Требует reflection для entity materialization.
- `DbSet<T>.FromSqlRaw` — dynamic.
- Планируется AOT-support в EF Core 10+ (issue #34446), но не стабильно.

**Альтернативы**:
- `Dapper.AOT` — source-gen Dapper wrapper, AOT-safe.
- Raw `Microsoft.Data.Sqlite` + `SqliteCommand` — ноль reflection, but tedious.
- `RepoDb` — но тоже reflection-based.

**Решение для harbor**: Dapper.AOT для queries.

### 2.7. Microsoft.Extensions.DependencyInjection

✅ AOT-compatible.

- `IServiceCollection` и `IServiceProvider` — не используют reflection для resolution.
- BUT: `IServiceCollection.BuildServiceProvider` с `ValidateScopes = true` использует reflection — отключить в AOT-mode.
- `ActivatorUtilities.CreateInstance` — требует `[DynamicallyAccessedMembers]`.

### 2.8. Microsoft.Extensions.Configuration

✅ AOT-compatible через source-gen:

```csharp
[ConfigurationSource]
public partial class HarborConfig
{
    public string Model { get; set; } = "";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

// Generated:
public static class HarborConfigBinder
{
    public static HarborConfig Bind(IConfiguration configuration)
    {
        var result = new HarborConfig();
        result.Model = configuration["model"] ?? "";
        // ...
        return result;
    }
}
```

Без source-gen — `ConfigurationBinder.Get<T>()` использует reflection → IL2026.

### 2.9. Microsoft.Extensions.AI

✅ AOT-compatible (через source-gen).

- `IChatClient` interface — нет reflection.
- `AIFunctionFactory.Create` — source-gen mode.
- `Microsoft.Extensions.AI.OpenAI` / `Azure.AI.OpenAI` — official, AOT-ready.

### 2.10. ModelContextProtocol (C# SDK)

⚠️ Требует тестирования под AOT.

- Официальный SDK от Microsoft — `ModelContextProtocol` NuGet.
- JSON-RPC over stdio/HTTP.
- Использует `System.Text.Json` source-gen (нужно настроить).

### 2.11. AssemblyLoadContext

⚠️ Collectible (unloadable) НЕ работает под AOT.

- `AssemblyLoadContext.LoadFromAssemblyPath` — работает под JIT, не под AOT.
- `CollectibleAssemblyLoadContext` (isCollectible: true) — требует runtime codegen.
- `AssemblyDependencyResolver` — работает, но только в JIT.

**Решение для harbor**: 
- JIT mode: `AssemblyLoadContext` collectible (hot-reload).
- AOT mode: out-of-process plugins (см. `02-plugins.md` §4.2).

### 2.12. Markdig

⚠️ Требует конфигурации для AOT.

- Markdig использует reflection для pipeline builder.
- Решение: `MarkdownPipelineBuilder` с явно указанными extensions, без `UseAdvancedExtensions()` (который reflection-scans).

```csharp
// AOT-friendly:
var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseTaskLists()
    .UseAutoLinks()
    .Build();  // без UseAdvancedExtensions

// ВАРИАНТ С UseAdvancedExtensions — IL2026 warnings
```

### 2.13. SixLabors.ImageSharp

✅ AOT-compatible.

- Не использует reflection (managed code only).
- Image format detection — explicit registration.
- Работает с `System.Drawing.Common` alternatives.

### 2.14. DiffPlex

✅ AOT-compatible (нет reflection).

### 2.15. Polly (resilience)

✅ AOT-compatible (v8+).

### 2.16. Serilog

✅ AOT-compatible (с правильными sinks).

- `Serilog.Sinks.File` — OK.
- `Serilog.Sinks.Console` — OK.
- `Serilog.Sinks.Async` — OK.
- Избегать `Serilog.Sinks.Seq` (reflection-based).

## 3. .csproj для NativeAOT

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    
    <!-- NativeAOT -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    
    <!-- Size optimizations -->
    <DebuggerSupport>false</DebuggerSupport>
    <EventSourceSupport>false</EventSourceSupport>
    <HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>
    <MetricsSupport>false</MetricsSupport>
    <StackTraceSupport>false</StackTraceSupport>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
    <TrimmerRemoveSymbols>true</TrimmerRemoveSymbols>
    <StripSymbols>true</StripSymbols>
    <IlcTrimMetadata>true</IlcTrimMetadata>
    <IlcOptimizationPreference>Size</IlcOptimizationPreference>
    
    <!-- Catch AOT issues early -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS1591</NoWarn>  <!-- missing XML doc -->
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="ConsoleAppFramework" Version="5.5.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
    <PackageReference Include="Dapper" Version="2.1.44" />
    <PackageReference Include="Dapper.AOT" Version="1.0.0" />
  </ItemGroup>
</Project>
```

### 3.1. Size breakdown (типичный AOT binary)

| Component | Размер |
|---|---|
| Bootstrap (entry, args parsing) | ~50 KB |
| ConsoleAppFramework generated | ~100 KB |
| `Microsoft.Extensions.*` | ~500 KB |
| `System.Text.Json` source-gen | ~200 KB |
| `Microsoft.Data.Sqlite` (managed) | ~200 KB |
| `e_sqlite3` native lib | ~1.5 MB |
| `HttpClient` + networking | ~500 KB |
| BCL core (strings, IO, threading) | ~2 MB |
| Harbor code | ~500 KB |
| **Total (stripped)** | **~5–7 MB** |

С Spectre.Console — ~10–15 МБ. Без Spectre — ~5–7 МБ.

## 4. Source generators — обязательный паттерн

### 4.1. JSON serialization

Каждый тип, который сериализуется, должен быть в `JsonSerializerContext`:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(LLMEvent))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(LlmRequest))]
[JsonSerializable(typeof(LlmMessage))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(AgentMessage))]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(ToolResultMessage))]
// ... все типы
public partial class HarborJsonContext : JsonSerializerContext { }
```

Для polymorphic types:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextStartEvent), "text_start")]
[JsonDerivedType(typeof(TextDeltaEvent), "text_delta")]
// ...
public abstract record LLMEvent;
```

### 4.2. Configuration binding

```csharp
[ConfigurationSource]
public partial class HarborConfig
{
    public string Model { get; set; } = "anthropic/claude-opus-4";
    public string Agent { get; set; } = "code";
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

// Generated:
public partial class HarborConfig
{
    public static HarborConfig Bind(IConfiguration configuration) => /* ... */;
}
```

### 4.3. Dapper.AOT

```csharp
[DapperAot]
public static class SessionQueries
{
    [Sql]
    public const string InsertSession = """
        INSERT INTO sessions (id, project_id, directory, title, agent, model, version, created_at, updated_at)
        VALUES (@Id, @ProjectId, @Directory, @Title, @Agent, @Model, @Version, @CreatedAt, @UpdatedAt)
        """;
}
```

Source generator создаёт typed mapper для каждого query.

### 4.4. ConsoleAppFramework v5

XML doc comments → help text, source-gen dispatch:

```csharp
/// <summary>Run a prompt.</summary>
/// <param name="prompt">-p, The prompt</param>
public static async Task Run(string prompt, CancellationToken ct = default)
{
    // ...
}
```

Generated:
```csharp
partial class App
{
    static partial void AddCore(string commandName, Delegate command)
    {
        switch (commandName)
        {
            case "run": _run = Unsafe.As<Func<string, CancellationToken, Task>>(command); break;
            // ...
        }
    }
}
```

## 5. Что НЕ работает под NativeAOT (полный список)

| Что | Альтернатива |
|---|---|
| `System.Reflection.Emit` | Source generators |
| `Expression.Compile()` | Static methods, delegates |
| `dynamic` keyword | `object` + explicit casts |
| `Assembly.Load` / `Assembly.LoadFrom` | Static references, NativeLibrary.Load |
| `AssemblyLoadContext` collectible | Process isolation (out-of-process plugins) |
| `Type.GetProperties()` (без DAM) | `[DynamicallyAccessedMembers]` annotations |
| `Activator.CreateInstance(Type)` | `Activator.CreateInstance<T>()` (generic) |
| `TypeDescriptor.GetConverter` | Custom converters, source-gen |
| `Newtonsoft.Json` reflection | `System.Text.Json` source-gen |
| EF Core | Dapper.AOT, raw ADO.NET |
| AutoMapper | Mapperly (source-gen) |
| MediatR | Mediator (source-gen) |
| `Assembly.Location` | `AppContext.BaseDirectory` |
| `Type.GetType(string)` for non-rooted | `Type.GetType("..., AssemblyName")` rooted |
| `XmlSerializer` (legacy) | `System.Text.Json` или `XmlSerializer` source-gen |
| Reflection-based serialization (MessagePack without source-gen) | MessagePack with `[MessagePackObject]` + source-gen |
| Reflection-based DI registration (`AddSingleton(typeof(T))`) | Generic `AddSingleton<T>()` |

## 6. Trimming и `[DynamicallyAccessedMembers]`

### 6.1. DAM annotations

```csharp
public class ToolRegistry
{
    // ✅ BAD: trim warning IL2070
    public void Register(Type toolType)
    {
        var tool = (ITool)Activator.CreateInstance(toolType);
        // ...
    }
    
    // ✅ GOOD: with DAM annotation
    public void Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>()
        where T : ITool
    {
        var tool = Activator.CreateInstance<T>();
        // ...
    }
    
    // ✅ GOOD: type parameter with DAM
    public void Register(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type toolType)
    {
        var tool = (ITool)Activator.CreateInstance(toolType);
        // ...
    }
}
```

### 6.2. Common DAM patterns

```csharp
// Public parameterless constructor
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]

// All public constructors
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]

// All public methods
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]

// All public properties
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]

// All public fields
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)]

// All public events
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicEvents)]

// All public nested types
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicNestedTypes)]

// All public members
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Public)]
```

### 6.3. `[RequiresUnreferencedCode]`

Если код принципиально использует reflection и не может быть AOT-friendly:

```csharp
[RequiresUnreferencedCode("This method uses reflection to scan for plugins. Use Register<T>() for AOT.")]
public void ScanForPlugins(string directory)
{
    // ...
}
```

Под AOT — warning IL2026. Можно suppress, но лучше переписать.

## 7. Runtime динамическая загрузка под AOT — workarounds

### 7.1. `NativeLibrary.Load` (native libs)

```csharp
// AOT-safe: load native .so/.dll/.dylib
IntPtr libHandle = NativeLibrary.Load("myplugin.so");

// Get function pointer
delegate* unmanaged[Cdecl]<PluginContext*, int> entryPoint;
entryPoint = (delegate* unmanaged[Cdecl]<PluginContext*, int>)NativeLibrary.GetExport(libHandle, "plugin_entry");
```

Это работает для C ABI. Для managed code — не работает (нужен JIT для IL).

### 7.2. Process isolation (рекомендация для harbor)

Плагин — отдельный процесс, общается по JSON-RPC over stdio.

```csharp
public sealed class RemotePluginProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    
    public RemotePluginProcess(string executablePath)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        _process.Start();
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
    }
    
    public async Task<JsonNode> CallAsync(string method, JsonNode? args, CancellationToken ct)
    {
        var request = new JsonRpcRequest(NextId(), method, args);
        await _stdin.WriteLineAsync(JsonSerializer.Serialize(request, HarborJsonContext.Default.JsonRpcRequest));
        
        var line = await _stdout.ReadLineAsync(ct);
        return JsonNode.Parse(line);
    }
}
```

Plugin-host — это просто отдельный harbor-подобный процесс, который регистрирует tools и выполняет их. Может быть NativeAOT binary сам по себе.

### 7.3. Pre-registered source-gen dispatch

При сборке harbor плагин statically линкуется:

```bash
harbor build --plugins Harbor.Plugin.WebSearch,Harbor.Plugin.TodoWrite
```

Source generator сканирует csproj-ссылки и генерирует:

```csharp
// Harbor.NativeAot/PluginDispatch.Generated.cs
public static class PluginDispatch
{
    public static void RegisterAll(IPluginHost host)
    {
        host.Register(new Harbor.Plugin.WebSearch.WebSearchPlugin());
        host.Register(new Harbor.Plugin.TodoWrite.TodoWritePlugin());
    }
}
```

Плюс: zero runtime overhead, max size optimization.
Минус: нельзя добавить плагин без пересборки.

## 8. NativeAOT dotnet tool (новинка .NET 10)

.NET 10 позволяет публиковать native binaries как `dotnet tool`:

```bash
dotnet tool install harbor --global
# Установит native binary harbor для текущей платформы
```

Это идеально для distribution. Пользователь ставит `dotnet` SDK или runtime, и `dotnet tool install harbor` тянет native binary.

```xml
<!-- harbor.csproj -->
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>harbor</ToolCommandName>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
</PropertyGroup>

<ItemGroup>
  <RuntimeTargets Include="runtimes\**\*" Pack="true" />
</ItemGroup>
```

В CI собираем под все платформы:
- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

NuGet package содержит нативные бинарники для каждой платформы, `dotnet tool install` выбирает правильный.

## 9. InvariantGlobalization

`<InvariantGlobalization>true</InvariantGlobalization>` — отключает culture-aware string operations.

**Плюсы**:
- Меньший binary (нет ICU для всех культур).
- Предсказуемое поведение строк (always invariant).

**Минусы**:
- `DateTime.ToString("d")` всегда в invariant format.
- `decimal.ToString()` всегда с ".".
- Не работает `CultureInfo.CurrentCulture`.

Для CLI/TUI — обычно OK. Текст LLM всегда invariant (UTF-8, no culture-specific formatting).

Если нужна локализация — `<InvariantGlobalization>false</InvariantGlobalization>` + `<HybridGlobalization>true</HybridGlobalization>` (lite ICU, ~1 MB overhead).

## 10. Бинарник size optimization tricks

### 10.1. `<StripSymbols>true</StripSymbols>`

Удаляет debug symbols из binary. Экономит ~30% размера.

### 10.2. `<DebuggerSupport>false</DebuggerSupport>`

Удаляет debugger support code. Экономит ~200 KB.

### 10.3. `<EventSourceSupport>false</EventSourceSupport>`

Удаляет EventSource infrastructure. Экономит ~100 KB. Если не нужен ETW/EventTracing — отключить.

### 10.4. `<StackTraceSupport>false</StackTraceSupport>`

Удаляет stack trace generation. Экономит ~50 KB. Для CLI — обычно OK (errors показываем без stack trace).

Но: при `Exception.ToString()` stack trace будет "stack trace not available". Для debug-mode оставляем включённым.

### 10.5. `<IlcOptimizationPreference>Size</IlcOptimizationPreference>`

ILC оптимизирует на size, не speed. Экономит ~10–20% размера.

### 10.6. `<IlcTrimMetadata>true</IlcTrimMetadata>`

Trim metadata assemblies. Экономит ~5%.

### 10.7. `<TrimmerRemoveSymbols>true</TrimmerRemoveSymbols>`

Удаляет символы из trimmed assemblies. Экономит ~10%.

### 10.8. Итоговый размер

| Configuration | Binary size |
|---|---|
| Default (no AOT, JIT self-contained) | ~80 МБ |
| AOT, default settings | ~15 МБ |
| AOT, all optimizations | ~5 МБ |
| AOT, all optimizations, with Spectre.Console | ~10 МБ |
| AOT, all optimizations, no Spectre | ~5 МБ |

## 11. Build pipeline

### 11.1. Dev mode (JIT)

```bash
dotnet run --project src/Harbor.Cli -- run --prompt "hello"
```

- Быстрая итерация (5s rebuild).
- Hot reload плагинов (collectible ALC).
- Полная reflection поддержка.

### 11.2. Release mode (AOT)

```bash
dotnet publish src/Harbor.Cli -c Release -r linux-x64
# Output: bin/Release/net10.0/linux-x64/publish/harbor
```

Build time — 60–120 секунд.

### 11.3. CI/CD (multi-platform)

```yaml
# .github/workflows/release.yml
strategy:
  matrix:
    include:
      - os: ubuntu-latest
        rid: linux-x64
      - os: ubuntu-latest
        rid: linux-arm64
      - os: macos-latest
        rid: osx-arm64
      - os: windows-latest
        rid: win-x64
      
steps:
  - uses: actions/checkout@v4
  - uses: actions/setup-dotnet@v4
    with:
      dotnet-version: '10.0.x'
  
  - name: Publish NativeAOT
    run: dotnet publish src/Harbor.Cli -c Release -r ${{ matrix.rid }} --self-contained
  
  - name: Upload binary
    uses: actions/upload-artifact@v4
    with:
      name: harbor-${{ matrix.rid }}
      path: src/Harbor.Cli/bin/Release/net10.0/${{ matrix.rid }}/publish/harbor*
```

### 11.4. NuGet tool package

```bash
dotnet pack src/Harbor.Cli -c Release
# Output: bin/Release/Harbor.Cli.1.0.0.nupkg
```

Package содержит RID-specific бинарники для всех платформ.

## 12. Testing under AOT

В CI — отдельный job, который собирает AOT-бинарник и прогоняет integration tests:

```bash
dotnet publish tests/Harbor.E2e.Tests -c Release -r linux-x64
./Harbor.E2e.Tests # запускает tests против AOT binary
```

Также — `TreatWarningsAsErrors` ловит IL2026/IL3053 на этапе компиляции.

## 13. Common pitfalls

### 13.1. Забыли `[JsonSerializable]`

```
System.InvalidOperationException: JsonSerializerContext 'HarborJsonContext' 
does not contain a JsonTypeInfo for type 'Harbor.Core.LLMEvent'.
```

**Fix**: добавить `[JsonSerializable(typeof(LLMEvent))]` в `HarborJsonContext`.

### 13.2. Забыли `[DynamicallyAccessedMembers]`

```
IL2070: 'T' argument does not satisfy 'DynamicallyAccessedMemberTypes.PublicConstructors' 
in call to 'System.Activator.CreateInstance(Type)'.
```

**Fix**: добавить DAM annotation на type parameter.

### 13.3. Reflection на trimmed type

```
System.MissingMethodException: Method not found: 'MyPlugin.MyTool.Initialize'.
```

**Fix**: использовать `[DynamicDependency]` или source-gen dispatch.

### 13.4. `Assembly.Location` в single-file

```
IL3000: 'System.Reflection.Assembly.Location.get' returns empty string 
in single-file mode.
```

**Fix**: использовать `AppContext.BaseDirectory`.

### 13.5. `Type.GetType(string)` without assembly

```
Type.GetType("MyPlugin.MyTool") returns null
```

**Fix**: использовать `Type.GetType("MyPlugin.MyTool, MyPlugin")` с assembly name.

## 14. Performance сравнение

| Metric | JIT (dev) | NativeAOT (release) | Node.js | Go binary |
|---|---|---|---|---|
| Cold start | ~200 ms | **~10-30 ms** | ~50-200 ms | ~5-20 ms |
| RSS idle | ~50 MB | **~15-25 MB** | ~30-50 MB | ~10-20 MB |
| Binary size | N/A (self-contained ~80 MB) | **~5-15 MB** | ~40-90 MB | ~10-25 MB |
| Build time | ~5 s | ~60-120 s | ~5 s | ~10-30 s |
| Peak throughput | Excellent | -5..15% vs JIT | Excellent | Excellent |

**Для CLI/TUI**: startup и memory — ключевые метрики. NativeAOT выигрывает у JIT в 5-10 раз по startup, в 2-3 раза по RSS.

## 15. Что нового в .NET 10 для NativeAOT

1. **File-based programs** — по умолчанию NativeAOT.
2. **NativeAOT dotnet tools** — нативные бинарники через `dotnet tool install`.
3. **Multicast delegate DAM fix** (dotnet/runtime#115431) — чинит Spectre.Console warnings на net10.
4. **Smaller binaries** — ~14% reduction vs .NET 9 (1.05 MB vs 1.22 MB для Hello World).
5. **Android NativeAOT** — nearly ready (MAUI .NET 10 RC2).
6. **iOS/MacCatalyst** — stable since .NET 9.
7. **Platform-specific NuGet packages** — компромиссный паттерн для старых SDK + AOT для net10+.

## 16. Рекомендация для harbor

### 16.1. Architecture decision

- **Main binary**: NativeAOT, single-file, all optimizations.
- **Plugin loading**: out-of-process (Approach 2 from `02-plugins.md` §4.2).
- **JSON**: `System.Text.Json` source-gen, zero reflection.
- **CLI**: `ConsoleAppFramework v5`.
- **TUI**: custom ANSI wrapper, no Spectre.Console in MVP.
- **DB**: `Microsoft.Data.Sqlite` + `Dapper.AOT`.
- **DI**: `Microsoft.Extensions.DependencyInjection` (AOT-compatible).
- **Configuration**: `Microsoft.Extensions.Configuration` + source-gen binder.

### 16.2. dev vs release

- **Dev** (`dotnet run`): JIT, hot reload, full debugging, all Spectre features available.
- **Release** (`dotnet publish -c Release`): NativeAOT, stripped, single-file.

Один и тот же source code, разные `<PublishAot>` settings.

### 16.3. Размер binary — realistic estimate

| Build | Размер | RSS idle | Cold start |
|---|---|---|---|
| Dev (JIT) | N/A | ~80 MB | ~500 ms |
| Release (AOT, all opt) | ~5-7 MB | ~25 MB | ~30 ms |
| Release (AOT, with Spectre render) | ~10-12 MB | ~30 MB | ~50 ms |

**Цель**: release build = 5–10 МБ, <30 ms startup, <30 MB RSS idle.

---

**Next**: `09-benchmarks.md` — анализ memory Node.js tools vs .NET target, конкретные цифры.
