using Nuke.Common.IO;
namespace Harbor.Build.Extensions;
/// <summary>
///     Convenience extension methods for <see cref="AbsolutePath" /> and
///     related NUKE filesystem tasks. Reduces call-site verbosity in target
///     definitions.
/// </summary>
public static class FileSystemTasksExtensions
{
    /// <summary>
    ///     Creates the directory (and any missing parents). No-op if it exists.
    ///     Uses <see cref="System.IO.Directory.CreateDirectory(string)" />
    ///     directly to avoid the ambiguity with
    ///     <c>FileSystemAclExtensions.CreateDirectory(DirectorySecurity, string)</c>.
    /// </summary>
    public static AbsolutePath EnsureDirectoryExists(this AbsolutePath path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    ///     Returns the size in bytes of the directory tree rooted at
    ///     <paramref name="path" />. Used by the build log to report published
    ///     artifact sizes.
    /// </summary>
    public static long GetDirectorySizeBytes(this AbsolutePath path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException)
            { /* race — file disappeared */
            }
            catch (UnauthorizedAccessException)
            { /* skip */
            }
        }
        return total;
    }

    /// <summary>
    ///     Formats the directory size as a human-readable string (e.g.
    ///     "12.3 MB"). Used in build log output.
    /// </summary>
    public static string GetHumanReadableSize(this AbsolutePath path)
    {
        long bytes = path.GetDirectorySizeBytes();
        return bytes switch
        {
            < 1024L => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
