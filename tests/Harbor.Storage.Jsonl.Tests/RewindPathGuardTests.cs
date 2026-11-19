namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #2659 U06: a stored rewind path is confined to the workspace root.
///     Absolute paths, parent segments, escapes and symlinks are rejected
///     before a restore can write through them.
/// </summary>
public class RewindPathGuardTests
{
    private static string NewWorkspace() =>
        Path.Combine(Path.GetTempPath(), $"harbor-pathguard-test-{Guid.NewGuid():N}");

    [Test]
    public async Task Check_AcceptsARelativePathInsideTheRoot()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            var guard = new RewindPathGuard(root);

            var result = guard.Check(Path.Combine("sub", "file.txt"));

            await Assert.That(result.Outcome).IsEqualTo(RewindPathGuardOutcome.Safe);
            await Assert.That(result.IsRejected).IsFalse();
            await Assert.That(result.Reason).IsNull();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_RejectsAbsoluteAndRootedPaths()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            var guard = new RewindPathGuard(root);
            string absolute = Path.Combine(root, "file.txt");

            var absoluteResult = guard.Check(absolute);
            var rootedResult = guard.Check(Path.DirectorySeparatorChar + "etc" + Path.DirectorySeparatorChar + "passwd");

            await Assert.That(absoluteResult.IsRejected).IsTrue();
            await Assert.That(absoluteResult.Reason!).Contains("relative to the workspace root");
            await Assert.That(rootedResult.IsRejected).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_RejectsParentTraversalSegments()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            var guard = new RewindPathGuard(root);

            var dotDot = guard.Check(Path.Combine("sub", "..", "file.txt"));
            var slashDotDot = guard.Check("sub/../file.txt");

            await Assert.That(dotDot.IsRejected).IsTrue();
            await Assert.That(dotDot.Reason!).Contains("parent-directory segment");
            await Assert.That(slashDotDot.IsRejected).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_RejectsEmptyPath()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            var guard = new RewindPathGuard(root);

            var result = guard.Check("   ");

            await Assert.That(result.IsRejected).IsTrue();
            await Assert.That(result.Reason!).Contains("empty");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_RejectsSymlinkedFinalComponent()
    {
        if (OperatingSystem.IsWindows()) return;

        string root = NewWorkspace();
        string outside = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            string target = Path.Combine(outside, "target.txt");
            File.WriteAllText(target, "outside");
            string link = Path.Combine(root, "link.txt");
            File.CreateSymbolicLink(link, target);

            var result = new RewindPathGuard(root).Check("link.txt");

            await Assert.That(result.IsRejected).IsTrue();
            await Assert.That(result.Reason!).Contains("symlink");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Test]
    public async Task Check_RejectsSymlinkedAncestorDirectory()
    {
        if (OperatingSystem.IsWindows()) return;

        string root = NewWorkspace();
        string outside = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            string link = Path.Combine(root, "escape");
            Directory.CreateSymbolicLink(link, outside);

            var result = new RewindPathGuard(root).Check(Path.Combine("escape", "file.txt"));

            await Assert.That(result.IsRejected).IsTrue();
            await Assert.That(result.Reason!).Contains("symlinked directory");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }
}
