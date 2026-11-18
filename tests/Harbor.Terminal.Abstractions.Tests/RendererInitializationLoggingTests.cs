using CSharpFunctionalExtensions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.TestKit;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     The guarded renderer setup converts a thrown prelude into
///     <c>Result.Failure</c>. That result only carries the message, so the
///     exception has to reach the log or the reason is lost. The optional
///     backend message must not be the switch that decides whether logging
///     happens at all.
/// </summary>
public class RendererInitializationLoggingTests
{
    private sealed class LoggingTestRenderer(ILogger logger) : BaseTuiRenderer(logger)
    {
        public CaptureRenderContext Capture { get; } = new();

        public override ITuiRenderContext Context => Capture;

        public Task<Result> Guarded(Action setup, string? detail = null) =>
            InitializeGuardedAsync(setup, detail);

        public override Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Maybe.From(string.Empty));

        public override Task<Result> WriteAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public override Task<Result> ClearAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    [Test]
    public async Task ThrowingSetup_WithoutBackendDetail_LogsTheFailure()
    {
        var logger = new CapturingLogger();
        var renderer = new LoggingTestRenderer(logger);

        Result result = await renderer.Guarded(static () => throw new InvalidOperationException("setup exploded"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(logger.Capture.DetailedEntries.Any(e =>
                e.Level == LogLevel.Error && e.EventId == 1 && e.Message.Contains("initialization failed")))
            .IsTrue()
            .Because("the caller only sees Result.Failure(message); event 1 is the exception's only record");
    }

    [Test]
    public async Task ThrowingSetup_WithBackendDetail_LogsTheDetail()
    {
        var logger = new CapturingLogger();
        var renderer = new LoggingTestRenderer(logger);

        Result result = await renderer.Guarded(
            static () => throw new InvalidOperationException("setup exploded"),
            "backend prelude failed");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(logger.Capture.DetailedEntries.Any(e =>
                e.Level == LogLevel.Error && e.EventId == 2 && e.Message.Contains("backend prelude failed")))
            .IsTrue()
            .Because("the backend message is the extra detail, not the condition for logging at all");
    }
}
