namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #2659 U01/U04: an object is named by the SHA-256 of its bytes, writes
///     are idempotent, a null pre-image is a tombstone that writes no object,
///     and a malformed hash is rejected before any filesystem call.
/// </summary>
public class RewindBlobStoreTests
{
    private static string NewSessionDirectory() =>
        Path.Combine(Path.GetTempPath(), $"harbor-rewind-test-{Guid.NewGuid():N}");

    [Test]
    public async Task Store_NamesObjectByContentHash()
    {
        string root = NewSessionDirectory();
        try
        {
            var store = new RewindBlobStore(root);
            byte[] content = "pre-image"u8.ToArray();

            string hash = store.Store(content)!;

            await Assert.That(hash).IsEqualTo(RewindBlobStore.ComputeHash(content));
            await Assert.That(RewindBlobStore.IsValidHash(hash)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(root, "objects", hash))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Store_IsIdempotentAndDoesNotRewriteAnExistingObject()
    {
        string root = NewSessionDirectory();
        try
        {
            var store = new RewindBlobStore(root);
            byte[] content = "same bytes"u8.ToArray();
            string first = store.Store(content)!;
            string objectPath = Path.Combine(root, "objects", first);
            var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(objectPath, old);

            string second = store.Store(content)!;

            await Assert.That(second).IsEqualTo(first);
            await Assert.That(File.GetLastWriteTimeUtc(objectPath)).IsEqualTo(old);
            await Assert.That(Directory.GetFiles(Path.Combine(root, "objects")).Length).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Store_NullIsTombstoneAndWritesNoObject()
    {
        string root = NewSessionDirectory();
        try
        {
            var store = new RewindBlobStore(root);

            string? hash = store.Store(null);

            await Assert.That(hash).IsNull();
            await Assert.That(Directory.Exists(Path.Combine(root, "objects"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Read_ReturnsStoredBytesAndRejectsMalformedHash()
    {
        string root = NewSessionDirectory();
        try
        {
            var store = new RewindBlobStore(root);
            byte[] content = "restore me"u8.ToArray();
            string hash = store.Store(content)!;

            byte[]? read = store.Read(hash);

            await Assert.That(read).IsNotNull();
            await Assert.That(read!.SequenceEqual(content)).IsTrue();
            await Assert.That(store.Read("../outside")).IsNull();
            await Assert.That(store.Read(new string('0', 63) + "g")).IsNull();
            await Assert.That(store.Read("missing")).IsNull();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
