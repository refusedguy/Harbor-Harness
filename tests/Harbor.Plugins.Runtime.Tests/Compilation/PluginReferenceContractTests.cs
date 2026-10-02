using System.Runtime.InteropServices;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;

// TUnit's generated sources declare a HookType with an Assembly member, which makes the bare
// name ambiguous in this project. Alias once rather than fully-qualify every use.
using Assembly = System.Reflection.Assembly;

namespace Harbor.Plugins.Runtime.Tests.Compilation;

/// <summary>
///     #1004 — a CS plugin's ability to name a Harbor contract must not depend on what the
///     host process happened to have loaded.
/// </summary>
/// <remarks>
///     <para>
///         <c>PluginAssemblyReferences</c> used to take its entire Harbor contract surface
///         from an <see cref="AppDomain" /> snapshot plus a handful of
///         <c>typeof(...).Assembly</c> pins. Those pins are sound for the assemblies the
///         project references at COMPILE time — the JIT loaded them to resolve the token, so
///         a pin cannot miss — and unsound for a transitive runtime dependency, which is
///         what <c>Harbor.Ui.Framework.State</c> (owner of <c>Harbor.Ui.Framework.Panels</c>,
///         the one live TUI plugin seam after #564) is.
///     </para>
///     <para>
///         <b>Why none of these tests reads <c>PluginAssemblyReferences.References</c>.</b>
///         That property is the declared core UNION the ambient sweep, so in a shared test
///         process any other test that has already touched a panel type would supply the
///         missing reference and turn the assertion green. That is the #903 shape: a test
///         green because the ambient state was convenient, checking a different set than a
///         user gets. Every assertion below is therefore made against the DECLARED surface,
///         and the cold reference set is rebuilt here from that declaration alone — which is
///         what a process that has initialised nothing sees.
///     </para>
///     <para>
///         The input is <c>docs/PLUGIN_DEVELOPMENT.md</c> itself: its
///         <c>LspDiagnosticsPanel</c> example is the only panel plugin Harbor documents and it
///         is copy-paste material. If its imports stop resolving, a reader copying it gets a
///         CS0234.
///     </para>
/// </remarks>
public sealed class PluginReferenceContractTests
{
    /// <summary>The canonical plugin-author guide, relative to the repository root.</summary>
    private const string GuideRelativePath = "docs/PLUGIN_DEVELOPMENT.md";

    /// <summary>
    ///     A line only the documented panel example carries. The fence holding it is the
    ///     surface under test, so every test asserts the extraction found it first — a fence
    ///     that lost the marker would otherwise leave an empty source that compiles trivially.
    /// </summary>
    private const string PanelMarker = "using Harbor.Ui.Framework.Panels;";

    /// <summary>
    ///     Mirrors the global usings <c>RoslynPluginCompiler</c> injects. Written out here
    ///     rather than read from the product so a defect in that prelude cannot quietly remove
    ///     the very namespaces under test.
    /// </summary>
    private static readonly string[] ColdProcessGlobalUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    /// <summary>
    ///     The third-party contracts the collector pins with <c>typeof</c>, and a cold process
    ///     legitimately has every one of them: each is a compile-time reference of
    ///     <c>Harbor.Plugins.Compilation</c> itself, so the JIT must have loaded it. This is
    ///     exactly the class the missing panel reference was NOT in, and that difference is the
    ///     whole of #1004.
    /// </summary>
    private static readonly Assembly[] ColdProcessThirdPartyPins =
    [
        typeof(object).Assembly,
        typeof(List<>).Assembly,
        typeof(Task).Assembly,
        typeof(Enumerable).Assembly,
        typeof(JsonDocument).Assembly,
        typeof(ILogger).Assembly,
        typeof(Result).Assembly,
    ];

    /// <summary>
    ///     The BCL contract facades a cold process still gets. The product adds these from the
    ///     runtime directory by a fixed name list, and so does this test — a cold process is
    ///     "nothing loaded yet", not "no framework at all", and leaving them out turns the
    ///     compile into a CS0012 about <c>Task</c> and hides the claim under test.
    /// </summary>
    private static readonly string[] ColdProcessBclContracts =
    [
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Collections.Concurrent.dll",
        "System.Linq.dll",
        "System.Threading.Tasks.dll",
        "System.Threading.dll",
        "System.Text.Json.dll",
        "System.Console.dll",
        "System.Net.Http.dll",
    ];

    /// <summary>
    ///     The load-bearing assertion: the panel plugin <c>docs/PLUGIN_DEVELOPMENT.md</c>
    ///     publishes must compile in a process that loaded nothing but the compiler itself.
    /// </summary>
    /// <remarks>
    ///     Before the fix this fails with <c>CS0234: The type or namespace name 'Panels' does
    ///     not exist in the namespace 'Harbor.Ui.Framework'</c>, because
    ///     <c>Harbor.Ui.Framework.State</c> was absent from the declared surface and had to be
    ///     inherited from whatever the host happened to have loaded.
    /// </remarks>
    [Test]
    public async Task DocumentedPanelExample_CompilesAgainstTheDeclaredContractSurfaceAlone()
    {
        string source = await ReadDocumentedPanelExampleAsync().ConfigureAwait(false);

        string[] harborUsings = HarborUsings(source);
        await Assert.That(harborUsings.Length).IsGreaterThanOrEqualTo(3)
            .Because("the documented panel example declares its own Harbor imports; found: ["
                     + string.Join(", ", harborUsings) + "]");
        await Assert.That(source).Contains("ITuiPanelPlugin")
            .Because("the extracted fence must be the panel plugin, not an unrelated one");

        List<Diagnostic> errors = CompileCold(source);

        await Assert.That(errors).IsEmpty()
            .Because(
                "docs/PLUGIN_DEVELOPMENT.md publishes this panel plugin, and a reader who copies "
                + "it must not hit CS0234 because the host had not loaded "
                + "Harbor.Ui.Framework.State. Diagnostics: "
                + string.Join(" | ", errors.Select(d => d.Id + " " + d.GetMessage())));
    }

    /// <summary>
    ///     The same claim as the compile above, stated without a compiler so the failure names
    ///     the missing owner instead of a cascade of CS0246s.
    /// </summary>
    [Test]
    public async Task DocumentedPanelExampleImports_AreAllOwnedByADeclaredAssembly()
    {
        string source = await ReadDocumentedPanelExampleAsync().ConfigureAwait(false);
        HashSet<string> owned = DeclaredExportedNamespaces();

        // Anti-vacuity, checked before the loop so "nothing is missing" cannot come from an
        // empty expected set.
        await Assert.That(owned.Count).IsGreaterThan(20)
            .Because("the declared surface should export many namespaces; it exported "
                     + owned.Count);

        string[] missing = HarborUsings(source).Where(ns => !owned.Contains(ns)).ToArray();

        await Assert.That(missing).IsEmpty()
            .Because(
                "a namespace the guide tells a plugin author to import must be owned by an "
                + "assembly PluginAssemblyReferences declares by name; unowned: ["
                + string.Join(", ", missing) + "]. Declared: ["
                + string.Join(", ", PluginAssemblyReferences.PluginContractAssemblies) + "]");
    }

    /// <summary>
    ///     Non-vacuity for the resolution mechanism itself: every declared name must find a
    ///     deployed file, and a name that cannot exist must find none. Without the second half,
    ///     "all declared names resolve" is satisfied by a probe that resolves everything.
    /// </summary>
    [Test]
    public async Task DeclaredNames_ResolveToDeployedFiles_AndAnInventedNameDoesNot()
    {
        string dir = DeploymentDirectory();
        string[] declared = PluginAssemblyReferences.PluginContractAssemblies;

        // A guard that passes on an empty list proves nothing.
        await Assert.That(declared.Length).IsGreaterThanOrEqualTo(3)
            .Because("the declared plugin contract surface must be non-trivial; it had "
                     + declared.Length);

        string[] unresolved = declared
            .Where(name => !File.Exists(Path.Combine(dir, name + ".dll")))
            .ToArray();
        await Assert.That(unresolved).IsEmpty()
            .Because("every declared name must be a deployed assembly beside " + dir
                     + "; missing: [" + string.Join(", ", unresolved) + "]");

        // The control: no project in this repository produces this assembly. If the probe
        // accepted it anyway, the assertion above would be hollow.
        const string invented = "Harbor.Not.A.Real.Assembly";
        await Assert.That(File.Exists(Path.Combine(dir, invented + ".dll"))).IsFalse()
            .Because(invented + ".dll must not be deployed, or the resolution check cannot "
                     + "discriminate");
    }

    /// <summary>
    ///     Build the reference set a process that has initialised nothing would get: the
    ///     declared Harbor surface by name, plus the compile-time third-party contracts and the
    ///     BCL. Deliberately not <c>PluginAssemblyReferences.References</c> — see the class
    ///     remarks.
    /// </summary>
    private static List<Diagnostic> CompileCold(string source)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in PluginAssemblyReferences.PluginContractAssemblies)
        {
            string path = Path.Combine(DeploymentDirectory(), name + ".dll");
            if (File.Exists(path) && seen.Add(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        foreach (Assembly asm in ColdProcessThirdPartyPins)
        {
#pragma warning disable IL3000 // Test-only mirror of the product's JIT-only pin.
            string location = asm.Location;
#pragma warning restore IL3000
            if (!string.IsNullOrEmpty(location) && seen.Add(location))
            {
                refs.Add(MetadataReference.CreateFromFile(location));
            }
        }

        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        foreach (string name in ColdProcessBclContracts)
        {
            string path = Path.Combine(runtimeDir, name);
            if (File.Exists(path) && seen.Add(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        string prelude = string.Join(
            Environment.NewLine,
            ColdProcessGlobalUsings.Select(ns => "global using " + ns + ";"));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Harbor.Plugin.ColdProcessProbe",
            new[]
            {
                CSharpSyntaxTree.ParseText(prelude),
                CSharpSyntaxTree.ParseText(source),
            },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        EmitResult emit = compilation.Emit(ms);
        return emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error)
            .ToList();
    }

    /// <summary>Namespaces exported by the declared surface, parent namespaces included.</summary>
    private static HashSet<string> DeclaredExportedNamespaces()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string name in PluginAssemblyReferences.PluginContractAssemblies)
        {
            string path = Path.Combine(DeploymentDirectory(), name + ".dll");
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (Type type in Assembly.LoadFrom(path).GetExportedTypes())
            {
                string? current = type.Namespace;
                while (current is { Length: > 0 })
                {
                    if (!found.Add(current))
                    {
                        break;
                    }

                    int lastDot = current.LastIndexOf('.');
                    current = lastDot < 0 ? null : current[..lastDot];
                }
            }
        }

        return found;
    }

    /// <summary>The Harbor namespaces a source imports, in source order, deduplicated.</summary>
    private static string[] HarborUsings(string source) => source
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("using Harbor.", StringComparison.Ordinal))
        .Select(line => line["using ".Length..].TrimEnd(';').Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    ///     The body of the one <c>```csharp</c> fence in the guide carrying
    ///     <see cref="PanelMarker" />. Throws rather than returning empty — a silent empty
    ///     return is how a doc edit turns this guard into a no-op.
    /// </summary>
    private static async Task<string> ReadDocumentedPanelExampleAsync()
    {
        string guide = await File.ReadAllTextAsync(Path.Combine(LocateRepoRoot(), GuideRelativePath))
            .ConfigureAwait(false);

        string[] lines = guide.Split('\n');
        var fence = new List<string>();
        bool inFence = false;
        bool carriesMarker = false;

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');

            if (!inFence)
            {
                inFence = line.StartsWith("```csharp", StringComparison.Ordinal);
                carriesMarker = false;
                fence.Clear();
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (carriesMarker)
                {
                    return string.Join("\n", fence);
                }

                inFence = false;
                continue;
            }

            if (line.Contains(PanelMarker, StringComparison.Ordinal))
            {
                carriesMarker = true;
            }

            fence.Add(line);
        }

        throw new InvalidOperationException(
            "no ```csharp fence in " + GuideRelativePath + " contains '" + PanelMarker
            + "'. The guide's panel example was renamed or removed — update this test rather "
            + "than let it pass on an empty extraction.");
    }

    /// <summary>
    ///     Where the Harbor assemblies sit beside the test binary. The same answer the
    ///     collector derives, written out here so the test does not inherit a defect in that
    ///     derivation.
    /// </summary>
    private static string DeploymentDirectory()
    {
        string? located = Path.GetDirectoryName(typeof(IPlugin).Assembly.Location);
        return !string.IsNullOrEmpty(located) ? located : AppContext.BaseDirectory;
    }

    /// <summary>Walk up from the test binaries to the repository root (contains docs/).</summary>
    private static string LocateRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, GuideRelativePath)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Repository root with " + GuideRelativePath + " not found above "
            + AppContext.BaseDirectory + ".");
    }
}