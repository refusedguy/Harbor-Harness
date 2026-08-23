// Engines layer — engine resource limits. See IScriptEngine.cs for layering rules.
namespace Harbor.Scripting.Abstractions;
/// <summary>
///     Resource limits and execution context for a single
///     <see cref="IScriptEngine.Evaluate" /> call.
/// </summary>
/// <remarks>
///     <para>
///         This type holds ONLY engine-side concerns: timeouts, memory caps,
///         statement budgets, cancellation. The Harbor bridge surface
///         (registries, logger) lives in <see cref="ScriptGlobals" /> —
///         deliberately separated so that engines don't need to know about
///         the bridge to interpret code.
///     </para>
///     <para>
///         Defaults are conservative; callers constructing a fresh
///         <see cref="ScriptEngineOptions" /> per invocation cannot forget to
///         set them — the property initializers provide safe defaults.
///     </para>
/// </remarks>
public sealed record ScriptEngineOptions
{
    /// <summary>
    ///     Hard execution timeout. Default: 5 seconds. Enforced between
    ///     statements; a single non-interruptible statement (e.g. catastrophic
    ///     regex backtracking) may exceed this.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Memory budget for the engine's internal structures. Default: 10 MB.
    ///     For in-process engines this is an allocation cap, not a true RSS cap.
    ///     For subprocess engines it is enforced as a process working-set limit
    ///     where supported by the host OS.
    /// </summary>
    public long MemoryLimitBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    ///     Maximum number of statements a script may execute. Default: 1,000,000.
    ///     Ignored by subprocess engines that don't expose a statement counter.
    /// </summary>
    public int MaxStatements { get; init; } = 1_000_000;

    /// <summary>
    ///     Maximum call-stack depth. Default: 1,000.
    /// </summary>
    public int MaxRecursionDepth { get; init; } = 1_000;

    /// <summary>
    ///     Cancellation token observed by the engine. Aborts execution at the
    ///     next safe boundary (between statements).
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     Optional source name (file path or REPL label) used in error messages.
    /// </summary>
    public string? SourceName { get; init; }

    /// <summary>
    ///     Default options: 5s timeout, 10MB memory, 1M statements, 1K recursion.
    /// </summary>
    public static ScriptEngineOptions Default { get; } = new();
}
