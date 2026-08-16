using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Publish target — runs <c>dotnet publish</c> on the named app with the
///     given <see cref="PublishVariant" /> + <see cref="FeatureFlags" />.
///     Output lands in <c>artifacts/publish/&lt;app&gt;/&lt;variant&gt;/</c>.
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
        FeatureFlags flags)
    {
        var resolvedFlags = flags.Resolved();
        Console.WriteLine($"==> Publish: {appName} variant={variant} flags=[{resolvedFlags}]");

        var projectFile = resolver.GetAppProjectFile(appName);
        var outputDir = resolver.GetPublishOutputDir(appName, variant);

        var baseSettings = new DotNetPublishSettings()
            .SetProject(projectFile)
            .EnableNoRestore()
            .EnableNoBuild();

        var settings = variantBuilder.Configure(baseSettings, variant, resolvedFlags, outputDir);

        DotNetTasks.DotNetPublish(settings);

        string size = outputDir.GetHumanReadableSize();
        Console.WriteLine($"==> Publish: done — {outputDir} ({size})");
        return outputDir;
    }
}
