// Engines layer — pure script engine abstraction.
//
// Layering rule (see docs/SCRIPTING.md §Architecture):
//   This layer knows NOTHING about the filesystem, storage, or compilation.
//   Given (code, options, globals), it evaluates the code and returns a Result.
//   Implementations may run in-process (Jint) or as a subprocess (SharpTS).
namespace Harbor.Scripting.Abstractions;
/// <summary>
///     Abstraction over a script engine for Harbor plugins.
/// </summary>
/// <remarks>
///     <para>
///         An engine takes TypeScript / JavaScript source code, evaluates it, and
///         returns either <see cref="Result" /> (side-effect-only evaluation) or
///         <see cref="Result{T}" /> (evaluating an expression whose value is
///         converted to the type parameter of
///         <see cref="Evaluate{T}" />).
///     </para>
///     <para>
///         <b>Thread safety:</b> implementations MUST be safe for concurrent
///         <see cref="Evaluate" /> calls from multiple threads. Engine instances
///         are typically singletons shared across the host.
///     </para>
///     <para>
///         <b>Failure modes:</b> all expected failures (syntax error, runtime
///         exception, timeout, denied built-in, conversion error, missing
///         external tool) return <see cref="Result" />.<see cref="Result.Failure(string)" /> —
///         no exceptions leak to the caller. Unexpected infrastructure errors
///         (out of memory, stack overflow) still throw.
///     </para>
///     <para>
///         <b>Layering:</b> engines MUST NOT touch the filesystem or know about
///         script storage / discovery. That is the <c>Hosting</c> layer's job.
///     </para>
/// </remarks>
public interface IScriptEngine
{
    /// <summary>
    ///     Evaluate the supplied code for side effects only, discarding the
    ///     result value. Use this for scripts that register tools via
    ///     <c>Harbor.registerTool</c>.
    /// </summary>
    /// <param name="code">TypeScript or JavaScript source code.</param>
    /// <param name="options">Engine resource limits (timeout, memory, cancellation).</param>
    /// <param name="globals">Bridge globals exposed as the <c>Harbor</c> object.</param>
    /// <returns>Success, or failure with a descriptive error message.</returns>
    public Result Evaluate(string code, ScriptEngineOptions options, ScriptGlobals globals);

    /// <summary>
    ///     Evaluate the supplied code and convert the resulting value to
    ///     <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">Target .NET type. Primitives convert directly; complex types use JSON round-trip.</typeparam>
    /// <param name="code">TypeScript / JavaScript expression returning a value convertible to <typeparamref name="T" />.</param>
    /// <param name="options">Engine resource limits.</param>
    /// <param name="globals">Bridge globals.</param>
    /// <returns>Success with the converted value, or failure with a descriptive error message.</returns>
    public Result<T> Evaluate<T>(string code, ScriptEngineOptions options, ScriptGlobals globals);
}
