// Compilation layer — pass-through compiler. See IScriptCompiler.cs for layering rules.
namespace Harbor.Scripting.Compilation;
/// <summary>
///     <see cref="IScriptCompiler" /> that returns the source unchanged.
/// </summary>
/// <remarks>
///     Pair with engines that accept TypeScript natively — SharpTS is the
///     canonical example. Also suitable for plain JavaScript sources fed to
///     Jint when no TypeScript is involved.
/// </remarks>
public sealed class PassThroughCompiler : IScriptCompiler
{
    /// <inheritdoc />
    public Result<string> Compile(string sourceName, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Result.Failure<string>("Script source is empty.");
        }
        return Result.Success(source);
    }
}
