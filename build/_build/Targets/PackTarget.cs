using Harbor.Build.Components;
using Harbor.Build.Meta;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Pack target — runs <c>dotnet pack</c> on the whole solution into
///     <c>artifacts/packages/</c>. Every project with
///     <c>IsPackable=true</c> (Harbor.Abstractions,
///     Harbor.Abstractions.Contracts, providers, storage, tools, …)
///     produces one <c>.nupkg</c>. When <c>--release-tag vX.Y.Z</c> is
///     passed, the tag (minus the leading <c>v</c>) overrides
///     <c>Version</c> so release packages are versioned from the tag;
///     otherwise the version from <c>Directory.Build.props</c> applies.
///     Dry-run emits only the equivalent argv.
/// </summary>
public static class PackTarget
{
    /// <summary>
    ///     Executes <c>dotnet pack</c> on the given solution. Uses
    ///     <c>--no-build</c> (assumes <see cref="CompileTarget" /> ran
    ///     first) and deterministic build properties, mirroring the
    ///     <c>Pack NuGet libraries</c> step in <c>.github/workflows/ci.yml</c>.
    /// </summary>
    public static void Execute(
        ArtifactPathResolver resolver,
        Solution solution,
        BuildSettings settings,
        string releaseTag,
        BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var solutionPath = solution.Path.ToString();
        var packagesDir = resolver.GetPackagesOutputDir();
        var version = ToPackageVersion(releaseTag);
        var argv = new List<string>
        {
            "dotnet", "pack", solutionPath, "-c", configuration,
            "--no-build", "-p:ContinuousIntegrationBuild=true",
            "-o", packagesDir.ToString()
        };
        if (version is not null)
        {
            argv.Add($"-p:Version={version}");
        }
        output.Cmd("Pack", argv);
        if (output.IsDryRun)
        {
            return;
        }
        packagesDir.CreateDirectory();
        DotNetTasks.DotNetPack(s =>
        {
            s = s.SetProject(solutionPath)
                .SetConfiguration(configuration)
                .EnableNoBuild()
                .EnableContinuousIntegrationBuild()
                .SetOutputDirectory(packagesDir.ToString());
            if (version is not null)
            {
                s = s.SetVersion(version);
            }
            return s;
        });
        output.Artifact("Pack", packagesDir.ToString(), bytes: null);
    }

    /// <summary>
    ///     Derives the NuGet version from a release tag (<c>v0.7.0</c> →
    ///     <c>0.7.0</c>). Returns <c>null</c> when no tag was passed, in
    ///     which case the version from <c>Directory.Build.props</c> applies.
    /// </summary>
    private static string? ToPackageVersion(string releaseTag)
    {
        var version = releaseTag.Trim().TrimStart('v');
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
