namespace Harbor.Tui.ConsoleEx.Rendering;

/// <summary>
/// Production backend writing frames to stdout. The stream handle is taken
/// once (never through <c>Console.Write</c>, which locks and auto-flushes per
/// call) and reused for the lifetime of the renderer.
/// </summary>
public sealed class StdoutBackend : ITerminalBackend
{
    private Stream? _stdout;

    private Stream Stdout => _stdout ??= Console.OpenStandardOutput();

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        await Stdout.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await Stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
