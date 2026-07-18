using System.Diagnostics;
using System.IO.Compression;
using Harbor.Build.Configuration;
using Nuke.Common;
using Nuke.Common.IO;

namespace Harbor.Build.Components;

/// <summary>
///     Creates <c>.tar.gz</c> or <c>.zip</c> archives from a publish output
///     directory. Uses the system <c>tar</c> binary (must be on <c>PATH</c>)
///     for <c>tar.gz</c> and <c>System.IO.Compression.ZipFile</c> for
///     <c>.zip</c> (no external binary required).
/// </summary>
public sealed class ArchiveBuilder
{
    /// <summary>
    ///     Creates a <c>.tar.gz</c> archive of <paramref name="sourceDir"/>
    ///     in <paramref name="outputDir"/> with the given base name.
    ///     Returns the path to the created archive.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    ///     Thrown if the <c>tar</c> binary is not on <c>PATH</c>.
    /// </exception>
    public AbsolutePath CreateTarGz(AbsolutePath sourceDir, AbsolutePath outputDir, string name)
    {
        outputDir.CreateDirectory();
        var outputFile = outputDir / $"{name}.tar.gz";

        // tar -czf <output> -C <source> .  — bundle the directory contents.
        // We use Process.Start (not NUKE's ProcessTasks) to avoid a hard
        // dependency on a specific tar wrapper; the GNU/BSD tar CLI is stable.
        var psi = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-czf {Quote(outputFile)} -C {Quote(sourceDir)} .",
            UseShellExecute = false,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 'tar' process.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"tar exited with code {process.ExitCode}: {stderr}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                "tar binary not found on PATH. Install it (e.g. 'apt install tar') " +
                "or use --archive Zip instead.", ex);
        }

        return outputFile;
    }

    /// <summary>
    ///     Creates a <c>.zip</c> archive of <paramref name="sourceDir"/>
    ///     in <paramref name="outputDir"/> with the given base name.
    ///     Returns the path to the created archive. Uses
    ///     <c>System.IO.Compression.ZipFile</c> (no external binary required).
    /// </summary>
    public AbsolutePath CreateZip(AbsolutePath sourceDir, AbsolutePath outputDir, string name)
    {
        outputDir.CreateDirectory();
        var outputFile = outputDir / $"{name}.zip";
        ZipFile.CreateFromDirectory(sourceDir, outputFile, CompressionLevel.Optimal, includeBaseDirectory: false);
        return outputFile;
    }

    /// <summary>
    ///     Dispatches to <see cref="CreateTarGz"/> or <see cref="CreateZip"/>
    ///     based on <paramref name="format"/>. Returns the archive path, or
    ///     <c>null</c> if <paramref name="format"/> is <see cref="ArchiveFormat.None"/>.
    /// </summary>
    public AbsolutePath? Create(
        AbsolutePath sourceDir,
        AbsolutePath outputDir,
        string name,
        ArchiveFormat format) => format switch
        {
            ArchiveFormat.None => null,
            ArchiveFormat.TarGz => CreateTarGz(sourceDir, outputDir, name),
            ArchiveFormat.Zip => CreateZip(sourceDir, outputDir, name),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown archive format")
        };

    private static string Quote(AbsolutePath path) => $"\"{path}\"";
}
