namespace Harbor.ChangeTracking;

/// <summary>
///     Shared best-effort cleanup of a temporary file after a failed atomic
///     replace. A missing file is a no-op and IO / authorization failures are
///     swallowed so cleanup never masks the original write failure.
/// </summary>
internal static class FileCleanup
{
    internal static void QuietDelete(string path)
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
