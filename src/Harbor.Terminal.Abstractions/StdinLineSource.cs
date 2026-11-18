using System.Threading.Channels;
using CSharpFunctionalExtensions;

namespace Harbor.Terminal.Abstractions;

/// <summary>
///     Single owner of a renderer's standard input (#2639, #2640). One dedicated
///     pump is the only caller of <see cref="Console.ReadLine"/>. Tests inject their
///     own reader. Lines reach consumers through an unbounded channel.
///     Cancelling one <see cref="ReadLineAsync"/> abandons only that consumer's
///     wait, never the reader itself, so the line the user types next reaches
///     the following caller instead of being stolen by an orphaned reader.
/// </summary>
/// <remarks>
///     Shared by the CellForge and AnsiPlain renderers so both honour the
///     <see cref="ITuiRenderer.ReadLineAsync"/> cancellation contract with one
///     implementation instead of two copies (#2640).
/// </remarks>
public sealed class StdinLineSource : IDisposable
{
    private readonly Func<string?> _readLine;
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly object _gate = new();
    private Task? _pump;
    private int _disposed;

    public StdinLineSource(Func<string?>? readLine = null)
    {
        _readLine = readLine ?? new Func<string?>(Console.ReadLine);
    }

    /// <summary>Starts the one pump on first use, so a renderer that never
    /// reads a line never parks a thread on stdin.</summary>
    private void EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_pump is null && Volatile.Read(ref _disposed) == 0)
            {
                _pump = Task.Factory.StartNew(
                    Pump,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }
    }

    private void Pump()
    {
        ChannelWriter<string?> writer = _lines.Writer;
        try
        {
            while (true)
            {
                string? line = _readLine();
                if (!writer.TryWrite(line))
                {
                    // Disposed while the read was in flight.
                    break;
                }

                if (line is null)
                {
                    // EOF: the null item carries absence to current and future
                    // callers, then the channel closes (#589).
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // stdin went away; treat as EOF rather than faulting consumers.
            writer.TryWrite(null);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>Reads one line, honouring <paramref name="ct"/>. Cancellation
    /// only detaches this caller; the single pump keeps owning stdin so no
    /// later caller races a second reader for the next line.</summary>
    public async ValueTask<Maybe<string>> ReadLineAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsurePumpStarted();

        try
        {
            string? line = await _lines.Reader.ReadAsync(ct).ConfigureAwait(false);
            // EOF is absence, not failure: Maybe.None, not Success("") (#589).
            return Maybe.From(line);
        }
        catch (ChannelClosedException)
        {
            // The pump finished with no line pending, on EOF or Dispose.
            return Maybe<string>.None;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lines.Writer.TryComplete();
    }
}
