// BannedApiProbe.cs — the harness that runs the REAL RS0030 analyzer in-process.
//
// WHY THIS FILE EXISTS
// --------------------
// The dynamic-code ban (#626) is enforced by the compiler since #1844: the rule
// is the ban file `BannedSymbols.DynamicCode.txt`, and the compiler's RS0030
// diagnostic is what fails the build. A rule that lives in the build has no
// test of its own — the only evidence it works is a build that has not failed
// yet, which is indistinguishable from a rule that matches nothing.
//
// So this harness puts the compiler's own analyzer in front of a compilation
// the test builds, and asks it the two questions a guard has to answer:
//
//   * does RS0030 FIRE on a synthetic violation?   (the negative control)
//   * does RS0030 STAY SILENT where it must?        (the positive controls)
//
// The analyzer is the one the build uses: the same package, the same version,
// loaded from the same place. A home-grown re-implementation of "is this symbol
// banned" would test the re-implementation, not the rule.
//
// THE REFERENCES ARE THE RUNTIME'S OWN
// ------------------------------------
// `TRUSTED_PLATFORM_ASSEMBLIES` is how an analyzer test host finds the
// framework it is running on. The ban list was checked against the .NET 10
// TARGETING pack — the surface the compiler sees — and every entry in it is
// public API that exists in both.
//
// THE ANALYZER IS FOUND, NOT ASSUMED
// ----------------------------------
// The path comes from the `PkgMicrosoft_CodeAnalysis_BannedApiAnalyzers`
// property that NuGet writes for the analyzer package (passed into the test
// assembly as metadata by the csproj). If that is missing — a host that did
// not restore, a trimmed test runner — the NuGet cache is searched instead, so
// the probe fails LOUDLY with "no analyzer" rather than silently passing.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

// `Assembly` is ambiguous in this project: `TUnit.Core.HookType` is an enum
// with an `Assembly` member, and `global using TUnit.Core;` puts that member in
// scope for every expression. `GlobalUsings.cs` solves it the same way.
using Assembly = System.Reflection.Assembly;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Runs the build's own banned-API analyzer against a compilation the test
///     builds, so the ban file can be proved live instead of assumed.
/// </summary>
internal static class BannedApiProbe
{
    /// <summary>The ban file this probe enforces — the dynamic-code rule of #626.</summary>
    internal const string BanFileName = "BannedSymbols.DynamicCode.txt";

    /// <summary>The diagnostic the banned-API analyzer reports.</summary>
    internal const string DiagnosticId = "RS0030";

    /// <summary>
    ///     The assembly metadata key carrying the analyzer package's install
    ///     path. Set by the csproj from NuGet's generated path property.
    /// </summary>
    private const string PackagePathMetadataKey = "BannedApiAnalyzersPackagePath";

    private static readonly Lazy<DiagnosticAnalyzer> LazyAnalyzer = new(LoadAnalyzer);

    private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences = new(BuildReferences);

    /// <summary>The analyzer the build itself uses.</summary>
    internal static DiagnosticAnalyzer Analyzer => LazyAnalyzer.Value;

    /// <summary>Absolute path of the ban file in this checkout.</summary>
    internal static string BanFilePath =>
        Path.Combine(RepoPaths.RepoRoot ?? ".", BanFileName);

    /// <summary>
    ///     The declaration IDs the ban file lists, in file order, parsed the way
    ///     the analyzer parses it: a <c>//</c> starts a comment, and the first
    ///     <c>;</c> separates the ID from the reason.
    /// </summary>
    internal static IReadOnlyList<string> BanFileDeclarationIds()
    {
        string path = BanFilePath;
        if (!File.Exists(path))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (string raw in File.ReadAllLines(path))
        {
            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            string text = (comment < 0 ? raw : raw[..comment]).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            int separator = text.IndexOf(';');
            ids.Add((separator < 0 ? text : text[..separator]).Trim());
        }

        return ids;
    }

    /// <summary>
    ///     Runs the analyzer over <paramref name="source" /> and returns what it
    ///     reported. With <paramref name="withBanFile" /> false the analyzer sees
    ///     NO ban file at all, which is what a plugin project sees.
    /// </summary>
    internal static async Task<ImmutableArray<Diagnostic>> RunAsync(
        string source,
        bool withBanFile,
        CancellationToken cancellationToken = default)
    {
        var compilation = MakeCompilation(CSharpSyntaxTree.ParseText(source, path: "Probe.cs"));

        ImmutableArray<AdditionalText> additionalFiles = withBanFile
            ? [new InMemoryAdditionalText(BanFilePath, ReadBanFile())]
            : ImmutableArray<AdditionalText>.Empty;

        var options = new AnalyzerOptions(additionalFiles);
        var withAnalyzers = new CompilationWithAnalyzers(compilation, [Analyzer], options);

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
    }

    /// <summary>The RS0030 diagnostics of a run, and nothing else.</summary>
    internal static async Task<IReadOnlyList<string>> ReportAsync(
        string source,
        bool withBanFile,
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(source, withBanFile, cancellationToken);

        return
        [
            .. diagnostics
                .Where(static d => string.Equals(d.Id, DiagnosticId, StringComparison.Ordinal))
                .Select(static d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(static m => m, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    ///     Resolves a documentation-comment ID against the framework, the way
    ///     the analyzer does. An ID that resolves to nothing is a dead ban entry:
    ///     the analyzer skips it without a word.
    /// </summary>
    internal static ImmutableArray<ISymbol> Resolve(string declarationId) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(declarationId, EmptyCompilation());

    /// <summary>
    ///     The documentation-comment IDs of the public static members of a type
    ///     with the given names — the framework's own surface, as the ban list
    ///     has to name it.
    /// </summary>
    /// <remarks>
    ///     Read with plain reflection rather than through a Roslyn semantic
    ///     model: the probe's references hold the framework's implementation
    ///     assemblies AND their type-forwarding facades, and a semantic lookup
    ///     across both does not resolve to one symbol. The runtime's own
    ///     reflection has no such problem, and the IDs it produces are the ones
    ///     the analyzer resolves — a method's ID is its declaring type, its name
    ///     and its parameter types, spelled the way a documentation comment
    ///     spells them.
    /// </remarks>
    internal static IReadOnlyList<string> PublicMemberIds(Type type, params string[] memberNames)
    {
        var ids = new List<string>();

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!memberNames.Contains(method.Name, StringComparer.Ordinal))
            {
                continue;
            }

            string parameters = string.Join(
                ",",
                method.GetParameters().Select(static p => p.ParameterType.FullName ?? p.ParameterType.Name));

            ids.Add(
                "M:" + (type.FullName ?? type.Name) + "." + method.Name
                + "(" + parameters + ")");
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>A compilation over the framework's own assemblies and nothing else.</summary>
    private static CSharpCompilation EmptyCompilation() => MakeCompilation();

    /// <summary>The ban file's text, or a loud failure.</summary>
    private static string ReadBanFile()
    {
        string path = BanFilePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The ban file '{path}' is missing. The dynamic-code rule IS that file: without it the "
                + "compiler has nothing to enforce, and a rule that is enforced by nothing reads exactly "
                + "like a rule that is enforced.",
                path);
        }

        return File.ReadAllText(path);
    }

    /// <summary>A compilation over the given trees and the framework's assemblies.</summary>
    private static CSharpCompilation MakeCompilation(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            "Harbor.BannedApiProbe",
            trees,
            LazyReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

    /// <summary>
    ///     The framework's own metadata references. The runtime's assemblies are
    ///     the ones the test host is running on, which is what an analyzer
    ///     resolves symbols against.
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (string path in tpa.Split(Path.PathSeparator))
            {
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException)
                {
                    // A file the runtime lists but the compiler cannot read as a
                    // reference (a native shim, a resource-only assembly). The
                    // probe does not need it.
                }
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    ///     Loads the analyzer assembly the build uses and returns the C# instance
    ///     of the banned-API analyzer.
    /// </summary>
    /// <remarks>
    ///     The assembly holds more than one analyzer (the banned-API rule and the
    ///     restricted-internals one), so the instance is chosen by the diagnostic
    ///     it reports rather than by being the first type in the file.
    /// </remarks>
    private static DiagnosticAnalyzer LoadAnalyzer()
    {
        string assemblyPath = AnalyzerAssemblyPath();

        // S3885: `Assembly.Load(byte[])` cannot be used here — the analyzer
        // assembly has a sibling (the core banned-API analyzer) that only
        // resolves from the same folder, and a byte-array load has no folder
        // to probe. Path-based loading is the point.
#pragma warning disable S3885
        var assembly = Assembly.LoadFrom(assemblyPath);
#pragma warning restore S3885

        foreach (Type candidate in AnalyzerTypes(assembly))
        {
            DiagnosticAnalyzer? analyzer;
            try
            {
                analyzer = (DiagnosticAnalyzer?)Activator.CreateInstance(candidate, nonPublic: true);
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
            {
                continue;
            }

            if (analyzer is not null
                && analyzer.SupportedDiagnostics.Any(static d =>
                    string.Equals(d.Id, DiagnosticId, StringComparison.Ordinal)))
            {
                return analyzer;
            }
        }

        throw new InvalidOperationException(
            $"'{assemblyPath}' contains no analyzer that reports {DiagnosticId}. The banned-API rule "
            + "cannot be proved live without it.");
    }

    /// <summary>The concrete analyzer types in an assembly.</summary>
    private static IEnumerable<Type> AnalyzerTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Where(static t =>
                typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
                && !t.IsAbstract
                && !t.IsGenericTypeDefinition);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(static t =>
                    t is not null
                    && typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
                    && !t.IsAbstract
                    && !t.IsGenericTypeDefinition)
                .Select(static t => t!);
        }
    }

    /// <summary>
    ///     The path of the analyzer assembly: the NuGet install path the csproj
    ///     passes in, or the NuGet cache, or nothing.
    /// </summary>
    private static string AnalyzerAssemblyPath()
    {
        foreach (AssemblyMetadataAttribute attribute in
                 typeof(BannedApiProbe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            string? packagePath = attribute.Value;
            if (!string.Equals(attribute.Key, PackagePathMetadataKey, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(packagePath))
            {
                continue;
            }

            string candidate = Path.Combine(
                packagePath,
                "analyzers",
                "dotnet",
                "cs",
                "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? cached = FindInNuGetCache();
        if (cached is not null)
        {
            return cached;
        }

        throw new InvalidOperationException(
            "The banned-API analyzer assembly was not found. The csproj passes the package path in as "
            + $"assembly metadata ('{PackagePathMetadataKey}') and the NuGet cache is searched as a "
            + "fallback; neither produced "
            + "'Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll'.");
    }

    /// <summary>
    ///     The analyzer assembly in the NuGet cache, newest version first. A
    ///     fallback for hosts where the package-path metadata is absent.
    /// </summary>
    private static string? FindInNuGetCache()
    {
        string root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");

        string packageRoot = Path.Combine(root, "microsoft.codeanalysis.bannedapianalyzers");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        return Directory.GetDirectories(packageRoot)
            .OrderByDescending(static d =>
                Version.TryParse(Path.GetFileName(d), out Version? parsed) ? parsed : new Version(0, 0))
            .Select(static d => Path.Combine(
                d,
                "analyzers",
                "dotnet",
                "cs",
                "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll"))
            .FirstOrDefault(static p => File.Exists(p));
    }

    /// <summary>An <see cref="AdditionalText" /> over a file the test holds in memory.</summary>
    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
    {
        /// <inheritdoc />
        public override string Path { get; } = path;

        /// <inheritdoc />
        public override SourceText GetText(CancellationToken cancellationToken = default) =>
            SourceText.From(text);
    }
}
