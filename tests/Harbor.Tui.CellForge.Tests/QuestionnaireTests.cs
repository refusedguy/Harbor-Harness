using System.Text;
using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// The Questionnaire contract: host-owned steps, an answer draft that survives
// Back, validation on Next/Submit, host errors that override the step rule,
// and a domain-neutral public surface. Goldens are inline and style-blind:
// the glyphs a user reads are the pinned output.
// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class QuestionnaireTests
{
    private const int Width = 48;

    private static Questionnaire TwoSteps() => new([
        new QuestionnaireStep("name", "Your name", "Pick one or type another.", ["Ada", "Grace"], Required: true, Freeform: true),
        new QuestionnaireStep("detail", "Detail", null, ["Short", "Long"], Required: true),
    ]);

    private static string[] Lines(Questionnaire questionnaire, int width = Width) =>
        ScreenFixtures.PaintedLines(questionnaire.DesiredHeight, width,
            buffer => questionnaire.Paint(buffer, new Rect(0, 0, width, questionnaire.DesiredHeight)));

    private static string Bar(int width, int filled) => new string('█', filled) + new string('░', width - filled);

    [Test]
    public async Task Paint_Golden_FirstStepShowsTitleProgressChoicesInputAndHint()
    {
        var questionnaire = TwoSteps();

        string[] lines = Lines(questionnaire);
        string expected = string.Join("\n",
            "Step 1/2  Your name",
            Bar(Width, 24),
            "  Pick one or type another.",
            "> ( ) Ada",
            "  ( ) Grace",
            "  type an answer",
            "  [Enter] Next  [Esc] Back");

        await Assert.That(questionnaire.DesiredHeight).IsEqualTo(7);
        await Assert.That(string.Join("\n", lines)).IsEqualTo(expected);
    }

    [Test]
    public async Task Next_RequiredEmpty_StaysOnStepAndPaintsError()
    {
        var questionnaire = TwoSteps();

        await Assert.That(questionnaire.GoNext()).IsFalse();
        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(0);
        await Assert.That(questionnaire.Error).IsEqualTo("Required.");

        string[] lines = Lines(questionnaire);
        await Assert.That(lines[6]).IsEqualTo("  ! Required.");
        await Assert.That(lines[7]).IsEqualTo("  [Enter] Next  [Esc] Back");
        await Assert.That(questionnaire.DesiredHeight).IsEqualTo(8);
    }

    [Test]
    public async Task ChooseNextBack_PreservesDraftAndPaintsSelectedChoice()
    {
        var questionnaire = TwoSteps();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(questionnaire.Answer("name").Choice).IsEqualTo("Ada");

        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(1);
        string[] second = Lines(questionnaire);
        await Assert.That(second[0]).IsEqualTo("Step 2/2  Detail");
        await Assert.That(second[1]).IsEqualTo(Bar(Width, Width));
        await Assert.That(second[2]).IsEqualTo("> ( ) Short");
        await Assert.That(second[3]).IsEqualTo("  ( ) Long");
        await Assert.That(second[4]).IsEqualTo("  [Enter] Submit  [Esc] Back");

        await Assert.That(questionnaire.GoBack()).IsTrue();
        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(0);
        await Assert.That(questionnaire.Answer("name").Choice).IsEqualTo("Ada");
        await Assert.That(Lines(questionnaire)[3]).IsEqualTo("> (x) Ada");
    }

    [Test]
    public async Task Space_TogglesChoice_AndEnterSelectsFocusedChoiceWhenEmpty()
    {
        var questionnaire = TwoSteps();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(questionnaire.Answer("name").Choice).IsEqualTo("Ada");
        await Assert.That(questionnaire.HandleKey(KeyEvent.Char(new Rune(' ')))).IsTrue();
        await Assert.That(questionnaire.Answer("name").Choice).IsNull();

        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(questionnaire.Answer("name").Choice).IsEqualTo("Grace");
        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(1);
    }

    [Test]
    public async Task TabToInput_TypingStoresFreeformTextInDraft()
    {
        var questionnaire = TwoSteps();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(questionnaire.FocusedIndex).IsEqualTo(2);

        await Assert.That(questionnaire.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Char(new Rune('b')))).IsTrue();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Backspace))).IsTrue();
        await Assert.That(questionnaire.Answer("name").Text).IsEqualTo("a");
        await Assert.That(questionnaire.Answers()["name"].Text).IsEqualTo("a");
    }

    [Test]
    public async Task Submit_ValidatesEveryStep_AndJumpsToFirstInvalid()
    {
        var questionnaire = TwoSteps();
        questionnaire.SetAnswer("name", new QuestionnaireAnswer("Ada"));

        await Assert.That(questionnaire.GoNext()).IsTrue();
        await Assert.That(questionnaire.Submit()).IsFalse();
        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(1);
        await Assert.That(questionnaire.Error).IsEqualTo("Required.");
        await Assert.That(questionnaire.IsComplete).IsFalse();

        questionnaire.SetAnswer("detail", new QuestionnaireAnswer("Short"));
        await Assert.That(questionnaire.Submit()).IsTrue();
        await Assert.That(questionnaire.IsComplete).IsTrue();
        await Assert.That(string.Join("\n", Lines(questionnaire))).IsEqualTo("Done: 2/2");
        await Assert.That(questionnaire.DesiredHeight).IsEqualTo(1);
    }

    [Test]
    public async Task StepRule_ReceivesAnswerAndWholeDraft()
    {
        var questionnaire = new Questionnaire([
            new QuestionnaireStep("first", "First", null, ["one"], Required: true),
            new QuestionnaireStep("second", "Second", Validate: (answer, draft) =>
                string.Equals(answer.Text, draft["first"].Choice, StringComparison.Ordinal)
                    ? null
                    : "Must repeat the first answer."),
        ]);
        questionnaire.SetAnswer("first", new QuestionnaireAnswer("one"));
        await Assert.That(questionnaire.GoNext()).IsTrue();
        questionnaire.SetAnswer("second", new QuestionnaireAnswer(Text: "two"));

        await Assert.That(questionnaire.Submit()).IsFalse();
        await Assert.That(questionnaire.Error).IsEqualTo("Must repeat the first answer.");

        questionnaire.SetAnswer("second", new QuestionnaireAnswer(Text: "one"));
        await Assert.That(questionnaire.Submit()).IsTrue();
    }

    [Test]
    public async Task ExternalError_OverridesStepRule_AndClears()
    {
        var questionnaire = TwoSteps();
        questionnaire.SetExternalError("name", "Server rejected it.");

        await Assert.That(questionnaire.GoNext()).IsFalse();
        await Assert.That(questionnaire.Error).IsEqualTo("Server rejected it.");
        await Assert.That(Lines(questionnaire)[6]).IsEqualTo("  ! Server rejected it.");

        questionnaire.SetExternalError("name", null);
        await Assert.That(questionnaire.Error).IsEqualTo("Required.");
        questionnaire.SetAnswer("name", new QuestionnaireAnswer("Ada"));
        await Assert.That(questionnaire.Error).IsNull();
    }

    [Test]
    public async Task Skip_OptionalStep_MovesOnAndRecordsSkip()
    {
        var questionnaire = new Questionnaire([
            new QuestionnaireStep("name", "Name", Required: true, Freeform: true),
            new QuestionnaireStep("note", "Note", Freeform: true),
        ]);
        questionnaire.SetAnswer("name", new QuestionnaireAnswer(Text: "Ada"));
        await Assert.That(questionnaire.GoNext()).IsTrue();
        await Assert.That(questionnaire.Skip()).IsTrue();
        await Assert.That(questionnaire.IsComplete).IsTrue();
        await Assert.That(questionnaire.IsSkipped("note")).IsTrue();
        await Assert.That(questionnaire.Answers()["note"].IsEmpty).IsTrue();

        var requiredFirst = new Questionnaire([
            new QuestionnaireStep("must", "Must", Required: true, Freeform: true),
        ]);
        await Assert.That(requiredFirst.Skip()).IsFalse();
    }

    [Test]
    public async Task Reset_ClearsDraftErrorsAndCompletion()
    {
        var questionnaire = TwoSteps();
        questionnaire.SetAnswer("name", new QuestionnaireAnswer("Ada"));
        questionnaire.SetExternalError("detail", "Rejected.");
        await Assert.That(questionnaire.GoNext()).IsTrue();
        await Assert.That(questionnaire.Submit()).IsFalse();

        questionnaire.Reset();

        await Assert.That(questionnaire.CurrentIndex).IsEqualTo(0);
        await Assert.That(questionnaire.IsComplete).IsFalse();
        await Assert.That(questionnaire.Answers()["name"].IsEmpty).IsTrue();
        await Assert.That(questionnaire.ErrorAt("detail")).IsNull();
        await Assert.That(questionnaire.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
    }

    [Test]
    public async Task DegenerateRect_PaintsNothing_AndSchemaErrorsAreRejected()
    {
        var questionnaire = TwoSteps();
        var buffer = TestRender.Buffer(12, 4);
        questionnaire.Paint(buffer, new Rect(0, 0, 0, 0));
        questionnaire.Paint(buffer, new Rect(0, 0, 8, 0));
        questionnaire.Paint(buffer, new Rect(0, 0, 0, 4));
        await Assert.That(TestRender.Art(buffer).Trim().Length).IsEqualTo(0);

        await Assert.That(() => new Questionnaire([])).Throws<ArgumentException>();
        await Assert.That(() => new Questionnaire([
            new QuestionnaireStep("a", "A"),
            new QuestionnaireStep("a", "B"),
        ])).Throws<ArgumentException>();
    }
}
