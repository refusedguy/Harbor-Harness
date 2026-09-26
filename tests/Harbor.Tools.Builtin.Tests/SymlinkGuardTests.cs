using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;
using TUnit.Core.Enums;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     A2 (sprint 5): behavior matrix for the write-path symlink guard.
///     Uses real temp directories and real symlinks — Linux only.
/// </summary>
[NotInParallel("filesystem")]
[SkipWhenNotLinux]
public class SymlinkGuardTests
{
    private static string NewWorkspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "symlink-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Check_PlainFileUnderRoot_Succeeds()
    {
        string root = NewWorkspace();
        try
        {
            string file = Path.Combine(root, "note.txt");
            File.WriteAllText(file, "x");

            Result result = SymlinkGuard.Check(file, root);

            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Check_TargetFileIsSymlink_Fails()
    {
        string root = NewWorkspace();
        try
        {
            string outside = Path.Combine(root, "..", "outside-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(outside, "secret");
            string link = Path.Combine(root, "link.txt");
            File.CreateSymbolicLink(link, outside);

            Result result = SymlinkGuard.Check(link, root);

            await Assert.That(result.IsFailure).IsTrue();
            File.Delete(outside);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Check_ParentBelowRootIsSymlink_Fails()
    {
        string root = NewWorkspace();
        try
        {
            string realDir = Path.Combine(root, "..", "real-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(realDir);
            string linkDir = Path.Combine(root, "sub");
            Directory.CreateSymbolicLink(linkDir, realDir);

            Result result = SymlinkGuard.Check(
                Path.Combine(linkDir, "payload.txt"), root);

            await Assert.That(result.IsFailure).IsTrue();
            Directory.Delete(realDir, recursive: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Check_GrandparentBelowRootIsSymlink_Fails()
    {
        string root = NewWorkspace();
        try
        {
            string realDir = Path.Combine(root, "..", "real2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(realDir, "deep"));
            string linkDir = Path.Combine(root, "lvl1");
            Directory.CreateSymbolicLink(linkDir, Path.Combine(realDir));

            // lvl1 is a symlink; payload sits two levels under it.
            Result result = SymlinkGuard.Check(
                Path.Combine(linkDir, "deep", "payload.txt"), root);

            await Assert.That(result.IsFailure).IsTrue();
            Directory.Delete(realDir, recursive: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Check_RootWithTrailingSeparator_DoesNotBypassGuard()
    {
        // CONFIRMED BUG A2: IsAtOrAboveWorkspace compared `root.StartsWith(prefix)`
        // instead of `dir.StartsWith(prefix)`. With a trailing separator on the
        // workspace root the guard returned Success immediately for EVERY path,
        // skipping all symlink inspection. This case pins the fix.
        string root = NewWorkspace();
        try
        {
            string realDir = Path.Combine(root, "..", "escape-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(realDir);
            string linkDir = Path.Combine(root, "sub");
            Directory.CreateSymbolicLink(linkDir, realDir);

            Result result = SymlinkGuard.Check(
                Path.Combine(linkDir, "payload.txt"), root + Path.DirectorySeparatorChar);

            await Assert.That(result.IsFailure).IsTrue();
            Directory.Delete(realDir, recursive: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
