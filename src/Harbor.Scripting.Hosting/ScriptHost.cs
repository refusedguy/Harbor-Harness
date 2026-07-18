// Hosting layer — the public facade. See ScriptHostOptions.cs for layering rules.
namespace Harbor.Scripting.Hosting;

/// <summary>
///     The script host: composes an <see cref="IScriptEngine" />,
///     <see cref="IScriptStore" />, and <see cref="IScriptCompiler" /> into a
///     load / evaluate pipeline.
/// </summary>
/// <remarks>
///     <para>
///         This is the <b>only</b> layer that knows about all three
///         sub-layers. Callers (the CLI, tests, future plugins) interact with
///         scripts through this facade — they never touch engines, stores, or
///         compilers directly.
///     </para>
///     <para>
///         <b>Pipeline:</b>
///         <list type="number">
///             <item><see cref="IScriptStore.ListAsync" /> — enumerate scripts.</item>
///             <item><see cref="IScriptCompiler.Compile" /> — compile each script's source.</item>
///             <item><see cref="IScriptEngine.Evaluate" /> — evaluate the compiled source with the supplied globals.</item>
///         </list>
///         Each step can fail; the host returns a <see cref="Result" />
///         aggregate. Individual failures are logged and (by default)
///         don't abort the batch — see <see cref="ScriptHostOptions.ContinueOnFailure" />.
///     </para>
/// </remarks>
public sealed class ScriptHost
{
    private readonly IScriptEngine _engine;
    private readonly IScriptStore _store;
    private readonly IScriptCompiler _compiler;
    private readonly ILogger<ScriptHost> _logger;
    private readonly ScriptHostOptions _options;

    /// <summary>
    ///     Construct a script host.
    /// </summary>
    /// <param name="engine">Script engine used for evaluation.</param>
    /// <param name="store">Script storage (where scripts live on disk / memory).</param>
    /// <param name="compiler">Source compiler (passthrough for native-TS engines, tsc for Jint).</param>
    /// <param name="logger">Logger for host lifecycle events.</param>
    /// <param name="options">Host options (continue-on-failure, engine limits).</param>
    public ScriptHost(
        IScriptEngine engine,
        IScriptStore store,
        IScriptCompiler compiler,
        ILogger<ScriptHost> logger,
        ScriptHostOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new ScriptHostOptions();
    }

    /// <summary>
    ///     Load (compile + evaluate) every script in the store, in store order.
    /// </summary>
    /// <param name="globals">Bridge globals (registries + logger) passed to each evaluation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success if all scripts loaded; failure with the first error otherwise. Per-script results are in <see cref="ScriptHostLoadResult.Instances" />.</returns>
    public async Task<Result<ScriptHostLoadResult>> LoadAllAsync(ScriptGlobals globals, CancellationToken cancellationToken = default)
    {
        var list = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsFailure)
        {
            return Result.Failure<ScriptHostLoadResult>(list.Error);
        }

        var instances = new List<ScriptInstance>(list.Value.Count);
        var errors = new List<string>();
        foreach (var entry in list.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await LoadOneAsync(entry, globals, cancellationToken).ConfigureAwait(false);
            instances.Add(instance);
            if (!instance.Succeeded)
            {
                _logger.LogWarning("Script {Name} failed to load: {Error}", entry.Name, instance.Error);
                errors.Add($"{entry.Name}: {instance.Error}");
                if (!_options.ContinueOnFailure)
                {
                    return Result.Failure<ScriptHostLoadResult>(errors[0]);
                }
            }
            else
            {
                _logger.LogInformation("Script {Name} loaded from {Path}", entry.Name, entry.Path);
            }
        }

        return Result.Success(new ScriptHostLoadResult(instances, errors));
    }

    /// <summary>
    ///     Load (compile + evaluate) a single named script.
    /// </summary>
    public async Task<Result<ScriptInstance>> LoadByNameAsync(string name, ScriptGlobals globals, CancellationToken cancellationToken = default)
    {
        var read = await _store.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return Result.Failure<ScriptInstance>(read.Error);
        }
        return Result.Success(await LoadOneAsync(read.Value, globals, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Evaluate an arbitrary source string (not from the store) with the
    ///     host's configured engine + compiler. Used by the <c>--script &lt;path&gt;</c>
    ///     CLI flag for one-shot scripts.
    /// </summary>
    public async Task<Result<ScriptInstance>> EvaluateAsync(string sourceName, string source, ScriptGlobals globals, CancellationToken cancellationToken = default)
    {
        var entry = new ScriptEntry(
            Name: Path.GetFileNameWithoutExtension(sourceName),
            Path: sourceName,
            Content: source,
            Hash: HashContent(source),
            LastModified: null);
        return await LoadOneAsync(entry, globals, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScriptInstance> LoadOneAsync(ScriptEntry entry, ScriptGlobals globals, CancellationToken cancellationToken)
    {
        var compiled = _compiler.Compile(entry.Path, entry.Content);
        if (compiled.IsFailure)
        {
            return new ScriptInstance
            {
                Source = entry,
                Compiled = string.Empty,
                Succeeded = false,
                Error = compiled.Error,
                Elapsed = TimeSpan.Zero
            };
        }

        var opts = _options.EngineOptions with { CancellationToken = cancellationToken, SourceName = entry.Path };
        var sw = Stopwatch.StartNew();
        var eval = await Task.Run(() => _engine.Evaluate(compiled.Value, opts, globals)).ConfigureAwait(false);
        sw.Stop();
        return new ScriptInstance
        {
            Source = entry,
            Compiled = compiled.Value,
            Succeeded = eval.IsSuccess,
            Error = eval.IsFailure ? eval.Error : null,
            Elapsed = sw.Elapsed
        };
    }

    private static string HashContent(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
///     Aggregate result of a <see cref="ScriptHost.LoadAllAsync" /> call:
///     per-script instances + the list of failures (empty if all succeeded).
/// </summary>
public sealed record ScriptHostLoadResult(
    IReadOnlyList<ScriptInstance> Instances,
    IReadOnlyList<string> Errors);
