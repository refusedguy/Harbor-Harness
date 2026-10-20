using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     An opaque freshness token for a file's on-disk content (#1757) — the
///     compare-and-swap half of Harbor's filesystem observation policy.
/// </summary>
/// <remarks>
///     <para>
///         The token is the model's handle on "the file I saw": <c>read</c>
///         reports the token of the bytes it read, and <c>write</c>/<c>edit</c>
///         take it back as <c>expectedVersion</c>. A write whose
///         <c>expectedVersion</c> no longer matches the file on disk is
///         REJECTED with a diagnostic instead of silently clobbering whatever
///         changed the file in the meantime.
///     </para>
///     <para>
///         <b>Representation.</b> <c>sha256:</c> plus the first 16 hex
///         characters of the SHA-256 digest of the file's raw bytes — 64 bits
///         of the digest, short enough for a model to copy verbatim. The digest
///         covers the BYTES, not the decoded text: the token moves only when
///         the bytes move. Content hash rather than mtime+size because a
///         timestamp is not a fact about the content — <c>touch</c>, a
///         <c>tar -x</c> that preserves mtimes, or a filesystem with coarse
///         timestamp granularity all make mtime+size report "unchanged" for a
///         file that changed (or "changed" for one that did not). A hash of the
///         bytes has neither failure mode; the price is one sequential read of
///         the file, which the tools already pay for the file they are
///         touching.
///     </para>
///     <para>
///         <b>Comparison.</b> The digest the caller observed is compared
///         against the CURRENT digest as a PREFIX, so a caller that hands back
///         the full 64 hex characters is accepted as well as one that hands back
///         the short token. A caller that hands back a shorter prefix has
///         weakened the check to that many bits — that is the caller's choice,
///         never the tool's.
///     </para>
///     <para>
///         This is the CAS half only. The "you must read the file before you
///         write it" gate needs a per-session record of what was observed,
///         which Harbor has no home for yet; see the issue for the seam.
///     </para>
/// </remarks>
internal static class FileVersion
{
    /// <summary>The algorithm marker every token starts with.</summary>
    internal const string Prefix = "sha256:";

    /// <summary>Hex characters of the digest a token carries (64 bits).</summary>
    internal const int TokenHexLength = 16;

    /// <summary>Text a tool result carries the token in.</summary>
    private const string Annotation = "[file-version: ";

    /// <summary>Streaming buffer for the digest pass.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>Characters per chunk when digesting text (keeps the transient byte[] bounded).</summary>
    private const int ChunkChars = 32 * 1024;

    /// <summary>
    ///     Digest of the file's current bytes, or <c>null</c> when the file does
    ///     not exist. Streamed, so the memory cost does not grow with the file.
    /// </summary>
    /// <param name="path">The file to digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lowercase hex digest, or <c>null</c> when the file is absent.</returns>
    internal static async Task<string?> ComputeDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;

                hash.AppendData(buffer, 0, read);
            }

            return ToHex(hash.GetHashAndReset());
        }
        catch (FileNotFoundException)
        {
            // The file is gone — "absent" is a fact the caller can be told.
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Digest of a byte sequence — what a write is about to put on disk.
    /// </summary>
    /// <param name="bytes">The bytes to digest.</param>
    /// <returns>The lowercase hex digest.</returns>
    internal static string ComputeDigest(ReadOnlySpan<byte> bytes) =>
        ToHex(SHA256.HashData(bytes));

    /// <summary>
    ///     Digest of text encoded with <paramref name="encoding" /> — the bytes a
    ///     write of that text will produce. Chunked so a multi-megabyte file
    ///     does not cost a second multi-megabyte allocation.
    /// </summary>
    /// <param name="text">The text to digest.</param>
    /// <param name="encoding">The encoding the write will use.</param>
    /// <returns>The lowercase hex digest.</returns>
    internal static string ComputeDigest(ReadOnlySpan<char> text, Encoding encoding)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int offset = 0;
        while (offset < text.Length)
        {
            int remaining = text.Length - offset;
            int length = Math.Min(ChunkChars, remaining);
            if (length < remaining && char.IsHighSurrogate(text[offset + length - 1]))
            {
                // Never split a surrogate pair across chunks: the two halves
                // would encode as two replacement characters instead of one
                // code point, and the digest would stop matching the bytes the
                // write actually produces.
                length--;
            }

            ReadOnlySpan<char> chunk = text.Slice(offset, length);

            // The span-based GetBytes needs a destination, so the encoded
            // chunk goes through a pooled buffer: a multi-megabyte file would
            // otherwise cost one throwaway byte[] per chunk.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(encoding.GetByteCount(chunk));
            try
            {
                int written = encoding.GetBytes(chunk, buffer);
                hash.AppendData(buffer, 0, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            offset += length;
        }

        return ToHex(hash.GetHashAndReset());
    }

    /// <summary>
    ///     The short token a tool result carries for a digest.
    /// </summary>
    /// <param name="digest">The lowercase hex digest.</param>
    /// <returns>The token, e.g. <c>sha256:0123456789abcdef</c>.</returns>
    internal static string Token(string digest) =>
        Prefix + (digest.Length <= TokenHexLength ? digest : digest[..TokenHexLength]);

    /// <summary>
    ///     The annotation a tool result appends so the model can copy the token
    ///     into a later <c>expectedVersion</c>.
    /// </summary>
    /// <param name="digest">The lowercase hex digest.</param>
    /// <returns>The annotation, e.g. <c>[file-version: sha256:0123456789abcdef]</c>.</returns>
    internal static string Annotate(string digest) => $"{Annotation}{Token(digest)}]";

    /// <summary>
    ///     Normalize the <c>expectedVersion</c> argument into the hex prefix to
    ///     compare against the current digest.
    /// </summary>
    /// <param name="raw">The raw argument value; the algorithm prefix is optional.</param>
    /// <returns>The lowercase hex prefix, or a failure explaining what a valid token looks like.</returns>
    internal static Result<string> NormalizeExpected(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Failure<string>("'expectedVersion' must not be empty.");

        string value = raw.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            value = value[Prefix.Length..];

        if (value.Length == 0)
            return Result.Failure<string>("'expectedVersion' must not be empty.");

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return Result.Failure<string>(
                    $"'expectedVersion' is not a file version token: \"{raw}\". " +
                    "Expected the [file-version: sha256:…] value reported by read, write or edit.");
        }

        return Result.Success(value.ToLowerInvariant());
    }

    /// <summary>
    ///     Whether the current digest still starts with the expected hex prefix.
    /// </summary>
    /// <param name="expectedHex">The normalized expected prefix.</param>
    /// <param name="currentDigest">The file's current lowercase hex digest.</param>
    /// <returns><c>true</c> when the file is still the version the caller observed.</returns>
    internal static bool Matches(string expectedHex, string currentDigest) =>
        currentDigest.StartsWith(expectedHex, StringComparison.Ordinal);

    /// <summary>
    ///     The compare-and-swap gate: does the file on disk still hold the
    ///     version the caller says it observed?
    /// </summary>
    /// <param name="path">The file about to be written (for the diagnostic).</param>
    /// <param name="expectedRaw">The raw <c>expectedVersion</c> argument; blank means "no constraint".</param>
    /// <param name="currentDigest">The file's current digest, or <c>null</c> when the file is absent.</param>
    /// <returns>Success when the write may proceed, or a failure carrying the rejection diagnostic.</returns>
    internal static Result Check(string path, string? expectedRaw, string? currentDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedRaw))
            return Result.Success();

        Result<string> normalized = NormalizeExpected(expectedRaw);
        if (normalized.IsFailure)
            return normalized.ConvertFailure();

        string expectedHex = normalized.Value;
        if (currentDigest is null)
            return Result.Failure(MissingMessage(path, expectedHex));

        if (!Matches(expectedHex, currentDigest))
            return Result.Failure(ChangedMessage(path, expectedHex, currentDigest));

        return Result.Success();
    }

    /// <summary>
    ///     Diagnostic for a write rejected because the file moved on.
    /// </summary>
    /// <param name="path">The file that changed.</param>
    /// <param name="expectedHex">The version the caller observed.</param>
    /// <param name="currentDigest">The file's current digest.</param>
    /// <returns>The message the tool hands back to the model.</returns>
    internal static string ChangedMessage(string path, string expectedHex, string currentDigest) =>
        $"Rejected: \"{path}\" changed on disk since you read it " +
        $"(your version {Token(expectedHex)}, current version {Token(currentDigest)}). " +
        "Something outside this session changed the file after you observed it — another " +
        "process, a git checkout, or an editor. Re-read the file and re-apply your change " +
        "against the current content; retrying with the same expectedVersion will keep " +
        "failing. If the change was expected and you want to overwrite it anyway, " +
        "re-issue the call without expectedVersion.";

    /// <summary>
    ///     Diagnostic for a write rejected because the file is gone.
    /// </summary>
    /// <param name="path">The file that disappeared.</param>
    /// <param name="expectedHex">The version the caller observed.</param>
    /// <returns>The message the tool hands back to the model.</returns>
    internal static string MissingMessage(string path, string expectedHex) =>
        $"Rejected: \"{path}\" no longer exists (your version {Token(expectedHex)}). " +
        "It was deleted after you read it, so writing now would recreate a file whose " +
        "current state you never saw. Confirm the deletion was intended; if it was, " +
        "re-issue the call without expectedVersion.";

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
