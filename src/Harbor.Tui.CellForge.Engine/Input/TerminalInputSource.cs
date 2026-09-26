using System.Threading.Channels;
using Harbor.Tui.CellForge.Parsing;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Raw stdin pipeline (design §5.1): a dedicated long-running reader thread
/// pulls byte chunks from the stream, feeds them through the shared
/// <see cref="EscapeSequenceParser"/> and publishes typed events into an
/// unbounded single-reader channel.
///
/// Timer policies run ON the reader thread only (no cross-thread parser
/// mutation): ESC-flush at chunk boundaries (§2.4) and the paste watchdog
/// (§4.2) are applied as due-deadline checks between reads.
/// </summary>
public sealed class TerminalInputSource : IDisposable
{
    private readonly Stream _stdin;
    private readonly TerminalInputSourceOptions _options;
    private readonly Channel<InputEvent> _channel;
    private readonly object _gate = new();

    /// <summary>The parser feeding this source. Exposed so capability probing
    /// (phase 1) can intercept CapabilityEvents before UI dispatch.</summary>
    public EscapeSequenceParser Parser { get; } = new();

    public ChannelReader<InputEvent> Events => _channel.Reader;

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private long _escDeadlineTicks = long.MaxValue;
    private long _pasteDeadlineTicks = long.MaxValue;
    private long _nextResizePollTicks = long.MaxValue;

    private int _lastWidth = -1;
    private int _lastHeight = -1;

    public TerminalInputSource(
        Stream stdin,
        TerminalInputSourceOptions? options = null)
    {
        _stdin = stdin ?? throw new ArgumentNullException(nameof(stdin));
        _options = options ?? TerminalInputSourceOptions.Default;
        _channel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Starts the reader loop on a dedicated thread. Returns a task
    /// completing on EOF, cancellation or stream failure.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("TerminalInputSource already started.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _runTask = Task.Factory.StartNew(
            () => RunLoop(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        return _runTask;
    }

    private async Task RunLoop(CancellationToken token)
    {
        var buffer = new byte[_options.ReadBufferSize];
        try
        {
            InitializeResizeBaseline();
            ArmTimers();
            // Exactly ONE in-flight read at any time: WhenAny-timer iterations
            // reuse the pending task instead of issuing a competing one (an
            // orphaned read would steal the next chunk from the live one).
            Task<int>? readTask = null;
            while (!token.IsCancellationRequested)
            {
                readTask ??= _stdin.ReadAsync(buffer.AsMemory(), token).AsTask();
                var wake = NextWakeIn();
                if (wake is { } delay)
                {
                    var completed = await Task.WhenAny(readTask, Task.Delay(delay, token)).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        ApplyDueTimers();
                        continue;
                    }
                }

                var bytesRead = await readTask.ConfigureAwait(false);
                readTask = null;
                if (bytesRead <= 0)
                {
                    break; // EOF
                }

                lock (_gate)
                {
                    Parser.Parse(buffer.AsSpan(0, bytesRead));
                    ArmTimers();
                    DrainParserToChannel();
                }

                PollResize(force: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (ObjectDisposedException)
        {
            // Stream torn down underneath us during shutdown.
        }
        catch (IOException)
        {
            // Stdin vanished (pipe/tty closed) — EOF-equivalent teardown.
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    // ── Timer policy (reader-thread-only access) ──────────────────────────

    /// <summary>Pure read of current deadlines — NEVER re-arms them, otherwise
    /// each wake would push its own deadline out and timers would never fire.</summary>
    private TimeSpan? NextWakeIn()
    {
        long? earliest = null;

        void Consider(long deadlineTicks)
        {
            if (deadlineTicks == long.MaxValue)
            {
                return;
            }

            earliest = earliest is { } current ? Math.Min(current, deadlineTicks) : deadlineTicks;
        }

        lock (_gate)
        {
            Consider(_escDeadlineTicks);
            Consider(_pasteDeadlineTicks);
            Consider(_nextResizePollTicks);
        }

        return earliest is null ? null : TimeSpan.FromMilliseconds(Math.Max(1, earliest.Value - Environment.TickCount64));
    }

    private void ArmTimers() => ArmTimers(Environment.TickCount64);

    private void ArmTimers(long now)
    {
        var escEnabled = _options.EscFlushTimeout > TimeSpan.Zero;
        _escDeadlineTicks = escEnabled && Parser.State == ParserState.Escape
            ? now + (long)_options.EscFlushTimeout.TotalMilliseconds
            : long.MaxValue;

        var pasteEnabled = _options.PasteAbortTimeout > TimeSpan.Zero;
        _pasteDeadlineTicks = pasteEnabled && Parser.IsAwaitingPasteClose
            ? now + (long)_options.PasteAbortTimeout.TotalMilliseconds
            : long.MaxValue;

        if (_options.SizeProvider is not null && _options.ResizePollInterval is { } interval)
        {
            _nextResizePollTicks = now + (long)interval.TotalMilliseconds;
        }
        else
        {
            _nextResizePollTicks = long.MaxValue;
        }
    }

    private void ApplyDueTimers()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            var dirty = false;
            if (_escDeadlineTicks != long.MaxValue && now >= _escDeadlineTicks)
            {
                Parser.FlushPendingEscape();
                dirty = true;
            }

            if (_pasteDeadlineTicks != long.MaxValue && now >= _pasteDeadlineTicks)
            {
                Parser.AbortPendingPaste();
                dirty = true;
            }

            if (dirty)
            {
                DrainParserToChannel();
            }

            // Re-arm from post-apply state so satisfied deadlines clear.
            ArmTimers(now);
        }

        PollResize(force: true);
    }

    // ── Resize polling (§6 matrix: polling baseline for CE-0) ─────────────

    private void InitializeResizeBaseline()
    {
        if (_options.SizeProvider is null)
        {
            return;
        }

        try
        {
            (_lastWidth, _lastHeight) = _options.SizeProvider();
        }
        catch (IOException)
        {
            // No real terminal behind the stream (tests/pipes).
        }
    }

    private void PollResize(bool force)
    {
        var provider = _options.SizeProvider;
        if (provider is null || _options.ResizePollInterval is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && (_nextResizePollTicks == long.MaxValue || now < _nextResizePollTicks))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var (width, height) = provider();
                if ((width != _lastWidth || height != _lastHeight) && width > 0 && height > 0)
                {
                    _lastWidth = width;
                    _lastHeight = height;
                    _channel.Writer.TryWrite(InputEvent.FromResize(new ResizeSignal(width, height)));
                }
            }
            catch (IOException)
            {
                // Size probe unavailable mid-session — keep polling.
            }

            _nextResizePollTicks = now + (long)(_options.ResizePollInterval?.TotalMilliseconds ?? 0);
        }
    }

    private void DrainParserToChannel()
    {
        while (Parser.TryTakeEvent(out var evt))
        {
            _channel.Writer.TryWrite(evt);
        }
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
                // The run task is expected to end via OCE/EOF/IOException; a
                // faulted task (stream implementation bug) must not break the
                // caller's finally block.
                _ = _runTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by a concurrent Dispose.
            }
            catch (AggregateException)
            {
                // Faulted reader task — teardown still proceeds to CTS disposal.
            }

            _cts.Dispose();
            _cts = null;
        }
    }
}
