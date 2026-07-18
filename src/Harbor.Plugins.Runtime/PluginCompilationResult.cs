using Microsoft.CodeAnalysis;
namespace Harbor.Plugins.Runtime;

/// <summary>
///     Result of compiling a single CS-source plugin. Success carries the live
///     <see cref="CompiledPlugin" />; failure carries the Roslyn diagnostics as a
///     formatted error string.
/// </summary>
public readonly record struct PluginCompilationResult
{
    private readonly Result<CompiledPlugin> _result;
    private readonly IReadOnlyList<Diagnostic>? _diagnostics;

    private PluginCompilationResult(Result<CompiledPlugin> result, IReadOnlyList<Diagnostic>? diagnostics)
    {
        _result = result;
        _diagnostics = diagnostics;
    }

    /// <summary>Whether the compilation succeeded.</summary>
    public bool IsSuccess => _result.IsSuccess;

    /// <summary>Whether the compilation failed.</summary>
    public bool IsFailure => _result.IsFailure;

    /// <summary>The compiled plugin (only valid when <see cref="IsSuccess" />).</summary>
    public CompiledPlugin Value => _result.Value;

    /// <summary>The error message (only valid when <see cref="IsFailure" />).</summary>
    public string Error => _result.Error;

    /// <summary>
    ///     All Roslyn diagnostics emitted during compilation (warnings + errors). Empty on
    ///     success-only paths or when the failure occurred before reaching the compiler
    ///     (e.g. file-not-found).
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics ?? Array.Empty<Diagnostic>();

    /// <summary>Create a successful compilation result.</summary>
    public static PluginCompilationResult Success(CompiledPlugin plugin) =>
        new(Result.Success(plugin), null);

    /// <summary>Create a failed compilation result with diagnostics.</summary>
    public static PluginCompilationResult Failure(string error, IReadOnlyList<Diagnostic> diagnostics) =>
        new(Result.Failure<CompiledPlugin>(error), diagnostics);

    /// <summary>Create a failed compilation result without diagnostics (pre-compiler failures).</summary>
    public static PluginCompilationResult Failure(string error) =>
        new(Result.Failure<CompiledPlugin>(error), null);
}
