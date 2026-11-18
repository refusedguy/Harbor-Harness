using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// The scaffold's contract: host-owned labels and errors, one field container,
// a titled group frame and an aligned description list. Goldens are inline and
// style-blind: the glyphs a user reads are the pinned output.
// Painting reads the global TerminalColorPalette — serialized with other paint tests.
[NotInParallel("pty")]
public class FormScaffoldTests
{
    private const int Width = 32;
    private const int InnerWidth = 30;

    private static string[] Lines(FormScaffold form, int width = Width)
    {
        int height = form.DesiredHeight;
        var buffer = TestRender.Buffer(width, height);
        form.Paint(buffer, new Rect(0, 0, width, height));
        return TestRender.Art(buffer).Split('\n')
            .Select(line => line.TrimEnd())
            .Take(height)
            .ToArray();
    }

    private static string Row(string inner) => "│" + inner.PadRight(InnerWidth) + "│";

    [Test]
    public async Task Paint_Golden_TitledGroupLabelFieldsAndDescription()
    {
        var form = new FormScaffold
        {
            Title = "Profile",
            Label = new FormLabel("Identity"),
        };
        form.Add(new StubField("name", "Ada"));
        form.Add(new StubField("role", "dev"));
        form.SetLabel(0, new FormLabel("name", "(required)"));
        form.SetError(0, "Name is required");
        form.AddDescription("mode", "local");
        form.AddDescription("region", "eu");

        string[] lines = Lines(form);
        string expected = string.Join("\n",
            "┌─Profile " + new string('─', 21) + "┐",
            Row("Identity"),
            Row("name (required)› Ada"),
            Row(new string(' ', 17) + "! Name is req"),
            Row("role" + new string(' ', 13) + "dev"),
            Row(new string('─', InnerWidth)),
            Row("mode    local"),
            Row("region  eu"),
            "└" + new string('─', InnerWidth) + "┘");

        await Assert.That(form.DesiredHeight).IsEqualTo(9);
        await Assert.That(string.Join("\n", lines)).IsEqualTo(expected);
    }

    [Test]
    public async Task ExternalError_OverridesFieldValidation_AndClears()
    {
        var field = new StubField("code", "x", "Internal error.");
        var form = new FormScaffold();
        form.Add(field);

        form.SetError(0, "External error.");
        await Assert.That(form.ValidateAll()[0]).IsEqualTo("External error.");
        await Assert.That(form.IsValid()).IsFalse();
        string[] painted = Lines(form);
        await Assert.That(painted[1]).IsEqualTo(Row("code› x"));
        await Assert.That(painted[2]).IsEqualTo(Row(new string(' ', 6) + "! External error."));

        form.SetError(0, null);
        await Assert.That(form.ValidateAll()[0]).IsEqualTo("Internal error.");
        await Assert.That(form.IsValid()).IsFalse();
        await Assert.That(Lines(form)[2]).IsEqualTo(Row(new string(' ', 6) + "! Internal error."));
    }

    [Test]
    public async Task ValidForm_ReportsNoErrors()
    {
        var form = new FormScaffold();
        form.Add(new StubField("name", "Ada"));

        await Assert.That(form.IsValid()).IsTrue();
        await Assert.That(form.ValidateAll()[0]).IsNull();
        await Assert.That(form.DesiredHeight).IsEqualTo(3);
    }

    [Test]
    public async Task Tab_CyclesFocus_AndTypingReachesFocusedField()
    {
        var first = new StubField("first");
        var second = new StubField("second");
        var form = new FormScaffold();
        form.Add(first);
        form.Add(second);

        await Assert.That(form.FocusedIndex).IsEqualTo(0);
        await Assert.That(form.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(first.Value).IsEqualTo("a");

        await Assert.That(form.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsTrue();
        await Assert.That(form.FocusedIndex).IsEqualTo(1);
        await Assert.That(form.HandleKey(KeyEvent.Char(new Rune('b')))).IsTrue();
        await Assert.That(second.Value).IsEqualTo("b");
        await Assert.That(first.Value).IsEqualTo("a");
    }

    [Test]
    public async Task DescriptionList_AlignsValuesToWidestKey()
    {
        var description = new DescriptionList();
        description.Add("a", "1");
        description.Add("longer", "2");

        var buffer = TestRender.Buffer(24, description.Count);
        description.Paint(buffer, new Rect(0, 0, 24, description.Count));
        string[] lines = TestRender.Art(buffer).Split('\n')
            .Select(line => line.TrimEnd())
            .Take(description.Count)
            .ToArray();

        // Widest key is 6 cells, values start at column 8.
        await Assert.That(lines[0]).IsEqualTo("a      1");
        await Assert.That(lines[1]).IsEqualTo("longer  2");
        await Assert.That(description.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NarrowTitle_TruncatesInsideTheFrame()
    {
        var form = new FormScaffold { Title = "A very long title" };
        form.Add(new StubField("x", "y"));

        string[] lines = Lines(form, 16);

        await Assert.That(lines[0].StartsWith("┌─")).IsTrue();
        await Assert.That(lines[0].Contains("…")).IsTrue();
        await Assert.That(lines[0].EndsWith("┐")).IsTrue();
        await Assert.That(lines[0].Length).IsEqualTo(16);
    }

    [Test]
    public async Task DegenerateRect_PaintsNothingAndDoesNotThrow()
    {
        var form = new FormScaffold { Title = "x" };
        form.Add(new StubField("f", "v"));
        var buffer = TestRender.Buffer(12, 4);

        form.Paint(buffer, new Rect(0, 0, 0, 0));
        form.Paint(buffer, new Rect(0, 0, 1, 4));
        form.Paint(buffer, new Rect(0, 0, 4, 1));

        await Assert.That(TestRender.Art(buffer).Trim().Length).IsEqualTo(0);
        await Assert.That(form.DesiredHeight).IsEqualTo(3);
    }

    private sealed class StubField : IGenericFormField
    {
        public StubField(string label, string value = "", string? error = null)
        {
            Label = label;
            Value = value;
            InternalError = error;
        }

        public string Label { get; }

        public string Value { get; private set; }

        public string? InternalError { get; set; }

        public int DesiredHeight => 1;

        public void Paint(ScreenBuffer buffer, Rect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0 || Value.Length == 0)
            {
                return;
            }

            buffer.SetText(rect.X, rect.Y, Value.AsSpan(0, Math.Min(Value.Length, rect.Width)), new CellStyle());
        }

        public bool HandleKey(in KeyEvent key)
        {
            if (key.EventType != KeyEventType.Press || key.Key != KeyCode.Char)
            {
                return false;
            }

            Value += key.Character.ToString();
            return true;
        }

        public string? Validate() => InternalError;
    }
}
