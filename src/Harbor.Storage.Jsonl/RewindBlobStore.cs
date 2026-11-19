// RewindBlobStore.cs — content-addressed pre-image blobs for #2659 (U01/U04).
//
// The store is deliberately dependency-free: it writes one object per distinct
// pre-image under <sessionDirectory>/objects/<sha256> and never rewrites an
// object that is already there. A null pre-image means the file did not exist
// before the agent first touched it; no object is written for that case, and
// the tombstone itself is carried by later append-only metadata.

using System.Security.Cryptography;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Content-addressed blob store for file pre-images. Object names are the
///     upper-case SHA-256 hex of the bytes they hold, so a write is idempotent:
///     identical pre-images share one object and an object already on disk is
///     left untouched. The caller supplies the per-session rewind directory,
///     which keeps session isolation outside this type.
/// </summary>
public sealed class RewindBlobStore
{
    private const string ObjectsDirectoryName = "objects";
    private readonly string _objectsDirectory;

    /// <summary>Creates a store rooted at the given session rewind directory.</summary>
    public RewindBlobStore(string sessionRewindDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRewindDirectory);
        _objectsDirectory = Path.Combine(sessionRewindDirectory, ObjectsDirectoryName);
    }

    /// <summary>Returns the upper-case SHA-256 hex of the given content.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    ///     Stores a pre-image and returns its object hash. Passing
    ///     <see langword="null"/> records a tombstone and returns
    ///     <see langword="null"/>: no object is written for a file that did
    ///     not exist before the first write.
    /// </summary>
    public string? Store(byte[]? content)
    {
        if (content is null)
            return null;

        string hash = ComputeHash(content);
        string objectPath = Path.Combine(_objectsDirectory, hash);
        if (!File.Exists(objectPath))
        {
            Directory.CreateDirectory(_objectsDirectory);
            File.WriteAllBytes(objectPath, content);
        }
        return hash;
    }

    /// <summary>
    ///     Reads the object named by the given hash, or <see langword="null"/>
    ///     when the hash is malformed or the object is absent. A malformed hash
    ///     is rejected before any filesystem call, so a stored value cannot be
    ///     used to escape the objects directory.
    /// </summary>
    public byte[]? Read(string? hash)
    {
        if (!IsValidHash(hash))
            return null;
        string objectPath = Path.Combine(_objectsDirectory, hash!);
        return File.Exists(objectPath) ? File.ReadAllBytes(objectPath) : null;
    }

    /// <summary>True when the value is a 64-character upper-case SHA-256 hex string.</summary>
    public static bool IsValidHash(string? hash)
    {
        if (hash is null || hash.Length != 64)
            return false;
        for (int i = 0; i < hash.Length; i++)
        {
            char c = hash[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}
