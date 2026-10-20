using Microsoft.Extensions.Logging;

namespace Harbor.TestKit;

/// <summary>
///     The shared record of what was logged, kept by the capturing loggers.
/// </summary>
/// <remarks>
///     <para>
///         The shape is a TUPLE, not a class, on purpose: eleven hand-written
///         loggers recorded <c>(LogLevel, message)</c> pairs, and the
///         assertions over them read
///         <c>logger.Entries.Contains((LogLevel.Warning, "…"))</c>. A class
///         would have made every one of those assertions a rewrite, and a
///         rewrite of an assertion is a chance to change what it proves.
///     </para>
///     <para>
///         The event id is kept alongside, in a second projection, because one
///         of the copies was indexed by event id — "give me everything logged
///         under id 41" — and that question has no answer without it.
///     </para>
/// </remarks>
public sealed class LogCapture
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, int EventId, string Message, object? State)> _entries = [];

    /// <summary>
    ///     Creates a capture that records everything from
    ///     <see cref="LogLevel.Trace" /> upwards.
    /// </summary>
    /// <param name="minimumLevel">
    ///     Entries below this level are not recorded. The level filter is a
    ///     RECORDING filter, not an <c>IsEnabled</c> one: the loggers answer
    ///     <c>IsEnabled</c> with <see langword="true" /> for every level, so
    ///     production code always takes its logging branch and the test
    ///     observes what it would really have written.
    /// </param>
    public LogCapture(LogLevel minimumLevel = LogLevel.Trace) => MinimumLevel = minimumLevel;

    /// <summary>Lowest level this capture records.</summary>
    public LogLevel MinimumLevel { get; }

    /// <summary>
    ///     Everything recorded, in order, as <c>(level, message)</c> pairs.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(e => (e.Level, e.Message))];
            }
        }
    }

    /// <summary>
    ///     Everything recorded, in order, with the event id each entry was
    ///     logged under.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, int EventId, string Message)> DetailedEntries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(e => (e.Level, e.EventId, e.Message))];
            }
        }
    }

    /// <summary>
    ///     Everything recorded, in order, with the STRUCTURED STATE each entry
    ///     was logged with.
    /// </summary>
    /// <remarks>
    ///     The state is what a source-generated <c>[LoggerMessage]</c> call
    ///     hands the logger: a list of name/value pairs. One of the hand-written
    ///     loggers existed to read those names, so they are kept here rather
    ///     than flattened away at capture time.
    /// </remarks>
    public IReadOnlyList<(LogLevel Level, int EventId, string Message, object? State)> RawEntries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    ///     Reads one named field out of a structured log state.
    /// </summary>
    /// <param name="state">The state a logger was handed.</param>
    /// <param name="name">The field name to look for.</param>
    /// <returns>
    ///     The field's value as a string, or <see langword="null" /> when the
    ///     state carries no such field.
    /// </returns>
    public static string? FieldOf(object? state, string name)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            return null;
        }

        foreach (KeyValuePair<string, object?> field in fields)
        {
            if (field.Key == name)
            {
                return field.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>Every recorded message, in order.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Select(e => e.Message)];
            }
        }
    }

    /// <summary>
    ///     The recorded messages at <see cref="LogLevel.Warning" /> and above.
    /// </summary>
    public IReadOnlyList<string> Warnings => MessagesAtOrAbove(LogLevel.Warning);

    /// <summary>
    ///     The recorded messages at <see cref="LogLevel.Error" /> and above.
    /// </summary>
    public IReadOnlyList<string> Errors => MessagesAtOrAbove(LogLevel.Error);

    /// <summary>
    ///     The recorded messages at the given level and above.
    /// </summary>
    /// <param name="level">Lowest level to include.</param>
    public IReadOnlyList<string> MessagesAtOrAbove(LogLevel level)
    {
        lock (_gate)
        {
            return [.. _entries.Where(e => e.Level >= level).Select(e => e.Message)];
        }
    }

    /// <summary>
    ///     Every message logged under the given event id, joined by newlines.
    /// </summary>
    /// <param name="eventId">Event id to filter by.</param>
    public string MessagesFor(int eventId)
    {
        lock (_gate)
        {
            return string.Join(
                "\n",
                _entries.Where(e => e.EventId == eventId).Select(e => e.Message));
        }
    }

    /// <summary>
    ///     Whether any recorded message contains the given text.
    /// </summary>
    /// <param name="substring">Text to look for.</param>
    public bool Contains(string substring) =>
        Messages.Any(m => m.Contains(substring, StringComparison.Ordinal));

    internal void Add(LogLevel level, int eventId, string message, object? state)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Add((level, eventId, message, state));
        }
    }
}

/// <summary>
///     An <see cref="ILogger" /> that keeps what was written, for assertions.
/// </summary>
/// <remarks>
///     Eleven hand-written loggers across nine files implemented this same
///     shape. They differed in three ways only: the level they recorded from,
///     whether they also kept the event id, and whether they were the
///     non-generic <see cref="ILogger" /> or the <see cref="ILogger{TCategoryName}" />
///     flavour. All three are parameters here.
/// </remarks>
public class CapturingLogger : ILogger
{
    /// <summary>
    ///     Creates a logger with its own capture.
    /// </summary>
    /// <param name="minimumLevel">Lowest level to record.</param>
    public CapturingLogger(LogLevel minimumLevel = LogLevel.Trace) => Capture = new LogCapture(minimumLevel);

    /// <summary>
    ///     Creates a logger that writes into an existing capture — the shape a
    ///     <see cref="ILoggerFactory" /> needs so that every logger it hands
    ///     out lands in one place.
    /// </summary>
    /// <param name="capture">The capture to write into.</param>
    public CapturingLogger(LogCapture capture) => Capture = capture;

    /// <summary>What this logger recorded.</summary>
    public LogCapture Capture { get; }

    /// <summary>Everything recorded, in order, as <c>(level, message)</c> pairs.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => Capture.Entries;

    /// <summary>Every recorded message, in order.</summary>
    public IReadOnlyList<string> Messages => Capture.Messages;

    /// <summary>The recorded messages at <see cref="LogLevel.Warning" /> and above.</summary>
    public IReadOnlyList<string> Warnings => Capture.Warnings;

    /// <summary>The recorded messages at <see cref="LogLevel.Error" /> and above.</summary>
    public IReadOnlyList<string> Errors => Capture.Errors;

    /// <summary>Every message logged under the given event id, joined by newlines.</summary>
    /// <param name="eventId">Event id to filter by.</param>
    public string MessagesFor(int eventId) => Capture.MessagesFor(eventId);

    /// <summary>Whether any recorded message contains the given text.</summary>
    /// <param name="substring">Text to look for.</param>
    public bool Contains(string substring) => Capture.Contains(substring);

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Capture.Add(logLevel, eventId.Id, formatter(state, exception), state);
}

/// <summary>
///     The <see cref="ILogger{TCategoryName}" /> flavour of
///     <see cref="CapturingLogger" />.
/// </summary>
/// <typeparam name="TCategoryName">The category the logger is typed for.</typeparam>
public sealed class CapturingLogger<TCategoryName> : ILogger<TCategoryName>
{
    private readonly LogCapture _capture;

    /// <summary>
    ///     Creates a logger with its own capture.
    /// </summary>
    /// <param name="minimumLevel">Lowest level to record.</param>
    public CapturingLogger(LogLevel minimumLevel = LogLevel.Trace) => _capture = new LogCapture(minimumLevel);

    /// <summary>
    ///     Creates a logger that writes into an existing capture.
    /// </summary>
    /// <param name="capture">The capture to write into.</param>
    public CapturingLogger(LogCapture capture) => _capture = capture;

    /// <summary>What this logger recorded.</summary>
    public LogCapture Capture => _capture;

    /// <summary>Everything recorded, in order, as <c>(level, message)</c> pairs.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _capture.Entries;

    /// <summary>Every recorded message, in order.</summary>
    public IReadOnlyList<string> Messages => _capture.Messages;

    /// <summary>The recorded messages at <see cref="LogLevel.Warning" /> and above.</summary>
    public IReadOnlyList<string> Warnings => _capture.Warnings;

    /// <summary>The recorded messages at <see cref="LogLevel.Error" /> and above.</summary>
    public IReadOnlyList<string> Errors => _capture.Errors;

    /// <summary>Every message logged under the given event id, joined by newlines.</summary>
    /// <param name="eventId">Event id to filter by.</param>
    public string MessagesFor(int eventId) => _capture.MessagesFor(eventId);

    /// <summary>Whether any recorded message contains the given text.</summary>
    /// <param name="substring">Text to look for.</param>
    public bool Contains(string substring) => _capture.Contains(substring);

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _capture.Add(logLevel, eventId.Id, formatter(state, exception), state);
}

/// <summary>
///     An <see cref="ILoggerFactory" /> whose loggers all write into one
///     capture.
/// </summary>
/// <remarks>
///     Two of the eleven hand-written loggers were a factory plus a logger that
///     took the factory's list, so that a whole composition root's logging
///     could be asserted at once. That is the only reason this type exists.
/// </remarks>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    /// <summary>
    ///     Creates a factory with its own capture.
    /// </summary>
    /// <param name="minimumLevel">Lowest level to record.</param>
    public CapturingLoggerFactory(LogLevel minimumLevel = LogLevel.Trace) => Capture = new LogCapture(minimumLevel);

    /// <summary>What the loggers this factory hands out recorded.</summary>
    public LogCapture Capture { get; }

    /// <summary>Everything recorded, in order, as <c>(level, message)</c> pairs.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => Capture.Entries;

    /// <summary>Every recorded message, in order.</summary>
    public IReadOnlyList<string> Messages => Capture.Messages;

    /// <summary>The recorded messages at <see cref="LogLevel.Warning" /> and above.</summary>
    public IReadOnlyList<string> Warnings => Capture.Warnings;

    /// <summary>The recorded messages at <see cref="LogLevel.Error" /> and above.</summary>
    public IReadOnlyList<string> Errors => Capture.Errors;

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // No providers: this factory exists to capture, not to route.
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Capture);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }
}
