using Harbor.Build.Components;
using Harbor.Build.Meta;
using Nuke.Common.IO;
namespace Harbor.Build.Targets;
/// <summary>
///     Archive target — wraps a publish output directory into a
///     <c>.tar.gz</c> or <c>.zip</c> archive using <see cref="ArchiveBuilder" />.
///     Dry-run reports the planned archive path without touching the filesystem.
/// </summary>
public static class ArchiveTarget
{
    /// <summary>
    ///     Archives <paramref name="publishOutputDir" /> into the format
    ///     specified by <paramref name="format" />. Returns the archive path,
    ///     or <c>null</c> if <paramref name="format" /> is
    ///     <see cref="ArchiveFormat.None" />.
    /// </summary>
    public static AbsolutePath? Execute(
        ArtifactPathResolver resolver,
        ArchiveBuilder archiveBuilder,
        AbsolutePath publishOutputDir,
        string appName,
        PublishVariant variant,
        BuildSettings settings,
        ArchiveFormat format,
        BuildOutput output)
    {
        if (format == ArchiveFormat.None)
        {
            output.Info("Archive", "skipped (format=None)");
            return null;
        }
        output.Info("Archive", $"{format} <- {publishOutputDir}");
        string baseName = resolver.GetArchiveBaseName(appName, variant, settings.Runtime);
        var archiveDir = resolver.GetArchiveOutputDir();
        var archivePath = archiveBuilder.Create(publishOutputDir, archiveDir, baseName, format, output.IsDryRun);
        if (archivePath is not null && !output.IsDryRun)
        {
            long bytes = new FileInfo(archivePath).Length;
            output.Artifact("Archive", archivePath.ToString(), bytes);
        }
        else if (archivePath is not null)
        {
            output.Artifact("Archive", archivePath.ToString(), bytes: null, planned: true);
        }
        return archivePath;
    }
}
