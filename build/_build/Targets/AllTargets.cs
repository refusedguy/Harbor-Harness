using Harbor.Build.Components;
using Harbor.Build.Meta;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
namespace Harbor.Build.Targets;
/// <summary>
///     Composition helpers — convenience overloads that bundle multiple
///     targets into a single invocation. All of them thread the
///     <see cref="BuildOutput" /> through so dry-run and Json mode apply
///     uniformly.
/// </summary>
public static class AllTargets
{
    /// <summary>
    ///     Runs <c>Clean → Restore → Compile</c> in sequence. Equivalent to
    ///     invoking <c>./build.sh Clean Compile</c> but with explicit ordering.
    /// </summary>
    public static void CleanBuild(
        ArtifactPathResolver resolver,
        Solution solution,
        BuildSettings settings,
        BuildOutput output)
    {
        CleanTarget.Execute(resolver, output);
        RestoreTarget.Execute(solution, output);
        CompileTarget.Execute(solution, settings, output);
    }
    /// <summary>
    ///     Runs <c>Compile → Test → ArchitectureTests</c>. Assumes a clean
    ///     build has already happened (use <see cref="CleanBuild" /> first).
    /// </summary>
    public static void CompileAndTest(
        Solution solution,
        ArtifactPathResolver resolver,
        BuildSettings settings,
        BuildOutput output)
    {
        CompileTarget.Execute(solution, settings, output);
        TestTarget.Execute(solution, settings, output);
        ArchitectureTestTarget.Execute(resolver, settings, output);
    }
    /// <summary>
    ///     Runs <c>Publish → Archive</c> for the given app + variant + flags.
    /// </summary>
    public static AbsolutePath? PublishAndArchive(
        ArtifactPathResolver resolver,
        PublishVariantBuilder variantBuilder,
        ArchiveBuilder archiveBuilder,
        string appName,
        PublishVariant variant,
        FeatureFlags flags,
        BuildSettings settings,
        ArchiveFormat archiveFormat,
        BuildOutput output)
    {
        var publishDir = PublishTarget.Execute(resolver, variantBuilder, appName, variant, flags, output);
        return ArchiveTarget.Execute(
            resolver, archiveBuilder, publishDir, appName, variant, settings, archiveFormat, output);
    }
}
