using Harbor.Abstractions.Extensions;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2678 item 100: model-supplied tool names and arguments must be inert
///     before a terminal paints them. The shared sanitizer covers ESC/C0/C1
///     controls and the bidi formatting characters, and the confirmation card
///     and modal call it at their boundary.
/// </summary>
public class TerminalTextSanitizerTests
{
    [Test]
    public async Task Sanitize_CleanInput_ReturnsSameInstance()
    {
        const string clean = "bash -lc 'echo hello' /tmp/a.txt → Γ";

        await Assert.That(ReferenceEquals(clean, TerminalTextSanitizer.Sanitize(clean))).IsTrue();
        await Assert.That(ReferenceEquals(clean, TerminalTextSanitizer.SanitizeSingleLine(clean))).IsTrue();
        await Assert.That(TerminalTextSanitizer.Sanitize(null)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Sanitize_DropsControls_KeepsLineFeedAndTab()
    {
        string raw = "bash\x1b[2J\u0000 \u007f\u0085 \"a\tb\"\nnext";

        await Assert.That(TerminalTextSanitizer.Sanitize(raw)).IsEqualTo("bash[2J \"a\tb\"\nnext");
    }

    [Test]
    public async Task Sanitize_DropsEveryBidiControl()
    {
        string raw = "safe\u061c\u200e\u200f\u202a\u202b\u202c\u202d\u202e\u2066\u2067\u2068\u2069name";
        string clean = TerminalTextSanitizer.Sanitize(raw);

        await Assert.That(clean).IsEqualTo("safename");
        foreach (char c in clean)
        {
            await Assert.That(c is not ('\u061c' or '\u200e' or '\u200f' or '\u202a' or '\u202b'
                or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069')).IsTrue();
        }
    }

    [Test]
    public async Task SanitizeSingleLine_CollapsesWhitespace_AndStripsControls()
    {
        string raw = "  bash\r\n\u001b[31m  rm  \t-rf\u202e /  ";

        await Assert.That(TerminalTextSanitizer.SanitizeSingleLine(raw)).IsEqualTo("bash  [31m  rm   -rf /");
    }

    [Test]
    public async Task ApprovalGateView_SanitizesToolNameAndDetail()
    {
        var gate = new ApprovalGateView("\u001b[31mbash\u202e", "rm\u0000 -rf\u2066 /\u2069");

        await Assert.That(gate.ToolName).IsEqualTo("[31mbash");
        await Assert.That(string.Concat(gate.WrappedDetail(120))).IsEqualTo("rm -rf /");
    }

    [Test]
    public async Task DialogOverlay_ShowApproval_SanitizesNameDetailAndPath()
    {
        var dialog = new DialogOverlay();
        dialog.ShowApproval("\u001bbash\u202e", "rm\u0000 -rf /", filePath: "\u001b/etc/\u2066passwd\u2069");

        await Assert.That(dialog.ApprovalTool).IsEqualTo("bash");
        await Assert.That(dialog.Message).IsEqualTo("rm -rf /");
        await Assert.That(dialog.ApprovalFilePath).IsEqualTo("/etc/passwd");
    }
}
