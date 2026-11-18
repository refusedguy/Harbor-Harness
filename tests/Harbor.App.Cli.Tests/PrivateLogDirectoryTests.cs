using Harbor.App.Cli.Logging;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2678 item 114. Log files hold full prompts, so the directory that
///     holds them must not be a symlink or readable by other local users.
///     Every test uses a private temp root; the symlink cases are Unix-only
///     because creating directory symlinks on Windows needs elevated rights.
/// </summary>
public class PrivateLogDirectoryTests
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode NonOwnerBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Test]
    public async Task Ensure_CreatesOwnerOnlyDirectory()
    {
        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "logs");
            string result = PrivateLogDirectory.Ensure(dir);

            await Assert.That(result).IsEqualTo(dir);
            await Assert.That(Directory.Exists(dir)).IsTrue();
            if (!OperatingSystem.IsWindows())
            {
                UnixFileMode mode = File.GetUnixFileMode(dir);
                await Assert.That((mode & NonOwnerBits) == 0).IsTrue();
                await Assert.That((mode & UnixFileMode.UserWrite) != 0).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Ensure_TightensLooseExistingDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = NewRoot();
        try
        {
            string dir = Path.Combine(root, "logs");
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            PrivateLogDirectory.Ensure(dir);

            UnixFileMode mode = File.GetUnixFileMode(dir);
            await Assert.That(mode).IsEqualTo(OwnerOnly);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Ensure_RejectsSymlinkedDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = NewRoot();
        try
        {
            string target = Path.Combine(root, "real");
            string link = Path.Combine(root, "logs");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);

            await Assert.That(() => PrivateLogDirectory.Ensure(link)).Throws<IOException>();
            await Assert.That(Directory.Exists(target)).IsTrue();
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task FileLoggerProvider_RefusesSymlinkedLogDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = NewRoot();
        try
        {
            string target = Path.Combine(root, "real");
            string link = Path.Combine(root, "logs");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);

            await Assert.That(() => new FileLoggerProvider("cli", link)).Throws<IOException>();
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "harbor-private-log-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort: a leaked temp directory is not a test failure.
        }
    }
}
