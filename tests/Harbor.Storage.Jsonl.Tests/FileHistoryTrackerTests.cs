namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #2659 U02: a write tool captures the pre-image before its first touch
///     of a file in a turn. Later touches of the same path reuse that first
///     capture, an absent file is recorded as a tombstone, and unsafe paths
///     are rejected before any filesystem read.
/// </summary>
public class FileHistoryTrackerTests
{
    private static string NewWorkspace() =>
        Path.Combine(Path.GetTempPath(), $"harbor-history-test-{Guid.NewGuid():N}");

    [Test]
    public async Task TrackBeforeWrite_CapturesPreImageInContentAddressedStore()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "original");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var tracker = new FileHistoryTracker(root, store);

            var result = tracker.TrackBeforeWrite("file.txt");

            await Assert.That(result.IsCaptured).IsTrue();
            await Assert.That(result.Reason).IsNull();
            var preImage = result.PreImage!;
            await Assert.That(preImage.Path).IsEqualTo("file.txt");
            await Assert.That(preImage.ExistedBefore).IsTrue();
            await Assert.That(preImage.IsTombstone).IsFalse();
            await Assert.That(preImage.Version).IsEqualTo(1);
            await Assert.That(preImage.BlobHash).IsEqualTo(RewindBlobStore.ComputeHash("original"u8.ToArray()));
            await Assert.That(File.ReadAllText(Path.Combine(root, ".rewind", "session-1", "objects", preImage.BlobHash!)))
                .IsEqualTo("original");
            await Assert.That(tracker.Count).IsEqualTo(1);
            await Assert.That(tracker.PreImages[0]).IsEqualTo(preImage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TrackBeforeWrite_SecondTouchReusesFirstPreImage()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "file.txt");
            File.WriteAllText(file, "original");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var tracker = new FileHistoryTracker(root, store);

            var first = tracker.TrackBeforeWrite("file.txt");
            File.WriteAllText(file, "first edit");
            var second = tracker.TrackBeforeWrite("file.txt");
            File.WriteAllText(file, "second edit");
            var third = tracker.TrackBeforeWrite("file.txt");

            await Assert.That(second.Outcome).IsEqualTo(FileHistoryTrackOutcome.AlreadyTracked);
            await Assert.That(third.Outcome).IsEqualTo(FileHistoryTrackOutcome.AlreadyTracked);
            await Assert.That(second.PreImage).IsEqualTo(first.PreImage);
            await Assert.That(third.PreImage).IsEqualTo(first.PreImage);
            await Assert.That(tracker.Count).IsEqualTo(1);
            await Assert.That(store.Read(first.PreImage!.BlobHash)).IsNotNull();
            var stored = System.Text.Encoding.UTF8.GetString(store.Read(first.PreImage.BlobHash)!);
            await Assert.That(stored).IsEqualTo("original");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TrackBeforeWrite_AbsentFileIsRecordedAsTombstone()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            var session = Path.Combine(root, ".rewind", "session-1");
            var store = new RewindBlobStore(session);
            var tracker = new FileHistoryTracker(root, store);

            var result = tracker.TrackBeforeWrite("created-by-agent.txt");

            await Assert.That(result.IsCaptured).IsTrue();
            var preImage = result.PreImage!;
            await Assert.That(preImage.BlobHash).IsNull();
            await Assert.That(preImage.ExistedBefore).IsFalse();
            await Assert.That(preImage.IsTombstone).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(session, "objects"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TrackBeforeWrite_RecordsDistinctPathsInFirstTouchOrder()
    {
        string root = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var tracker = new FileHistoryTracker(root, store);

            tracker.TrackBeforeWrite("b.txt");
            tracker.TrackBeforeWrite("a.txt");
            tracker.TrackBeforeWrite("b.txt");

            await Assert.That(tracker.Count).IsEqualTo(2);
            await Assert.That(tracker.PreImages[0].Path).IsEqualTo("b.txt");
            await Assert.That(tracker.PreImages[1].Path).IsEqualTo("a.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TrackBeforeWrite_RejectsUnsafePathsWithoutTouchingTheDisk()
    {
        string root = NewWorkspace();
        string outside = NewWorkspace();
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var tracker = new FileHistoryTracker(root, store);

            var absolute = tracker.TrackBeforeWrite(Path.Combine(root, "file.txt"));
            var traversal = tracker.TrackBeforeWrite(Path.Combine("sub", "..", "file.txt"));
            var blank = tracker.TrackBeforeWrite("   ");

            await Assert.That(absolute.IsRejected).IsTrue();
            await Assert.That(absolute.PreImage).IsNull();
            await Assert.That(absolute.Reason!).Contains("relative to the workspace root");
            await Assert.That(traversal.IsRejected).IsTrue();
            await Assert.That(traversal.Reason!).Contains("parent-directory segment");
            await Assert.That(blank.IsRejected).IsTrue();
            await Assert.That(blank.Reason!).Contains("empty");
            await Assert.That(tracker.Count).IsEqualTo(0);
            await Assert.That(Directory.Exists(Path.Combine(root, ".rewind", "session-1", "objects"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Test]
    public async Task TrackBeforeWrite_RejectsSymlinkedPath()
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
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            var store = new RewindBlobStore(Path.Combine(root, ".rewind", "session-1"));
            var tracker = new FileHistoryTracker(root, store);

            var result = tracker.TrackBeforeWrite("link.txt");

            await Assert.That(result.IsRejected).IsTrue();
            await Assert.That(result.Reason!).Contains("symlink");
            await Assert.That(tracker.Count).IsEqualTo(0);
            await Assert.That(File.ReadAllText(target)).IsEqualTo("outside");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }
}
