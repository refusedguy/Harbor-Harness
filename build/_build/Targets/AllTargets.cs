using Harbor.Build.Components;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
namespace Harbor.Build.Targets;
/// <summary>
///     Composition helpers — convenience overloads that bundle multiple
///     targets into a single invocation. Used by the <c>All</c> target on
///     the <c>Build</c> class.
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
        BuildSettings settings)
    {
        CleanTarget.Execute(resolver);
        RestoreTarget.Execute(solution);
        CompileTarget.Execute(solution, settings);
    }

    /// <summary>
    ///     Runs <c>Compile → Test → ArchitectureTests</c>. Assumes a clean
    ///     build has already happened (use <see cref="CleanBuild" /> first).
    /// </summary>
    public static void CompileAndTest(
        Solution solution,
        ArtifactPathResolver resolver,
        BuildSettings settings)
    {
        CompileTarget.Execute(solution, settings);
        TestTarget.Execute(solution, settings);
        ArchitectureTestTarget.Execute(resolver, settings);
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
        ArchiveFormat archiveFormat)
    {
        var publishDir = PublishTarget.Execute(resolver, variantBuilder, appName, variant, flags);
        return ArchiveTarget.Execute(
            resolver, archiveBuilder, publishDir, appName, variant, settings, archiveFormat);
    }
}
