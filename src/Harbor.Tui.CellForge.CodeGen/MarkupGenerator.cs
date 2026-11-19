using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Entry point for the CellForge markup generator (issue #2424).
/// </summary>
/// <remarks>
///     <para>
///         Issue #2425 wires <c>.harbor-ui</c> files into the incremental
///         pipeline as <see cref="AdditionalText"/>. The cache key is the
///         project-relative <c>TargetPath</c> MSBuild forwards as the base64
///         <c>build_metadata.AdditionalFiles.TargetPath</c> option, not
///         <see cref="AdditionalText.Path"/>, which is absolute and therefore
///         changes between checkouts and build agents.
///     </para>
///     <para>
///         Issue #2426 calls <see cref="MarkupParser.Parse"/> on every file, so
///         the compiler path reads the same grammar as the dev interpreter.
///         Diagnostics from that parse reach the compiler.
///     </para>
///     <para>
///         Issue #2428 adds the two phases. Post-initialization puts the shared
///         root-builder contract into the compilation first. Phase 1 emits the
///         partial class and its declarations; phase 2 depends on phase 1's
///         provider and emits <c>BuildRoot</c> with local sequence numbers.
///     </para>
///     <para>
///         Issue #2427 puts 1-based source spans on the IR. Issue #2431 reports
///         parse errors as <c>HZ0001</c> with a location, refuses to emit either
///         half for a document with diagnostics, and carries a source checksum
///         and positional comments in the generated code.
///     </para>
///     <para>
///         Issue #2688 adds a fourth output: declarative <c>.bind</c>
///         operations become <c>Binding</c> data, emitted only when the
///         consumer references the CellForge input contracts.
///     </para>
/// </remarks>
[Generator]
public sealed class MarkupGenerator : IIncrementalGenerator
{
    private const string TargetPathOption = "build_metadata.AdditionalFiles.TargetPath";
    private const string ProjectDirectoryOption = "build_property.MSBuildProjectDirectory";
    private const string HarborUiExtension = ".harbor-ui";

    private static readonly DiagnosticDescriptor MissingStateRule = new(
        id: "HUI002",
        title: "Binding without a state type",
        messageFormat: "{0}",
        category: "CellForge.Markup",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A {Binding} needs a state: Type annotation in the markup file.");

    private static readonly DiagnosticDescriptor UnknownStateTypeRule = new(
        id: "HUI003",
        title: "State type not found",
        messageFormat: "{0}",
        category: "CellForge.Markup",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The type named by state: is not in the compilation.");

    private static readonly DiagnosticDescriptor UnknownPropertyRule = new(
        id: "HUI004",
        title: "Unknown binding property",
        messageFormat: "{0}",
        category: "CellForge.Markup",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A binding path does not resolve against the state type.");

    private static readonly DiagnosticDescriptor ReadOnlyPropertyRule = new(
        id: "HUI005",
        title: "Read-only binding property",
        messageFormat: "{0}",
        category: "CellForge.Markup",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A two-way binding points at a property without a public setter.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The type of the root parameter exists before any input is read.
        context.RegisterPostInitializationOutput(static spc => spc.AddSource(
            BuildRootEmitter.RootContractHintName,
            SourceText.From(BuildRootEmitter.RootContract(), Encoding.UTF8)));

        // The cache key is the project-relative target path plus the file text.
        // Both are strings, so Roslyn can compare them structurally across runs.
        var declarations = context.AdditionalTextsProvider
            .Where(static file => IsHarborUiFile(file.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, ct) =>
            {
                var targetPath = ResolveTargetPath(pair.Left, pair.Right);
                return new DeclarationInput(
                    targetPath,
                    pair.Left.GetText(ct)?.ToString() ?? string.Empty,
                    MakeIdentifier(targetPath));
            })
            .WithTrackingName("MarkupGenerator.Phase1Declarations");

        context.RegisterSourceOutput(declarations, static (spc, input) => EmitDeclarations(spc, input));

        // Phase 2 takes phase 1's provider as its input, so it cannot be
        // evaluated before the declarations have been produced.
        // #3083: Collect makes the set of all markup files the graph key.
        // Editing an imported component therefore changes this step's input
        // and re-emits every screen that imports it, while single-file markup
        // still goes through the original emitter unchanged.
        var moduleGraph = declarations
            .Collect()
            .Select(static (files, _) => MarkupModuleResolver.Resolve(
                files.Select(static file => new MarkupModuleSource(file.TargetPath, file.Text)).ToArray()))
            .WithTrackingName("MarkupGenerator.Phase2ModuleGraph");

        context.RegisterSourceOutput(moduleGraph, static (spc, graph) =>
        {
            foreach (var finding in graph.Findings)
            {
                spc.ReportDiagnostic(MarkupDiagnostics.GraphError(
                    finding.Id, finding.Path, finding.Text, finding.Diagnostic));
            }

            foreach (var module in graph.Modules)
            {
                if (!module.Parse.IsValid || graph.BlockedPaths.Contains(module.Path))
                    continue;

                // Every module gets its own BuildRoot and its own offset
                // BuildModule, so an importing screen can call it directly.
                var source = BuildRootEmitter.EmitBuildRootWithModules(
                    module.Identifier, module.Path, module.Arena, module, graph);

                spc.AddSource(
                    module.Identifier + ".BuildRoot.g.cs",
                    SourceText.From(source, Encoding.UTF8));
            }
        });

        // #2429. Resolving property paths needs the compilation's symbols.
        // Compilation identity changes on every edit, but the phase's output
        // is value-comparable, so AddSource only re-runs when the generated
        // text or a diagnostic actually changes.
        var bindings = declarations
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => TypedBindingsEmitter.Emit(
                pair.Right,
                pair.Left.TargetPath,
                pair.Left.Identifier,
                pair.Left.Text,
                MarkupParser.Parse(pair.Left.Text)))
            .WithTrackingName("MarkupGenerator.TypedBindings");

        context.RegisterSourceOutput(bindings, static (spc, output) =>
        {
            foreach (var diagnostic in output.Diagnostics.Items)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Descriptor(diagnostic.Id),
                    MarkupLocation(output.TargetPath, diagnostic.Line, diagnostic.Column),
                    diagnostic.Message));
            }

            if (output.Source.Length != 0)
            {
                spc.AddSource(
                    output.Identifier + ".Bindings.g.cs",
                    SourceText.From(output.Source, Encoding.UTF8));
            }
        });

        // #2688. Key bindings are data, not C# code-behind. The emitter checks
        // the compilation for the runtime type, so this phase adds a source
        // only where the consumer can actually bind it.
        var keyBindings = declarations
            .Combine(context.CompilationProvider)
            .Select(static (pair, _) => KeyBindingsEmitter.Emit(
                pair.Right,
                pair.Left.TargetPath,
                pair.Left.Identifier,
                MarkupParser.Parse(pair.Left.Text)))
            .WithTrackingName("MarkupGenerator.KeyBindings");

        context.RegisterSourceOutput(keyBindings, static (spc, output) =>
        {
            if (output.Source.Length != 0)
            {
                spc.AddSource(
                    output.Identifier + ".KeyBindings.g.cs",
                    SourceText.From(output.Source, Encoding.UTF8));
            }
        });
    }

    private static DiagnosticDescriptor Descriptor(string id) => id switch
    {
        "HUI002" => MissingStateRule,
        "HUI003" => UnknownStateTypeRule,
        "HUI004" => UnknownPropertyRule,
        _ => ReadOnlyPropertyRule,
    };

    private static Location MarkupLocation(string path, int line, int column)
    {
        var position = new LinePosition(Math.Max(line - 1, 0), Math.Max(column - 1, 0));
        return Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(position, position));
    }

    /// <summary>Whether a path names a CellForge markup file.</summary>
    internal static bool IsHarborUiFile(string path) =>
        path.EndsWith(HarborUiExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The stable, project-relative path of a markup file. Mirrors
    ///     <c>RazorSourceGenerator.RazorProviders.cs</c>, <c>ComputeProjectItems</c>:
    ///     MSBuild passes the <c>&lt;TargetPath&gt;</c> metadata base64-encoded,
    ///     falls back to a path relative to the project directory, and finally
    ///     to the physical path.
    /// </summary>
    internal static string ResolveTargetPath(AdditionalText file, AnalyzerConfigOptionsProvider options)
    {
        var perFile = options.GetOptions(file);
        if (perFile.TryGetValue(TargetPathOption, out var encoded)
            && !string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                if (!string.IsNullOrWhiteSpace(decoded))
                    return Normalize(decoded);
            }
            catch (FormatException)
            {
                // A malformed option is not a reason to fail a build over wiring;
                // fall through to the two documented fallbacks.
            }
        }

        if (options.GlobalOptions.TryGetValue(ProjectDirectoryOption, out var projectDirectory)
            && !string.IsNullOrWhiteSpace(projectDirectory)
            && file.Path.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(file.Path[projectDirectory.Length..]);
        }

        return file.Path;
    }

    private static void EmitDeclarations(SourceProductionContext spc, DeclarationInput input)
    {
        var parse = MarkupParser.Parse(input.Text);
        foreach (var diagnostic in parse.Diagnostics)
            spc.ReportDiagnostic(MarkupDiagnostics.SyntaxError(input.TargetPath, input.Text, diagnostic));

        // A document with syntax errors gets no half-generated class. The
        // HZ0001 diagnostic breaks the build; there is no stale BuildRoot to
        // keep and nothing for the incremental pipeline to cache.
        if (parse.Diagnostics.Count != 0)
            return;

        spc.AddSource(
            input.Identifier + ".Declarations.g.cs",
            SourceText.From(
                BuildRootEmitter.EmitDeclaration(
                    input.TargetPath,
                    input.Identifier,
                    parse,
                    MarkupChecksum.Compute(input.Text)),
                Encoding.UTF8));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string MakeIdentifier(string path)
    {
        var sb = new StringBuilder("HarborUi_");
        foreach (var c in path)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
