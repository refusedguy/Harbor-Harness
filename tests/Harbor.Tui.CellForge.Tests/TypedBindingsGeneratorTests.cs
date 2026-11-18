using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harbor.Tui.CellForge.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2429: a <c>state:</c> annotation makes every <c>{Binding}</c>
///     resolve at build time to a typed <see cref="Func{T,TResult}"/> or
///     <see cref="Action{T1,T2}"/>. Bad paths break the build at the markup
///     position; the generated code has no reflection.
/// </summary>
public class TypedBindingsGeneratorTests
{
    private const string TargetPath = "Fixtures/State.harbor-ui";

    private const string StateSource = """
        #nullable enable
        namespace Sample;
        public sealed class CounterState
        {
            public int Count { get; set; }
            public string Title { get; set; } = "";
            public UserState User { get; set; } = new();
            public string ReadOnly => "fixed";
        }
        public sealed class UserState
        {
            public string Name { get; set; } = "";
        }
        """;

    private const string RenamedStateSource = """
        #nullable enable
        namespace Sample;
        public sealed class CounterState
        {
            public int Total { get; set; }
        }
        """;

    [Test]
    public async Task ResolvedBindings_EmitTypedDelegates_AndCompileWithoutReflection()
    {
        const string markup = """
            state: Sample.CounterState
            panel "main":
                text "{Binding Count}"
                text "{Binding User.Name}"
                text_input "{Binding Title, Mode=TwoWay}"
            """;

        (Compilation output, GeneratorRunResult generator) = Run(markup, StateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(0);
        string source = BindingsSource(generator);
        await Assert.That(source).Contains("internal const string StateType = \"Sample.CounterState\"");
        await Assert.That(source).Contains("Func<global::Sample.CounterState, global::System.Int32>");
        await Assert.That(source).Contains("s.Count");
        await Assert.That(source).Contains("s.User.Name");
        await Assert.That(source).Contains("Action<global::Sample.CounterState, global::System.String>");
        await Assert.That(source).Contains("s.Title = v");
        await Assert.That(source).DoesNotContain("GetType");
        await Assert.That(source).DoesNotContain("GetProperty");
        await Assert.That(source).DoesNotContain("System.Reflection");
        await Assert.That(source).DoesNotContain("dynamic");
        await Assert.That(Errors(output)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BindingWithoutStateAnnotation_BreaksTheBuild()
    {
        const string markup = "text \"{Binding Count}\"\n";

        (_, GeneratorRunResult generator) = Run(markup, StateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(1);
        Diagnostic diagnostic = generator.Diagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo("HUI002");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.GetMessage()).Contains("state: Type");
        await Assert.That(HasBindingsSource(generator)).IsFalse();
    }

    [Test]
    public async Task UnknownProperty_BreaksBuild_AtTheBindingPosition()
    {
        const string markup = "state: Sample.CounterState\npanel \"main\":\n    text \"{Binding Nope}\"\n";

        (_, GeneratorRunResult generator) = Run(markup, StateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(1);
        Diagnostic diagnostic = generator.Diagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo("HUI004");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.GetMessage()).Contains("Nope");
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        await Assert.That(span.StartLinePosition.Line).IsEqualTo(2);
        await Assert.That(span.StartLinePosition.Character).IsGreaterThan(0);
        await Assert.That(span.Path).Contains("State.harbor-ui");
        await Assert.That(HasBindingsSource(generator)).IsFalse();
    }

    [Test]
    public async Task RenamedProperty_BreaksBuild_WithTheOldNameInTheMessage()
    {
        const string markup = "state: Sample.CounterState\ntext \"{Binding Count}\"\n";

        (_, GeneratorRunResult generator) = Run(markup, RenamedStateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(1);
        Diagnostic diagnostic = generator.Diagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo("HUI004");
        await Assert.That(diagnostic.GetMessage()).Contains("Count");
        await Assert.That(diagnostic.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(1);
    }

    [Test]
    public async Task TwoWayBindingOnReadOnlyProperty_BreaksTheBuild()
    {
        const string markup = "state: Sample.CounterState\ntext \"{Binding ReadOnly, Mode=TwoWay}\"\n";

        (_, GeneratorRunResult generator) = Run(markup, StateSource);
        Diagnostic diagnostic = await OnlyDiagnostic(generator);

        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(diagnostic.Id).IsEqualTo("HUI005");
        await Assert.That(diagnostic.GetMessage()).Contains("ReadOnly");
        await Assert.That(diagnostic.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(1);
        await Assert.That(HasBindingsSource(generator)).IsFalse();
    }

    [Test]
    public async Task MissingStateType_BreaksTheBuild()
    {
        const string markup = "state: Sample.DoesNotExist\ntext \"{Binding Count}\"\n";

        (_, GeneratorRunResult generator) = Run(markup, StateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(generator.Diagnostics[0].Id).IsEqualTo("HUI003");
    }

    [Test]
    public async Task MarkupWithoutBindings_EmitsNoBindingsSource()
    {
        const string markup = "state: Sample.CounterState\npanel \"main\":\n    text \"plain\"\n";

        (_, GeneratorRunResult generator) = Run(markup, StateSource);

        await Assert.That(generator.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(HasBindingsSource(generator)).IsFalse();
    }

    private static async Task<Diagnostic> OnlyDiagnostic(GeneratorRunResult generator)
    {
        await Assert.That(generator.Diagnostics.Length).IsEqualTo(1);
        return generator.Diagnostics[0];
    }

    private static string BindingsSource(GeneratorRunResult generator) =>
        generator.GeneratedSources.Single(source => source.HintName.EndsWith(".Bindings.g.cs"))
            .SourceText.ToString();

    private static bool HasBindingsSource(GeneratorRunResult generator) =>
        generator.GeneratedSources.Any(source => source.HintName.EndsWith(".Bindings.g.cs"));

    private static string Errors(Compilation compilation) => string.Join(
        " | ",
        compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.Id + ": " + d.GetMessage()));

    private static (Compilation Output, GeneratorRunResult Generator) Run(string markup, string stateSource)
    {
        SyntaxTree state = CSharpSyntaxTree.ParseText(stateSource, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TypedBindings.Sample",
            [state],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var file = new MarkupFile("/checkout/" + TargetPath, markup);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new MarkupGenerator().AsSourceGenerator()],
            additionalTexts: [file]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return (output, driver.GetRunResult().Results[0]);
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        var references = new List<MetadataReference>();
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }

    /// <summary>An AdditionalText with an in-memory body and a synthetic path.</summary>
    private sealed class MarkupFile(string path, string body) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(body);
    }
}
