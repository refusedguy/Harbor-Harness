namespace Harbor.Application.Tests;

using Harbor.Application.Notifications;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     The two things <c>ProcessNotificationRunner</c> promises that its
///     contract can be held to without starting a process (issue #665).
/// </summary>
/// <remarks>
/// <para>
///     A notification announces a run that already finished. Every failure mode
///     here — libnotify missing, no notification daemon, a hung notifier — is an
///     expected outcome on some real machine, and none of them may reach the
///     renderer. That is a property of the CONTRACT ("does not throw for an
///     expected failure"), so it is testable, and it is the one property worth
///     testing here.
/// </para>
/// <para>
///     The tests are hermetic by construction: a path that cannot exist, and a
///     token cancelled before the call. Nothing is spawned, which matters more
///     than usual in this file — the whole point of #665 is that a test should
///     not have to fork a notifier to observe the notification path. The
///     pre-fix version of this behaviour had no seam, which is why
///     <c>Notifications_FullEventSweep_DoesNotThrow</c> really did shell out to
///     <c>notify-send</c> on whatever machine ran it.
/// </para>
/// <para>
///     The two tests are deliberately a matched pair: the SAME impossible path,
///     differing only in the token. A runner that swallowed everything and
///     always logged a warning would fail the cancellation test; a runner that
///     never attempted a start at all would fail the containment test. Together
///     they pin the branch, not just the absence of a throw.
/// </para>
/// <para>
///     The launch-and-wait path has no test here, and that is a deliberate gap
///     rather than an oversight: observing it requires a real process, which is
///     what this change exists to keep out of test runs. It is a
///     <c>ProcessStartInfo</c> plus <c>WaitForExit</c>, the same shape as
///     <c>ProcessGitQuery.RunGit</c> (#537), which is likewise untested for
///     success.
/// </para>
/// </remarks>
public class ProcessNotificationRunnerTests
{
    /// <summary>Cannot exist on any platform, so <c>Process.Start</c> always fails fast.</summary>
    private const string MissingBinary = "/harbor-665-no-such-dir/notify-send";

    [Test]
    public async Task MissingNotifier_IsContained_AndWarned()
    {
        var logger = new CapturingNotificationLogger();
        var runner = new ProcessNotificationRunner(logger);

        // No await, no Assert.Throws: the assertion IS that this returns at all.
        runner.Run(MissingBinary, ["title", "body"]);

        await Assert.That(logger.Warnings).IsNotEmpty()
            .Because("a notifier that is not installed is the single most common real "
                   + "outcome (a CI box with no libnotify, a container with no dbus), and "
                   + "it is worth a line in the log; what it must never be is an exception "
                   + "in the renderer's event path");
    }

    [Test]
    public async Task PreCancelledCall_StartsNothing_AndStaysAtDebug()
    {
        var logger = new CapturingNotificationLogger();
        var runner = new ProcessNotificationRunner(logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        runner.Run(MissingBinary, ["title", "body"], cts.Token);

        await Assert.That(logger.Warnings).IsEmpty()
            .Because("this is the observable proof that no start was attempted: the "
                   + "identical path in the other test warns precisely because it reaches "
                   + "Process.Start. A warning here would mean a renderer shutting down "
                   + "still forked a notifier on the way out — the cancellation gap #665 "
                   + "closes");
        await Assert.That(logger.Entries).IsNotEmpty()
            .Because("silence is a valid outcome, not an informative one: the caller that "
                   + "cancelled should be able to see in the log that its notification was "
                   + "dropped deliberately rather than lost");
    }
}

/// <summary>Minimal <see cref="ILogger{T}" /> that keeps what was written, for assertions.</summary>
internal sealed class CapturingNotificationLogger : ILogger<ProcessNotificationRunner>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <summary>Everything logged, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    /// <summary>Just the warnings — the ones a caller must never be surprised by.</summary>
    public IReadOnlyList<string> Warnings =>
        [.. _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];

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
        => _entries.Add((logLevel, formatter(state, exception)));
}
