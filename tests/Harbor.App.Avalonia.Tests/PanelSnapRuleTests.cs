// PanelSnapRuleTests.cs — K17 (issue #2683): resizable dock panels with snap.
//
// Pure decision tests. They pin the width band, the snap-closed behaviour and
// the reopen contract without standing up an Avalonia window or dispatcher.

using Harbor.App.Avalonia.ViewModels;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

public sealed class PanelSnapRuleTests
{
    private static readonly PanelResizeConstraints Band = PanelSnapRule.DrawerConstraints;

    [Test]
    public async Task ShippedBand_IsOrdered_AndUsable()
    {
        await Assert.That(Band.SnapClosedWidth).IsLessThan(Band.MinWidth);
        await Assert.That(Band.MinWidth).IsLessThan(Band.DefaultWidth);
        await Assert.That(Band.DefaultWidth).IsLessThan(Band.MaxWidth);
    }

    [Test]
    public async Task Release_InSnapClosedBand_ClosesAndRestoresDefaultWidth()
    {
        PanelSnapResult result = PanelSnapRule.Resolve(Band.SnapClosedWidth, Band);

        await Assert.That(result.IsOpen).IsFalse();
        await Assert.That(result.Width).IsEqualTo(Band.DefaultWidth);
    }

    [Test]
    public async Task Release_BelowSnapClosedBand_AlsoCloses()
    {
        PanelSnapResult result = PanelSnapRule.Resolve(Band.SnapClosedWidth - 40, Band);

        await Assert.That(result.IsOpen).IsFalse();
        await Assert.That(result.Width).IsEqualTo(Band.DefaultWidth);
    }

    [Test]
    public async Task Release_JustBelowMinimum_SnapsOpenAtMinimum()
    {
        double proposed = Band.SnapClosedWidth + (Band.MinWidth - Band.SnapClosedWidth) / 2;

        PanelSnapResult result = PanelSnapRule.Resolve(proposed, Band);

        await Assert.That(result.IsOpen).IsTrue();
        await Assert.That(result.Width).IsEqualTo(Band.MinWidth);
    }

    [Test]
    public async Task Release_AtMinimum_KeepsThatExactWidth()
    {
        PanelSnapResult result = PanelSnapRule.Resolve(Band.MinWidth, Band);

        await Assert.That(result.IsOpen).IsTrue();
        await Assert.That(result.Width).IsEqualTo(Band.MinWidth);
    }

    [Test]
    public async Task Release_InsideBand_KeepsProposedWidth()
    {
        foreach (double proposed in new[] { 300d, Band.DefaultWidth, 480d, Band.MaxWidth })
        {
            PanelSnapResult result = PanelSnapRule.Resolve(proposed, Band);

            await Assert.That(result.IsOpen).IsTrue();
            await Assert.That(result.Width).IsEqualTo(proposed);
        }
    }

    [Test]
    public async Task Release_PastMaximum_ClampsToMaximum()
    {
        PanelSnapResult result = PanelSnapRule.Resolve(Band.MaxWidth + 200, Band);

        await Assert.That(result.IsOpen).IsTrue();
        await Assert.That(result.Width).IsEqualTo(Band.MaxWidth);
    }

    [Test]
    public async Task ReopeningAClosedPanel_RestoresDefaultWidth()
    {
        PanelSnapResult closed = PanelSnapRule.Resolve(Band.SnapClosedWidth, Band);

        // The shell reopens with the width the close step restored, so the next
        // drag starts from a usable size rather than from the snap band.
        PanelSnapResult reopened = PanelSnapRule.Resolve(closed.Width, Band);

        await Assert.That(reopened.IsOpen).IsTrue();
        await Assert.That(reopened.Width).IsEqualTo(Band.DefaultWidth);
    }

    [Test]
    public async Task DragWidth_IsClampedToTheDragBand()
    {
        await Assert.That(PanelSnapRule.ClampDragWidth(10, Band)).IsEqualTo(Band.SnapClosedWidth);
        await Assert.That(PanelSnapRule.ClampDragWidth(400, Band)).IsEqualTo(400);
        await Assert.That(PanelSnapRule.ClampDragWidth(9999, Band)).IsEqualTo(Band.MaxWidth);
    }

    [Test]
    public async Task NonFiniteInput_FallsBackToClosedDefault()
    {
        PanelSnapResult nan = PanelSnapRule.Resolve(double.NaN, Band);
        PanelSnapResult inf = PanelSnapRule.Resolve(double.PositiveInfinity, Band);

        await Assert.That(nan.IsOpen).IsFalse();
        await Assert.That(nan.Width).IsEqualTo(Band.DefaultWidth);
        await Assert.That(inf.IsOpen).IsFalse();
        await Assert.That(inf.Width).IsEqualTo(Band.DefaultWidth);
        await Assert.That(PanelSnapRule.ClampDragWidth(double.NaN, Band)).IsEqualTo(Band.DefaultWidth);
    }
}
