// Engines layer — Jint in-process ECMAScript engine (fallback for when SharpTS is unavailable).
//
// Layering rule (see IScriptEngine.cs):
//   Knows about ScriptGlobals (Bridge) and Harbor.Abstractions only.
//   Knows NOTHING about filesystem, storage, or compilation.
namespace Harbor.Scripting.Engines;
/// <summary>
///     Jint-based <see cref="IScriptEngine" /> — pure-.NET (no native deps),
///     AOT-friendly, with per-call engine instances for thread safety.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why Jint?</b> Chosen as the fallback engine when SharpTS is not
///         available. Jint is MIT-licensed, ships as a single pure-.NET
///         assembly, supports ECMAScript 2020+, and has a mature security
///         sandbox. See <c>docs/SCRIPTING.md</c> for the full comparison.
///     </para>
///     <para>
///         <b>Thread safety:</b> Jint <c>Engine</c> instances are <b>not</b>
///         thread-safe. This engine creates a fresh <c>Engine</c> per
///         <see cref="Evaluate" /> call, so the same
///         <see cref="JintScriptEngine" /> can be used concurrently from
///         multiple threads. Each call pays the cold-start cost (~1-2 ms).
///     </para>
///     <para>
///         <b>Resource limits:</b> enforced via Jint's <c>Limits</c> options:
///         <see cref="ScriptEngineOptions.Timeout" />,
///         <see cref="ScriptEngineOptions.MaxStatements" />,
///         <see cref="ScriptEngineOptions.MaxRecursionDepth" />, and a memory
///         allocation cap from <see cref="ScriptEngineOptions.MemoryLimitBytes" />.
///         The supplied <see cref="ScriptEngineOptions.CancellationToken" /> is
///         observed by a watchdog that complements Jint's wall-clock timeout.
///     </para>
///     <para>
///         <b>Security model:</b> CLR access is disabled
///         (<c>AllowClr=false</c>, <c>AllowOperatorOverloading=false</c>).
///         Dangerous globals (<c>require</c>, <c>process</c>, <c>print</c>)
///         are not registered. Scripts reach the .NET host only through the
///         injected <c>Harbor</c> bridge.
///     </para>
/// </remarks>
public sealed class JintScriptEngine : IScriptEngine
{
    private const string HarborBridgeScript = """
                                              var __harbor_state = { tools: {} };
                                              var Harbor = {
                                                __tools: __harbor_state.tools,
                                                registerTool: function(def) {
                                                  if (typeof def !== 'object' || def === null) throw new Error("registerTool requires an object");
                                                  if (typeof def.name !== 'string' || def.name.length === 0) throw new Error("registerTool: .name (non-empty string) required");
                                                  if (typeof def.execute !== 'function') throw new Error("registerTool: .execute (function) required");
                                                  def.displayName = def.displayName || def.name;
                                                  def.description  = def.description  || ('Script tool: ' + def.name);
                                                  def.parameterSchema = def.parameterSchema || { type: 'object', properties: {} };
                                                  def.executionMode = def.executionMode || 'Parallel';
                                                  __harbor_registerTool(def);
                                                  __harbor_state.tools[def.name] = def;
                                                  return def;
                                                },
                                                log: function(msg) { __harbor_log(String(msg)); },
                                                tools:    { get: function(n) { return __harbor_getTool(n); }, list: function() { return __harbor_listTools(); } },
                                                providers:{ list: function() { return __harbor_listProviders(); } },
                                                agents:   { list: function() { return __harbor_listAgents(); } }
                                              };
                                              """;

    private readonly ILogger _logger;

    /// <summary>
    ///     Construct a Jint-based script engine.
    /// </summary>
    /// <param name="logger">
    ///     Logger for engine lifecycle events (script <c>Harbor.log</c> calls go through
    ///     <see cref="ScriptGlobals.Logger" />).
    /// </param>
    public JintScriptEngine(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Result Evaluate(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure("Script code is empty.");
        }

        var setup = BuildEngine(options, globals);
        if (setup.IsFailure)
        {
            return Result.Failure(setup.Error);
        }

        var (engine, watcher) = setup.Value;
        try
        {
            engine.Execute(code);
            return Result.Success();
        }
        catch (Exception ex) when (IsScriptException(ex))
        {
            return Result.Failure(FormatScriptError(ex, options));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    /// <inheritdoc />
    public Result<T> Evaluate<T>(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<T>("Script code is empty.");
        }

        var setup = BuildEngine(options, globals);
        if (setup.IsFailure)
        {
            return Result.Failure<T>(setup.Error);
        }

        var (engine, watcher) = setup.Value;
        try
        {
            var value = engine.Evaluate(code);
            return Convert<T>(value);
        }
        catch (Exception ex) when (IsScriptException(ex))
        {
            return Result.Failure<T>(FormatScriptError(ex, options));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    private Result<(Engine engine, CancellationWatcher watcher)> BuildEngine(ScriptEngineOptions opts, ScriptGlobals globals)
    {
        Engine engine;
        try
        {
            engine = new Engine(o =>
            {
                o.TimeoutInterval(opts.Timeout);
                o.MaxStatements(opts.MaxStatements);
                o.LimitRecursion(opts.MaxRecursionDepth);
                o.LimitMemory(opts.MemoryLimitBytes);
                o.CancellationToken(opts.CancellationToken);
                // Security: CLR access disabled by default (AllowClr is opt-in via
                // extension method; we deliberately do NOT call it). Operator
                // overloading is also disabled.
                o.AllowOperatorOverloading(false);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to construct Jint engine");
            return Result.Failure<(Engine, CancellationWatcher)>($"Engine construction failed: {ex.Message}");
        }

        var watcher = new CancellationWatcher(opts.CancellationToken);
        try
        {
            // Cast the entire lambda, not the parameter — `(Action<string>)msg => ...`
            // is parsed as `((Action<string>)msg) => ...` which is not a valid lambda.
            // The `(Action<string>)(msg => ...)` form casts the lambda expression itself.
            engine.SetValue("__harbor_log", (Action<string>)(msg => globals.Logger.LogInformation("Harbor.script: {Message}", msg)));
            engine.SetValue("__harbor_registerTool", (Action<JsValue>)(def => JintBridge.RegisterTool(def, engine, globals, _logger)));
            engine.SetValue("__harbor_registerProvider", (Action<JsValue>)(def => JintBridge.RegisterProvider(def, globals, _logger)));
            engine.SetValue("__harbor_getTool", (Func<string, JsValue>)(name => JintBridge.GetTool(name, engine, globals)));
            engine.SetValue("__harbor_listTools", (Func<JsValue>)(() => JintBridge.ListTools(engine, globals)));
            engine.SetValue("__harbor_listProviders", (Func<JsValue>)(() => JintBridge.ListProviders(engine, globals)));
            engine.SetValue("__harbor_listAgents", (Func<JsValue>)(() => JintBridge.ListAgents(engine, globals)));
            engine.Execute(HarborBridgeScript);
        }
        catch (Exception ex)
        {
            watcher.Dispose();
            _logger.LogError(ex, "Failed to set up Harbor bridge");
            return Result.Failure<(Engine, CancellationWatcher)>($"Bridge setup failed: {ex.Message}");
        }

        return Result.Success((engine, watcher));
    }

    private static Result<T> Convert<T>(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null
                ? Result.Failure<T>($"Script returned null/undefined; cannot convert to non-nullable {typeof(T).Name}.")
                : Result.Success<T>(default!);
        }

        object? direct = value.ToObject();
        if (direct is T typed)
        {
            return Result.Success(typed);
        }

        try
        {
            string json = JsonSerializer.Serialize(direct, JintJsonOptions.Default);
            var deserialized = JsonSerializer.Deserialize<T>(json, JintJsonOptions.Default);
            return deserialized is null
                ? Result.Failure<T>($"Script result deserialized to null for target type {typeof(T).Name}.")
                : Result.Success(deserialized);
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"Cannot convert script result to {typeof(T).Name}: {ex.Message}");
        }
    }

    private static bool IsScriptException(Exception ex)
    {
        return ex is JavaScriptException
                   or JintException
                   or TimeoutException
                   or OperationCanceledException
               || ex.GetType().Name.Contains("Limit", StringComparison.Ordinal)
               || ex.GetType().Name.Contains("Recursion", StringComparison.Ordinal)
               || ex.GetType().Name.Contains("Memory", StringComparison.Ordinal)
               || ex.GetType().Name.Contains("Timeout", StringComparison.Ordinal);
    }

    private static string FormatScriptError(Exception ex, ScriptEngineOptions opts)
    {
        string source = opts.SourceName ?? "<eval>";
        string typeName = ex.GetType().Name;
        if (ex is JavaScriptException jsEx)
        {
            return $"Script error in {source}: {jsEx.Message}";
        }

        if (ex is TimeoutException || typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return $"Script timed out in {source} after {opts.Timeout.TotalSeconds:0.###}s";
        }

        if (ex is OperationCanceledException)
        {
            return $"Script cancelled in {source}";
        }

        return $"Script error in {source} ({typeName}): {ex.Message}";
    }

    /// <summary>
    ///     Disposes the cancellation token registration captured at engine
    ///     construction time. Jint's wall-clock <c>TimeoutInterval</c> is the
    ///     hard backstop; the registered <c>CancellationToken</c> (wired via
    ///     <c>Options.CancellationToken(ct)</c>) gives the engine a chance to
    ///     abort earlier at the next statement boundary.
    /// </summary>
    private sealed class CancellationWatcher : IDisposable
    {
        private readonly CancellationTokenRegistration _reg;

        public CancellationWatcher(CancellationToken token)
        {
            _reg = token.Register(static state => _ = state, null);
        }

        public void Dispose() => _reg.Dispose();
    }
}

/// <summary>Shared JSON serializer options for Jint value conversion.</summary>
internal static class JintJsonOptions
{
    public static readonly JsonSerializerOptions Default = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>
///     Jint-specific wiring of the <c>Harbor</c> bridge: stateless helpers that
///     take the engine and globals as parameters (so they work across the
///     per-call engine instances).
/// </summary>
internal static class JintBridge
{
    public static void RegisterTool(JsValue def, Engine engine, ScriptGlobals globals, ILogger logger)
    {
        var obj = def.AsObject();
        string name = obj.Get("name").AsString();
        string displayName = obj.Get("displayName").IsUndefined() ? name : obj.Get("displayName").AsString();
        string description = obj.Get("description").IsUndefined() ? $"Script tool: {name}" : obj.Get("description").AsString();
        var schemaValue = obj.Get("parameterSchema");
        string executionModeStr = obj.Get("executionMode").IsUndefined() ? "Parallel" : obj.Get("executionMode").AsString();
        var executeFn = obj.Get("execute");

        JsonDocument schema;
        try
        {
            object? schemaDotnet = schemaValue.IsUndefined()
                ? new { type = "object", properties = new { } }
                : schemaValue.ToObject();
            string schemaJson = JsonSerializer.Serialize(schemaDotnet, JintJsonOptions.Default);
            schema = JsonDocument.Parse(schemaJson);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse parameterSchema for tool {Tool}", name);
            schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        }

        var mode = executionModeStr.Equals("Sequential", StringComparison.OrdinalIgnoreCase)
            ? ExecutionMode.Sequential
            : ExecutionMode.Parallel;

        // Capture the execute function source so ScriptTool can re-evaluate it on
        // each invocation. Jint's JsFunction.ToString() returns the original source
        // for user-defined functions; for arrow functions, it returns `(args) => { ... }`.
        string executeSource = CaptureExecuteSource(executeFn, name, logger);
        var executeOptions = ScriptEngineOptions.Default;

        // The execute delegate closes over a fresh engine instance per call (so it's
        // thread-safe). ScriptTool itself stays engine-agnostic — it just holds the
        // delegate and the execute source.
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute =
            (args, ct) =>
            {
                var invokeEngine = new JintScriptEngine(logger);
                var invokeOpts = executeOptions with { CancellationToken = ct, SourceName = $"script:{name}" };
                string argsJson = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
                string code = $"var __execute = {executeSource};\n__execute({argsJson});";
                var invokeGlobals = new ScriptGlobals
                {
                    Tools = globals.Tools,
                    Providers = globals.Providers,
                    Agents = globals.Agents,
                    Logger = globals.Logger
                };
                var result = invokeEngine.Evaluate<JsonElement>(code, invokeOpts, invokeGlobals);
                if (result.IsFailure)
                {
                    logger.LogWarning("Script tool {Tool} invocation failed: {Error}", name, result.Error);
                    return Task.FromResult(ToolResult.Error($"Script tool '{name}' failed: {result.Error}"));
                }
                return Task.FromResult(ScriptTool.ConvertToToolResult(result.Value));
            };

        var tool = new ScriptTool(
            name,
            displayName,
            description,
            schema,
            mode,
            execute);

        var result = globals.Tools.Register(tool);
        if (result.IsFailure)
        {
            logger.LogDebug("Tool {Tool} registration skipped: {Error}", name, result.Error);
        }
        else
        {
            logger.LogInformation("Script registered tool {Tool}", name);
        }
    }

    private static string CaptureExecuteSource(JsValue executeFn, string toolName, ILogger logger)
    {
        try
        {
            if (executeFn is Function fn)
            {
                return fn.ToString();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to capture execute function source for tool {Tool}", toolName);
        }

        return $"(args) => {{ throw new Error('execute not callable for tool {toolName}'); }}";
    }

    public static void RegisterProvider(JsValue def, ScriptGlobals globals, ILogger logger)
    {
        var obj = def.AsObject();
        string? name = obj.Get("name").IsUndefined() ? null : obj.Get("name").AsString();
        logger.LogWarning("Script attempted to register provider {Provider} — script-side provider registration is not yet supported (use a CS plugin).", name ?? "<unnamed>");
    }

    public static JsValue GetTool(string name, Engine engine, ScriptGlobals globals)
    {
        var result = globals.Tools.GetTool(ToolName.Create(name));
        if (result.IsFailure)
        {
            return JsValue.Undefined;
        }

        var tool = result.Value;
        var dotnet = new Dictionary<string, object?>
        {
            ["name"] = tool.Name.Value,
            ["displayName"] = tool.DisplayName,
            ["description"] = tool.Description
        };
        return JsValue.FromObject(engine, dotnet);
    }

    public static JsValue ListTools(Engine engine, ScriptGlobals globals)
    {
        var all = globals.Tools.GetAllTools();
        object[] arr = new object[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            arr[i] = new Dictionary<string, object?>
            {
                ["name"] = t.Name.Value,
                ["displayName"] = t.DisplayName,
                ["description"] = t.Description
            };
        }
        return JsValue.FromObject(engine, arr);
    }

    public static JsValue ListProviders(Engine engine, ScriptGlobals globals)
    {
        string[] ids = globals.Providers is null
            ? Array.Empty<string>()
            : globals.Providers.GetRegisteredProviderIds().Select(p => p.Value).ToArray();
        return JsValue.FromObject(engine, ids);
    }

    public static JsValue ListAgents(Engine engine, ScriptGlobals globals)
    {
        string[] ids = globals.Agents is null
            ? Array.Empty<string>()
            : globals.Agents.GetAllAgents().Select(a => a.Name.Value).ToArray();
        return JsValue.FromObject(engine, ids);
    }
}
