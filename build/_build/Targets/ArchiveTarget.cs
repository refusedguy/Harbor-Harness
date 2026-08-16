using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Nuke.Common.IO;
namespace Harbor.Build.Targets;
/// <summary>
///     Archive target — wraps a publish output directory into a
///     <c>.tar.gz</c> or <c>.zip</c> archive using <see cref="ArchiveBuilder" />.
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
        ArchiveFormat format)
    {
        if (format == ArchiveFormat.None)
        {
            Console.WriteLine("==> Archive: skipped (format=None)");
            return null;
        }

        Console.WriteLine($"==> Archive: {format} <- {publishOutputDir}");

        string baseName = resolver.GetArchiveBaseName(appName, variant, settings.Runtime);
        var archiveDir = resolver.GetArchiveOutputDir();
        Directory.CreateDirectory(archiveDir);
        var archivePath = archiveBuilder.Create(publishOutputDir, archiveDir, baseName, format);

        if (archivePath is not null)
        {
            string size = archivePath.GetHumanReadableSize();
            Console.WriteLine($"==> Archive: done — {archivePath} ({size})");
        }
        return archivePath;
    }
}
