using System.Globalization;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [compose/spectre] SC6 form framework (#1214, epic #1195): generic prompt +
/// chained validators + converters + defaults + secret + injectable input.
/// Pure state transitions — no backends, no keys (the host owns the key gate).
/// </summary>
public class FormPromptEngineTests
{
    [Test]
    public async Task String_RoundTrip_SubmitsVerbatim()
    {
        var prompt = new FormPromptState<string>(FormConverters.String());
        prompt.SetText("hello");

        bool ok = prompt.TrySubmit(out string value, out string error);

        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo("hello");
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That(prompt.Error).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Validators_ChainInOrder_FirstFailureWins()
    {
        var prompt = new FormPromptState<string>(FormConverters.String());
        prompt.AddValidator(static _ => "first");
        prompt.AddValidator(static _ => "second");
        prompt.SetText("x");

        await Assert.That(prompt.Validate()).IsEqualTo("first");

        var chained = new FormPromptState<string>(FormConverters.String());
        chained.AddValidator(static _ => string.Empty);
        chained.AddValidator(static _ => "second");
        chained.SetText("x");

        await Assert.That(chained.Validate()).IsEqualTo("second");
    }

    [Test]
    public async Task Validate_CalledTwice_Appends_NothingOverwritten()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.AddValidator(static v => v > 0 ? string.Empty : "must be positive");
        prompt.SetText("5");

        await Assert.That(prompt.Validate()).IsEqualTo(string.Empty);

        // Second Validate() call adds (Spectre wrapping semantics) — the old rule still holds.
        prompt.AddValidator(static v => v < 10 ? string.Empty : "must be small");
        prompt.SetText("50");

        await Assert.That(prompt.Validate()).IsEqualTo("must be small");
        prompt.SetText("-1");

        await Assert.That(prompt.Validate()).IsEqualTo("must be positive");
    }

    [Test]
    public async Task Int_InvalidText_ReportsConverterError()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.SetText("abc");

        bool ok = prompt.TrySubmit(out _, out string error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error.Contains("abc")).IsTrue();
        await Assert.That(prompt.Error).IsEqualTo(error);
    }

    [Test]
    public async Task Double_UsesPromptCulture_ForCommaDecimals()
    {
        var prompt = new FormPromptState<double>(FormConverters.Double(), new CultureInfo("fr-FR"));
        prompt.SetText("1,5");

        bool ok = prompt.TrySubmit(out double value, out string error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsEqualTo(string.Empty);
        await Assert.That(value).IsEqualTo(1.5);
    }

    [Test]
    public async Task Empty_WithoutDefault_RequiresValue()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.SetText(string.Empty);

        await Assert.That(prompt.Validate()).IsEqualTo("Value is required.");
    }

    [Test]
    public async Task Empty_WithDefault_SubmitsDefault_AndValidatesIt()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.SetDefault(7);
        prompt.AddValidator(static v => v == 7 ? "unlucky" : string.Empty);
        prompt.SetText(string.Empty);

        bool ok = prompt.TrySubmit(out int value, out string error);

        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(7);
        await Assert.That(error).IsEqualTo("unlucky");
    }

    [Test]
    public async Task Empty_AllowEmpty_String_SubmitsEmpty()
    {
        var prompt = new FormPromptState<string>(FormConverters.String()) { AllowEmpty = true };
        prompt.SetText(string.Empty);

        bool ok = prompt.TrySubmit(out string value, out string error);

        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(string.Empty);
        await Assert.That(error).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Secret_MasksEcho_AndKeepsText()
    {
        var prompt = new FormPromptState<string>(FormConverters.String()) { IsSecret = true, MaskChar = '*' };
        prompt.SetText("s3cr3t");

        await Assert.That(prompt.MaskedText()).IsEqualTo("*******");
        await Assert.That(prompt.Text).IsEqualTo("s3cr3t");
    }

    [Test]
    public async Task Choices_RejectsUnlisted_AcceptsListed()
    {
        var prompt = new FormPromptState<string>(FormConverters.String());
        prompt.SetChoices(["a", "b"]);
        prompt.SetText("c");

        await Assert.That(prompt.Validate()).IsEqualTo("Value is not one of the available choices.");

        prompt.SetText("b");

        await Assert.That(prompt.Validate()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextValidator_RunsBeforeConversion()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.AddTextValidator(static t => t.StartsWith("0x", StringComparison.Ordinal) ? "hex not allowed" : string.Empty);
        prompt.SetText("0x10");

        await Assert.That(prompt.Validate()).IsEqualTo("hex not allowed");
    }

    [Test]
    public async Task ResetToDefault_FormatsDefault_IntoText()
    {
        var prompt = new FormPromptState<int>(FormConverters.Int32());
        prompt.SetDefault(42);
        prompt.SetText("junk");

        prompt.ResetToDefault();

        await Assert.That(prompt.Text).IsEqualTo("42");
        await Assert.That(prompt.Error).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task QueueSource_Submits_Cancels_And_ReportsInvalid()
    {
        var source = new QueueFormInputSource();
        source.Enqueue("5");
        source.Enqueue("abc");
        var prompt = new FormPromptState<int>(FormConverters.Int32());

        FormPromptOutcome first = prompt.ReadAndSubmit(source, out int v1, out _);
        FormPromptOutcome second = prompt.ReadAndSubmit(source, out _, out string error2);
        FormPromptOutcome third = prompt.ReadAndSubmit(source, out _, out _);

        await Assert.That(first).IsEqualTo(FormPromptOutcome.Submitted);
        await Assert.That(v1).IsEqualTo(5);
        await Assert.That(second).IsEqualTo(FormPromptOutcome.Invalid);
        await Assert.That(error2).IsNotEqualTo(string.Empty);
        await Assert.That(third).IsEqualTo(FormPromptOutcome.Cancelled);
    }

    [Test]
    public async Task Bool_AcceptsYesNoWords_CaseInsensitive()
    {
        var prompt = new FormPromptState<bool>(FormConverters.Boolean());
        prompt.SetText("YES");

        bool ok = prompt.TrySubmit(out bool value, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsTrue();

        prompt.SetText("maybe");

        await Assert.That(prompt.Validate().Contains("maybe")).IsTrue();
    }

    [Test]
    public async Task Confirm_Empty_SubmitsDefault_YesNo_Parse()
    {
        var confirm = new FormConfirmState { DefaultChoice = false };
        confirm.SetText(string.Empty);

        bool ok = confirm.TrySubmit(out bool choice, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(choice).IsFalse();

        confirm.SetText("Y");
        await Assert.That(confirm.Validate()).IsEqualTo(string.Empty);

        confirm.SetText("nope");
        await Assert.That(confirm.Validate().Contains("nope")).IsTrue();
    }
}
