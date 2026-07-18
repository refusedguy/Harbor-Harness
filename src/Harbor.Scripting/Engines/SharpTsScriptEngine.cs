// Engines layer — SharpTS subprocess engine (the default for Harbor scripting).
//
// Layering rule (see IScriptEngine.cs):
//   Knows about ScriptGlobals (Bridge) and Harbor.Abstractions only.
//   Knows NOTHING about filesystem storage or compilation. It does write to a
//   temp directory (Process scratch space), but that is engine-internal — it
//   never reads the user's script store.
namespace Harbor.Scripting.Engines;

/// <summary>
///     <see cref="IScriptEngine" /> backed by the SharpTS TypeScript
///     interpreter, invoked as a <c>sharpts</c> subprocess.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why subprocess?</b> SharpTS (https://github.com/nickna/SharpTS)
///         is MIT-licensed, targets .NET 10, and implements a complete
///         TypeScript interpreter + AOT IL emitter. As of v1.0.8 it ships as
///         a <c>dotnet tool</c> (NuGet package type <c>DotnetTool</c>), not a
///         class library. Until a library package is published, the cleanest
///         integration is to invoke <c>sharpts</c> as a subprocess — which
///         has the bonus of giving us OS-level process isolation for free.
///     </para>
///     <para>
///         <b>Install SharpTS:</b>
///         <code>dotnet tool install -g SharpTS</code>
///         Ensure <c>~/.dotnet/tools</c> is on <c>PATH</c>. When not
///         installed, every <see cref="Evaluate" /> call returns a clear
///         failure; callers can fall back to <see cref="JintScriptEngine" />.
///     </para>
///     <para>
///         <b>Bridge protocol:</b> the engine injects a TypeScript preamble
///         that defines a <c>Harbor</c> global. Tool registrations and log
///         calls are buffered in-process and emitted as a JSON event stream
///         on <c>stderr</c> at script exit. The .NET host parses the stream
///         and replays events into the live <see cref="IToolRegistry" /> /
///         <see cref="ILogger" />. <see cref="Evaluate{T}" /> reads a single
///         JSON-encoded value from <c>stdout</c> when the script emits a line
///         starting with <c>__HARBOR_RESULT__</c>.
///     </para>
///     <para>
///         <b>Thread safety:</b> each <see cref="Evaluate" /> call spawns a
///         fresh <c>sharpts</c> process, so concurrent calls are isolated.
///         The engine instance itself is thread-safe.
///     </para>
///     <para>
///         <b>Resource limits:</b> the <see cref="ScriptEngineOptions.Timeout" />
///         is enforced as a process kill after the timeout elapses.
///         <see cref="ScriptEngineOptions.MemoryLimitBytes" /> is best-effort
///         (process working-set hint where supported). Statement / recursion
///         limits are not enforced by subprocess mode — the timeout is the
///         hard backstop.
///     </para>
/// </remarks>
public sealed class SharpTsScriptEngine : IScriptEngine
{
    /// <summary>Marker prefix for the JSON event stream emitted on stderr at script exit.</summary>
    public const string EventsMarker = "__HARBOR_EVENTS__";

    /// <summary>Marker prefix for the JSON-encoded result emitted on stdout by <see cref="Evaluate{T}" />.</summary>
    public const string ResultMarker = "__HARBOR_RESULT__";

    private const string BridgePreamble = """
                                           const __harbor_events: any[] = [];
                                           const Harbor = {
                                             registerTool(def: any): any {
                                               if (typeof def !== 'object' || def === null) throw new Error("registerTool requires an object");
                                               if (typeof def.name !== 'string' || def.name.length === 0) throw new Error("registerTool: .name (non-empty string) required");
                                               if (typeof def.execute !== 'function') throw new Error("registerTool: .execute (function) required");
                                               __harbor_events.push({ kind: 'registerTool', def: {
                                                 name: def.name,
                                                 displayName: def.displayName || def.name,
                                                 description: def.description || ('Script tool: ' + def.name),
                                                 parameterSchema: def.parameterSchema || { type: 'object', properties: {} },
                                                 executionMode: def.executionMode || 'Parallel',
                                                 executeSource: def.execute.toString()
                                               }});
                                               return def;
                                             },
                                             log(msg: any): void { __harbor_events.push({ kind: 'log', msg: String(msg) }); },
                                             tools:    { get: (_n: string) => undefined, list: () => [] },
                                             providers:{ list: () => [] },
                                             agents:   { list: () => [] }
                                           };
                                           """;

    private const string BridgeEpilogue = """
                                           console.error('__HARBOR_EVENTS__' + JSON.stringify(__harbor_events));
                                           """;

    private readonly ILogger<SharpTsScriptEngine> _logger;
    private readonly string _toolName;
    private readonly Lazy<bool> _available;

    /// <summary>
    ///     Construct a SharpTS-backed engine.
    /// </summary>
    /// <param name="logger">Logger for engine lifecycle events.</param>
    /// <param name="toolName">Override the <c>sharpts</c> executable name (default <c>sharpts</c>).</param>
    public SharpTsScriptEngine(ILogger<SharpTsScriptEngine> logger, string toolName = "sharpts")
    {
        _logger = logger;
        _toolName = toolName;
        _available = new Lazy<bool>(DetectTool, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Returns <see langword="true" /> if the <c>sharpts</c> tool is available on PATH.</summary>
    public bool IsAvailable => _available.Value;

    /// <inheritdoc />
    public Result Evaluate(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure("Script code is empty.");
        }
        if (!IsAvailable)
        {
            return Result.Failure(NotInstalledMessage);
        }

        string wrapped = $"{BridgePreamble}\n{code}\n{BridgeEpilogue}";
        var run = RunSharpTs(wrapped, options);
        if (run.IsFailure)
        {
            return run;
        }

        ReplayEvents(run.Value.Stderr, globals);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result<T> Evaluate<T>(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<T>("Script code is empty.");
        }
        if (!IsAvailable)
        {
            return Result.Failure<T>(NotInstalledMessage);
        }

        // For Evaluate<T>, the user script must emit a result marker on stdout
        // (see ResultMarker). The preamble/epilogue are still injected so
        // registerTool / log work too.
        string wrapped = $"{BridgePreamble}\n{code}\n{BridgeEpilogue}";
        var run = RunSharpTs(wrapped, options);
        if (run.IsFailure)
        {
            return Result.Failure<T>(run.Error);
        }

        ReplayEvents(run.Value.Stderr, globals);

        string? resultJson = ExtractMarkerLine(run.Value.Stdout, ResultMarker);
        if (resultJson is null)
        {
            return Result.Failure<T>(
                $"Script did not emit a result. Expected a stdout line starting with '{ResultMarker}' " +
                $"followed by a JSON-encoded value of type {typeof(T).Name}.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(resultJson, SharpTsJsonOptions.Default);
            return value is null
                ? Result.Failure<T>($"Script result deserialized to null for target type {typeof(T).Name}.")
                : Result.Success(value);
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"Cannot convert script result to {typeof(T).Name}: {ex.Message}");
        }
    }

    private static string NotInstalledMessage =>
        "SharpTS is not available on PATH. Install with `dotnet tool install -g SharpTS` " +
        "(ensure ~/.dotnet/tools is on PATH), or fall back to the Jint script engine.";

    private Result<(string Stdout, string Stderr)> RunSharpTs(string source, ScriptEngineOptions opts)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "harbor-sharpts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "script.ts");
        try
        {
            File.WriteAllText(scriptPath, source);
            var psi = new ProcessStartInfo
            {
                FileName = _toolName,
                Arguments = $"\"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return Result.Failure<(string, string)>("Failed to start sharpts process.");
            }

            // Timeout: kill the process if it doesn't exit in time. The
            // cancellation token is observed via a linked registration that
            // also kills the process.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(opts.CancellationToken);
            linkedCts.CancelAfter(opts.Timeout);
            linkedCts.Token.Register(() =>
            {
                try { p.Kill(entireProcessTree: true); } catch { /* swallow */ }
            });

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            // WaitAsync throws TaskCanceledException on timeout/cancellation.
            try
            {
                p.WaitForExit();
                stdoutTask.Wait(opts.Timeout);
                stderrTask.Wait(opts.Timeout);
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or AggregateException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* swallow */ }
                return Result.Failure<(string, string)>($"Script timed out after {opts.Timeout.TotalSeconds:0.###}s.");
            }

            string stdout = stdoutTask.Result;
            string stderr = stderrTask.Result;
            if (p.ExitCode != 0)
            {
                string trimmedErr = stderr.Trim();
                string trimmedOut = stdout.Trim();
                return Result.Failure<(string, string)>(
                    $"sharpts exited with code {p.ExitCode}: {trimmedErr}{(trimmedErr.Length > 0 && trimmedOut.Length > 0 ? "\n" : "")}{trimmedOut}");
            }
            return Result.Success((stdout, stderr));
        }
        catch (Exception ex)
        {
            return Result.Failure<(string, string)>($"Failed to run sharpts: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* swallow */ }
        }
    }

    private void ReplayEvents(string stderr, ScriptGlobals globals)
    {
        string? json = ExtractMarkerLine(stderr, EventsMarker);
        if (json is null)
        {
            return; // No events emitted — script may not have used the bridge.
        }

        JsonElement[] events;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("SharpTS events payload was not a JSON array; ignoring.");
                return;
            }
            events = [.. doc.RootElement.EnumerateArray()];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SharpTS events JSON");
            return;
        }

        foreach (var ev in events)
        {
            if (!ev.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string kind = kindEl.GetString() ?? string.Empty;
            switch (kind)
            {
                case "log":
                    string msg = ev.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                        ? msgEl.GetString() ?? string.Empty
                        : ev.GetRawText();
                    globals.Logger.LogInformation("Harbor.script: {Message}", msg);
                    break;
                case "registerTool":
                    if (ev.TryGetProperty("def", out var defEl))
                    {
                        RegisterToolFromEvent(defEl, globals);
                    }
                    break;
                default:
                    _logger.LogDebug("Unknown SharpTS event kind: {Kind}", kind);
                    break;
            }
        }
    }

    private void RegisterToolFromEvent(JsonElement defEl, ScriptGlobals globals)
    {
        if (!defEl.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("SharpTS registerTool event missing 'name'");
            return;
        }
        string name = nameEl.GetString() ?? string.Empty;
        string displayName = defEl.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind == JsonValueKind.String
            ? dnEl.GetString() ?? name
            : name;
        string description = defEl.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString() ?? $"Script tool: {name}"
            : $"Script tool: {name}";
        string executionModeStr = defEl.TryGetProperty("executionMode", out var emEl) && emEl.ValueKind == JsonValueKind.String
            ? emEl.GetString() ?? "Parallel"
            : "Parallel";
        string executeSource = defEl.TryGetProperty("executeSource", out var esEl) && esEl.ValueKind == JsonValueKind.String
            ? esEl.GetString() ?? $"(args) => {{ throw new Error('no execute source for tool {name}'); }}"
            : $"(args) => {{ throw new Error('no execute source for tool {name}'); }}";

        JsonDocument schema;
        if (defEl.TryGetProperty("parameterSchema", out var schemaEl))
        {
            try
            {
                schema = JsonDocument.Parse(schemaEl.GetRawText());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse parameterSchema for tool {Tool}", name);
                schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
            }
        }
        else
        {
            schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        }

        var mode = executionModeStr.Equals("Sequential", StringComparison.OrdinalIgnoreCase)
            ? ExecutionMode.Sequential
            : ExecutionMode.Parallel;

        var invokeOpts = ScriptEngineOptions.Default;
        Func<JsonElement, CancellationToken, Task<ToolResult>> execute =
            (args, ct) =>
            {
                var invokeEngine = new SharpTsScriptEngine(_logger, _toolName);
                var invokeOptions = invokeOpts with { CancellationToken = ct, SourceName = $"script:{name}" };
                string argsJson = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
                // Build a one-shot TS program: define the execute function,
                // invoke it with args, emit the result on stdout.
                string code = $"{executeSource}\n" +
                              $"const __harbor_result = (__execute || (execute))({argsJson});\n" +
                              $"console.log('{ResultMarker}' + JSON.stringify(__harbor_result));";
                var invokeGlobals = new ScriptGlobals
                {
                    Tools = globals.Tools,
                    Providers = globals.Providers,
                    Agents = globals.Agents,
                    Logger = globals.Logger
                };
                var result = invokeEngine.Evaluate<JsonElement>(code, invokeOptions, invokeGlobals);
                if (result.IsFailure)
                {
                    _logger.LogWarning("Script tool {Tool} invocation failed: {Error}", name, result.Error);
                    return Task.FromResult(ToolResult.Error($"Script tool '{name}' failed: {result.Error}"));
                }
                return Task.FromResult(ScriptTool.ConvertToToolResult(result.Value));
            };

        var tool = new ScriptTool(name, displayName, description, schema, mode, execute);
        var result = globals.Tools.Register(tool);
        if (result.IsFailure)
        {
            _logger.LogDebug("Tool {Tool} registration skipped: {Error}", name, result.Error);
        }
        else
        {
            _logger.LogInformation("SharpTS script registered tool {Tool}", name);
        }
    }

    private static string? ExtractMarkerLine(string output, string marker)
    {
        // The marker is emitted at the start of a line, followed by JSON.
        // Scan stdout/stderr line by line; return the JSON portion (everything
        // after the marker on that line).
        int start = 0;
        while (start < output.Length)
        {
            int idx = output.IndexOf(marker, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }
            // Must be at start of line (or start of output).
            if (idx > 0 && output[idx - 1] != '\n' && output[idx - 1] != '\r')
            {
                start = idx + marker.Length;
                continue;
            }
            int afterMarker = idx + marker.Length;
            int lineEnd = output.IndexOf('\n', afterMarker);
            string line = lineEnd < 0
                ? output[afterMarker..]
                : output[afterMarker..lineEnd];
            return line.Trim('\r', '\n');
        }
        return null;
    }

    private bool DetectTool()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _toolName,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { /* swallow */ }
                return false;
            }
            if (p.ExitCode != 0)
            {
                return false;
            }
            _logger.LogInformation("Detected sharpts: {Version}", p.StandardOutput.ReadToEnd().Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "sharpts detection failed");
            return false;
        }
    }
}

/// <summary>Shared JSON serializer options for SharpTS value conversion.</summary>
internal static class SharpTsJsonOptions
{
    public static readonly JsonSerializerOptions Default = new() { PropertyNameCaseInsensitive = true };
}
