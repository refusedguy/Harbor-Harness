using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Harbor.Plugins.Runtime.Storage;
namespace Harbor.Plugins.Runtime.Compilation;

/// <summary>
///     <see cref="IPluginCompiler" /> that compiles CS source via
/// <see cref="CSharpCompilation" /> in-memory. The compiled assembly bytes are loaded
/// via <see cref="System.Reflection.Assembly.Load(byte[])" /> — no file is written to
/// disk by this class (caching is the responsibility of <see cref="CachingCompiler" />).
/// </summary>
/// <remarks>
///     <para>
///         Metadata references are gathered once at construction time from
/// <see cref="PluginAssemblyReferences" />. Plugin authors can therefore reference any
///         type already loaded in the host's <see cref="AppDomain" />.
///     </para>
///     <para>
///         This compiler is the only one in the default plugin runtime stack that
///         requires the JIT (Roslyn cannot run under NativeAOT). For AOT scenarios, swap
///         in a DLL-pre-built or out-of-process compiler implementation.
///     </para>
/// </remarks>
public sealed class RoslynPluginCompiler : IPluginCompiler
{
    private readonly PluginAssemblyReferences _references;

    /// <summary>
    ///     Construct a new Roslyn compiler.
    /// </summary>
    /// <param name="references">
    ///     Pre-built metadata references. If <see langword="null" />, a fresh snapshot
    ///     of <see cref="AppDomain.CurrentDomain" /> is taken via
    ///     <see cref="PluginAssemblyReferences" />.
    /// </param>
    public RoslynPluginCompiler(PluginAssemblyReferences references)
    {
        _references = references ?? throw new ArgumentNullException(nameof(references));
    }

    /// <inheritdoc />
    public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));
        ct.ThrowIfCancellationRequested();

        SourceText sourceText = SourceText.From(script.Source);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: script.Path);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"Harbor.Plugin.Dynamic.{script.Hash}",
            syntaxTrees: new[] { syntaxTree },
            references: _references.References,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                .ToList();
            return Task.FromResult(CompilationResult.Failure(
                $"Roslyn compilation failed for '{script.Path}':\n{string.Join("\n", FormatAll(errors))}",
                errors));
        }

        byte[] assemblyBytes = ms.ToArray();
        var asm = System.Reflection.Assembly.Load(assemblyBytes);
        var compiled = new CompiledPluginAssembly(asm, script.Hash, script.Path, assemblyBytes, FromCache: false);
        return Task.FromResult(CompilationResult.Fresh(compiled));
    }

    private static IEnumerable<string> FormatAll(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
            yield return FormatDiagnostic(d);
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        var loc = d.Location.GetLineSpan();
        string pos = loc.IsValid
            ? $"{loc.Path}({loc.StartLinePosition.Line + 1},{loc.StartLinePosition.Character + 1})"
            : "(unknown)";
        return $"  [{d.Severity}] {pos}: {d.Id} — {d.GetMessage()}";
    }
}