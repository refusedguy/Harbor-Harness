namespace Harbor.ChangeTracking;

/// <summary>
///     Single owner of the best-effort temp-file cleanup policy in the
///     verified-change pipeline. Every fail path writes an artifact to a
///     temporary path first and removes it when the write fails; the cleanup
///     must never mask the error that caused it. Keeping the swallowed
///     exception set here means a future change (a new transient exception,
///     or logging) is made in one place instead of five.
/// </summary>
internal static class QuietFileDelete
{
    /// <summary>
    ///     Deletes <paramref name="path" /> when it exists. Only filesystem
    ///     failures that cannot be acted on by the caller are swallowed;
    ///     every other exception propagates rather than leaving a half-written
    ///     artifact behind unnoticed.
    /// </summary>
    internal static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
