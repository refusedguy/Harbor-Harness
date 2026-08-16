namespace Harbor.Cli.Logging;
/// <summary>
///     Keeps the <c>~/.harbor/logs/</c> directory from growing without bound.
///     On each process startup, <see cref="Cleanup" /> deletes the oldest
///     <c>harbor-*.log</c> files until at most <see cref="MaxFiles" /> remain.
/// </summary>
/// <remarks>
///     Default <see cref="MaxFiles" /> is 50. With one file per CLI run, that's
///     roughly 50 runs of history — enough to debug a regression reported
///     "yesterday" without disk usage ballooning over months of daily use.
/// </remarks>
public sealed class RollingLogCleaner
{
    private readonly string _logDir;

    /// <summary>
    ///     Create a cleaner for <paramref name="logDir" />.
    /// </summary>
    /// <param name="logDir">Directory holding <c>harbor-*.log</c> files.</param>
    /// <param name="maxFiles">How many recent files to keep. Defaults to 50.</param>
    public RollingLogCleaner(string logDir, int maxFiles = 50)
    {
        if (string.IsNullOrWhiteSpace(logDir))
            throw new ArgumentException("logDir must not be null or empty", nameof(logDir));
        if (maxFiles < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFiles), "must keep at least 1 file");
        _logDir = logDir;
        MaxFiles = maxFiles;
    }

    /// <summary>Convenience accessor.</summary>
    public int MaxFiles
    {
        get;
    }

    /// <summary>
    ///     Delete all but the newest <see cref="MaxFiles" /> <c>harbor-*.log</c>
    ///     files in <see cref="_logDir" />. Files are sorted by
    ///     <see cref="FileInfo.CreationTimeUtc" /> descending. Missing directory
    ///     is a no-op. Per-file delete failures are swallowed (best-effort).
    /// </summary>
    public void Cleanup()
    {
        if (!Directory.Exists(_logDir))
            return;

        FileInfo[] files;
        try
        {
            files = Directory.GetFiles(_logDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            // Directory not readable — nothing we can do.
            return;
        }
        catch (IOException)
        {
            return;
        }

        if (files.Length <= MaxFiles)
            return;

        foreach (var file in files.Skip(MaxFiles))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // File may be in use by a concurrent run — skip.
            }
            catch (UnauthorizedAccessException)
            {
                // Permission issue — skip.
            }
        }
    }
}
