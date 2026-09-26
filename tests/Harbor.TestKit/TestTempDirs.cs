namespace Harbor.TestKit;

/// <summary>
///     S5443-safe temp paths for tests. Real directories live under
///     <c>~/.cache/harbor-tests</c> (user-private) instead of the
///     world-writable shared <c>/tmp</c>.
/// </summary>
public static class TestTempDirs
{
    /// <summary>Root for all test temp paths; created on first use.</summary>
    public static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "harbor-tests");

    /// <summary>Creates and returns a fresh unique directory for one test.</summary>
    public static string NewDirectory(string prefix)
    {
        string dir = Path.Combine(BaseDir, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Returns a fresh unique file path (not created; the caller writes it).</summary>
    public static string NewFilePath(string prefix, string extension = ".tmp")
    {
        Directory.CreateDirectory(BaseDir);
        return Path.Combine(BaseDir, $"{prefix}-{Guid.NewGuid():N}{extension}");
    }
}
