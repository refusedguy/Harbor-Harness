using Harbor.Abstractions.Models;
using Harbor.TestKit;
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

    // #1770: one table per mapper. The former copies differed only in the
    // input and the expected key — the assert never changed — so the input
    // and the expectation are the table now. Every row keeps its original
    // test name as the case display name.

    [Test]
    [Arguments("running", "StatusRunningBrush", DisplayName = "StatusToBrushKey_Running_ReturnsRunningBrush")]
    [Arguments("compacting", "StatusCompactBrush", DisplayName = "StatusToBrushKey_Compacting_ReturnsCompactBrush")]
    [Arguments("error", "StatusErrorBrush", DisplayName = "StatusToBrushKey_Error_ReturnsErrorBrush")]
    [Arguments("idle", "StatusIdleBrush", DisplayName = "StatusToBrushKey_Idle_ReturnsIdleBrush")]
    [Arguments("xyz", "StatusIdleBrush", DisplayName = "StatusToBrushKey_Unknown_ReturnsIdleBrush")]
    [Arguments(null, "StatusIdleBrush", DisplayName = "StatusToBrushKey_Null_ReturnsIdleBrush")]
    public async Task StatusToBrushKey_ReturnsBrushKey(string? status, string expectedBrush)
    {
        await Assert.That(StatusMappers.StatusToBrushKey(status))
            .IsEqualTo(expectedBrush);
    }

    // ── ToolCallStateToBrushKey ────────────────────────────────────

    // #1810: the table names EVERY member of the enum, and the guard below
    // proves it — a member added to ToolCallState has no row here and the
    // guard fails. Before the table the three rows below were three separate
    // methods, and the other three members were covered by a different test
    // each, so "which member has a row" was a question about the file.

    [Test]
    [Arguments(ToolCallState.Pending, "MochaOverlay2", DisplayName = "ToolCallStateToBrushKey_Pending_ReturnsOverlay2")]
    [Arguments(ToolCallState.Running, "MochaYellow", DisplayName = "ToolCallStateToBrushKey_Running_ReturnsYellow")]
    [Arguments(ToolCallState.Success, "MochaGreen", DisplayName = "ToolCallStateToBrushKey_Success_ReturnsGreen")]
    [Arguments(ToolCallState.Error, "MochaRed", DisplayName = "ToolCallStateToBrushKey_Error_ReturnsRed")]
    [Arguments(ToolCallState.Cancelled, "MochaOverlay1", DisplayName = "ToolCallStateToBrushKey_Cancelled_ReturnsOverlay1")]
    [Arguments(ToolCallState.TimedOut, "MochaRed", DisplayName = "ToolCallStateToBrushKey_TimedOut_ReturnsRed")]
    public async Task ToolCallStateToBrushKey_ReturnsBrushKey(ToolCallState state, string expectedBrush)
    {
        await Assert.That(StatusMappers.ToolCallStateToBrushKey(state))
            .IsEqualTo(expectedBrush);
    }

    // ── ToolCallStateToPill ────────────────────────────────────────

    [Test]
    [Arguments(ToolCallState.Pending, "pending", DisplayName = "ToolCallStateToPill_Pending_ReturnsPendingLabel")]
    [Arguments(ToolCallState.Running, "running", DisplayName = "ToolCallStateToPill_Running_ReturnsRunningLabel")]
    [Arguments(ToolCallState.Success, "ok", DisplayName = "ToolCallStateToPill_Success_ReturnsOkLabel")]
    [Arguments(ToolCallState.Error, "err", DisplayName = "ToolCallStateToPill_Error_ReturnsErrLabel")]
    [Arguments(ToolCallState.Cancelled, "cancelled", DisplayName = "ToolCallStateToPill_Cancelled_ReturnsCancelledLabel")]
    [Arguments(ToolCallState.TimedOut, "timeout", DisplayName = "ToolCallStateToPill_TimedOut_ReturnsTimeoutLabel")]
    public async Task ToolCallStateToPill_ReturnsLabel(ToolCallState state, string expectedPill)
    {
        await Assert.That(StatusMappers.ToolCallStateToPill(state))
            .IsEqualTo(expectedPill);
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
    /// <remarks>
    /// This is the property over the PRODUCT; the guard below is the
    /// property over the TABLE. They are different claims and both are worth
    /// making: a table row whose expected value is empty would satisfy the
    /// table and fail this one.
    /// </remarks>
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

    /// <summary>
    /// #1810: the totality check. Both ToolCallState tables above name every
    /// member of the enum, and this says so — a member added to
    /// <c>ToolCallState</c> has no row in either table, and the guard fails
    /// until it gets one.
    /// </summary>
    /// <remarks>
    /// The covered set is READ from the tables' own rows rather than written
    /// out here, so there is no second list to forget: a table and a
    /// hand-written member list drift, and the drift is invisible until a
    /// member is added.
    /// </remarks>
    [Test]
    public async Task Every_ToolCallState_Has_A_Row_In_Every_Table()
    {
        await Assert.That(Enum.GetValues<ToolCallState>().Length).IsGreaterThan(0).Because("the enum has members to name");
        Exhaustive.AssertAllCases<ToolCallState>([
            (typeof(StatusMappersTests), nameof(ToolCallStateToPill_ReturnsLabel)),
            (typeof(StatusMappersTests), nameof(ToolCallStateToBrushKey_ReturnsBrushKey)),
        ], "every ToolCallState must have a row in a table");
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
    [Arguments(SessionStatus.Working, "working", DisplayName = "SessionStatusToText_Working_ReturnsWorkingLabel")]
    [Arguments(SessionStatus.Done, "done", DisplayName = "SessionStatusToText_Done_ReturnsDoneLabel")]
    [Arguments(SessionStatus.Error, "error", DisplayName = "SessionStatusToText_Error_ReturnsErrorLabel")]
    [Arguments(SessionStatus.Aborted, "aborted", DisplayName = "SessionStatusToText_Aborted_ReturnsAbortedLabel")]
    [Arguments(SessionStatus.Idle, "idle", DisplayName = "SessionStatusToText_Idle_ReturnsIdleLabel")]
    public async Task SessionStatusToText_ReturnsLabel(SessionStatus status, string expectedText)
    {
        await Assert.That(StatusMappers.SessionStatusToText(status))
            .IsEqualTo(expectedText);
    }

    // ── SessionStatusToBrushKey ────────────────────────────────────

    // #1810: Aborted had no row here — the label table had one and the brush
    // table did not, so an aborted session's dot colour was untested. The
    // totality guard below is what makes that a failure rather than a gap.

    [Test]
    [Arguments(SessionStatus.Working, "MochaYellow", DisplayName = "SessionStatusToBrushKey_Working_ReturnsYellow")]
    [Arguments(SessionStatus.Done, "MochaGreen", DisplayName = "SessionStatusToBrushKey_Done_ReturnsGreen")]
    [Arguments(SessionStatus.Error, "MochaRed", DisplayName = "SessionStatusToBrushKey_Error_ReturnsRed")]
    [Arguments(SessionStatus.Aborted, "MochaOverlay2", DisplayName = "SessionStatusToBrushKey_Aborted_ReturnsOverlay2")]
    [Arguments(SessionStatus.Idle, "MochaOverlay0", DisplayName = "SessionStatusToBrushKey_Idle_ReturnsOverlay0")]
    public async Task SessionStatusToBrushKey_ReturnsBrushKey(SessionStatus status, string expectedBrush)
    {
        await Assert.That(StatusMappers.SessionStatusToBrushKey(status))
            .IsEqualTo(expectedBrush);
    }

    /// <summary>
    /// #1810: the totality check for the two SessionStatus tables. A member
    /// added to <c>SessionStatus</c> has no row in either table until it
    /// gets one.
    /// </summary>
    [Test]
    public async Task Every_SessionStatus_Has_A_Row_In_Every_Table()
    {
        await Assert.That(Enum.GetValues<SessionStatus>().Length).IsGreaterThan(0).Because("the enum has members to name");
        Exhaustive.AssertAllCases<SessionStatus>([
            (typeof(StatusMappersTests), nameof(SessionStatusToText_ReturnsLabel)),
            (typeof(StatusMappersTests), nameof(SessionStatusToBrushKey_ReturnsBrushKey)),
        ], "every SessionStatus must have a row in a table");
    }

    // ── DurationToText ─────────────────────────────────────────────

    // The millisecond count is the table because a TimeSpan is not an
    // attribute constant. 0.5 ms is the former FromMicroseconds(500) — the
    // same 5 000 ticks — so the sub-millisecond branch is still covered.
    [Test]
    [Arguments(0.5, "", DisplayName = "DurationToText_SubMillisecond_ReturnsEmpty")]
    [Arguments(234.0, "234ms", DisplayName = "DurationToText_BelowOneSecond_ReturnsMilliseconds")]
    [Arguments(1500.0, "1.5s", DisplayName = "DurationToText_AboveOneSecond_ReturnsSeconds")]
    public async Task DurationToText_ReturnsText(double milliseconds, string expectedText)
    {
        await Assert.That(StatusMappers.DurationToText(TimeSpan.FromMilliseconds(milliseconds)))
            .IsEqualTo(expectedText);
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

    // The offset is in minutes so it stays an attribute constant: 120 minutes
    // is the former AddHours(-2) and 4 320 minutes is the former AddDays(-3),
    // to the tick.
    [Test]
    [Arguments(5, "5m ago", DisplayName = "TimeAgo_FiveMinutesAgo_Returns5mAgo")]
    [Arguments(120, "2h ago", DisplayName = "TimeAgo_TwoHoursAgo_Returns2hAgo")]
    [Arguments(4320, "3d ago", DisplayName = "TimeAgo_ThreeDaysAgo_Returns3dAgo")]
    public async Task TimeAgo_PastInstant_ReturnsRelativeLabel(int minutesAgo, string expectedLabel)
    {
        var past = DateTime.UtcNow.AddMinutes(-minutesAgo);
        await Assert.That(StatusMappers.TimeAgo(past)).IsEqualTo(expectedLabel);
    }

    // ── TokensToCompact ────────────────────────────────────────────

    [Test]
    [Arguments(0L, "0", DisplayName = "TokensToCompact_Zero_ReturnsZero")]
    [Arguments(-5L, "0", DisplayName = "TokensToCompact_Negative_ReturnsZero")]
    [Arguments(500L, "500", DisplayName = "TokensToCompact_BelowThousand_ReturnsRaw")]
    [Arguments(1200L, "1.2K", DisplayName = "TokensToCompact_AboveThousand_ReturnsK")]
    [Arguments(1_400_000L, "1.4M", DisplayName = "TokensToCompact_AboveMillion_ReturnsM")]
    public async Task TokensToCompact_ReturnsCompactText(long tokens, string expectedText) =>
        await Assert.That(StatusMappers.TokensToCompact(tokens)).IsEqualTo(expectedText);

    // ── CostToUsd ──────────────────────────────────────────────────

    // The cost is a double because `decimal` is NOT an attribute parameter
    // type (CS0182). The cast back is exact for these four values: a
    // double-to-decimal conversion rounds to 15 significant digits, and
    // 0, -1.5, 0.0123 and 12.5 all survive that unchanged.
    [Test]
    [Arguments(0d, "$0.0000", DisplayName = "CostToUsd_Zero_ReturnsZeroUsd")]
    [Arguments(-1.5d, "$0.0000", DisplayName = "CostToUsd_Negative_ReturnsZeroUsd")]
    [Arguments(0.0123d, "$0.0123", DisplayName = "CostToUsd_SmallCost_ReturnsFourDecimal")]
    [Arguments(12.5d, "$12.5000", DisplayName = "CostToUsd_LargeCost_ReturnsFourDecimal")]
    public async Task CostToUsd_ReturnsUsdText(double costUsd, string expectedText) =>
        await Assert.That(StatusMappers.CostToUsd((decimal)costUsd)).IsEqualTo(expectedText);
}
