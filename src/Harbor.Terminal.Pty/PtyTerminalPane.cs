using Harbor.Abstractions.Terminal;

namespace Harbor.Terminal.Pty;

/// <summary>
///     Adapts <see cref="PtyProcess" /> to the Domain <see cref="ITerminalPane" />
///     contract. The only reason this type exists is that a Domain contract cannot
///     name an Infrastructure class: returning <see cref="PtyProcess" /> from
///     <see cref="ITerminalPaneLauncher" /> would have leaked the process handle
///     straight back into Presentation, which is the coupling #672 removes.
/// </summary>
internal sealed class PtyTerminalPane(PtyProcess process) : ITerminalPane
{
    private readonly PtyProcess _process = process;

    /// <inheritdoc />
    public int Pid => _process.Pid;

    /// <inheritdoc />
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived
    {
        add => _process.OutputReceived += OnOutputReceived;
        remove => _process.OutputReceived -= OnOutputReceived;
    }

    /// <inheritdoc />
    public event EventHandler? OutputClosed
    {
        add => _process.OutputClosed += OnOutputClosed;
        remove => _process.OutputClosed -= OnOutputClosed;
    }

    /// <inheritdoc />
    public void WriteLine(string line) => _process.WriteLine(line);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _process.DisposeAsync();

    private void OnOutputReceived(object? sender, PtyOutputEventArgs e)
        => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(e.Data));

    private void OnOutputClosed(object? sender, EventArgs e)
        => OutputClosed?.Invoke(this, EventArgs.Empty);
}
