using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

namespace Harbor.Build.Components;

/// <summary>
///     Resolves filesystem paths for build outputs (publish artifacts,
///     archives, source dirs) per app + variant. Centralizes the path
///     conventions so <c>PublishTarget</c>, <c>ArchiveTarget</c>, and
///     <c>ReleaseTarget</c> all agree on where outputs land.
/// </summary>
public sealed class ArtifactPathResolver
{
    private readonly AbsolutePath _rootDirectory;
    private readonly AbsolutePath _artifactsDirectory;

    /// <summary>Construct a resolver bound to the given NUKE root.</summary>
    public ArtifactPathResolver(AbsolutePath rootDirectory, AbsolutePath artifactsDirectory)
    {
        _rootDirectory = rootDirectory;
        _artifactsDirectory = artifactsDirectory;
    }

    /// <summary>Root of the solution (contains src/, apps/, tests/, build/).</summary>
    public AbsolutePath RootDirectory => _rootDirectory;

    /// <summary>Artifacts directory (cleaned on each Clean target run).</summary>
    public AbsolutePath ArtifactsDirectory => _artifactsDirectory;

    /// <summary>Source projects directory (src/).</summary>
    public AbsolutePath SourceDirectory => _rootDirectory / "src";

    /// <summary>Apps directory (apps/).</summary>
    public AbsolutePath AppsDirectory => _rootDirectory / "apps";

    /// <summary>Tests directory (tests/).</summary>
    public AbsolutePath TestsDirectory => _rootDirectory / "tests";

    /// <summary>Samples directory (samples/).</summary>
    public AbsolutePath SamplesDirectory => _rootDirectory / "samples";

    /// <summary>
    ///     Returns the <c>Project</c> instance for the named app
    ///     (e.g. <c>Harbor.App.Cli</c>) from the given solution.
    /// </summary>
    public Project GetAppProject(Solution solution, string appName) =>
        solution.GetProject(appName)
        ?? throw new InvalidOperationException(
            $"App project '{appName}' not found in solution. " +
            $"Available: {string.Join(", ", solution.AllProjects.Select(p => p.Name))}");

    /// <summary>
    ///     Returns the absolute path to the <c>.csproj</c> for the named app.
    /// </summary>
    public AbsolutePath GetAppProjectFile(string appName) =>
        AppsDirectory / appName / $"{appName}.csproj";

    /// <summary>
    ///     Publish output directory for a specific app + variant combination.
    ///     Example: <c>artifacts/publish/Harbor.App.Cli/framework-dependent</c>.
    /// </summary>
    public AbsolutePath GetPublishOutputDir(string appName, Configuration.PublishVariant variant) =>
        _artifactsDirectory / "publish" / appName / GetVariantDirName(variant);

    /// <summary>
    ///     Archive output directory. Archives land here with names like
    ///     <c>harbor-cli-framework-dependent-linux-x64.tar.gz</c>.
    /// </summary>
    public AbsolutePath GetArchiveOutputDir() => _artifactsDirectory / "archives";

    /// <summary>
    ///     Returns the canonical archive base name for an app + variant + RID.
    ///     Example: <c>harbor-cli-framework-dependent-linux-x64</c>.
    /// </summary>
    public string GetArchiveBaseName(string appName, Configuration.PublishVariant variant, string runtime)
    {
        string appSlug = appName.Equals("Harbor.App.Cli", StringComparison.OrdinalIgnoreCase)
            ? "harbor-cli"
            : appName.ToLowerInvariant();
        string variantSlug = GetVariantDirName(variant);
        string rid = runtime.Replace("-", "_");
        return $"{appSlug}-{variantSlug}-{rid}";
    }

    /// <summary>Directory slug for a variant (e.g. <c>framework-dependent</c>).</summary>
    public static string GetVariantDirName(Configuration.PublishVariant variant) => variant switch
    {
        Configuration.PublishVariant.FrameworkDependent => "framework-dependent",
        Configuration.PublishVariant.SelfContained => "self-contained",
        Configuration.PublishVariant.SingleFile => "single-file",
        Configuration.PublishVariant.SingleFileSelfContained => "single-file-self-contained",
        Configuration.PublishVariant.Trimmed => "trimmed",
        Configuration.PublishVariant.AOT => "aot",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant")
    };
}
