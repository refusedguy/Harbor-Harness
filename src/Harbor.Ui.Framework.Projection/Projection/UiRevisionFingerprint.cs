namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Allocation-free change signal carried by <see cref="UiScreenModel.StateRevision" />
///     (issue #2634). Replaces the interpolated <see langword="string" /> that
///     <see cref="DefaultUiProjector.Project" /> materialised on every screen
///     rebuild, once per streaming token at 1000+ tokens/sec.
/// </summary>
/// <remarks>
///     A <c>readonly record struct</c> supplies value equality, including
///     <see cref="IEquatable{T}" />, without boxing or a heap allocation.
///     <see cref="ToString" /> keeps the former human-readable form for
///     diagnostics; it runs on demand, never on the projection hot path.
/// </remarks>
/// <param name="Revision">State revision; stored first for stale-drop ordering (#94).</param>
/// <param name="LinesLength">Number of committed transcript lines.</param>
/// <param name="IsStreaming">Whether the state is currently streaming.</param>
/// <param name="TextLength">Visible text length, including unflushed pending deltas.</param>
/// <param name="ThinkLength">Visible thinking length, including unflushed pending deltas.</param>
public readonly record struct UiRevisionFingerprint(
    long Revision,
    int LinesLength,
    bool IsStreaming,
    int TextLength,
    int ThinkLength)
{
    /// <summary>
    ///     Renders the same <c>revision:lines:streaming:text:think</c> form the
    ///     previous <see langword="string" /> StateRevision exposed. Diagnostic
    ///     only: callers on the hot path compare the value type directly.
    /// </summary>
    public override string ToString() =>
        $"{Revision}:{LinesLength}:{IsStreaming}:{TextLength}:{ThinkLength}";
}
