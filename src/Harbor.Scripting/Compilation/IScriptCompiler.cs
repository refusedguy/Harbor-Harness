// Compilation layer — pure script source compiler.
//
// Layering rule (see docs/SCRIPTING.md §Architecture):
//   This layer knows NOTHING about engines or storage. Given a source string,
//   it produces a compiled form (also a string). The contract is engine-neutral:
//   the Hosting layer pairs an engine with the right compiler.
namespace Harbor.Scripting.Compilation;

/// <summary>
///     Compiles script source into a form ready for an
///     <see cref="IScriptEngine" /> to evaluate.
/// </summary>
/// <remarks>
///     <para>
///         Most engines accept TypeScript directly (SharpTS) or JavaScript
///         directly (Jint), in which case the
///         <see cref="PassThroughCompiler" /> is the right choice. Engines
///         that don't speak TypeScript natively (e.g. Jint) pair with the
///         <see cref="TscCompiler" />, which shells out to <c>tsc</c>.
///     </para>
///     <para>
///         <b>Layering:</b> this interface MUST NOT reference engines,
///         storage, or the Harbor bridge. It is a pure function from source
///         to source.
///     </para>
/// </remarks>
public interface IScriptCompiler
{
    /// <summary>
    ///     Compile the supplied source into an engine-ready form.
    /// </summary>
    /// <param name="sourceName">Source name (file path or label) — used for error messages only.</param>
    /// <param name="source">Script source code.</param>
    /// <returns>Success with the compiled source, or failure with a descriptive error.</returns>
    Result<string> Compile(string sourceName, string source);
}
