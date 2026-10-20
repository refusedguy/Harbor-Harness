namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     The screen wait ended because the CHILD is gone, not because the bound
///     expired: the process exited, the reader saw EOF (so the child's output
///     is final), and the pump wrote all of it to the grid. No further repaint
///     can arrive, so the screen condition the scenario was waiting for can
///     never become true. The wait was already decided when the child died
///     (#2001).
///     <para />
///     It derives from <see cref="TimeoutException" /> on purpose: the wait
///     DID fail to produce the screen, and the best-effort loops of the
///     scenarios (<c>catch (TimeoutException)</c>, "the marker did not
///     appear, try the next tick") must keep treating that as the same
///     family of failure. The type adds the part a timeout cannot say: that
///     the process is gone, so waiting longer is not a fix.
/// </summary>
public sealed class PtyChildExitedException : TimeoutException
{
    /// <summary>Creates the error with the failure message.</summary>
    /// <param name="message">What was expected, and why it can never arrive.</param>
    public PtyChildExitedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the error with a message and an inner cause.</summary>
    /// <param name="message">What was expected, and why it can never arrive.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public PtyChildExitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the error for deserialization paths.</summary>
    public PtyChildExitedException()
    {
    }
}
