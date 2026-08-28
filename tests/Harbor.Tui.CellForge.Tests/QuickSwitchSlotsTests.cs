using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

public class QuickSwitchSlotsTests
{
    [Test]
    public async Task Get_InvalidSlot_Throws()
    {
        var slots = new QuickSwitchSlots();
        await Assert.That(() => slots.Get(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => slots.Get(10)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Resolve_EmptySlots_ReturnsNull()
    {
        var slots = new QuickSwitchSlots();
        await Assert.That(slots.Resolve('1')).IsNull();
        await Assert.That(slots.Resolve('9')).IsNull();
    }

    [Test]
    public async Task Resolve_OutOfRangeChord_ReturnsNull()
    {
        var slots = new QuickSwitchSlots();
        await Assert.That(slots.Resolve('0')).IsNull();
        await Assert.That(slots.Resolve('a')).IsNull();
    }

    [Test]
    public async Task Assign_BindsExactSlot()
    {
        var slots = new QuickSwitchSlots();
        slots.Assign(5, "session-e");
        slots.Assign(9, "session-i");
        await Assert.That(slots.Resolve('5')).IsEqualTo("session-e");
        await Assert.That(slots.Resolve('9')).IsEqualTo("session-i");
        await Assert.That(slots.Resolve('1')).IsNull();
    }

    [Test]
    public async Task Push_NewSession_LandsInSlot1_AndShiftsOthers()
    {
        var slots = new QuickSwitchSlots();
        slots.Assign(1, "old-1");
        slots.Assign(2, "old-2");

        slots.Push("new");

        await Assert.That(slots.Resolve('1')).IsEqualTo("new");
        await Assert.That(slots.Resolve('2')).IsEqualTo("old-1");
        await Assert.That(slots.Resolve('3')).IsEqualTo("old-2");
        await Assert.That(slots.Resolve('4')).IsNull();
    }

    [Test]
    public async Task Push_ExistingSession_MovesToFront_WithoutDuplicates()
    {
        var slots = new QuickSwitchSlots();
        slots.Assign(1, "a");
        slots.Assign(2, "b");
        slots.Assign(3, "c");

        slots.Push("b");

        await Assert.That(slots.Resolve('1')).IsEqualTo("b");
        await Assert.That(slots.Resolve('2')).IsEqualTo("a");
        await Assert.That(slots.Resolve('3')).IsEqualTo("c");
        await Assert.That(slots.Resolve('4')).IsNull();
    }

    [Test]
    public async Task Clear_RemovesBinding()
    {
        var slots = new QuickSwitchSlots();
        slots.Assign(3, "gone");
        slots.Clear(3);
        await Assert.That(slots.Resolve('3')).IsNull();
    }

    [Test]
    public async Task Push_TenSessions_Slot9Evicted()
    {
        var slots = new QuickSwitchSlots();
        for (int i = 1; i <= 10; i++)
        {
            slots.Push($"s{i}");
        }

        await Assert.That(slots.Resolve('1')).IsEqualTo("s10");
        await Assert.That(slots.Resolve('8')).IsEqualTo("s3");
        await Assert.That(slots.Resolve('9')).IsEqualTo("s2");

        slots.Push("s11");
        await Assert.That(slots.Resolve('9')).IsEqualTo("s3");
        await Assert.That(slots.Resolve('1')).IsEqualTo("s11");
    }
}
