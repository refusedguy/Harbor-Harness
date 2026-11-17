using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class TextInputTests
{
    private static string Paint(ITextInput input, int width, int height)
    {
        var buffer = TestRender.Buffer(width, height);
        input.Paint(buffer, new Rect(0, 0, width, height));
        return ScreenFixtures.Art(buffer);
    }

    [Test]
    public async Task SingleLine_PlainText_Golden()
    {
        var state = new TextInputState();
        state.SetText("ab");
        var input = new TextInput(state) { IsFocused = false };

        await Assert.That(Paint(input, 8, 1)).IsEqualTo("ab      ");
    }

    [Test]
    public async Task SingleLine_Mask_HidesText_Golden()
    {
        var state = new TextInputState { Mask = new Rune('•') };
        state.SetText("secret");
        var input = new TextInput(state) { IsFocused = false };

        await Assert.That(Paint(input, 8, 1)).IsEqualTo("••••••  ");
    }

    [Test]
    public async Task SingleLine_Affixes_Golden()
    {
        var state = new TextInputState();
        state.SetText("hi");
        var input = new TextInput(state)
        {
            IsFocused = false,
            Affixes = new TextInputAffixes
            {
                Prefix = "$",
                PrefixButton = new TextInputButton("clear", "x"),
                Suffix = "USD",
                SuffixButton = new TextInputButton("go", "OK"),
            },
        };

        await Assert.That(Paint(input, 20, 1)).IsEqualTo("[x]$hi       USD[OK]");
    }

    [Test]
    public async Task AffixButtons_HitTest_ById()
    {
        var state = new TextInputState();
        state.SetText("hi");
        var input = new TextInput(state)
        {
            Affixes = new TextInputAffixes
            {
                PrefixButton = new TextInputButton("clear", "x"),
                SuffixButton = new TextInputButton("go", "OK"),
            },
        };

        Paint(input, 20, 1);
        await Assert.That(input.HitTest(1, 0)).IsEqualTo("clear");
        await Assert.That(input.HitTest(17, 0)).IsEqualTo("go");
        await Assert.That(input.HitTest(7, 0)).IsNull();
    }

    [Test]
    public async Task Validation_Hook_Wins_ThenRequiredFallback()
    {
        var state = new TextInputState { Validator = s => s == "bad" ? "Invalid." : null };
        state.SetText("bad");
        await Assert.That(state.Validate()).IsEqualTo("Invalid.");

        state.SetText("ok");
        await Assert.That(state.Validate()).IsNull();

        state.Required = true;
        state.SetText(string.Empty);
        await Assert.That(state.Validate()).IsEqualTo("Required.");
    }

    [Test]
    public async Task BothWidgets_ShareTheHostState()
    {
        var state = new TextInputState();
        state.SetText("shared");
        ITextInput a = new TextInput(state) { IsFocused = false };
        ITextInput b = new MultilineTextInput(state) { IsFocused = false, WrapWidth = 20 };

        await Assert.That(a.State).IsSameReferenceAs(b.State);
        await Assert.That(Paint(a, 8, 1)).IsEqualTo("shared  ");

        state.SetText("next");
        await Assert.That(Paint(b, 8, 1)).IsEqualTo("next    ");
    }

    [Test]
    public async Task Multiline_SoftWrap_Autogrows_Golden()
    {
        var state = new TextInputState { Multiline = true };
        state.SetText("abcdef ghijk");
        var input = new MultilineTextInput(state) { IsFocused = false, WrapWidth = 6 };

        await Assert.That(input.DesiredHeight).IsEqualTo(2);
        await Assert.That(Paint(input, 6, 3)).IsEqualTo("abcdef\nghijk \n      ");
    }

    [Test]
    public async Task Multiline_LongWord_WrapsAtRuneBoundary_Golden()
    {
        var state = new TextInputState { Multiline = true };
        state.SetText("abcdefgh");
        var input = new MultilineTextInput(state) { IsFocused = false, WrapWidth = 3 };

        await Assert.That(input.DesiredHeight).IsEqualTo(3);
        await Assert.That(Paint(input, 3, 3)).IsEqualTo("abc\ndef\ngh ");
    }

    [Test]
    public async Task Multiline_ExplicitNewline_IsHardBreak_Golden()
    {
        var state = new TextInputState { Multiline = true };
        state.SetText("ab\ncd");
        var input = new MultilineTextInput(state) { IsFocused = false, WrapWidth = 10 };

        await Assert.That(input.DesiredHeight).IsEqualTo(2);
        await Assert.That(Paint(input, 6, 2)).IsEqualTo("ab    \ncd    ");
    }

    [Test]
    public async Task Multiline_Mask_CoversEveryVisualLine_Golden()
    {
        var state = new TextInputState { Multiline = true, Mask = new Rune('*') };
        state.SetText("ab\ncd");
        var input = new MultilineTextInput(state) { IsFocused = false, WrapWidth = 10 };

        await Assert.That(Paint(input, 6, 2)).IsEqualTo("**    \n**    ");
    }

    [Test]
    public async Task Multiline_CursorAfterWrap_LandsOnVisualCell_Golden()
    {
        var state = new TextInputState { Multiline = true };
        state.SetText("abcdef ghijk");
        state.MoveTo(state.Text.Length);
        var input = new MultilineTextInput(state) { WrapWidth = 6 };

        // The caret sits on the blank cell after the wrapped last line.
        await Assert.That(Paint(input, 6, 2)).IsEqualTo("abcdef\nghijk_");
    }

    [Test]
    public async Task State_Editing_KeepsSurrogatePairsWhole()
    {
        var state = new TextInputState();
        state.SetText("a\uD834\uDD1E");
        state.MoveTo(state.Text.Length);
        await Assert.That(state.Backspace()).IsTrue();
        await Assert.That(state.Text).IsEqualTo("a");

        await Assert.That(state.Insert(new Rune(0x1D11E))).IsTrue();
        await Assert.That(state.Text).IsEqualTo("a\uD834\uDD1E");
        await Assert.That(state.MoveCursor(-1)).IsTrue();
        await Assert.That(state.Cursor).IsEqualTo(1);
        await Assert.That(state.DeleteForward()).IsTrue();
        await Assert.That(state.Text).IsEqualTo("a");
    }

    [Test]
    public async Task State_MaxLength_AndNewlineGate()
    {
        var state = new TextInputState { MaxLength = 2 };
        await Assert.That(state.Insert(new Rune('a'))).IsTrue();
        await Assert.That(state.Insert(new Rune('b'))).IsTrue();
        await Assert.That(state.Insert(new Rune('c'))).IsFalse();
        await Assert.That(state.Text).IsEqualTo("ab");
        await Assert.That(state.Insert(new Rune('\n'))).IsFalse();

        state.MaxLength = 0;
        state.Multiline = true;
        await Assert.That(state.Insert(new Rune('\n'))).IsTrue();
        await Assert.That(state.Text).IsEqualTo("ab\n");
    }
}
