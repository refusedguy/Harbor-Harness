using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Harbor.TestKit;

namespace Harbor.TestKit.Generators.Tests;

/// <summary>
///     The guard for issue #1769: the generator must EMIT a working double, and
///     it must REFUSE a shape it cannot express.
/// </summary>
/// <remarks>
///     <para>
///         The positive control runs the generator over a synthetic compilation
///         and asserts two things: the double was emitted, and the compilation
///         that results from adding it has NO errors. The second half is the
///         load-bearing one — a double that does not actually implement the
///         interface would show up there as a compile error, not as a green
///         test that only looked at the text.
///     </para>
///     <para>
///         The negative control asks for an interface with a static abstract
///         member — a shape a class cannot implement. The generator must report
///         a diagnostic and emit NOTHING, rather than emit a half-double that
///         compiles into something silently wrong.
///     </para>
/// </remarks>
public class FakeGeneratorDriverTests
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

    private const string SupportedSource = """
        using System.Threading;
        using System.Threading.Tasks;

        [assembly: Harbor.TestKit.GenerateFakes(typeof(Sample.IGreeter))]

        namespace Sample;

        public interface IGreeter
        {
            string Greet(string name);

            int GreetCount { get; }

            Task<int> CountAsync(CancellationToken ct = default);
        }
        """;

    private const string UnsupportedSource = """
        [assembly: Harbor.TestKit.GenerateFakes(typeof(Sample.IStaticGreeter))]

        namespace Sample;

        public interface IStaticGreeter
        {
            static abstract string Kind { get; }
        }
        """;

    [Test]
    public async Task Generator_EmitsADouble_ThatImplementsTheInterface()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(SupportedSource);

        GeneratorRunResult generator = run.Results[0];
        string generated = generator.GeneratedSources.Length == 0
            ? string.Empty
            : generator.GeneratedSources[0].SourceText.ToString();

        await Assert.That(generated).Contains("class FakeGreeter");
        await Assert.That(generated).Contains("GreetValue");
        await Assert.That(generated).Contains("CountAsyncValue");

        // The load-bearing assertion: the compilation that now CONTAINS the
        // generated double must be free of errors. That is what proves the
        // double really implements the interface.
        string errors = string.Join(
            " | ",
            output.GetDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(static d => d.ToString()));

        await Assert.That(errors).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Generator_ReportsADiagnostic_AndEmitsNothing_ForAnUnsupportedShape()
    {
        (Compilation output, GeneratorDriverRunResult run) = Run(UnsupportedSource);

        GeneratorRunResult generator = run.Results[0];

        await Assert.That(generator.GeneratedSources.Length).IsEqualTo(0);

        string reported = string.Join(
            " | ",
            generator.Diagnostics.Select(static d => d.Id + ": " + d.GetMessage()));

        await Assert.That(reported).Contains("HARBORFAKE002");

        // The unsupported interface is reported, and the compilation is left
        // alone rather than half-filled.
        string errors = string.Join(
            " | ",
            output.GetDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(static d => d.ToString()));

        await Assert.That(errors).IsEqualTo(string.Empty);
    }

    private static (Compilation Output, GeneratorDriverRunResult Run) Run(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Harbor.TestKit.Generators.Tests.Sample",
            [tree],
            BuildReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new IIncrementalGenerator[] { new FakeGenerator() });
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

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Add(assembly);
        }

        // The two the compilation cannot do without: the core library and the
        // assembly that carries the opt-in marker.
        Add(typeof(object).Assembly);
        Add(typeof(GenerateFakesAttribute).Assembly);

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
}
