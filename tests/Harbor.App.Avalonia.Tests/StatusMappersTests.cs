using Harbor.Abstractions.Models;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.ViewModels;
namespace Harbor.App.Avalonia.Tests;
/// <summary>
///     Unit tests for the platform-agnostic <see cref="StatusMappers" />
///     helpers. These ensure the resource-key / display-string lookups
///     stay stable across UI frameworks (Avalonia / WPF / MAUI / Blazor
///     all wrap these functions in their own IValueConverter adapters).
/// </summary>
/// <remarks>
///     No UI framework is loaded — these tests run in headless mode and
///     exercise pure C# logic. They guard against accidental drift the
///     next time someone adds a new status or renames a resource key.
/// </remarks>
public class StatusMappersTests
{
    // ── StatusToBrushKey ────────────────────────────────────────────

    [Test]
    public async Task StatusToBrushKey_Running_ReturnsRunningBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey("running"))
            .IsEqualTo("StatusRunningBrush");
    }

    [Test]
    public async Task StatusToBrushKey_Compacting_ReturnsCompactBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey("compacting"))
            .IsEqualTo("StatusCompactBrush");
    }

    [Test]
    public async Task StatusToBrushKey_Error_ReturnsErrorBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey("error"))
            .IsEqualTo("StatusErrorBrush");
    }

    [Test]
    public async Task StatusToBrushKey_Idle_ReturnsIdleBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey("idle"))
            .IsEqualTo("StatusIdleBrush");
    }

    [Test]
    public async Task StatusToBrushKey_Unknown_ReturnsIdleBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey("xyz"))
            .IsEqualTo("StatusIdleBrush");
    }

    [Test]
    public async Task StatusToBrushKey_Null_ReturnsIdleBrush()
    {
        await Assert.That(StatusMappers.StatusToBrushKey(null))
            .IsEqualTo("StatusIdleBrush");
    }

    // ── ToolCallStateToBrushKey ────────────────────────────────────

    [Test]
    public async Task ToolCallStateToBrushKey_Running_ReturnsYellow()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Running))
            .IsEqualTo("MochaYellow");
    }

    [Test]
    public async Task ToolCallStateToBrushKey_Success_ReturnsGreen()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Success))
            .IsEqualTo("MochaGreen");
    }

    [Test]
    public async Task ToolCallStateToBrushKey_Error_ReturnsRed()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Error))
            .IsEqualTo("MochaRed");
    }

    // ── ToolCallStateToPill ────────────────────────────────────────

    [Test]
    public async Task ToolCallStateToPill_Running_ReturnsRunningLabel()
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Running))
            .IsEqualTo("running");
    }

    [Test]
    public async Task ToolCallStateToPill_Success_ReturnsOkLabel()
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Success))
            .IsEqualTo("ok");
    }

    [Test]
    public async Task ToolCallStateToPill_Error_ReturnsErrLabel()
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Error))
            .IsEqualTo("err");
    }

    // #567: the two terminal states the old `_ =>` arms could not express. Both
    // used to fall into the unknown-state arm, which read "?" for the pill and
    // "MochaOverlay2" for the brush — and, worse, painted as still-running in
    // CellForge. A stopped call must never borrow the live presentation.
    [Test]
    public async Task ToolCallStateToPill_Cancelled_And_TimedOut_Are_Not_Running()
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Cancelled))
            .IsEqualTo("cancelled");
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.TimedOut))
            .IsEqualTo("timeout");

        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.Cancelled))
            .IsNotEqualTo(StatusMappers.ToolCallStateToPill(ToolCallState.Running));
        await Assert.That(StatusMappers.ToolCallStateToPill(ToolCallState.TimedOut))
            .IsNotEqualTo(StatusMappers.ToolCallStateToPill(ToolCallState.Running));
    }

    [Test]
    public async Task ToolCallStateToBrushKey_Cancelled_Is_Neutral_TimedOut_Is_Red()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Cancelled))
            .IsEqualTo("MochaOverlay1");
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.TimedOut))
            .IsEqualTo("MochaRed");

        // Cancelled is deliberate, not a fault — it must not wear the error brush.
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Cancelled))
            .IsNotEqualTo(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Error));
    }

    /// <summary>
    /// The brush half of the #567 guard. "Neutral, not a fault" is a claim about
    /// fault-ness, not about lifecycle: folding Cancelled onto Pending's key made
    /// a deliberately stopped card paint identically to one that never started,
    /// and the two were distinguishable only by pill text.
    /// </summary>
    [Test]
    public async Task ToolCallStateToBrushKey_Cancelled_Does_Not_Borrow_Pending()
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Cancelled))
            .IsNotEqualTo(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Pending))
            .Because("a stopped call must not look like one that has not started");
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Cancelled))
            .IsNotEqualTo(StatusMappers.ToolCallStateToBrushKey(ToolCallState.Running))
            .Because("a stopped call must not look like a live one");
    }

    /// <summary>
    /// Every declared state must have a non-empty pill and brush, and no state may
    /// resolve to the old unknown-state placeholder. Walks
    /// <c>Enum.GetValues</c> so adding a member without a presentation is a test
    /// failure (#567).
    /// </summary>
    [Test]
    public async Task Every_ToolCallState_Has_Pill_And_Brush()
    {
        foreach (ToolCallState state in Enum.GetValues<ToolCallState>())
        {
            await Assert.That(StatusMappers.ToolCallStateToPill(state)).IsNotEmpty()
                .Because($"{state} needs a pill label");
            await Assert.That(StatusMappers.ToolCallStateToPill(state)).IsNotEqualTo("?")
                .Because($"{state} hit the unknown-state placeholder");
            await Assert.That(StatusMappers.ToolCallStateToBrushKey(state)).IsNotEmpty()
                .Because($"{state} needs a brush key");
        }
    }

    [Test]
    public async Task IsTerminal_Classifies_Each_State_Once()
    {
        foreach (ToolCallState state in Enum.GetValues<ToolCallState>())
        {
            bool expected = state is ToolCallState.Success or ToolCallState.Error
                or ToolCallState.Cancelled or ToolCallState.TimedOut;
            await Assert.That(state.IsTerminal()).IsEqualTo(expected)
                .Because($"{state} terminal classification drifted");
        }
    }

    // ── SessionStatusToText ────────────────────────────────────────

    [Test]
    public async Task SessionStatusToText_Working_ReturnsWorkingLabel()
    {
        await Assert.That(StatusMappers.SessionStatusToText(SessionStatus.Working))
            .IsEqualTo("working");
    }

    [Test]
    public async Task SessionStatusToText_Done_ReturnsDoneLabel()
    {
        await Assert.That(StatusMappers.SessionStatusToText(SessionStatus.Done))
            .IsEqualTo("done");
    }

    [Test]
    public async Task SessionStatusToText_Error_ReturnsErrorLabel()
    {
        await Assert.That(StatusMappers.SessionStatusToText(SessionStatus.Error))
            .IsEqualTo("error");
    }

    [Test]
    public async Task SessionStatusToText_Aborted_ReturnsAbortedLabel()
    {
        await Assert.That(StatusMappers.SessionStatusToText(SessionStatus.Aborted))
            .IsEqualTo("aborted");
    }

    [Test]
    public async Task SessionStatusToText_Idle_ReturnsIdleLabel()
    {
        await Assert.That(StatusMappers.SessionStatusToText(SessionStatus.Idle))
            .IsEqualTo("idle");
    }

    // ── SessionStatusToBrushKey ────────────────────────────────────

    [Test]
    public async Task SessionStatusToBrushKey_Working_ReturnsYellow()
    {
        await Assert.That(StatusMappers.SessionStatusToBrushKey(SessionStatus.Working))
            .IsEqualTo("MochaYellow");
    }

    [Test]
    public async Task SessionStatusToBrushKey_Done_ReturnsGreen()
    {
        await Assert.That(StatusMappers.SessionStatusToBrushKey(SessionStatus.Done))
            .IsEqualTo("MochaGreen");
    }

    [Test]
    public async Task SessionStatusToBrushKey_Error_ReturnsRed()
    {
        await Assert.That(StatusMappers.SessionStatusToBrushKey(SessionStatus.Error))
            .IsEqualTo("MochaRed");
    }

    [Test]
    public async Task SessionStatusToBrushKey_Idle_ReturnsOverlay0()
    {
        await Assert.That(StatusMappers.SessionStatusToBrushKey(SessionStatus.Idle))
            .IsEqualTo("MochaOverlay0");
    }

    // ── DurationToText ─────────────────────────────────────────────

    [Test]
    public async Task DurationToText_SubMillisecond_ReturnsEmpty()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMicroseconds(500)))
            .IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DurationToText_BelowOneSecond_ReturnsMilliseconds()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMilliseconds(234)))
            .IsEqualTo("234ms");
    }

    [Test]
    public async Task DurationToText_AboveOneSecond_ReturnsSeconds()
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMilliseconds(1500)))
            .IsEqualTo("1.5s");
    }

    // ── TimeAgo ────────────────────────────────────────────────────

    [Test]
    public async Task TimeAgo_Null_ReturnsEmpty() => await Assert.That(StatusMappers.TimeAgo(null)).IsEqualTo(string.Empty);

    [Test]
    public async Task TimeAgo_MinValue_ReturnsEmpty() => await Assert.That(StatusMappers.TimeAgo(DateTime.MinValue)).IsEqualTo(string.Empty);

    [Test]
    public async Task TimeAgo_JustNow_ReturnsJustNow()
    {
        var recent = DateTime.UtcNow.AddSeconds(5);
        await Assert.That(StatusMappers.TimeAgo(recent)).IsEqualTo("just now");
    }

    [Test]
    public async Task TimeAgo_FiveMinutesAgo_Returns5mAgo()
    {
        var past = DateTime.UtcNow.AddMinutes(-5);
        await Assert.That(StatusMappers.TimeAgo(past)).IsEqualTo("5m ago");
    }

    [Test]
    public async Task TimeAgo_TwoHoursAgo_Returns2hAgo()
    {
        var past = DateTime.UtcNow.AddHours(-2);
        await Assert.That(StatusMappers.TimeAgo(past)).IsEqualTo("2h ago");
    }

    [Test]
    public async Task TimeAgo_ThreeDaysAgo_Returns3dAgo()
    {
        var past = DateTime.UtcNow.AddDays(-3);
        await Assert.That(StatusMappers.TimeAgo(past)).IsEqualTo("3d ago");
    }

    // ── TokensToCompact ────────────────────────────────────────────

    [Test]
    public async Task TokensToCompact_Zero_ReturnsZero() => await Assert.That(StatusMappers.TokensToCompact(0)).IsEqualTo("0");

    [Test]
    public async Task TokensToCompact_Negative_ReturnsZero() => await Assert.That(StatusMappers.TokensToCompact(-5)).IsEqualTo("0");

    [Test]
    public async Task TokensToCompact_BelowThousand_ReturnsRaw() => await Assert.That(StatusMappers.TokensToCompact(500)).IsEqualTo("500");

    [Test]
    public async Task TokensToCompact_AboveThousand_ReturnsK() => await Assert.That(StatusMappers.TokensToCompact(1200)).IsEqualTo("1.2K");

    [Test]
    public async Task TokensToCompact_AboveMillion_ReturnsM() => await Assert.That(StatusMappers.TokensToCompact(1_400_000)).IsEqualTo("1.4M");

    // ── CostToUsd ──────────────────────────────────────────────────

    [Test]
    public async Task CostToUsd_Zero_ReturnsZeroUsd() => await Assert.That(StatusMappers.CostToUsd(0m)).IsEqualTo("$0.0000");

    [Test]
    public async Task CostToUsd_Negative_ReturnsZeroUsd() => await Assert.That(StatusMappers.CostToUsd(-1.5m)).IsEqualTo("$0.0000");

    [Test]
    public async Task CostToUsd_SmallCost_ReturnsFourDecimal() => await Assert.That(StatusMappers.CostToUsd(0.0123m)).IsEqualTo("$0.0123");

    [Test]
    public async Task CostToUsd_LargeCost_ReturnsFourDecimal() => await Assert.That(StatusMappers.CostToUsd(12.5m)).IsEqualTo("$12.5000");
}
