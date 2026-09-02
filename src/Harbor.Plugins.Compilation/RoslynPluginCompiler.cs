using System.Linq;
using System.Reflection;
using Harbor.Plugins.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
namespace Harbor.Plugins.Compilation;
/// <summary>
///     <see cref="IPluginCompiler" /> that compiles CS source via
///     <see cref="CSharpCompilation" /> in-memory. The compiled assembly bytes are loaded
///     via <see cref="System.Reflection.Assembly.Load(byte[])" /> — no file is written to
///     disk by this class (caching is the responsibility of <see cref="CachingCompiler" />).
/// </summary>
/// <remarks>
///     <para>
///         Metadata references are gathered once at construction time from
///         <see cref="PluginAssemblyReferences" />. Plugin authors can therefore reference any
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
    private readonly Func<PluginScript, Assembly>? _assemblyLoader;

    /// <summary>
    ///     Construct a new Roslyn compiler.
    /// </summary>
    /// <param name="references">
    ///     Pre-built metadata references. If <see langword="null" />, a fresh snapshot
    ///     of <see cref="AppDomain.CurrentDomain" /> is taken via
    ///     <see cref="PluginAssemblyReferences" />.
    /// </param>
    /// <param name="assemblyLoader">
    ///     Optional custom loader used to place the compiled PE image into an
    ///     <see cref="AssemblyLoadContext" />. When <see langword="null" />, the assembly
    ///     loads into a fresh <see cref="CollectiblePluginLoadContext" /> sandbox built
    ///     from the script's declared capabilities (fail-closed deny-list).
    /// </param>
    public RoslynPluginCompiler(PluginAssemblyReferences references, Func<PluginScript, Assembly>? assemblyLoader = null)
    {
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _assemblyLoader = assemblyLoader;
    }

    /// <inheritdoc />
    public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default)
    {
        if (script is null)
            throw new ArgumentNullException(nameof(script));
        ct.ThrowIfCancellationRequested();

        var sourceText = SourceText.From(script.Source);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: script.Path);

        var compilation = CSharpCompilation.Create(
            $"Harbor.Plugin.Dynamic.{script.Hash}",
            new[] { BuildImplicitUsingsSyntaxTree(), syntaxTree },
            _references.References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
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
        var sandbox = _assemblyLoader is null
            ? CollectiblePluginLoadContext.ForScript(script)
            : null;
        var asm = _assemblyLoader?.Invoke(script)
            ?? sandbox!.LoadFromImage(assemblyBytes);
        var compiled = new CompiledPluginAssembly(
            asm,
            script.Hash,
            script.Path,
            assemblyBytes,
            FromCache: false,
            script.DeclaredCapabilities);
        return Task.FromResult(CompilationResult.Fresh(compiled));
    }

    /// <summary>
    ///     Namespaces injected as <c>global using</c> directives into every
    ///     compiled plugin. Plugin authors typically copy sources from the
    ///     shipped samples, whose DLL projects rely on
    ///     <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>; a raw
    ///     Roslyn compilation has no SDK-level implicit usings, so without this
    ///     prelude those copies fail with CS0246 for even basic BCL types
    ///     (<c>Version</c>, <c>Task</c>, <c>Directory</c>). Duplicates of an
    ///     explicit using in the source are legal C# and produce no diagnostics.
    /// </summary>
    private static readonly string[] ImplicitUsingNamespaces =
    {
        // The .NET SDK implicit-usings set.
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
        // Harbor contract namespaces referenced by every plugin shape.
        "Harbor.Abstractions.Models",
        "Harbor.Abstractions.Plugins",
        "Harbor.Abstractions.Tools",
        "Microsoft.Extensions.Logging",
    };

    private static SyntaxTree BuildImplicitUsingsSyntaxTree()
    {
        var prelude = string.Join(
            Environment.NewLine,
            ImplicitUsingNamespaces.Select(ns => $"global using {ns};"));
        return CSharpSyntaxTree.ParseText(prelude, path: "<harbor-plugin-implicit-usings>");
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
