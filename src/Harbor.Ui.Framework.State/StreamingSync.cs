namespace Harbor.Ui.Framework.State;

/// <summary>
///     Flush policy shared by every UI reducer that accumulates streaming
///     deltas into a display buffer.
/// </summary>
/// <remarks>
///     <para>
///         Deltas land in a <see cref="ChunkedBuffer" /> (O(1) append). The
///         synced prefix string is rebuilt only when <see cref="ShouldFlush" />
///         says so:
///     </para>
///     <para>
///         Below <see cref="ExactThresholdChars" /> synced characters every
///         delta is concatenated immediately, so short messages (and every
///         test/replay that inspects buffers mid-stream) stay byte-exact.
///         Beyond that, a flush happens once pending characters reach
///         min(<see cref="MaxLagChars" />, max(<see cref="MinLagChars"/>, synced / 8)),
///         which caps both the visible lag (≤ 2 048 chars) and the total copy
///         work at ~9× the final message length instead of O(N²).
///     </para>
/// </remarks>
public static class StreamingSync
{
    /// <summary>Synced-prefix length under which deltas flush immediately.</summary>
    public const int ExactThresholdChars = 256;

    /// <summary>Minimum pending-character count that triggers an overdue flush.</summary>
    private const int MinLagChars = 256;

    /// <summary>Maximum visible lag between the chunk tail and the synced string.</summary>
    private const int MaxLagChars = 2048;

    /// <summary>
    ///     Whether the caller must materialize <paramref name="pendingLength" />
    ///     characters onto a synced prefix of <paramref name="syncedLength" />
    ///     characters. Callers guarantee <paramref name="pendingLength" /> &gt; 0;
    ///     the method still answers <see langword="false" /> for zero input.
    /// </summary>
    public static bool ShouldFlush(int syncedLength, int pendingLength)
    {
        if (pendingLength <= 0)
            return false;
        if (syncedLength < ExactThresholdChars)
            return true;
        int lagLimit = Math.Min(MaxLagChars, Math.Max(MinLagChars, syncedLength >> 3));
        return pendingLength >= lagLimit;
    }

    /// <summary>
    ///     Join a synced prefix with pending chunks. Returns
    ///     <paramref name="prefix" /> itself when nothing is pending (no allocation).
    /// </summary>
    public static string Concat(string prefix, ChunkedBuffer pending)
        => pending.Length == 0 ? prefix : prefix + pending.Materialize();
}
