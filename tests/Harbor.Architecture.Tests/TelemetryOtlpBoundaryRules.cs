// TelemetryOtlpBoundaryRules.cs — the OTLP exporter stays OUT of the product
// graph, and the OpenTelemetry SDK stays out of everything except that
// exporter (#1799).
//
// WHAT THE BOUNDARY IS
// --------------------
//
//   `Harbor.Telemetry.Otlp` (the OpenTelemetry SDK plus the OTLP exporter) may
//   not enter the dependency graph of a product binary, and no product project
//   may declare an `OpenTelemetry*` package. The ONE project allowed to carry
//   the SDK is the exporter itself — it exists precisely to keep the SDK out.
//
// WHY, MEASURED RATHER THAN REMEMBERED
// ------------------------------------
//
//   The SDK is not in the graph because the graph is published with NativeAOT
//   and the SDK is not AOT-shaped. On the pinned versions (1.20.0 of the two
//   OpenTelemetry packages, 10.0.0 of the Microsoft.Extensions.* ones):
//
//   * `OpenTelemetry.Exporter.OpenTelemetryProtocol` (net10.0) depends on
//     `OpenTelemetry`, which depends on
//     `Microsoft.Extensions.Logging.Configuration` — and THAT package depends on
//     `Microsoft.Extensions.Configuration.Binder`, the reflection-based
//     configuration binder. No `src/` or `apps/` project declares
//     `Microsoft.Extensions.Configuration.Binder` today, so the SDK would add a
//     NEW edge to the trimmed CLI graph, not a known one.
//   * The exporter assembly itself references
//     `Microsoft.Extensions.Configuration.Binder` and
//     `Microsoft.Extensions.Configuration.Binder.SourceGeneration`, and it
//     carries its own protobuf/gRPC framing (`GrpcMessageHeaderSize`,
//     `GrpcStartWritePosition`). The OTLP wire format is implemented IN the
//     package rather than delegated to `Google.Protobuf` / `Grpc.Net.Client`
//     any more — the framing is still there, and it is still not AOT-shaped.
//   * The `aot-publish` job publishes `apps/Harbor.App.Cli` with NativeAOT and
//     its pass criterion is that the PUBLISH SUCCEEDS, not "zero IL2026". So
//     the SDK in the CLI is a publish risk, not a warning-count risk.
//
//   The shipping answer to "where does the telemetry go?" is the visible sink
//   in `Harbor.Telemetry.Core`: an `ActivityListener` / `MeterListener` over the
//   canonical `ActivitySource` / `Meter` — framework types, no SDK, AOT-safe.
//
// WHAT PROTECTS THE BOUNDARY
// ---------------------------
//
//   1. The layer matrix (`FullLayerMatrixTests.Matrix`): the
//      `Harbor.Telemetry.Otlp` row carries an EMPTY allowed set, and
//      `Harbor.Hosting`'s allowed set does not name it — so an `src/` edge to
//      the exporter is already red there. That half predates this file.
//   2. THIS FILE, at the DECLARATION level: every `src/**` and `apps/**`
//      csproj is read and checked for a `<ProjectReference>` to the exporter
//      and for a `<PackageReference>` to any `OpenTelemetry*` package. The
//      declaration level is the one that matters for `apps/`, because the
//      layer matrix is not defined for composition roots — and
//      `apps/Harbor.App.Cli` is exactly the assembly that gets published.
//   3. THIS FILE, at the IL level: every product assembly this test project
//      loads is checked for an `AssemblyRef` to the exporter or to an
//      `OpenTelemetry*` assembly. This catches a TRANSITIVE arrival, which no
//      csproj in our tree declares.
//   4. The `aot-publish` ratchet (docs/AOT_PUBLISH.md): the NativeAOT publish
//      of `apps/Harbor.App.Cli` compares its diagnostic inventory against a
//      committed record, in BOTH directions.
//
// THE ALLOW-LIST HAS TWO SIDES
// ----------------------------
//
//   A one-sided ban turns "forbidden" into "delete it and forget it". The
//   allow-list here is a ratchet in both directions: a project that declares
//   the SDK and is not on the list is red, AND an entry on the list whose
//   project no longer declares the SDK is red too — because an exemption that
//   grants nothing is a row nobody has to think about, and it is how the next
//   OTel package arrives unremarked.
//
// NON-VACUITY
// -----------
//
//   The self-test at the bottom of this file plants a forbidden package in a
//   SYNTHETIC product tree and requires the same probe the rule uses to
//   report it — and requires the probe to stay silent on the same tree without
//   the plant. A guard that never fires is a guard that is not armed.

namespace Harbor.Architecture.Tests;

/// <summary>
///     One project allowed to carry the OpenTelemetry SDK, and why it is allowed.
/// </summary>
/// <param name="ProjectDir">The project directory name.</param>
/// <param name="Reason">Why this project may carry the SDK.</param>
internal readonly record struct TelemetrySdkOwner(string ProjectDir, string Reason);

/// <summary>
///     What a product tree says about the telemetry boundary. A MEASUREMENT, not
///     a verdict: the rule that consumes it applies the allow-list, so the same
///     probe can be pointed at a synthetic tree in the self-test.
/// </summary>
internal sealed class TelemetryBoundaryScan
{
    /// <summary>Product projects that declare an <c>OpenTelemetry*</c> package they may not carry.</summary>
    internal IReadOnlyList<string> PackageViolations { get; init; } = [];

    /// <summary>Product projects that declare a <c>ProjectReference</c> to the OTLP exporter.</summary>
    internal IReadOnlyList<string> ProjectViolations { get; init; } = [];

    /// <summary>Central props files that would push the SDK into every product project.</summary>
    internal IReadOnlyList<string> CentralPropsViolations { get; init; } = [];

    /// <summary>The projects that declare an <c>OpenTelemetry*</c> package, allowed or not.</summary>
    internal IReadOnlyList<string> SdkOwners { get; init; } = [];

    /// <summary>How many product projects the scan read.</summary>
    internal int ProjectsScanned { get; init; }
}

/// <summary>
///     Reads a product tree and answers the two questions the telemetry
///     boundary is stated in: who declares the OpenTelemetry SDK, and who points
///     at the OTLP exporter.
/// </summary>
internal static class TelemetryBoundaryProbe
{
    /// <summary>The project directory of the OTLP exporter.</summary>
    internal const string OtlpProjectDir = "Harbor.Telemetry.Otlp";

    /// <summary>The assembly name of the OTLP exporter.</summary>
    internal const string OtlpAssembly = "Harbor.Telemetry.Otlp";

    /// <summary>The prefix every OpenTelemetry SDK package and assembly shares.</summary>
    internal const string SdkPackagePrefix = "OpenTelemetry";

    /// <summary>The product trees the boundary covers.</summary>
    internal static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>
    ///     The one project allowed to carry the SDK. See the file header for the
    ///     reasoning; the row's reason is a VALUE because
    ///     <see cref="ExemptionReason" /> reads it, not a comment above it.
    /// </summary>
    internal static readonly TelemetrySdkOwner[] SdkAllowList =
    [
        new(
            OtlpProjectDir,
            "This project IS the boundary rather than a violation of it: it exists to carry the "
            + "OpenTelemetry SDK out of the product graph, so the SDK is declared here and nowhere "
            + "else. Its consumers are embedding JIT hosts that carry this assembly themselves, "
            + "never a Harbor binary."),
    ];

    /// <summary>Is this NuGet package id the OpenTelemetry SDK, or one of its parts?</summary>
    internal static bool IsSdkPackageId(string? id) =>
        id is not null && id.StartsWith(SdkPackagePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this assembly name the OTLP exporter, or an OpenTelemetry SDK assembly?</summary>
    internal static bool IsForbiddenAssemblyName(string? name) =>
        name is not null
        && (string.Equals(name, OtlpAssembly, StringComparison.Ordinal)
            || name.StartsWith(SdkPackagePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Reads the product tree under <paramref name="root" /> and reports what it
    ///     finds. The allow-list is applied HERE, so the same probe answers
    ///     "is this tree clean?" for the repository and for a synthetic fixture.
    /// </summary>
    internal static TelemetryBoundaryScan Scan(string root)
    {
        var packageViolations = new List<string>();
        var projectViolations = new List<string>();
        var centralViolations = new List<string>();
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var allowed = SdkAllowList.Select(o => o.ProjectDir).ToHashSet(StringComparer.Ordinal);

        int projects = 0;

        foreach (string tree in ProductTrees)
        {
            string directory = Path.Combine(root, tree);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string csproj in Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(csproj))
                {
                    continue;
                }

                projects++;
                string projectDir = Path.GetFileName(Path.GetDirectoryName(csproj)!);
                string relative = Relative(root, csproj);

                string[] sdkPackages = [.. RepoPaths.ReadPackageReferences(csproj).Where(IsSdkPackageId)];
                if (sdkPackages.Length > 0)
                {
                    owners.Add(projectDir);
                    if (!allowed.Contains(projectDir))
                    {
                        packageViolations.Add($"{relative}: declares {string.Join(", ", sdkPackages)}");
                    }
                }

                var (references, _) = RepoPaths.ReadProjectReferences(csproj);
                if (references.Contains(OtlpProjectDir, StringComparer.Ordinal))
                {
                    projectViolations.Add($"{relative}: declares a ProjectReference to {OtlpProjectDir}");
                }
            }
        }

        foreach (string props in CentralPropsFiles(root))
        {
            string[] sdkPackages = [.. RepoPaths.ReadPackageReferences(props).Where(IsSdkPackageId)];
            if (sdkPackages.Length > 0)
            {
                centralViolations.Add($"{Relative(root, props)}: declares {string.Join(", ", sdkPackages)}");
            }
        }

        return new TelemetryBoundaryScan
        {
            PackageViolations = packageViolations,
            ProjectViolations = projectViolations,
            CentralPropsViolations = centralViolations,
            SdkOwners = [.. owners],
            ProjectsScanned = projects,
        };
    }

    /// <summary>
    ///     The central props files whose <c>&lt;PackageReference&gt;</c> entries
    ///     flow into every product project beneath them: the repository root's
    ///     and the two per-tree ones.
    /// </summary>
    /// <remarks>
    ///     They carry NO exemption, and that is deliberate. A forbidden package in
    ///     a csproj reaches one project; the same package in a central props file
    ///     reaches all of them at once, including the allow-listed exporter —
    ///     so the entry cannot be excused by naming an owner.
    /// </remarks>
    internal static IEnumerable<string> CentralPropsFiles(string root)
    {
        yield return Path.Combine(root, "Directory.Build.props");
        yield return Path.Combine(root, "src", "Directory.Build.props");
        yield return Path.Combine(root, "apps", "Directory.Build.props");
    }

    /// <summary>Build output is not a project: a stale copy in <c>obj/</c> is not an edge.</summary>
    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
///     The OTLP exporter stays OUT of the product graph — a deliberate
///     decision, pinned so it cannot be undone by accident (#1799).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why.</b> Harbor's telemetry is instrumented on the canonical
///         <c>ActivitySource</c>/<c>Meter</c>, and the visible sink reads them
///         with <c>ActivityListener</c>/<c>MeterListener</c> — framework types.
///         The OTLP exporter (<c>Harbor.Telemetry.Otlp</c>) is the OTHER way out,
///         and it drags the OpenTelemetry SDK into the binary: four assemblies the
///         published CLI does not carry today, the protobuf/gRPC framing the OTLP
///         exporter implements itself, and a direct reference to the
///         reflection-based configuration binder. The <c>aot-publish</c> job
///         publishes <c>apps/Harbor.App.Cli</c> with NativeAOT and its pass
///         criterion is that publish SUCCEEDING, so the SDK is deliberately out of
///         the app graph.
///     </para>
///     <para>
///         The visible sink is the answer to "where does the telemetry go?" for
///         every host that ships today, including the CLI. OTLP remains available
///         for a JIT host that opts in by carrying the assembly itself — the
///         product binaries simply do not.
///     </para>
/// </remarks>
public class TelemetryOtlpBoundaryRules
{
    /// <summary>
    ///     Fewer product projects than this and the scan is not looking at the
    ///     product graph, so "no violation" would be the answer for the wrong
    ///     reason.
    /// </summary>
    private const int MinProductProjects = 20;

    /// <summary>The instrument: the architecture test project itself.</summary>
    private const string InstrumentAssembly = "Harbor.Architecture.Tests";

    /// <summary>
    ///     No product project declares a <c>ProjectReference</c> to the OTLP
    ///     exporter. The csproj is read directly, so a reference that binds no
    ///     type (and therefore emits no IL) is caught too.
    /// </summary>
    [Test]
    public async Task NoProductProject_ReferencesTheOtlpExporter()
    {
        TelemetryBoundaryScan scan = ScanRepository();

        await Assert.That(scan.ProjectViolations).IsEmpty()
            .Because(
                "The OTLP exporter pulls the OpenTelemetry SDK (protobuf/gRPC, not AOT-friendly) into whatever "
                + "binary references it, and the aot-publish job publishes apps/Harbor.App.Cli with NativeAOT. "
                + "The visible sink in Harbor.Telemetry.Core is the shipping path; OTLP is for a JIT host that "
                + "opts in by carrying the assembly itself. Violations: " + string.Join("; ", scan.ProjectViolations));
    }

    /// <summary>
    ///     No product project declares an <c>OpenTelemetry*</c> package. This is
    ///     the half the layer matrix cannot cover: a direct package reference
    ///     reaches a binary without any project edge at all, and the matrix only
    ///     knows about project references.
    /// </summary>
    [Test]
    public async Task NoProductProject_DeclaresTheOpenTelemetrySdk()
    {
        TelemetryBoundaryScan scan = ScanRepository();

        await Assert.That(scan.PackageViolations).IsEmpty()
            .Because(
                "The OpenTelemetry SDK is what the boundary is about: it would put four assemblies the "
                + "NativeAOT-published CLI does not carry today into that binary (OpenTelemetry, OpenTelemetry.Api, "
                + "OpenTelemetry.Api.ProviderBuilderExtensions, OpenTelemetry.Exporter.OpenTelemetryProtocol), and "
                + "the exporter references the reflection-based configuration binder directly. Only the exporter "
                + "itself may carry the SDK, and a project that declares it directly bypasses the "
                + "project-reference matrix entirely. Violations: "
                + string.Join("; ", scan.PackageViolations));
    }

    /// <summary>
    ///     No central props file declares an <c>OpenTelemetry*</c> package. A
    ///     package declared there reaches every product project at once, so it
    ///     cannot be excused by naming an owner.
    /// </summary>
    [Test]
    public async Task NoCentralPropsFile_PushesTheOpenTelemetrySdkIntoEveryProductProject()
    {
        TelemetryBoundaryScan scan = ScanRepository();

        await Assert.That(scan.CentralPropsViolations).IsEmpty()
            .Because(
                "A PackageReference in Directory.Build.props (or its src/ and apps/ siblings) flows into every "
                + "product project, so the SDK would reach the CLI and the TUI without a single csproj naming "
                + "it. The allow-list names a PROJECT, and a central declaration belongs to no project. "
                + "Violations: " + string.Join("; ", scan.CentralPropsViolations));
    }

    /// <summary>
    ///     No product assembly emits an assembly reference to the OTLP exporter
    ///     or to an OpenTelemetry SDK assembly — the compiled form of the same
    ///     boundary, and the half that catches a TRANSITIVE arrival.
    /// </summary>
    [Test]
    public async Task NoProductAssembly_EmitsAnAssemblyRefToTheOtlpExporterOrTheSdk()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        var failures = new List<string>();

        foreach ((string name, Assembly assembly) in loaded)
        {
            // The exporter itself, and the test assembly that inspects it.
            if (name == TelemetryBoundaryProbe.OtlpAssembly || name == InstrumentAssembly)
            {
                continue;
            }

            foreach (string referenced in ArchitectureTestHelpers.GetReferencedAssemblyNames(assembly))
            {
                if (TelemetryBoundaryProbe.IsForbiddenAssemblyName(referenced))
                {
                    failures.Add($"{name}: emits an AssemblyRef to {referenced}");
                }
            }
        }

        // Non-vacuity: the inventory must hold a product assembly the rule is
        // grading, or "no violation" is the answer for the wrong reason.
        await Assert.That(loaded.ContainsKey("Harbor.Telemetry.Core")).IsTrue()
            .Because(
                "Harbor.Telemetry.Core is the assembly the AOT-safe contract is written on, so it must be in "
                + "the inventory this rule grades. Without it the scan would pass on an empty set.");

        await Assert.That(failures).IsEmpty()
            .Because(
                "A product assembly that references the OTLP exporter carries the OpenTelemetry SDK into the "
                + "shipped binary, and a product assembly that references an OpenTelemetry* assembly has the "
                + "SDK in its closure whether or not any csproj declares it. Violations: "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     The allow-list and the tree agree in BOTH directions: every project
    ///     that carries the SDK is on the list, and every entry on the list is a
    ///     project that still carries it.
    /// </summary>
    [Test]
    public async Task TheSdkAllowList_MatchesTheProjectsThatDeclareTheSdk()
    {
        TelemetryBoundaryScan scan = ScanRepository();

        string[] owners = [.. scan.SdkOwners];
        string[] allowed = [.. TelemetryBoundaryProbe.SdkAllowList.Select(o => o.ProjectDir)];

        string[] unlisted = [.. owners.Except(allowed, StringComparer.Ordinal)];
        string[] dead = [.. allowed.Except(owners, StringComparer.Ordinal)];

        await Assert.That(string.Join(" | ", unlisted)).IsEqualTo(string.Empty)
            .Because(
                "a project that declares the OpenTelemetry SDK and is not on the allow-list is the violation "
                + "this whole file exists to catch. Unlisted owners: " + Describe(unlisted));

        await Assert.That(string.Join(" | ", dead)).IsEqualTo(string.Empty)
            .Because(
                "an allow-list entry whose project no longer carries the SDK grants nothing, and an exemption "
                + "that grants nothing is how the next package arrives unremarked. Delete the row or restore "
                + "the declaration — do not leave it dangling. Dead entries: " + Describe(dead));
    }

    /// <summary>
    ///     Every allow-list row states why its project may carry the SDK. The
    ///     reason is a VALUE, not a comment above the row.
    /// </summary>
    [Test]
    public async Task TheSdkAllowList_StatesWhyEachOwnerIsAllowed()
    {
        IReadOnlyList<string> failures = ExemptionReason.RowsWithoutAReason(
            "TelemetryBoundaryProbe.SdkAllowList",
            TelemetryBoundaryProbe.SdkAllowList.Select(o => (Key: o.ProjectDir, Row: new ExemptionReason.Row(o.Reason, null))));

        await Assert.That(failures).IsEmpty()
            .Because(
                "an exemption that does not say why is indistinguishable from an accident, and the reason is "
                + "the only thing that tells the next reader this was decided rather than overlooked. "
                + "Failures: " + string.Join("; ", failures));
    }

    /// <summary>
    ///     The negative control (#1730's rule): a synthetic product tree that
    ///     DECLARES the SDK in a project that may not carry it must be reported
    ///     by the same probe the rule uses.
    /// </summary>
    /// <remarks>
    ///     The fixture is written to disk and read by the production probe, so
    ///     what is proven is the instrument rather than a parallel copy of it.
    ///     A planted violation that the probe does not see means the probe is
    ///     blind — and a blind probe is green on every tree, including the
    ///     broken ones.
    /// </remarks>
    [Test]
    public async Task NonVacuity_TheGuard_FiresOnAPlantedViolation()
    {
        string scratch = Path.Combine(
            Path.GetTempPath(),
            "harbor-telemetry-boundary-" + Guid.NewGuid().ToString("N"));

        try
        {
            WriteFixtureProject(
                scratch,
                "src/Harbor.Telemetry.Otlp",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="OpenTelemetry"/>
                    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol"/>
                  </ItemGroup>
                </Project>
                """);

            WriteFixtureProject(
                scratch,
                "src/Harbor.Telemetry.Core",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"/>
                  </ItemGroup>
                </Project>
                """);

            // The clean tree first: the exporter carries the SDK (allowed), the
            // core project does not.
            TelemetryBoundaryScan clean = TelemetryBoundaryProbe.Scan(scratch);

            await Assert.That(clean.PackageViolations).IsEmpty()
                .Because(
                    "the fixture tree before the plant declares the SDK only in the allow-listed exporter, so "
                    + "the probe must report nothing — otherwise the planted violation below would prove "
                    + "nothing about the matcher");

            await Assert.That(clean.SdkOwners.Count).IsEqualTo(1)
                .Because("the fixture tree must carry the SDK in exactly one project before the plant");

            // Now the plant: the CLI declares the SDK directly.
            WriteFixtureProject(
                scratch,
                "apps/Harbor.App.Cli",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="OpenTelemetry"/>
                  </ItemGroup>
                </Project>
                """);

            TelemetryBoundaryScan planted = TelemetryBoundaryProbe.Scan(scratch);

            await Assert.That(planted.PackageViolations.Count).IsEqualTo(1)
                .Because(
                    "exactly one project in the fixture tree declares the SDK without being allowed to, and the "
                    + "probe must name it. Reported: " + string.Join("; ", planted.PackageViolations));

            await Assert.That(planted.PackageViolations[0]).Contains("Harbor.App.Cli")
                .Because("the report must name the project that carries the planted package");

            await Assert.That(planted.PackageViolations[0]).Contains("OpenTelemetry")
                .Because("the report must name the planted package, not just the project");
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>
    ///     The near-miss control: the same probe must stay SILENT on a tree that
    ///     declares the SDK in the allow-listed project and points a project
    ///     reference at a project that is not the exporter.
    /// </summary>
    /// <remarks>
    ///     A probe that reports everything is as useless as one that reports
    ///     nothing: it would train the next reader to ignore it. This is the
    ///     other half of the control above — the boundary is a boundary, and
    ///     the allowed side of it must not be red.
    /// </remarks>
    [Test]
    public async Task NonVacuity_TheGuard_StaysSilentOnTheAllowedSide()
    {
        string scratch = Path.Combine(
            Path.GetTempPath(),
            "harbor-telemetry-boundary-clean-" + Guid.NewGuid().ToString("N"));

        try
        {
            WriteFixtureProject(
                scratch,
                "src/Harbor.Telemetry.Otlp",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="OpenTelemetry"/>
                    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol"/>
                  </ItemGroup>
                </Project>
                """);

            WriteFixtureProject(
                scratch,
                "src/Harbor.Hosting",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\Harbor.Telemetry.Core\Harbor.Telemetry.Core.csproj"/>
                  </ItemGroup>
                </Project>
                """);

            TelemetryBoundaryScan scan = TelemetryBoundaryProbe.Scan(scratch);

            await Assert.That(scan.PackageViolations).IsEmpty()
                .Because(
                    "the exporter is the one project allowed to carry the SDK, so declaring it there is the "
                    + "boundary working and not a violation");

            await Assert.That(scan.ProjectViolations).IsEmpty()
                .Because(
                    "a project reference to Harbor.Telemetry.Core is not a reference to the exporter, and a probe "
                    + "that cannot tell the two apart is a probe that reports everything");

            await Assert.That(scan.SdkOwners.Count).IsEqualTo(1)
                .Because("only the exporter carries the SDK in the fixture tree");
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>Scans the repository's product tree, or fails the test loudly.</summary>
    private static TelemetryBoundaryScan ScanRepository()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            throw new InvalidOperationException(
                "[telemetry-otlp-boundary] no repository root was found. This rule reads the product tree on "
                + "disk, and without a checkout it would report a clean boundary for a tree it never saw.");
        }

        TelemetryBoundaryScan scan = TelemetryBoundaryProbe.Scan(root);

        if (scan.ProjectsScanned < MinProductProjects)
        {
            throw new InvalidOperationException(
                "[telemetry-otlp-boundary] the scan read " + scan.ProjectsScanned + " product projects, fewer "
                + "than the " + MinProductProjects + " it must see to be believed. A probe that walked nothing "
                + "would report a clean boundary for the wrong reason.");
        }

        return scan;
    }

    /// <summary>Writes one csproj into the fixture tree, creating its directory.</summary>
    private static void WriteFixtureProject(string root, string relativeDirectory, string xml)
    {
        string directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        string name = Path.GetFileName(relativeDirectory) + ".csproj";
        File.WriteAllText(Path.Combine(directory, name), xml);
    }

    /// <summary>Describes a list for a failure message.</summary>
    private static string Describe(IReadOnlyList<string> items) =>
        items.Count == 0 ? "(none)" : string.Join(" | ", items);
}
