using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Harbor.TestKit;

/// <summary>One line a <see cref="RecordingLoggerProvider"/> observed.</summary>
/// <param name="Category">The category the logger was created for.</param>
/// <param name="Level">The level the line was written at.</param>
/// <param name="EventId">The source-generated event id.</param>
/// <param name="Message">The fully formatted message.</param>
/// <param name="State">
///     The structured state a <c>[LoggerMessage]</c> call handed the logger: a
///     list of named fields, kept so a test can assert on a field such as
///     <c>Attempt</c> instead of matching formatted prose.
/// </param>
/// <param name="Scopes">Scope descriptions active at write time, outermost first.</param>
public sealed record RecordedLogEntry(
    string Category,
    LogLevel Level,
    int EventId,
    string Message,
    object? State,
    IReadOnlyList<string> Scopes);

/// <summary>
///     A real <see cref="ILoggerProvider"/> that records every line a test's
///     logging composition root writes, with the category and the active
///     scopes attached.
/// </summary>
/// <remarks>
///     <para>
///         This is deliberately NOT a rewrite of <see cref="CapturingLogger"/>.
///         That type is a hand-rolled <see cref="ILogger"/>/<see cref="ILoggerFactory"/>
///         pair for asserting a single logger in isolation; it cannot be handed
///         to <c>builder.Logging.AddProvider(...)</c>, and it drops both the
///         category and the scope. This provider plugs into a normal
///         <see cref="ILoggerFactory"/> and keeps what a production composition
///         root would actually emit, which is what makes a "this provider
///         warned with attempt N" assertion meaningful.
///     </para>
///     <para>
///         Entries live in a <see cref="ConcurrentQueue{T}"/> so a provider shared
///         by parallel consumers does not lose lines or corrupt the capture.
///     </para>
/// </remarks>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

    /// <summary>Everything recorded so far, in write order.</summary>
    public IReadOnlyList<RecordedLogEntry> Entries => [.. _entries];

    /// <summary>Only the lines written under <paramref name="category"/>.</summary>
    public IReadOnlyList<RecordedLogEntry> EntriesFor(string category) =>
        [.. _entries.Where(e => e.Category == category)];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new UntypedLogger(this, categoryName);

    /// <summary>
    ///     The typed flavour a component under test takes as a constructor
    ///     dependency. The category is the full type name, as in a real
    ///     <c>ILogger&lt;T&gt;</c>.
    /// </summary>
    public ILogger<T> CreateLogger<T>() =>
        new TypedLogger<T>(this, typeof(T).FullName ?? typeof(T).Name);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the queue is the capture and stays readable.
    }

    internal void Record(
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        object? state,
        IReadOnlyList<string> scopes) =>
        _entries.Enqueue(new RecordedLogEntry(category, level, eventId.Id, message, state, scopes));

    /// <summary>Shared write path: scope tracking plus the call into the provider.</summary>
    private sealed class Sink
    {
        private readonly RecordingLoggerProvider _owner;
        private readonly string _category;
        private readonly AsyncLocal<ScopeFrame?> _scope = new();

        public Sink(RecordingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var frame = new ScopeFrame(state.ToString() ?? string.Empty, _scope.Value);
            _scope.Value = frame;
            return new ScopeLease(_scope, frame);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            for (ScopeFrame? frame = _scope.Value; frame is not null; frame = frame.Parent)
            {
                scopes.Add(frame.Text);
            }

            scopes.Reverse();
            _owner.Record(_category, logLevel, eventId, formatter(state, exception), state, [.. scopes]);
        }

        private sealed record ScopeFrame(string Text, ScopeFrame? Parent);

        private sealed class ScopeLease : IDisposable
        {
            private readonly AsyncLocal<ScopeFrame?> _scope;
            private readonly ScopeFrame _frame;

            public ScopeLease(AsyncLocal<ScopeFrame?> scope, ScopeFrame frame)
            {
                _scope = scope;
                _frame = frame;
            }

            public void Dispose()
            {
                if (ReferenceEquals(_scope.Value, _frame))
                {
                    _scope.Value = _frame.Parent;
                }
            }
        }
    }

    private sealed class UntypedLogger : ILogger
    {
        private readonly Sink _sink;

        public UntypedLogger(RecordingLoggerProvider owner, string category) => _sink = new Sink(owner, category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _sink.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _sink.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class TypedLogger<T> : ILogger<T>
    {
        private readonly Sink _sink;

        public TypedLogger(RecordingLoggerProvider owner, string category) => _sink = new Sink(owner, category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _sink.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _sink.Log(logLevel, eventId, state, exception, formatter);
    }
}
