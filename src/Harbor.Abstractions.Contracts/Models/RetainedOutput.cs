using System.Text;

namespace Harbor.Abstractions.Models;

/// <summary>
///     A bounded retained copy of one tool output (issue #2672, F06).
/// </summary>
/// <remarks>
///     <para>
///         An inline preview is not the whole output: once the preview is cut,
///         the rest has to stay readable in bounded chunks. This value is the
///         shape both sides agree on — the producer knows how much was kept and
///         how much was dropped, and a consumer can walk the kept bytes without
///         loading the original output again.
///     </para>
///     <para>
///         The limits are named here rather than repeated in every tool: one
///         output keeps at most <see cref="MaxRetainedBytes" />, and a read
///         hands back at most <see cref="ReadChunkBytes" /> unless the caller
///         asks for a smaller slice.
///     </para>
/// </remarks>
public sealed record RetainedOutput
{
    /// <summary>The maximum number of UTF-8 bytes retained for one output.</summary>
    public const int MaxRetainedBytes = 1024 * 1024;

    /// <summary>The default number of UTF-8 bytes returned by one read.</summary>
    public const int ReadChunkBytes = 8 * 1024;

    private readonly byte[] _utf8;

    /// <summary>
    ///     Wrap an already-bounded output. Use <see cref="Create" /> when the
    ///     text may exceed the retention limit.
    /// </summary>
    /// <param name="toolCallId">The tool call that produced the output.</param>
    /// <param name="text">The retained text.</param>
    /// <param name="totalBytes">The size of the original output, in bytes.</param>
    public RetainedOutput(string toolCallId, string text, long totalBytes)
    {
        ToolCallId = toolCallId ?? string.Empty;
        Text = text ?? string.Empty;
        _utf8 = Encoding.UTF8.GetBytes(Text);

        // A reported total smaller than what is actually kept would hide the
        // truncation; the kept bytes are the lower bound for the total.
        TotalBytes = Math.Max(totalBytes, _utf8.Length);
    }

    /// <summary>The tool call that produced the output.</summary>
    public string ToolCallId { get; }

    /// <summary>The retained text.</summary>
    public string Text { get; }

    /// <summary>The size of the original output, in bytes.</summary>
    public long TotalBytes { get; }

    /// <summary>The number of UTF-8 bytes actually retained.</summary>
    public int RetainedBytes => _utf8.Length;

    /// <summary>True when the original output was larger than the retained copy.</summary>
    public bool IsTruncated => TotalBytes > _utf8.Length;

    /// <summary>
    ///     Retain at most <see cref="MaxRetainedBytes" /> of an output, without
    ///     cutting a multi-byte character in half.
    /// </summary>
    /// <param name="toolCallId">The tool call that produced the output.</param>
    /// <param name="text">The full output.</param>
    /// <param name="totalBytes">
    ///     The size of the full output in bytes. When it is not supplied, the
    ///     encoded length of <paramref name="text" /> is used.
    /// </param>
    /// <returns>The bounded retained output.</returns>
    public static RetainedOutput Create(string toolCallId, string text, long? totalBytes = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        long fullSize = totalBytes ?? bytes.Length;
        if (bytes.Length <= MaxRetainedBytes)
        {
            return new RetainedOutput(toolCallId, text ?? string.Empty, fullSize);
        }

        int end = MaxRetainedBytes;
        while (end > 0 && IsContinuationByte(bytes[end]))
        {
            end--;
        }

        return new RetainedOutput(
            toolCallId,
            Encoding.UTF8.GetString(bytes, 0, end),
            fullSize);
    }

    /// <summary>
    ///     Read one chunk at a UTF-8 byte offset. The chunk never ends inside a
    ///     multi-byte character.
    /// </summary>
    /// <param name="offsetBytes">Byte offset to start at.</param>
    /// <param name="countBytes">Maximum number of bytes to return.</param>
    /// <returns>The decoded chunk, or an empty string past the retained bytes.</returns>
    public string ReadChunk(int offsetBytes, int countBytes = ReadChunkBytes)
    {
        if (offsetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetBytes));
        }

        if (countBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(countBytes));
        }

        if (offsetBytes >= _utf8.Length)
        {
            return string.Empty;
        }

        int end = (int)Math.Min(_utf8.Length, (long)offsetBytes + countBytes);
        while (end > offsetBytes && end < _utf8.Length && IsContinuationByte(_utf8[end]))
        {
            end--;
        }

        return Encoding.UTF8.GetString(_utf8, offsetBytes, end - offsetBytes);
    }

    private static bool IsContinuationByte(byte value) => (value & 0xC0) == 0x80;
}
