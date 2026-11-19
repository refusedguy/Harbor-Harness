namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #2659 restore slice: a rewind writes back only touched paths, refuses
///     to overwrite a user edit made after the checkpoint, honours tombstones
///     for agent-created files, and rejects unsafe stored paths before any
///     disk write.
/// </summary>
public class FileRewinderTests
{
    private static string NewWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-rewind-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileRewindEntry Entry(
        string relativePath,
        string? blobHash,
        string fullPath,
        int version = 1) =>
        new(relativePath, blobHash, RewindOriginGuard.Capture(fullPath), version);

    [Test]
    public async Task Restore_WritesBackPreImageForTouchedPathOnly()
    {
        string root = NewWorkspace();
        try
        {
            string touched = Path.Combine(root, "touched.txt");
            string untouched = Path.Combine(root, "untouched.txt");
            File.WriteAllText(touched, "original");
            File.WriteAllText(untouched, "keep me");

            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            string hash = store.Store("original"u8.ToArray())!;

            // The checkpoint baseline is the state at rewind-request time,
            // which already includes the agent's own edits.
            File.WriteAllText(touched, "agent edit");
            var baseline = RewindOriginGuard.Capture(touched);

            var entry = new FileRewindEntry("touched.txt", hash, baseline);
            var rewinder = new FileRewinder(root, store);
            var result = rewinder.Restore(entry);

            await Assert.That(result.IsRestored).IsTrue();
            await Assert.That(File.ReadAllText(touched)).IsEqualTo("original");
            await Assert.That(File.ReadAllText(untouched)).IsEqualTo("keep me");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Restore_ConflictsWhenLiveFileChangedSinceCheckpoint()
    {
        string root = NewWorkspace();
        try
        {
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "agent edit");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            string hash = store.Store("original"u8.ToArray())!;
            var entry = Entry("file.txt", hash, path);

            // Parallel user edit after the checkpoint.
            File.AppendAllText(path, " + user edit");

            var result = new FileRewinder(root, store).Restore(entry);

            await Assert.That(result.Outcome).IsEqualTo(RewindRestoreOutcome.Conflict);
            await Assert.That(result.Reason!).Contains("size changed");
            await Assert.That(File.ReadAllText(path)).IsEqualTo("agent edit + user edit");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Restore_ConflictsOnSameSizeSameMtimeContentChange()
    {
        string root = NewWorkspace();
        try
        {
            string path = Path.Combine(root, "file.txt");
            var pinned = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.WriteAllText(path, "aaaa");
            File.SetLastWriteTimeUtc(path, pinned);
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            string hash = store.Store("original"u8.ToArray())!;
            var entry = Entry("file.txt", hash, path);

            File.WriteAllText(path, "bbbb");
            File.SetLastWriteTimeUtc(path, pinned);

            var result = new FileRewinder(root, store).Restore(entry);

            await Assert.That(result.IsRestored).IsFalse();
            await Assert.That(result.Reason!).Contains("content hash changed");
            await Assert.That(File.ReadAllText(path)).IsEqualTo("bbbb");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Restore_TombstoneDeletesAgentCreatedFile()
    {
        string root = NewWorkspace();
        try
        {
            string path = Path.Combine(root, "created.txt");
            File.WriteAllText(path, "created by agent");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var entry = Entry("created.txt", null, path);

            var result = new FileRewinder(root, store).Restore(entry);

            await Assert.That(result.IsRestored).IsTrue();
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Restore_RejectsEscapingPathBeforeAnyDiskWrite()
    {
        string root = NewWorkspace();
        string outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.txt");
        try
        {
            File.WriteAllText(outside, "original");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            string hash = store.Store("original"u8.ToArray())!;
            var entry = new FileRewindEntry("../outside.txt", hash, RewindOriginGuard.Capture(outside));

            var result = new FileRewinder(root, store).Restore(entry);

            await Assert.That(result.Outcome).IsEqualTo(RewindRestoreOutcome.Rejected);
            await Assert.That(result.Reason!).Contains("parent-directory segment");
            await Assert.That(File.ReadAllText(outside)).IsEqualTo("original");
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Restore_MissingBlobDoesNotWriteTheFile()
    {
        string root = NewWorkspace();
        try
        {
            string path = Path.Combine(root, "file.txt");
            File.WriteAllText(path, "agent edit");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var entry = Entry("file.txt", new string('A', 64), path);

            var result = new FileRewinder(root, store).Restore(entry);

            await Assert.That(result.Outcome).IsEqualTo(RewindRestoreOutcome.MissingBlob);
            await Assert.That(File.ReadAllText(path)).IsEqualTo("agent edit");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task RestoreAll_RestoresGoodPathsAndReportsConflictsSeparately()
    {
        string root = NewWorkspace();
        try
        {
            string good = Path.Combine(root, "good.txt");
            string conflicted = Path.Combine(root, "conflict.txt");
            File.WriteAllText(good, "agent edit");
            File.WriteAllText(conflicted, "agent edit");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            string hash = store.Store("original"u8.ToArray())!;

            var paths = new RewindTouchedPaths();
            paths.Track(Entry("good.txt", hash, good));
            paths.Track(Entry("conflict.txt", hash, conflicted));
            // Duplicate touch must be ignored: first touch wins.
            paths.Track(Entry("good.txt", null, good));
            File.AppendAllText(conflicted, " + user edit");

            var results = new FileRewinder(root, store).RestoreAll(paths);

            await Assert.That(paths.Count).IsEqualTo(2);
            await Assert.That(results.Count).IsEqualTo(2);
            await Assert.That(results[0].Result.IsRestored).IsTrue();
            await Assert.That(File.ReadAllText(good)).IsEqualTo("original");
            await Assert.That(results[1].Result.Outcome).IsEqualTo(RewindRestoreOutcome.Conflict);
            await Assert.That(File.ReadAllText(conflicted)).IsEqualTo("agent edit + user edit");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
