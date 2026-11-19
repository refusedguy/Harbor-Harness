// #2680 F124. Behaviour tests for the title writer: the composed text, the
// OSC framing and the control-character scrub. Each test asserts what a caller
// receives, not how the composition is written.

using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class TerminalTitleWriterTests
{
    [Test]
    [Arguments(null, null, TerminalActivityState.Idle, "○ idle Harbor")]
    [Arguments("", null, TerminalActivityState.Idle, "○ idle Harbor")]
    [Arguments("my session", null, TerminalActivityState.Busy, "● busy my session")]
    [Arguments("my session", "◆", TerminalActivityState.Busy, "● busy ◆ my session")]
    [Arguments("my session", "", TerminalActivityState.Waiting, "? attention my session")]
    [Arguments("my session", null, TerminalActivityState.Done, "✓ done my session")]
    [Arguments("my session", null, TerminalActivityState.Error, "✖ error my session")]
    public async Task Format_Inputs_ComposesIndicatorGlyphAndSubject(
        string? subject,
        string? glyph,
        TerminalActivityState state,
        string expected)
    {
        var title = TerminalTitleWriter.Format(subject, glyph, state);

        await Assert.That(title).IsEqualTo(expected);
    }

    [Test]
    public async Task Format_ControlCharactersInSubject_AreStripped()
    {
        var title = TerminalTitleWriter.Format("a\u001b]0;evil\u0007\nb", null, TerminalActivityState.Idle);

        await Assert.That(title).IsEqualTo("○ idle a]0;evilb");
    }

    [Test]
    public async Task Format_ControlCharactersInGlyph_AreStripped()
    {
        var title = TerminalTitleWriter.Format("session", "◆\u0007", TerminalActivityState.Busy);

        await Assert.That(title).IsEqualTo("● busy ◆ session");
    }

    [Test]
    public async Task Osc0_WrapsTitleWithEscPrefixAndBelTerminator()
    {
        var sequence = TerminalTitleWriter.Osc0("○ idle Harbor");

        await Assert.That(sequence).IsEqualTo("\u001b]0;○ idle Harbor\u0007");
    }

    [Test]
    public async Task Osc2_WrapsTitleWithEscPrefixAndBelTerminator()
    {
        var sequence = TerminalTitleWriter.Osc2("● busy my session");

        await Assert.That(sequence).IsEqualTo("\u001b]2;● busy my session\u0007");
    }

    [Test]
    public async Task Build_ComposesTitleAndWrapsItInOsc0()
    {
        var sequence = TerminalTitleWriter.Build("my session", "◆", TerminalActivityState.Waiting);

        await Assert.That(sequence).IsEqualTo("\u001b]0;? attention ◆ my session\u0007");
    }

    [Test]
    public async Task Osc0_InjectedTitle_CannotEscapeTheSequence()
    {
        var sequence = TerminalTitleWriter.Osc0("safe\u001b]2;hijack\u0007text");

        await Assert.That(sequence).IsEqualTo("\u001b]0;safe]2;hijacktext\u0007");
    }
}
