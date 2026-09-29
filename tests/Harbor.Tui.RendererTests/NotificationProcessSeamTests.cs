// NotificationProcessSeamTests.cs — the pin for issue #665.
//
// WHY THIS FILE EXISTS
// --------------------
// The three OS backends in Harbor.Tui.Notifications each built a
// ProcessStartInfo and called Process.Start. `INotificationBackend` was already
// in that file and already abstracted the three platforms — but it abstracted
// the SHAPE of the call and nothing else, so each implementation still owned
// its own child process. Three baseline rows in PresentationCapabilityRules
// grandfathred the result, which is why it survived #455.
//
// The seam is now INotificationProcessRunner (Domain) +
// ProcessNotificationRunner (Harbor.Application). These tests pin the two
// properties that seam buys and that a shape-only interface never had:
//
//   1. THE BACKENDS LAUNCH NOTHING. Every platform notifier is reached through
//      the injected runner, with the exact argv it has always used. Asserted
//      per backend, not "some call happened": a backend that quietly built its
//      own Process again would still compile and still pass a smoke test.
//   2. THE BACKENDS ARE CANCELLABLE. The token IAgentEventHandler.HandleAsync
//      is handed — and which the old code dropped on the floor — reaches the
//      runner unchanged.
//
// WHY THE BACKENDS ARE TESTED DIRECTLY, NOT THROUGH THE RENDERER
// -------------------------------------------------------------
// The renderer picks a backend from RuntimeInformation.IsOSPlatform, so on a
// Linux test host only the notify-send argv is reachable from outside. Two of
// the three backends would go unpinned, and the third would be pinned only on
// one OS. The backends are internal, so this assembly sees them through the
// InternalsVisibleTo added in Harbor.Tui.Notifications.csproj — the same shape
// as Harbor.Tools.Builtin -> Harbor.Tools.Builtin.Tests.
//
// The renderer-level path is still exercised, at the end, so the recording
// runner is proven to be the live seam rather than a decoration.

namespace Harbor.Tui.RendererTests;

using Harbor.Abstractions.Events;
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Pins the injected-runner seam behind the notification backends (#665).
///     See the file header for the incident and the argument for testing the
///     backends directly.
/// </summary>
public class NotificationProcessSeamTests
{
    // ---------------------------------------------------------------------
    // 1. The backends launch nothing themselves.
    // ---------------------------------------------------------------------

    [Test]
    public async Task LinuxBackend_AsksTheRunnerForNotifySend_WithTitleAndBody()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new LinuxNotifySendBackend(runner);

        backend.Notify("Harbor — done", "Agent finished.", isError: false);

        await Assert.That(runner.Count).IsEqualTo(1)
            .Because("one Notify call is one notification, not two");
        await Assert.That(runner.Single.FileName).IsEqualTo("notify-send");
        await Assert.That(string.Join(' ', runner.Single.Arguments))
            .IsEqualTo("Harbor — done Agent finished.")
            .Because("argv order is the contract: title then body, and no --urgency "
                   + "flag on a non-error notification");
    }

    [Test]
    public async Task LinuxBackend_PutsUrgencyFirst_OnlyForAnError()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new LinuxNotifySendBackend(runner);

        backend.Notify("Harbor — error", "boom", isError: true);

        await Assert.That(string.Join(' ', runner.Single.Arguments))
            .IsEqualTo("--urgency=critical Harbor — error boom")
            .Because("notify-send needs the flag BEFORE the summary and body, or it "
                   + "reads them as the flag's value");
    }

    [Test]
    public async Task MacBackend_AsksTheRunnerForOsascript_WithOneScriptArgument()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new MacOsascriptBackend(runner);

        backend.Notify("Harbor — done", "Agent finished.", isError: false);

        await Assert.That(runner.Single.FileName).IsEqualTo("osascript");
        await Assert.That(runner.Single.Arguments.Count).IsEqualTo(2)
            .Because("-e plus exactly one script: a second -e would be a second "
                   + "AppleScript statement, and a bare string would be a filename");
        await Assert.That(runner.Single.Arguments[0]).IsEqualTo("-e");
        await Assert.That(runner.Single.Arguments[1])
            .IsEqualTo("display notification \"Agent finished.\" with title \"Harbor — done\"");
    }

    [Test]
    public async Task MacBackend_EscapesQuotes_SoTheAppleScriptStaysValid()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new MacOsascriptBackend(runner);

        backend.Notify("say \"hi\"", "a \"quoted\" body", isError: false);

        await Assert.That(runner.Single.Arguments[1])
            .IsEqualTo("display notification \"a \\\"quoted\\\" body\" with title \"say \\\"hi\\\"\"")
            .Because("an unescaped quote terminates the AppleScript string literal; "
                   + "this is the one piece of injection-shaped logic on the whole "
                   + "path, so it is pinned rather than trusted");
    }

    [Test]
    public async Task WindowsBackend_AsksTheRunnerForMsg_WithRecipientAndTimeout()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new WindowsToastBackend(runner);

        backend.Notify("Harbor — error", "boom", isError: true);

        await Assert.That(runner.Single.FileName).IsEqualTo("msg");
        await Assert.That(string.Join(' ', runner.Single.Arguments))
            .IsEqualTo("* /TIME:10 Harbor — error\nboom")
            .Because("\"*\" is every session, /TIME:10 bounds the modal dialog, and "
                   + "the isError flag is presentation-only on this backend — msg has "
                   + "no urgency concept, which is why the swap to snoretoast.exe is "
                   + "still on the list");
    }

    [Test]
    public async Task NullBackend_LaunchesNothing()
    {
        var runner = new RecordingNotificationRunner();
        INotificationBackend backend = new NullNotificationBackend();

        backend.Notify("Harbor — done", "Agent finished.", isError: false);

        await Assert.That(runner.Count).IsEqualTo(0)
            .Because("the null backend exists for platforms with no notifier; if it "
                   + "launched something it would be a bug, not a fallback");
    }

    // ---------------------------------------------------------------------
    // 2. The backends are cancellable.
    // ---------------------------------------------------------------------

    [Test]
    public async Task Backends_ForwardTheCallersCancellationToken_Unchanged()
    {
        using var cts = new CancellationTokenSource();
        var runner = new RecordingNotificationRunner();

        new LinuxNotifySendBackend(runner).Notify("t", "b", isError: false, cts.Token);
        new MacOsascriptBackend(runner).Notify("t", "b", isError: false, cts.Token);
        new WindowsToastBackend(runner).Notify("t", "b", isError: false, cts.Token);

        await Assert.That(runner.Count).IsEqualTo(3);
        foreach (RecordingNotificationRunner.Launch launch in runner.Launches)
        {
            await Assert.That(launch.CancellationToken).IsEqualTo(cts.Token)
                .Because("#665's second half: IAgentEventHandler.HandleAsync is handed a "
                       + "token and the pre-fix code discarded it, so a renderer shutting "
                       + "down could not stop a notifier it had already started. A backend "
                       + "that swapped in its own token would pass every argv test above "
                       + "and still leave that hole open");
        }
    }

    [Test]
    public async Task Backend_DefaultsToNoCancellation_RatherThanADisposedOrSharedOne()
    {
        var runner = new RecordingNotificationRunner();

        new LinuxNotifySendBackend(runner).Notify("t", "b", isError: false);

        await Assert.That(runner.Single.CancellationToken.CanBeCanceled).IsFalse()
            .Because("the parameter is optional, so a caller that knows nothing about "
                   + "lifetime must get a token that never fires — not a shared one that "
                   + "some other component could cancel");
    }

    // ---------------------------------------------------------------------
    // 3. The renderer really goes through the seam (non-vacuity).
    // ---------------------------------------------------------------------

    [Test]
    public async Task Renderer_FiresTheNotification_ThroughTheInjectedRunner()
    {
        var runner = new RecordingNotificationRunner();
        using var cts = new CancellationTokenSource();
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance,
            runner);

        await renderer.InitializeAsync(cts.Token);
        await renderer.RenderAsync(new AgentEndEvent([]), cts.Token);

        await Assert.That(runner.Count).IsEqualTo(1)
            .Because("this is what makes the five tests above statements about the "
                   + "shipped path rather than about backends nobody constructs: the "
                   + "handler registry, the event mapping and the backend selection all "
                   + "have to line up for the recording runner to see a launch at all. It "
                   + "also means this assertion holds on every OS, because it checks that "
                   + "a launch happened, not which notifier it was");
        await Assert.That(runner.Single.CancellationToken).IsEqualTo(cts.Token)
            .Because("RenderAsync's token has to reach the runner, not just the backends' "
                   + "own default");
    }

    [Test]
    public async Task Renderer_PassesAnErrorEventsMessage_ThroughTheSeam()
    {
        var runner = new RecordingNotificationRunner();
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance,
            runner);

        await renderer.InitializeAsync();
        await renderer.RenderAsync(new AgentErrorEvent("provider exploded"));

        await Assert.That(runner.Count).IsEqualTo(1);
        await Assert.That(string.Join(' ', runner.Single.Arguments))
            .Contains("provider exploded")
            .Because("the body a user reads in the toast is the event's own message; "
                   + "routing it through the runner must not drop it");
    }
}
