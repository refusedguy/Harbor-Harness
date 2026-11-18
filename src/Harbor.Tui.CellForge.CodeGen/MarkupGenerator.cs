using System;
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
/// </remarks>
[Generator]
public sealed class MarkupGenerator : IIncrementalGenerator
{
    private const string TargetPathOption = "build_metadata.AdditionalFiles.TargetPath";
    private const string ProjectDirectoryOption = "build_property.MSBuildProjectDirectory";
    private const string HarborUiExtension = ".harbor-ui";

    private static readonly DiagnosticDescriptor MarkupSyntaxRule = new(
        id: "HUI001",
        title: "Invalid CellForge markup",
        messageFormat: "{0}:{1}: {2}",
        category: "CellForge.Markup",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The .harbor-ui parser rejected this line.");

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
        var implementations = declarations
            .Select(static (input, _) => EmitBuildRootModel(input))
            .WithTrackingName("MarkupGenerator.Phase2BuildRoot");

        context.RegisterSourceOutput(implementations, static (spc, output) =>
        {
            if (!output.IsValid)
                return;

            spc.AddSource(
                output.Identifier + ".BuildRoot.g.cs",
                SourceText.From(output.Source, Encoding.UTF8));
        });
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
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                MarkupSyntaxRule,
                Location.None,
                diagnostic.Line,
                diagnostic.Column,
                diagnostic.Message));
        }

        // A document with syntax errors gets no half-generated class. The
        // diagnostic breaks the build, and there is no stale BuildRoot to keep.
        if (parse.Diagnostics.Count != 0)
            return;

        spc.AddSource(
            input.Identifier + ".Declarations.g.cs",
            SourceText.From(
                BuildRootEmitter.EmitDeclaration(input.TargetPath, input.Identifier, parse),
                Encoding.UTF8));
    }

    private static BuildRootOutput EmitBuildRootModel(DeclarationInput input)
    {
        var parse = MarkupParser.Parse(input.Text);
        return new BuildRootOutput(
            input.Identifier,
            BuildRootEmitter.EmitBuildRoot(input.Identifier, parse.Arena),
            parse.Diagnostics.Count == 0);
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
