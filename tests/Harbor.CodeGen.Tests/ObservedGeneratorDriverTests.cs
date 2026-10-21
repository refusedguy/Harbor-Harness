using System.Reflection;
using System.Runtime.InteropServices;
using Harbor.Abstractions.Contracts;
using Harbor.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.CodeGen.Tests;

/// <summary>
///     The driver half of the guard for issue #1797: the generator is run
///     IN-PROCESS over a synthetic compilation, and it is asked the three
///     questions that decide whether the mechanism is honest.
/// </summary>
/// <remarks>
///     <para>
///         1. Does it emit a decorator for a marked seam — and does the
///         decorator COMPILE? The second half of that assertion is the
///         load-bearing one: a decorator that does not implement the seam it
///         claims to would show up as a compile error, not as a green test
///         that only looked at the text.
///     </para>
///     <para>
///         2. Does it stay SILENT for a seam that carries no marker? A
///         generator that decorates everything is not a generator, it is a
///         wrapper.
///     </para>
///     <para>
///         3. Does a contradictory marker FAIL THE BUILD? The diagnostic is
///         an error, and the assertion is made against the COMPILATION the
///         driver produced — the same channel a real build fails on. That is
///         the planted violator: a marker that says "emit nothing" and names
///         a span at the same time has to break the build, not resolve
///         silently.
///     </para>
/// </remarks>
public class ObservedGeneratorDriverTests
{
    /// <summary>
    ///     The BCL contract assemblies a Roslyn compilation needs to resolve
    ///     type-forwarded BCL types, even when System.Private.CoreLib is
    ///     referenced. Same list the in-tree plugin compiler uses
    ///     (<c>src/Harbor.Plugins.Compilation/PluginAssemblyReferences.cs</c>).
    /// </summary>
    private static readonly string[] WellKnownRuntimeAssemblies =
    [
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Collections.Concurrent.dll",
        "System.Text.RegularExpressions.dll",
        "System.Threading.Tasks.dll",
        "System.Threading.dll",
        "System.Linq.dll",
        "System.Console.dll",
        "System.Text.Json.dll",
    ];

    private const string ObservedSource = """
        #nullable enable
        using System.Threading;
        using System.Threading.Tasks;
        using Harbor.Abstractions.Contracts;

        namespace Sample;

        [Observed]
        public interface IGreeter
        {
            Task<string> GreetAsync(string name, CancellationToken ct = default);
        }
        """;

    private const string PlainSource = """
        #nullable enable
        using System.Threading;
        using System.Threading.Tasks;

        namespace Sample;

        public interface IGreeter
        {
            Task<string> GreetAsync(string name, CancellationToken ct = default);
        }
        """;

    /// <summary>
    ///     The planted violator: the statement asks for nothing AND for a
    ///     named span. The generator must refuse it, and the refusal must
    ///     reach the compilation as an ERROR.
    /// </summary>
    private const string ContradictorySource = """
        #nullable enable
        using System.Threading;
        using System.Threading.Tasks;
        using Harbor.Abstractions.Contracts;

        namespace Sample;

        [Observed("sample.greet", Scope = ObservedScope.None)]
        public interface IGreeter
        {
            Task<string> GreetAsync(string name, CancellationToken ct = default);
        }
        """;

    [Test]
    public async Task TheGenerator_EmitsADecorator_ThatCompiles()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(ObservedSource);

        GeneratorRunResult generator = run.Results[0];
        await Assert.That(HintNames(generator)).IsEqualTo("Observed.Sample_IGreeter.g.cs");

        string generated = generator.GeneratedSources[0].SourceText.ToString();
        await Assert.That(generated).Contains("class ObservedGreeter");
        await Assert.That(generated).Contains("greeter.greet");

        // The load-bearing assertion: the compilation that now CONTAINS the
        // generated decorator must be free of errors. That is what proves the
        // decorator really implements the seam it claims to.
        string errors = Errors(output);

        await Assert.That(errors).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TheGenerator_EmitsNothing_ForAnUnmarkedSeam()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(PlainSource);

        GeneratorRunResult generator = run.Results[0];

        await Assert.That(HintNames(generator)).IsEqualTo(string.Empty);
        await Assert.That(Errors(output)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TheGenerator_RefusesAContradictoryStatement_AndTheBuildBreaks()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(ContradictorySource);

        GeneratorRunResult generator = run.Results[0];

        // Nothing is emitted for a seam whose statement contradicts itself.
        await Assert.That(HintNames(generator)).IsEqualTo(string.Empty);

        // And the refusal is an ERROR. The generator's diagnostics travel
        // BESIDE the compilation the driver produced, not inside it — the
        // compiler collects them separately — so the severity is the thing
        // that breaks a build, and it is what this asserts.
        string reported = string.Join(
            " | ",
            generator.Diagnostics.Select(static d => d.Id + ": " + d.Severity + ": " + d.GetMessage()));

        await Assert.That(reported).Contains("HARBOROBS001");
        await Assert.That(generator.Diagnostics.Any(static d => d.Id == "HARBOROBS001" && d.Severity == DiagnosticSeverity.Error)).IsTrue();

        // The compilation itself is untouched: the generator refused, it did
        // not emit half a decorator.
        await Assert.That(Errors(output)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TheSwitch_TurnsTheWholeMechanismOff()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(ObservedSource, switchValue: "false");

        GeneratorRunResult generator = run.Results[0];

        // "Off" means NOTHING is emitted: no decorator, no instrument, no
        // span call. Not a decorator that measures nothing.
        await Assert.That(HintNames(generator)).IsEqualTo(string.Empty);
        await Assert.That(Errors(output)).IsEqualTo(string.Empty);
    }

    /// <summary>
    ///     The names of the files the generator emitted. The names are part of
    ///     the contract: two runs of the same compilation must produce the same
    ///     file names.
    /// </summary>
    private static string HintNames(GeneratorRunResult generator) => string.Join(
        ", ",
        generator.GeneratedSources.Select(static source => source.HintName));

    private static string Errors(Compilation compilation) => string.Join(
        " | ",
        compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(static d => d.Id + ": " + d.GetMessage()));

    private static (Compilation Output, GeneratorDriverRunResult Run) Run(string source, string? switchValue = null)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Harbor.CodeGen.Tests.Sample",
            [tree],
            BuildReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = switchValue is null
            ? CSharpGeneratorDriver.Create(new IIncrementalGenerator[] { new ObservedGenerator() })
            : CSharpGeneratorDriver.Create(
                new ISourceGenerator[] { new ObservedGenerator().AsSourceGenerator() },
                additionalTexts: null,
                parseOptions: null,
                optionsProvider: new SwitchOptionsProvider(
                    new SingleOption(ObservedGenerator.SwitchPropertyName, switchValue)));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);

        return (output, driver.GetRunResult());
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();

        void Add(Assembly? assembly)
        {
            if (assembly is null || assembly.IsDynamic)
            {
                return;
            }

            string? name = assembly.GetName().Name;
            if (name is null || !seenNames.Add(name))
            {
                return;
            }

            string location = assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(location));
            }
            catch (IOException)
            {
                // best-effort: a reference that cannot be read is simply absent.
            }
        }

        // The three the compilation cannot do without: the core library, the
        // assembly that carries the marker, and the assembly that carries the
        // contracts the generated decorator is written against.
        //
        // Nothing else: the generator decorates the marked seams of the
        // assemblies a compilation references, so a compilation that saw every
        // loaded assembly would also see the FIXTURE's marked seams — and the
        // decorator for them would be emitted into this synthetic compilation
        // too. The control has to be hermetic to mean anything.
        Add(typeof(object).Assembly);
        Add(typeof(ObservedAttribute).Assembly);
        Add(typeof(ITracer).Assembly);

        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDirectory) && Directory.Exists(runtimeDirectory))
        {
            foreach (string wellKnown in WellKnownRuntimeAssemblies)
            {
                string path = Path.Combine(runtimeDirectory, wellKnown);
                if (!File.Exists(path))
                {
                    continue;
                }

                string? name = Path.GetFileNameWithoutExtension(path);
                if (name is null || !seenNames.Add(name))
                {
                    continue;
                }

                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException)
                {
                    // best-effort.
                }
            }
        }

        return references;
    }

    /// <summary>
    ///     The build property the generator reads, as the compiler would hand
    ///     it over.
    /// </summary>
    private sealed class SingleOption(string key, string value) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string optionKey, out string? optionValue)
        {
            if (string.Equals(optionKey, key, StringComparison.Ordinal))
            {
                optionValue = value;
                return true;
            }

            optionValue = null;
            return false;
        }
    }

    private sealed class SwitchOptionsProvider(AnalyzerConfigOptions options) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => options;
    }
}
