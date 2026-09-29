namespace Harbor.Tui.RendererTests;

using CSharpFunctionalExtensions;
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     The <c>Maybe&lt;string&gt;</c> contract on <c>ITuiRenderer.ReadLineAsync</c> (#589).
/// </summary>
/// <remarks>
///     <para>
///         Every renderer used to answer end-of-input with
///         <c>Result.Success(line ?? string.Empty)</c>, so "no line was read" and "the
///         user submitted an empty line" were the same value. A line-buffered consumer
///         cannot tell them apart, and the one that had to — <c>ReplRunner</c>'s line
///         REPL — branched on <c>IsNullOrWhiteSpace(input) → continue</c>, so a closed
///         stdin or Ctrl-D spun it at 100% CPU with no exit.
///     </para>
///     <para>
///         These tests pin the <b>absence</b> half of the contract on the one renderer
///         whose behaviour is fully deterministic and needs no stdin: the notification
///         backend, which cannot read input at all. The six stdin-backed renderers are
///         covered mechanically by <c>TuiReadLineContractRules</c> in
///         <c>Harbor.Architecture.Tests</c> — reading a real console from a unit test
///         would make the assertion depend on the test host's stdin.
///     </para>
/// </remarks>
public class ReadLineAbsenceContractTests
{
    [Test]
    public async Task CannotReadInput_ReportsAbsence_NotASuccessfulEmptyLine()
    {
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance,
            new RecordingNotificationRunner());

        Maybe<string> line = await renderer.ReadLineAsync("> ");

        await Assert.That(line.HasNoValue).IsTrue()
            .Because(
                "A renderer that cannot read input has read nothing. Reporting Success(\"\") claimed " +
                "a line was read, which is what made the line REPL spin at 100% CPU on a non-reading " +
                "backend and on a closed stdin (#589).");
    }

    [Test]
    public async Task AbsentLine_IsDistinguishableFromAnEmptySubmission()
    {
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance,
            new RecordingNotificationRunner());

        Maybe<string> absent = await renderer.ReadLineAsync("> ");
        Maybe<string> emptySubmission = Maybe.From(string.Empty);

        // The two cases the old signature could not express at all. If this ever
        // compares equal again, the fix has been undone somewhere upstream.
        await Assert.That(absent.HasValue).IsNotEqualTo(emptySubmission.HasValue)
            .Because(
                "EOF and 'the user pressed Enter on an empty prompt' are different states. The old " +
                "Result<string> contract collapsed them, so a consumer branching on emptiness " +
                "silently treated EOF as a blank submission and looped forever.");
    }

    [Test]
    public async Task AbsentLine_StillDegradesToEmptyString_ForCallersThatRequireAValue()
    {
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance,
            new RecordingNotificationRunner());

        Maybe<string> line = await renderer.ReadLineAsync("> ");

        // The wizard readers (SetupVerb, SlashCommandDispatcher, ReplRunner's onboarding
        // reader) must keep working: they need a string, so they opt into the fallback
        // explicitly instead of having it forced on them by the return type.
        await Assert.That(line.GetValueOrDefault(string.Empty)).IsEqualTo(string.Empty)
            .Because(
                "Behaviour preservation: the onboarding wizard reader signature is " +
                "Func<string, Task<string>> and cannot express absence, so the fallback must still " +
                "produce an empty string. Only the REPL loop, which CAN branch, now distinguishes.");
    }
}
