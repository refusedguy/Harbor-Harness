using System;
using System.Collections.Generic;
using System.Text;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Generic.Forms;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Behavior coverage for the domain-free form primitives: the rule set,
///     the masked input, the error painter, the container, and two hosts from
///     different scenarios built from the same types.
/// </summary>
public class DeclarativeFormPrimitivesTests
{
    [Test]
    public async Task Validators_RequiredLengthPatternAndCrossField_AreDomainFree()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "abc",
            ["other"] = "abc",
        };
        var context = new FieldValidationContext("id", "abc", values);
        var blank = new FieldValidationContext("id", "  ", values);

        await Assert.That(FieldValidators.Required().Name).IsEqualTo("required");
        await Assert.That(FieldValidators.Required().Validate(context)).IsNull();
        await Assert.That(FieldValidators.Required().Validate(blank)).IsEqualTo("Value is required.");

        await Assert.That(FieldValidators.MinLength(4).Validate(context))
            .IsEqualTo("Use at least 4 characters.");
        await Assert.That(FieldValidators.MaxLength(2).Validate(context))
            .IsEqualTo("Use at most 2 characters.");
        await Assert.That(FieldValidators.MinLength(2).Validate(context)).IsNull();
        await Assert.That(FieldValidators.MaxLength(4).Validate(context)).IsNull();

        await Assert.That(FieldValidators.Matches("^[a-z]+$").Validate(context)).IsNull();
        await Assert.That(FieldValidators.Matches("^[0-9]+$").Validate(context))
            .IsEqualTo("Value has an invalid format.");

        await Assert.That(FieldValidators.MatchesField("other").Validate(context)).IsNull();
        await Assert.That(FieldValidators.MatchesField("missing").Validate(context))
            .IsEqualTo("Values must match.");
    }

    [Test]
    public async Task MaskedTextInput_PaintsMaskAndKeepsTheRealValue()
    {
        var descriptor = new FieldDescriptor(
            "code",
            "Code",
            FieldKind.Masked,
            mask: new Rune('*'),
            maxLength: 8);
        var input = new MaskedTextInput(descriptor);
        input.SetText("abc123");

        await Assert.That(input.Text).IsEqualTo("abc123");
        await Assert.That(input.Descriptor.Kind).IsEqualTo(FieldKind.Masked);
        await Assert.That(input.Validate()).IsNull();

        var buffer = TestRender.Buffer(20, 1);
        input.Paint(buffer, new Rect(0, 0, 12, 1));
        string art = TestRender.Art(buffer);

        await Assert.That(art.Contains('*')).IsTrue();
        await Assert.That(art.Contains('a')).IsFalse();
        await Assert.That(art.Contains('1')).IsFalse();
    }

    [Test]
    public async Task FieldErrorDisplay_PaintsOneClippedRowPerError()
    {
        var display = new FieldErrorDisplay { Prefix = "? " };
        var errors = new[] { new FieldError("a", "first"), new FieldError("b", "second") };

        await Assert.That(display.DesiredHeight(errors)).IsEqualTo(2);

        var buffer = TestRender.Buffer(8, 3);
        display.Paint(buffer, new Rect(0, 0, 8, 3), errors);
        string art = TestRender.Art(buffer);

        await Assert.That(art).Contains("? first");
        await Assert.That(art).Contains("? secon");
    }

    [Test]
    public async Task DeclarativeForm_ValidatesEveryFieldWithASharedSnapshot()
    {
        var form = new DeclarativeForm(new[]
        {
            new FieldDescriptor("term", "Term", validators: new FieldValidator[]
            {
                FieldValidators.Required(),
                FieldValidators.Matches("^[a-z]+$"),
            }),
            new FieldDescriptor("repeat", "Repeat", FieldKind.Masked, new Rune('*'), validators: new FieldValidator[]
            {
                FieldValidators.MatchesField("term"),
            }),
        });

        await Assert.That(form.Descriptors.Count).IsEqualTo(2);
        await Assert.That(form.IsValid()).IsFalse();
        await Assert.That(form.Errors().Count).IsEqualTo(2);

        form.SetValue("term", "abc");
        form.SetValue("repeat", "abd");
        await Assert.That(form.IsValid()).IsFalse();
        await Assert.That(form.Errors()[0].FieldId).IsEqualTo("repeat");
        await Assert.That(form.Values["term"]).IsEqualTo("abc");

        form.SetValue("repeat", "abc");
        await Assert.That(form.IsValid()).IsTrue();
        await Assert.That(form.ValidateAll()[0]).IsNull();

        form.SetExternalError("term", "host rejected");
        await Assert.That(form.IsValid()).IsFalse();
        form.SetExternalError("term", null);
        await Assert.That(form.IsValid()).IsTrue();

        form.MoveFocus(1);
        await Assert.That(form.FocusedIndex).IsEqualTo(1);

        var buffer = TestRender.Buffer(30, 6);
        form.Paint(buffer, new Rect(0, 0, 30, 6));
        await Assert.That(form.DesiredHeight).IsEqualTo(2);
        await Assert.That(TestRender.Art(buffer)).Contains("Term");
    }

    [Test]
    public async Task TwoScenarioHosts_UseTheSamePrimitiveSet()
    {
        var options = new OptionsFormHost();
        var query = new QueryFormHost();

        await Assert.That(options.Form.GetType()).IsEqualTo(typeof(DeclarativeForm));
        await Assert.That(query.Form.GetType()).IsEqualTo(typeof(DeclarativeForm));
        await Assert.That(options.Form.Descriptors[0].Validators[0].Name).IsEqualTo("required");
        await Assert.That(query.Form.Descriptors[0].Validators[1].Name).IsEqualTo("pattern");
        await Assert.That(query.Form.Descriptors[1].Kind).IsEqualTo(FieldKind.Masked);

        await Assert.That(options.TryApply()).IsFalse();
        options.Form.SetValue("display-name", "Panel");
        options.Form.SetValue("depth", "12");
        await Assert.That(options.TryApply()).IsTrue();
        await Assert.That(options.Snapshot()["depth"]).IsEqualTo("12");

        await Assert.That(query.TryRun()).IsFalse();
        query.Form.SetValue("term", "abc");
        query.Form.SetValue("repeat", "abc");
        await Assert.That(query.TryRun()).IsTrue();
        await Assert.That(query.Snapshot()["term"]).IsEqualTo("abc");
    }
}
