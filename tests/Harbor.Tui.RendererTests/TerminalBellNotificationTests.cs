// TerminalBellNotificationTests.cs: the pin for #2675 item 121.
//
// HARBOR_FORCE_BEL / AMP_FORCE_BEL decorate the selected notifier with a
// terminal bell. Two properties are worth pinning: the BEL byte is emitted
// exactly once per notification (an extra BEL is a spurious beep, none is a
// silent notification over SSH), and the inner backend still receives the
// notification unchanged. The bell is an extra cue, not a replacement. The
// env-flag parse is tested as a pure helper so no test mutates process-wide
// environment state.

namespace Harbor.Tui.RendererTests;

using Harbor.Tui.Notifications;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

public class TerminalBellNotificationTests
{
    [Test]
    public async Task Notify_DelegatesToInner_ThenWritesExactlyOneBell()
    {
        using var writer = new StringWriter();
        var inner = new RecordingBackend();
        INotificationBackend backend = new BellNotificationBackend(inner, writer);

        backend.Notify("Harbor — done", "Agent finished.", isError: false);

        await Assert.That(inner.Calls.Count).IsEqualTo(1);
        await Assert.That(inner.Calls[0].Title).IsEqualTo("Harbor — done");
        await Assert.That(inner.Calls[0].Body).IsEqualTo("Agent finished.");
        await Assert.That(inner.Calls[0].IsError).IsFalse();
        await Assert.That(writer.ToString()).IsEqualTo("\u0007")
            .Because("one notification rings the bell exactly once; the BEL is "
                   + "the only signal an SSH terminal can act on");
    }

    [Test]
    public async Task Notify_ForwardsSeverityAndCancellationToInner()
    {
        using var writer = new StringWriter();
        var inner = new RecordingBackend();
        INotificationBackend backend = new BellNotificationBackend(inner, writer);
        using var cts = new CancellationTokenSource();

        backend.Notify("t", "b", isError: true, cts.Token);

        await Assert.That(inner.Calls.Count).IsEqualTo(1);
        await Assert.That(inner.Calls[0].IsError).IsTrue();
        await Assert.That(inner.Calls[0].Token == cts.Token).IsTrue()
            .Because("the handler's token must reach the inner backend, not be "
                   + "swallowed by the decorator");
    }

    [Test]
    public async Task Notify_FlushesTheWriter_SoTheBellIsNotTrappedInABuffer()
    {
        using var writer = new FlushTrackingWriter();
        INotificationBackend backend = new BellNotificationBackend(new RecordingBackend(), writer);

        backend.Notify("t", "b", isError: false);

        await Assert.That(writer.ToString()).IsEqualTo("\u0007");
        await Assert.That(writer.FlushCount).IsEqualTo(1)
            .Because("without the flush a buffered stdout would hold the bell "
                   + "until the next write, which may never come for a finished run");
    }

    [Test]
    public async Task Name_NamesTheDecoratedBackend()
    {
        INotificationBackend backend = new BellNotificationBackend(
            new RecordingBackend(), new StringWriter());

        await Assert.That(backend.Name).IsEqualTo("terminal bell + recording");
    }

    [Test]
    [Arguments("1", null, true)]
    [Arguments(null, "1", true)]
    [Arguments("true", null, true)]
    [Arguments(null, "TRUE", true)]
    [Arguments("0", null, false)]
    [Arguments(null, "false", false)]
    [Arguments(null, null, false)]
    public async Task IsForceBellEnabled_ParsesBothFlags(string? harbor, string? amp, bool expected)
    {
        await Assert.That(NotificationTuiRenderer.IsForceBellEnabled(harbor, amp)).IsEqualTo(expected);
    }

    private sealed class RecordingBackend : INotificationBackend
    {
        public string Name => "recording";

        public List<(string Title, string Body, bool IsError, CancellationToken Token)> Calls { get; } = [];

        public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default)
            => Calls.Add((title, body, isError, cancellationToken));
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }
}
