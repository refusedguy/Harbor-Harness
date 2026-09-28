using Harbor.Abstractions.Models;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Images staged for the next user turn (issue #386). <c>/attach</c> pushes
///     here; the prompt pipeline drains the stash when the user submits.
/// </summary>
/// <remarks>
///     Frame-thread-only: the slash command that fills it and the submit path
///     that empties it both run on the CellForge frame loop, so no lock is
///     needed. Draining is destructive — a staged image belongs to exactly one
///     turn, and re-sending it on every subsequent prompt would silently bill
///     the user for the same image forever.
/// </remarks>
internal sealed class ImageAttachmentStash
{
    private readonly List<ImageAttachment> _pending = [];

    /// <summary>Staged images, oldest first (for the composer/status readout).</summary>
    public IReadOnlyList<ImageAttachment> Pending => _pending;

    /// <summary>Number of staged images.</summary>
    public int Count => _pending.Count;

    /// <summary>
    ///     Stage one image. Returns false (and stages nothing) when the stash is
    ///     already full, so a user cannot stage an unbounded payload by
    ///     repeating <c>/attach</c>.
    /// </summary>
    public bool TryStage(ImageAttachment image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (_pending.Count >= MaxPending)
            return false;

        _pending.Add(image);
        return true;
    }

    /// <summary>
    ///     Take everything staged, emptying the stash. Returns <see langword="null" />
    ///     when nothing is pending so the caller keeps the plain text-only prompt path.
    /// </summary>
    public IReadOnlyList<ImageAttachment>? Drain()
    {
        if (_pending.Count == 0)
            return null;

        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    /// <summary>Drop everything staged (abort / session switch).</summary>
    public void Clear() => _pending.Clear();

    /// <summary>
    ///     Cap on staged images. Providers bill per image and most vision
    ///     endpoints reject more than ~20 per request; 4 keeps a turn readable
    ///     and the payload reviewable.
    /// </summary>
    public const int MaxPending = 4;

    /// <summary>One-line summary for the timeline readout, or null when empty.</summary>
    public string? Describe() => _pending.Count == 0
        ? null
        : string.Join(", ", _pending.Select(DescribeOne));

    private static string DescribeOne(ImageAttachment image)
    {
        string name = Path.GetFileName(image.Path);
        return $"{name} ({image.DimensionsLabel})";
    }
}
