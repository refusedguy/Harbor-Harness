// TokenUsageUnpricedCellTests.cs — #942, second half: the Avalonia "token usage"
// overlay bound its cumulative-cost tile to a bare decimal and let the XAML
// supply the glyph:
//
//     <TextBlock Text="{Binding TotalCostUsd, StringFormat='{}${0:F4}'}" />
//
// so for an unpriced model — every model served by a catalogue entry with no
// rates, which is 11 of the 13 shipped providers — the tile read "$0.0000"
// while the status bar in the same app read "—".
//
// The XAML is not where this can be pinned, so it is not where it is pinned
// here: the cell is spelled in the view-model (`TotalCostText`), next to the
// number it describes, and THAT is what these tests assert. The XAML binding was
// changed to `{Binding TotalCostText}` with no StringFormat.
//
// The second test is the one that would have caught the real-world shape of the
// bug: `RecordUsage` returns early when no tokens moved, so a session whose
// FIRST stats frame is already unpriced would have kept the default (priced) bit
// and printed "$0.0000" for the rest of the run.

using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Issue #942: the token-usage overlay's cumulative-cost tile must print the
///     core's unpriced answer, not a fabricated zero.
/// </summary>
public class TokenUsageUnpricedCellTests
{
    private static TokenUsageViewModel NewVm() => new(NullLogger<TokenUsageViewModel>.Instance);

    private static UiState State(long tokensIn, long tokensOut, decimal cost, bool isCostUnpriced) => new()
    {
        Chat = ChatDomainState.Empty with
        {
            Cost = new CostSnapshot(tokensIn, tokensOut, cost, isCostUnpriced)
        }
    };

    [Test]
    public async Task TotalCostText_WhenUnpriced_PrintsTheEmDash()
    {
        var vm = NewVm();
        vm.RecordUsage(State(1500, 300, 0m, isCostUnpriced: true));

        await Assert.That(vm.TotalCostText).IsEqualTo(StatusBarText.UnknownCostCell)
            .Because("the core published IsCostUnpriced, so the tile must not print an amount");
        await Assert.That(vm.TotalCostText).DoesNotContain("0.0000");
    }

    [Test]
    public async Task TotalCostText_OnAnAlreadyUnpricedFirstFrame_StillPrintsTheEmDash()
    {
        // The early-return in RecordUsage is keyed on TOKENS, not on the price bit.
        // A session whose first stats frame is unpriced moves no tokens between
        // frames, so a fix that synced the bit after that guard would leave the
        // tile reading "$0.0000" for the whole run.
        var vm = NewVm();
        vm.RecordUsage(State(0, 0, 0m, isCostUnpriced: true));
        await Assert.That(vm.TotalCostText).IsEqualTo(StatusBarText.UnknownCostCell);

        // …and a later frame that moved no tokens must not resurrect the zero.
        vm.RecordUsage(State(0, 0, 0m, isCostUnpriced: true));
        await Assert.That(vm.TotalCostText).IsEqualTo(StatusBarText.UnknownCostCell)
            .Because("the bit is not a per-turn quantity; a tokenless frame must not reset it");
    }

    [Test]
    public async Task TotalCostText_WhenPriced_PrintsTheAmount()
    {
        var vm = NewVm();
        vm.RecordUsage(State(1500, 300, 0.0042m, isCostUnpriced: false));

        await Assert.That(vm.TotalCostText).IsEqualTo("$0.0042")
            .Because("a priced session keeps its bill");
    }

    [Test]
    public async Task TotalCostText_FlipsWithTheBit()
    {
        var vm = NewVm();
        vm.RecordUsage(State(1500, 300, 0m, isCostUnpriced: true));
        await Assert.That(vm.TotalCostText).IsEqualTo(StatusBarText.UnknownCostCell);

        // Same session, later frame, now priced — the cell must follow the core.
        vm.RecordUsage(State(2000, 400, 0.0071m, isCostUnpriced: false));
        await Assert.That(vm.TotalCostText).IsEqualTo("$0.0071")
            .Because("the bit is the core's answer for THIS frame, not a latch");
    }

    [Test]
    public async Task Reset_ClearsTheUnpricedBit()
    {
        // A session switch must not leave the previous session's dash behind:
        // Reset() is the shared entry point for the Clear button and the switch
        // path, so a stale bit here would show "—" over a priced session.
        var vm = NewVm();
        vm.RecordUsage(State(1500, 300, 0m, isCostUnpriced: true));
        await Assert.That(vm.TotalCostText).IsEqualTo(StatusBarText.UnknownCostCell);

        vm.Reset();
        await Assert.That(vm.IsCostUnpriced).IsFalse();
        await Assert.That(vm.TotalCostText).IsEqualTo("$0.0000")
            .Because("after a reset the cell is the default priced state, exactly as before #942");
    }
}