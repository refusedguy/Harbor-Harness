using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Parsing;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>OSC 777 desktop notifications (osc-sprint §777): encoder goldens,
/// envelope sanitization, urxvt-family detection, and the kitty OSC 99 probe
/// answer intercepted by the parser as a capability event.</summary>
public class Osc777NotifyTests
{
    // ── Encoder ────────────────────────────────────────────────────────────

    [Test]
    public async Task Encode777_GoldenVector_BelTerminated()
    {
        await Assert.That(Osc777Notify.Encode("Harbor", "done"))
            .IsEqualTo("\u001B]777;notify;Harbor;done\u0007");
    }

    [Test]
    public async Task Encode777_SanitizesEnvelopeBreakers()
    {
        string seq = Osc777Notify.Encode("ha;rbor\u001B", "li\u0007ne\n;next");

        // ';' split and control bytes cannot escape the notify fields.
        await Assert.That(seq).IsEqualTo("\u001B]777;notify;ha rbor ;li ne  next\u0007");
    }

    [Test]
    public async Task Encode99_GoldenVector_StTerminated()
    {
        await Assert.That(Osc99Notify.Encode("Harbor", "still working"))
            .IsEqualTo("\u001B]99;;Harbor\nstill working\u001B\\");
    }

    [Test]
    public async Task Encode99_SanitizesControlBytes()
    {
        string seq = Osc99Notify.Encode("a\u001Bb", "c\u0007d");

        await Assert.That(seq).IsEqualTo("\u001B]99;;a b\nc d\u001B\\");
    }

    [Test]
    public async Task Encode_EmptyTitle_Throws()
    {
        await Assert.That(() => Osc777Notify.Encode(" ", "x")).Throws<ArgumentException>();
        await Assert.That(() => Osc99Notify.Encode("", "x")).Throws<ArgumentException>();
    }

    // ── urxvt-family detection (no wire probe exists) ──────────────────────

    private static Func<string, string?> Env(params (string Key, string? Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Test]
    public async Task Detect_RxvtTerms_MapToOsc777()
    {
        await Assert.That(NotifyProbe.Detect(Env(("TERM", "rxvt-unicode-256color")))).IsEqualTo(DesktopNotifyKind.Osc777);
        await Assert.That(NotifyProbe.Detect(Env(("TERM", "urxvt")))).IsEqualTo(DesktopNotifyKind.Osc777);
    }

    [Test]
    public async Task Detect_UnknownTerminal_Suppresses()
    {
        await Assert.That(NotifyProbe.Detect(Env(("TERM", "xterm-256color")))).IsEqualTo(DesktopNotifyKind.None);
        await Assert.That(NotifyProbe.Detect(Env(("TERM_PROGRAM", "iTerm.app")))).IsEqualTo(DesktopNotifyKind.None);
        await Assert.That(NotifyProbe.Detect(Env())).IsEqualTo(DesktopNotifyKind.None);
    }

    [Test]
    public async Task Detect_OverrideWins()
    {
        await Assert.That(NotifyProbe.Detect(Env(("HARBOR_OSC777", "1"), ("TERM", "xterm")))).IsEqualTo(DesktopNotifyKind.Osc777);
        await Assert.That(NotifyProbe.Detect(Env(("HARBOR_OSC777", "true")))).IsEqualTo(DesktopNotifyKind.Osc777);
        await Assert.That(NotifyProbe.Detect(Env(("HARBOR_OSC777", "0"), ("TERM", "rxvt")))).IsEqualTo(DesktopNotifyKind.None);
        await Assert.That(NotifyProbe.Detect(Env(("HARBOR_OSC777", "false"), ("TERM", "rxvt")))).IsEqualTo(DesktopNotifyKind.None);
    }

    // ── kitty OSC 99 probe answer → capability event ───────────────────────

    [Test]
    public async Task Parser_Osc99Answer_BelTerminated_BecomesNotifyReport()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B]99;i=harbor:p=title,text\u0007");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Kind).IsEqualTo(InputEventKind.Capability);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.Osc99NotifyReport);
    }

    [Test]
    public async Task Parser_Osc99Answer_StTerminated_BecomesNotifyReport()
    {
        var events = T.Feed(new EscapeSequenceParser(), "\u001B]99;i=harbor:p=title,text\u001B\\");

        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].Capability.Kind).IsEqualTo(CapabilityEventKind.Osc99NotifyReport);
    }

    [Test]
    public async Task Parser_OtherOscStrings_StayDiscarded()
    {
        // 777 emissions, 52 clipboard traffic and unknown OSC families must
        // NOT surface as capability events — only the 99 probe answer does.
        await Assert.That(T.Feed(new EscapeSequenceParser(), "\u001B]777;notify;h;b\u0007")).IsEmpty();
        await Assert.That(T.Feed(new EscapeSequenceParser(), "\u001B]52;c;aGk=\u0007")).IsEmpty();
        await Assert.That(T.Feed(new EscapeSequenceParser(), "\u001B]0;title\u0007")).IsEmpty();
    }
}
