using Harbor.Build.Components;
using Nuke.Common.IO;
namespace Harbor.Build.Targets;
/// <summary>
///     Release target — publishes multiple variants, archives each, and
///     uploads the archives to a GitHub release identified by a tag.
///     Requires <c>GH_TOKEN</c> environment variable (see
///     <see cref="GitHubReleaseUploader" />).
/// </summary>
public static class ReleaseTarget
{
    /// <summary>
    ///     Executes the full release pipeline for the named app:
    ///     <c>Publish → Archive → Upload</c> for each variant in
    ///     <paramref name="variants" />.
    /// </summary>
    public static async Task ExecuteAsync(
        ArtifactPathResolver resolver,
        PublishVariantBuilder variantBuilder,
        ArchiveBuilder archiveBuilder,
        GitHubReleaseUploader releaseUploader,
        string appName,
        IReadOnlyList<PublishVariant> variants,
        FeatureFlags flags,
        BuildSettings settings,
        string releaseTag,
        string repo,
        ArchiveFormat archiveFormat = ArchiveFormat.TarGz)
    {
        if (string.IsNullOrWhiteSpace(releaseTag))
        {
            throw new ArgumentException("Release tag is required (pass --release-tag v0.7.0)", nameof(releaseTag));
        }

        Console.WriteLine($"==> Release: tag={releaseTag} repo={repo} variants={variants.Count}");
        Console.WriteLine($"             app={appName} archiveFormat={archiveFormat}");
        Console.WriteLine($"             flags=[{flags}]");

        var uploadedArchives = new List<AbsolutePath>();

        foreach (var variant in variants)
        {
            Console.WriteLine($"--- Release: variant={variant} ---");
            var publishDir = PublishTarget.Execute(resolver, variantBuilder, appName, variant, flags);
            var archive = ArchiveTarget.Execute(
                resolver, archiveBuilder, publishDir, appName, variant, settings, archiveFormat);
            if (archive is not null)
            {
                uploadedArchives.Add(archive);
            }
        }

        Console.WriteLine($"==> Release: uploading {uploadedArchives.Count} archive(s) to GitHub");
        await releaseUploader.UploadAsync(releaseTag, uploadedArchives, repo);
        Console.WriteLine("==> Release: done");
    }

    /// <summary>
    ///     Returns the standard release variant matrix: FrameworkDependent,
    ///     SelfContained, SingleFileSelfContained, AOT. AOT requires
    ///     <see cref="FeatureFlags.Minimal" /> or equivalent AOT-compatible flags.
    /// </summary>
    public static IReadOnlyList<PublishVariant> DefaultReleaseVariants(FeatureFlags flags)
    {
        var variants = new List<PublishVariant>
        {
            PublishVariant.FrameworkDependent,
            PublishVariant.SelfContained,
            PublishVariant.SingleFileSelfContained
        };
        if (flags.Resolved().IsAotCompatible)
        {
            variants.Add(PublishVariant.AOT);
        }
        return variants;
    }
}
