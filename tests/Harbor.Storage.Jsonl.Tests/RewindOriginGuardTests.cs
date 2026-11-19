namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #2659 U05: the origin guard checks stat (mode + size), then mtime, then
///     the content hash, and returns an explicit Conflict rather than allowing
///     a silent overwrite of an edit made after the snapshot.
/// </summary>
public class RewindOriginGuardTests
{
    private static string NewSessionDirectory() =>
        Path.Combine(Path.GetTempPath(), $"harbor-guard-test-{Guid.NewGuid():N}");

    [Test]
    public async Task Check_ReturnsUnchangedForAnUntouchedFile()
    {
        string root = NewSessionDirectory();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "original");

            var snapshot = RewindOriginGuard.Capture(path);
            var result = new RewindOriginGuard().Check(path, snapshot);

            await Assert.That(result.Outcome).IsEqualTo(RewindGuardOutcome.Unchanged);
            await Assert.That(result.IsConflict).IsFalse();
            await Assert.That(result.Reason).IsNull();
            await Assert.That(snapshot.ContentHash).IsEqualTo(RewindBlobStore.ComputeHash("original"u8.ToArray()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_ConflictsOnSizeChangeBeforeLookingAtContent()
    {
        string root = NewSessionDirectory();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "original");
            var snapshot = RewindOriginGuard.Capture(path);

            File.AppendAllText(path, "-appended");

            var result = new RewindOriginGuard().Check(path, snapshot);
            await Assert.That(result.IsConflict).IsTrue();
            await Assert.That(result.Reason!).Contains("size changed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_ConflictsOnMtimeChangeWhenSizeAndContentMatch()
    {
        string root = NewSessionDirectory();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "original");
            var snapshot = RewindOriginGuard.Capture(path);
            var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, old);

            var result = new RewindOriginGuard().Check(path, snapshot);

            await Assert.That(result.IsConflict).IsTrue();
            await Assert.That(result.Reason!).Contains("last-write time changed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_ConflictsOnContentChangeWithIdenticalStatAndMtime()
    {
        string root = NewSessionDirectory();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "file.txt");
            var pinnedMtime = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.WriteAllText(path, "aaaa");
            File.SetLastWriteTimeUtc(path, pinnedMtime);
            var snapshot = RewindOriginGuard.Capture(path);

            // Same length, different bytes, same mtime: only level 3 can fail.
            File.WriteAllText(path, "bbbb");
            File.SetLastWriteTimeUtc(path, pinnedMtime);

            var result = new RewindOriginGuard().Check(path, snapshot);

            await Assert.That(result.IsConflict).IsTrue();
            await Assert.That(result.Reason!).Contains("content hash changed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Check_ConflictsWhenTheOriginFileWasDeleted()
    {
        string root = NewSessionDirectory();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "original");
            var snapshot = RewindOriginGuard.Capture(path);

            File.Delete(path);

            var result = new RewindOriginGuard().Check(path, snapshot);
            await Assert.That(result.IsConflict).IsTrue();
            await Assert.That(result.Reason!).Contains("no longer exists");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
