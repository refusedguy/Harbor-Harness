namespace Harbor.Architecture.Tests;

/// <summary>
///     The OTLP exporter stays OUT of the product graph — a deliberate
///     decision, pinned so it cannot be undone by accident (#1741/#1742).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> Harbor's telemetry is instrumented on the canonical
///         <c>ActivitySource</c>/<c>Meter</c>, and the visible sink added in
///         #1741 reads them with <c>ActivityListener</c>/
///         <c>MeterListener</c> — framework types. The OTLP exporter
///         (<c>Harbor.Telemetry.Otlp</c>) is the OTHER way out, and it drags
///         the OpenTelemetry SDK into the binary: protobuf/gRPC surfaces that
///         are not AOT-friendly, and configuration binder diagnostics
///         (SYSLIB1100/1101) in the CLI's publish. The <c>aot-publish</c> job
///         publishes <c>apps/Harbor.App.Cli</c> with NativeAOT and its pass
///         criterion is that publish SUCCEEDING, so the SDK is deliberately
///         out of the app graph.
///     </para>
///     <para>
///         The visible sink is the answer to "where does the telemetry go?"
///         for every host that ships today, including the CLI. OTLP remains
///         available for a JIT host that opts in by carrying the assembly
///         itself — the product binaries simply do not.
///     </para>
/// </remarks>
public class TelemetryOtlpBoundaryRules
{
    /// <summary>The project directory of the OTLP exporter.</summary>
    private const string OtlpProjectDir = "Harbor.Telemetry.Otlp";

    /// <summary>The assembly name of the OTLP exporter.</summary>
    private const string OtlpAssembly = "Harbor.Telemetry.Otlp";

    /// <summary>
    ///     No product project declares a reference to the OTLP exporter. The
    ///     csproj is read directly, so a reference that binds no type (and
    ///     therefore emits no IL) is caught too.
    /// </summary>
    [Test]
    public async Task NoProductProject_ReferencesTheOtlpExporter()
    {
        List<string> projects = ProductProjects();
        var failures = new List<string>();

        foreach (string csproj in projects)
        {
            var (references, _) = RepoPaths.ReadProjectReferences(csproj);
            if (references.Contains(OtlpProjectDir, StringComparer.Ordinal))
            {
                failures.Add($"{Relative(csproj)}: declares a ProjectReference to {OtlpProjectDir}");
            }
        }

        // Non-vacuity: a scan that found no project would pass for the wrong
        // reason. The product graph is large and the OTLP exporter exists.
        await Assert.That(projects.Count).IsGreaterThan(20)
            .Because("the product-project scan must actually see the product graph");
        await Assert.That(RepoPaths.FindSrcProject(OtlpProjectDir)).IsNotNull()
            .Because($"{OtlpProjectDir} must exist — the boundary is about where it may be referenced, not whether it exists");

        await Assert.That(failures).IsEmpty()
            .Because(
                "The OTLP exporter pulls the OpenTelemetry SDK (protobuf/gRPC, not AOT-friendly) into whatever "
                + "binary references it, and the aot-publish job publishes apps/Harbor.App.Cli with NativeAOT. "
                + "The visible sink in Harbor.Telemetry.Core is the shipping path; OTLP is for a JIT host that "
                + "opts in by carrying the assembly itself. Violations: " + string.Join("; ", failures));
    }

    /// <summary>
    ///     No product assembly emits an assembly reference to the OTLP
    ///     exporter — the compiled form of the same boundary.
    /// </summary>
    [Test]
    public async Task NoProductAssembly_EmitsAnAssemblyRefToTheOtlpExporter()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var failures = new List<string>();

        foreach ((string name, Assembly assembly) in loaded)
        {
            // The exporter itself, and the test assembly that inspects it.
            if (name == OtlpAssembly || name == "Harbor.Architecture.Tests")
            {
                continue;
            }

            if (ArchitectureTestHelpers.GetReferencedAssemblyNames(assembly).Contains(OtlpAssembly))
            {
                failures.Add($"{name}: emits an AssemblyRef to {OtlpAssembly}");
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because(
                "A product assembly that references the OTLP exporter carries the OpenTelemetry SDK into the "
                + "shipped binary. Violations: " + string.Join("; ", failures));
    }

    /// <summary>Every csproj of the product graph: <c>src/</c> and <c>apps/</c>.</summary>
    private static List<string> ProductProjects()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return [];
        }

        var projects = new List<string>();
        foreach (string folder in new[] { "src", "apps" })
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string csproj in Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories))
            {
                if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                projects.Add(csproj);
            }
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    /// <summary>Repo-relative path of a file, for the failure message.</summary>
    private static string Relative(string path)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return path;
        }

        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }
}
