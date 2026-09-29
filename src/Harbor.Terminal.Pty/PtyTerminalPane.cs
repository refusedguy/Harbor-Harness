using Harbor.Abstractions.Terminal;

namespace Harbor.Terminal.Pty;

/// <summary>
///     Adapts <see cref="PtyProcess" /> to the Domain <see cref="ITerminalPane" />
///     contract. The only reason this type exists is that a Domain contract cannot
///     name an Infrastructure class: returning <see cref="PtyProcess" /> from
///     <see cref="ITerminalPaneLauncher" /> would have leaked the process handle
///     straight back into Presentation, which is the coupling #672 removes.
/// </summary>
internal sealed class PtyTerminalPane : ITerminalPane
{
    private readonly PtyProcess _process;

    /// <summary>Wrap a live PTY session in the Domain handle.</summary>
    /// <param name="process">The session to adapt. Not null: the launcher only constructs this on success.</param>
    public PtyTerminalPane(PtyProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        _process.OutputReceived += OnOutputReceived;
        _process.OutputClosed += OnOutputClosed;
    }

    /// <inheritdoc />
    public int Pid => _process.Pid;

    /// <summary>
    ///     A plain field-like event, subscribed through <see cref="_process" />'s own
    ///     events in the constructor. An add/remove accessor pair cannot work here:
    ///     forwarding one requires <c>OutputReceived?.Invoke</c> on the right-hand
    ///     side, which is not a legal use of an event outside += / -= (CS0079).
    /// </summary>
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    /// <inheritdoc cref="OutputReceived" />
    public event EventHandler? OutputClosed;

    /// <inheritdoc />
    public void WriteLine(string line) => _process.WriteLine(line);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _process.DisposeAsync();

    private void OnOutputReceived(object? sender, PtyOutputEventArgs e)
        => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(e.Data));

    private void OnOutputClosed(object? sender, EventArgs e)
        => OutputClosed?.Invoke(this, EventArgs.Empty);
}
