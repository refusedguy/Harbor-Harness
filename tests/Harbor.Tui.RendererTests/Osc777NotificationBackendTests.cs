// Osc777NotificationBackendTests.cs — the pin for #2685 item N07.
//
// The OSC 777 backend writes an escape sequence straight to the terminal,
// unlike the three OS backends which go through INotificationProcessRunner.
// The thing worth pinning is therefore not argv but the exact bytes: a wrong
// terminator makes the emulator ignore the notification, and an unsanitized
// field lets tool output break out of the OSC sequence and inject a different
// one.

namespace Harbor.Tui.RendererTests;

using Harbor.Tui.Notifications;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

public class Osc777NotificationBackendTests
{
    [Test]
    public async Task Name_IdentifiesTheTerminalBackend()
    {
        using var writer = new StringWriter();
        INotificationBackend backend = new Osc777NotificationBackend(writer);

        await Assert.That(backend.Name).IsEqualTo("OSC 777 (terminal)");
    }

    [Test]
    public async Task Notify_WritesTheNotifySequenceWithTitleThenBody()
    {
        using var writer = new StringWriter();
        INotificationBackend backend = new Osc777NotificationBackend(writer);

        backend.Notify("Harbor — done", "Agent finished.", isError: false);

        await Assert.That(writer.ToString()).IsEqualTo("\u001b]777;notify;Harbor — done;Agent finished.\u0007")
            .Because("OSC 777's field order is title then body, and BEL is the "
                   + "terminator; an emulator silently drops a malformed sequence");
    }

    [Test]
    public async Task Notify_KeepsTheSeverityInTheTitle_WithoutAnExtraField()
    {
        using var writer = new StringWriter();
        INotificationBackend backend = new Osc777NotificationBackend(writer);

        backend.Notify("Harbor — error", "boom", isError: true);
        string errorLine = writer.ToString();

        await Assert.That(errorLine).IsEqualTo("\u001b]777;notify;Harbor — error;boom\u0007");
        await Assert.That(errorLine.Contains("true")).IsFalse()
            .Because("OSC 777 has no severity field; the handler's title is the "
                   + "discriminator, and inventing a fifth field would corrupt the sequence");
    }

    [Test]
    public async Task Notify_SanitizesFieldSeparators_AndTerminalControlCharacters()
    {
        using var writer = new StringWriter();
        INotificationBackend backend = new Osc777NotificationBackend(writer);

        // Tool output is the body source. A semicolon would add an OSC field;
        // ESC/BEL would end this sequence early and start an injected one.
        backend.Notify("title;with;semicolons", "line1\nbad\u001b[31m\u0007end", isError: false);

        string rendered = writer.ToString();
        await Assert.That(rendered).IsEqualTo("\u001b]777;notify;title with semicolons;line1 bad [31m end\u0007")
            .Because("every control character and every field separator becomes "
                   + "a space, so the only ESC and BEL in the output are the ones "
                   + "this backend wrote itself");
        await Assert.That(rendered.Split('\u0007').Length).IsEqualTo(2)
            .Because("exactly one terminator: a single notify is one notification");
    }

    [Test]
    public async Task Notify_FlushesTheWriter_SoTheSequenceIsNotTrappedInABuffer()
    {
        using var writer = new FlushTrackingWriter();
        INotificationBackend backend = new Osc777NotificationBackend(writer);

        backend.Notify("t", "b", isError: false);

        await Assert.That(writer.ToString()).Contains("\u001b]777;notify;t;b\u0007");
        await Assert.That(writer.FlushCount).IsEqualTo(1)
            .Because("without the flush a buffered stdout would hold the banner "
                   + "until the next write, which may never come for a finished run");
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
