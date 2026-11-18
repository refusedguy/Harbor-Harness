using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harbor.Tui.CellForge.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2688: a <c>.bind</c> operation in markup becomes Binding data
///     and a Register method in generated code. The gesture grammar stays in
///     the runtime's KeySpec; registration and clash resolution stay in the
///     existing BindingsMap, so this slice adds no second key parser.
/// </summary>
public sealed class KeyBindingsGeneratorTests
{
    private const string TargetPath = "Fixtures/Keys.harbor-ui";

    [Test]
    public async Task BindOps_EmitBindingData_AndCompileWithoutReflection()
    {
        const string markup = """
            panel "main":
                .bind "ctrl+k" "panel.focus.next" "Next panel"
                .bind "ctrl+p" "panel.focus.prev" "Previous panel"
            """;

        (Compilation output, GeneratorRunResult generator) = Run(markup);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(0);
        string source = KeyBindingsSource(generator);
        await Assert.That(source).Contains("KeyBindings");
        await Assert.That(source).Contains("internal static global::System.Collections.Generic.IReadOnlyList");
        await Assert.That(source).Contains("\"ctrl+k\"");
        await Assert.That(source).Contains("\"panel.focus.next\"");
        await Assert.That(source).Contains("\"Next panel\"");
        await Assert.That(source).Contains("KeySpec.TryParse");
        await Assert.That(source).Contains("BindingsMap map");
        await Assert.That(source).DoesNotContain("System.Reflection");
        await Assert.That(source).DoesNotContain("dynamic");
        await Assert.That(Errors(output)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task MarkupWithoutBindOps_EmitsNoKeyBindingsSource()
    {
        (_, GeneratorRunResult generator) = Run("text \"plain\"\n");

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(HasKeyBindingsSource(generator)).IsFalse();
    }

    [Test]
    public async Task GeneratedBindingData_AppearsInShownKeys_AndResolvesGestureClash()
    {
        // BindOps_EmitBindingData_AndCompileWithoutReflection proves the
        // generator emits these three triples. Feeding the same triples to
        // BindingsMap is what the generated Register method does at runtime.
        const string markup = """
            panel "main":
                .bind "ctrl+k" "panel.focus.next" "Next panel"
                .bind "ctrl+p" "panel.focus.prev" "Previous panel"
            """;
        (_, GeneratorRunResult generator) = Run(markup);
        string source = KeyBindingsSource(generator);
        await Assert.That(source).Contains("\"ctrl+k\", \"panel.focus.next\", \"Next panel\"");

        var map = new BindingsMap();
        _ = KeySpec.TryParse("ctrl+k", out KeyGesture next);
        _ = KeySpec.TryParse("ctrl+p", out KeyGesture previous);
        map.Bind(new Binding("panel.focus.next", next, "panel.focus.next", "Next panel"));
        map.Bind(new Binding("panel.focus.prev", previous, "panel.focus.prev", "Previous panel"));

        List<Binding> shown = map.ShownKeys();
        await Assert.That(shown.Any(binding => binding.Action == "panel.focus.next")).IsTrue();
        await Assert.That(shown.Any(binding => binding.Action == "panel.focus.prev")).IsTrue();
        await Assert.That(shown.Single(binding => binding.Action == "panel.focus.next").Description)
            .IsEqualTo("Next panel");

        var focused = new BindingsMap();
        focused.Bind(new Binding("focus.next", next, "focus.next", "Focused next"));
        BindingsMap merged = BindingsMap.Merge([focused, map]);
        List<Binding> mergedShown = merged.ShownKeys();
        await Assert.That(mergedShown.Count(binding => binding.Gesture.Equals(next))).IsEqualTo(1);
        await Assert.That(mergedShown.Single(binding => binding.Gesture.Equals(next)).Action)
            .IsEqualTo("focus.next");
    }

    private static (Compilation Output, GeneratorRunResult Generator) Run(string markup)
    {
        SyntaxTree empty = CSharpSyntaxTree.ParseText(string.Empty, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "KeyBindings.Sample",
            [empty],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var file = new MarkupFile("/checkout/" + TargetPath, markup);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new Harbor.Tui.CellForge.CodeGen.MarkupGenerator().AsSourceGenerator()],
            additionalTexts: [file]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return (output, driver.GetRunResult().Results[0]);
    }

    private static IReadOnlyList<MetadataReference> References()
    {
        var references = new List<MetadataReference>();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        foreach (string path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        // The emitter only runs when the consumer can see the runtime type.
        references.Add(MetadataReference.CreateFromFile(typeof(Binding).Assembly.Location));
        return references;
    }

    private static string KeyBindingsSource(GeneratorRunResult generator) =>
        generator.GeneratedSources.Single(source => source.HintName.EndsWith(".KeyBindings.g.cs"))
            .SourceText.ToString();

    private static bool HasKeyBindingsSource(GeneratorRunResult generator) =>
        generator.GeneratedSources.Any(source => source.HintName.EndsWith(".KeyBindings.g.cs"));

    private static string Errors(Compilation compilation) => string.Join(
        " | ",
        compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id + ": " + diagnostic.GetMessage()));

    private sealed class MarkupFile(string path, string body) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(body);
    }
}
