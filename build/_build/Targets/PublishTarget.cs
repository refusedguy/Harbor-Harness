using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Harbor.Build.Meta;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Publish target — runs <c>dotnet publish</c> on the named app with the
///     given <see cref="PublishVariant" /> + <see cref="FeatureFlags" />.
///     Output lands in <c>artifacts/publish/&lt;app&gt;/&lt;variant&gt;/</c>.
///     Dry-run validates the variant/flag combination, prints the fully
///     expanded argv and reports the planned output directory without
///     creating it.
/// </summary>
public static class PublishTarget
{
    /// <summary>
    ///     Publishes the named app with the given variant + flags. Returns the
    ///     publish output directory.
    /// </summary>
    public static AbsolutePath Execute(
        ArtifactPathResolver resolver,
        PublishVariantBuilder variantBuilder,
        string appName,
        PublishVariant variant,
        FeatureFlags flags,
        BuildOutput output)
    {
        var resolvedFlags = flags.Resolved();
        output.Info("Publish", $"{appName} variant={variant} flags=[{resolvedFlags}]");
        var projectFile = resolver.GetAppProjectFile(appName);
        var outputDir = resolver.GetPublishOutputDir(appName, variant);
        var baseSettings = new DotNetPublishSettings()
            .SetProject(projectFile)
            .EnableNoRestore()
            .EnableNoBuild();
        // Configure() also runs EnsureVariantAllowed: invalid combinations fail
        // identically in dry-run and real mode (honesty requirement).
        var settings = variantBuilder.Configure(baseSettings, variant, resolvedFlags, outputDir);
        output.Cmd("Publish", DotNetArgv.RenderPublish(settings));
        if (output.IsDryRun)
        {
            output.Artifact("Publish", outputDir.ToString(), bytes: null, planned: true);
            return outputDir;
        }
        DotNetTasks.DotNetPublish(settings);
        long bytes = outputDir.GetDirectorySizeBytes();
        output.Artifact("Publish", outputDir.ToString(), bytes);
        return outputDir;
    }
}
