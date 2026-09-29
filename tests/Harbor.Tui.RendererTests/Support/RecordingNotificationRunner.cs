namespace Harbor.Tui.RendererTests.Support;

using Harbor.Abstractions.Notifications;

/// <summary>
///     Test double for <see cref="INotificationProcessRunner" /> — records every
///     launch instead of starting one.
/// </summary>
/// <remarks>
///     <para>
///         Issue #665. Before the seam existed there was nothing to inject, so
///         the only way to exercise a backend was to actually shell out to
///         <c>notify-send</c> / <c>osascript</c> / <c>msg</c>: the old
///         <c>Notifications_FullEventSweep_DoesNotThrow</c> really did try to
///         pop toasts on whatever machine ran it.
///     </para>
///     <para>
///         Records the token as well as the argv, because "the backend forwards
///         cancellation" is a claim about the wiring and not about the argv —
///         and it is exactly the claim that was impossible to make while each
///         backend owned its own process.
///     </para>
/// </remarks>
public sealed class RecordingNotificationRunner : INotificationProcessRunner
{
    private readonly List<Launch> _launches = [];

    /// <summary>One recorded call, in order.</summary>
    /// <param name="FileName">Executable the backend asked for.</param>
    /// <param name="Arguments">Argument vector, as separate entries.</param>
    /// <param name="CancellationToken">The token the backend passed down.</param>
    public sealed record Launch(
        string FileName,
        IReadOnlyList<string> Arguments,
        CancellationToken CancellationToken);

    /// <summary>Everything launched so far, in call order.</summary>
    public IReadOnlyList<Launch> Launches => _launches;

    /// <summary>Number of launches recorded.</summary>
    public int Count => _launches.Count;

    /// <summary>The single recorded launch.</summary>
    /// <exception cref="InvalidOperationException">Anything other than exactly one launch.</exception>
    public Launch Single
    {
        get
        {
            if (_launches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one launch, recorded {_launches.Count}.");
            }

            return _launches[0];
        }
    }

    /// <inheritdoc />
    public void Run(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        _launches.Add(new Launch(fileName, [.. arguments], cancellationToken));
    }
}
