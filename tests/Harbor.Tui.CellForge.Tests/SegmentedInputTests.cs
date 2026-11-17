using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2364: the segmented-input primitive behind OtpInput and TimeField. State is
/// external, so every test drives an immutable SegmentedInputState and asserts
/// the exact state the engine returned (old states are checked for stability).
/// </summary>
public class SegmentedInputTests
{
    [Test]
    public async Task Otp_EmptyState_StartsOnFirstSegment()
    {
        var otp = SegmentedInput.Otp();

        var state = otp.Create();

        await Assert.That(state.Values.Length).IsEqualTo(6);
        await Assert.That(state.ActiveIndex).IsEqualTo(0);
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Values[0]).IsEqualTo(string.Empty);
        await Assert.That(otp.Render(state)).IsEqualTo(string.Empty);
        await Assert.That(otp.IsComplete(state)).IsFalse();
    }

    [Test]
    public async Task Otp_TypingDigits_AutoAdvanceAndRender()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.Create();

        for (char c = '1'; c <= '6'; c++)
        {
            state = otp.InsertChar(state, c);
        }

        await Assert.That(state.Values).IsEquivalentTo(new[] { "1", "2", "3", "4", "5", "6" });
        await Assert.That(state.ActiveIndex).IsEqualTo(5);
        await Assert.That(otp.Render(state)).IsEqualTo("123456");
        await Assert.That(otp.IsComplete(state)).IsTrue();
    }

    [Test]
    public async Task Otp_Paste_DistributesAcrossEverySegment()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.Paste(otp.Create(), "123456");

        await Assert.That(state.Values).IsEquivalentTo(new[] { "1", "2", "3", "4", "5", "6" });
        await Assert.That(state.ActiveIndex).IsEqualTo(5);
        await Assert.That(otp.Render(state)).IsEqualTo("123456");
        await Assert.That(otp.IsComplete(state)).IsTrue();
    }

    [Test]
    public async Task Otp_Paste_IgnoresSeparatorsAndWhitespace()
    {
        var grouped = SegmentedInput.Otp(6, " ");
        var state = grouped.Paste(grouped.Create(), "123 456");

        await Assert.That(grouped.Render(state)).IsEqualTo("123 456");
        await Assert.That(state.ActiveIndex).IsEqualTo(5);
        await Assert.That(grouped.IsComplete(state)).IsTrue();

        var plain = SegmentedInput.Otp();
        await Assert.That(plain.Render(plain.Paste(plain.Create(), "12-34-56"))).IsEqualTo("123456");
    }

    [Test]
    public async Task Otp_InvalidCharacter_IsIgnored()
    {
        var otp = SegmentedInput.Otp();
        var original = otp.Create();
        var state = otp.InsertChar(original, 'x');

        await Assert.That(state.Values[0]).IsEqualTo(string.Empty);
        await Assert.That(state.ActiveIndex).IsEqualTo(0);
        await Assert.That(ReferenceEquals(state, original)).IsTrue();
    }

    [Test]
    public async Task Otp_Backspace_ClearsCurrentThenCrossesIntoPreviousSegment()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.CreateFrom(new[] { "1", "2", "3", "4", "5", "6" }, 2);

        state = otp.Backspace(state);
        await Assert.That(state.Values[2]).IsEqualTo(string.Empty);
        await Assert.That(state.ActiveIndex).IsEqualTo(2);

        state = otp.Backspace(state);
        await Assert.That(state.Values[1]).IsEqualTo(string.Empty);
        await Assert.That(state.ActiveIndex).IsEqualTo(1);
        await Assert.That(state.Cursor).IsEqualTo(0);
    }

    [Test]
    public async Task Navigation_ClampsAtEnds()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.Create();

        state = otp.MovePrevious(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(0);
        state = otp.MoveNext(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(1);
        state = otp.MoveEnd(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(5);
        state = otp.MoveNext(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(5);
        state = otp.MoveHome(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Navigation_WrapsOnlyWhenConfigured()
    {
        var engine = new SegmentedInput(new SegmentedInputConfig(
            new[] { SegmentSpec.Digits(), SegmentSpec.Digits(), SegmentSpec.Digits() },
            new[] { string.Empty, string.Empty },
            wrapNavigation: true));

        var state = engine.MovePrevious(engine.Create());
        await Assert.That(state.ActiveIndex).IsEqualTo(2);
        state = engine.MoveNext(state);
        await Assert.That(state.ActiveIndex).IsEqualTo(0);
    }

    [Test]
    public async Task SetSegment_KeepsOnlyAllowedCharactersUpToCapacity()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.SetSegment(otp.Create(), 0, "a1b2");

        await Assert.That(state.Values[0]).IsEqualTo("1");
        await Assert.That(state.Cursor).IsEqualTo(1);
    }

    [Test]
    public async Task Delete_RemovesCharacterAtCursor()
    {
        var engine = SegmentedInput.Time24();
        var filled = engine.CreateFrom(new[] { "09", "30" }, 1);

        // At the end of a filled segment there is nothing to delete.
        await Assert.That(ReferenceEquals(engine.Delete(filled), filled)).IsTrue();

        var state = engine.MoveCursorLeft(filled);
        await Assert.That(state.Cursor).IsEqualTo(1);
        state = engine.Delete(state);
        await Assert.That(state.Values[1]).IsEqualTo("3");
        await Assert.That(state.Cursor).IsEqualTo(1);
        await Assert.That(filled.Values[1]).IsEqualTo("30");
    }

    [Test]
    public async Task StatesAreImmutable_OldStateSurvivesEdits()
    {
        var otp = SegmentedInput.Otp();
        var original = otp.Create();
        var edited = otp.Paste(original, "12");

        await Assert.That(original.Values[0]).IsEqualTo(string.Empty);
        await Assert.That(original.Values[1]).IsEqualTo(string.Empty);
        await Assert.That(edited.Values[0]).IsEqualTo("1");
        await Assert.That(edited.Values[1]).IsEqualTo("2");
    }

    [Test]
    public async Task Clear_ResetsValuesAndFocus()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.Paste(otp.Create(), "123456");
        state = otp.Clear(state);

        await Assert.That(state.ActiveIndex).IsEqualTo(0);
        await Assert.That(otp.Render(state)).IsEqualTo(string.Empty);
        await Assert.That(otp.IsComplete(state)).IsFalse();
    }

    [Test]
    public async Task HandleKey_MapsNavigationAndIgnoresModifiedChords()
    {
        var otp = SegmentedInput.Otp();
        var state = otp.HandleKey(otp.Create(), new EngineInput.UiKeyDto(EngineInput.UiKeyKind.Right));
        await Assert.That(state.ActiveIndex).IsEqualTo(1);

        state = otp.HandleKey(state, new EngineInput.UiKeyDto(EngineInput.UiKeyKind.Left));
        await Assert.That(state.ActiveIndex).IsEqualTo(0);

        state = otp.HandleKey(state, EngineInput.UiKeyDto.ForChar('1'));
        await Assert.That(state.Values[0]).IsEqualTo("1");
        await Assert.That(state.ActiveIndex).IsEqualTo(1);

        var chord = new EngineInput.UiKeyDto(
            EngineInput.UiKeyKind.Char,
            EngineInput.UiKeyMods.Ctrl,
            '9');
        state = otp.HandleKey(state, chord);
        await Assert.That(state.Values[1]).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Time24_Validation_RejectsOutOfRangeAndIncomplete()
    {
        var time = SegmentedInput.Time24();

        var badHour = time.CreateFrom(new[] { "25", "00" });
        var bad = time.Validate(badHour);
        await Assert.That(bad.IsValid).IsFalse();
        await Assert.That(bad.InvalidSegment).IsEqualTo(0);
        await Assert.That(bad.Error).IsEqualTo("Segment 0 is invalid.");

        var incomplete = time.Validate(time.CreateFrom(new[] { "09", "3" }));
        await Assert.That(incomplete.InvalidSegment).IsEqualTo(1);
        await Assert.That(incomplete.Error).IsEqualTo("Segment 1 is incomplete.");
    }

    [Test]
    public async Task Time12_MeridiemNormalizesAndCompletes()
    {
        var time = SegmentedInput.Time12();
        var state = time.Create();

        foreach (char c in "0930p")
        {
            state = time.InsertChar(state, c);
        }

        await Assert.That(state.Values).IsEquivalentTo(new[] { "09", "30", "PM" });
        await Assert.That(state.ActiveIndex).IsEqualTo(2);
        await Assert.That(time.Render(state)).IsEqualTo("09:30 PM");
        await Assert.That(time.Validate(state).IsValid).IsTrue();
    }

    [Test]
    public async Task Config_RejectsMismatchedSeparators()
    {
        var refused = Assert.Throws<ArgumentException>(() => new SegmentedInputConfig(
            new[] { SegmentSpec.Digits(), SegmentSpec.Digits() },
            Array.Empty<string>()));

        await Assert.That(refused.Message.Contains("Separators count", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Paint_MarksActiveSegmentBoldAndOthersDim()
    {
        var engine = SegmentedInput.Time24();
        var state = engine.CreateFrom(new[] { "09", "30" }, 1);

        var buffer = new EngineCells.ScreenBuffer(8, 1);
        int width = engine.Paint(buffer, 0, 0, state);

        await Assert.That(width).IsEqualTo(5);
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('0');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Dim);
        await Assert.That(buffer.Get(2, 0).Rune).IsEqualTo(':');
        await Assert.That(buffer.Get(2, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.None);
        await Assert.That(buffer.Get(3, 0).Rune).IsEqualTo('3');
        await Assert.That(buffer.Get(3, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
        await Assert.That(buffer.Get(4, 0).Rune).IsEqualTo('0');
        await Assert.That(buffer.Get(4, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
    }
}

/// <summary>
/// #2364 golden table: a paste payload per profile maps to one exact rendered
/// string, active index and completeness verdict. Any change to distribution,
/// separators or validation shows up as a table-row mismatch.
/// </summary>
public class SegmentedInputGoldenTests
{
    [Test]
    [Arguments("otp6", "123456", "123456", 5, true)]
    [Arguments("otp6-group", "123 456", "123 456", 5, true)]
    [Arguments("otp6", "12-34-56", "123456", 5, true)]
    [Arguments("otp6", "12a456", "12456", 5, false)]
    [Arguments("time24", "0930", "09:30", 1, true)]
    [Arguments("time24", "23:59", "23:59", 1, true)]
    [Arguments("time24", "2500", "25:00", 1, false)]
    [Arguments("time12", "0930p", "09:30 PM", 2, true)]
    [Arguments("time12", "12:30 a", "12:30 AM", 2, true)]
    public async Task Paste_ProfilesProduceGoldenState(
        string profile,
        string payload,
        string expectedRender,
        int expectedActive,
        bool expectedComplete)
    {
        var engine = profile switch
        {
            "otp6" => SegmentedInput.Otp(),
            "otp6-group" => SegmentedInput.Otp(6, " "),
            "time24" => SegmentedInput.Time24(),
            "time12" => SegmentedInput.Time12(),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

        var state = engine.Paste(engine.Create(), payload);

        await Assert.That(engine.Render(state)).IsEqualTo(expectedRender);
        await Assert.That(state.ActiveIndex).IsEqualTo(expectedActive);
        await Assert.That(engine.IsComplete(state)).IsEqualTo(expectedComplete);
    }
}
