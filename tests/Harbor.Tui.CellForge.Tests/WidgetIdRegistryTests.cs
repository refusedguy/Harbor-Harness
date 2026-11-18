// #2496: the widget id registry is a stable-name lookup, distinct from the
// ElementId frame-identity path (#2361/#2436). These tests drive the exact-id
// contract and its two edge cases: a duplicate id is rejected explicitly and a
// missing id yields no widget. BCL-only test doubles, a LeafNode is enough,
// the registry never measures or paints anything.

using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public sealed class WidgetIdRegistryTests
{
    private sealed class FixedNode : LeafNode
    {
        public override Size Measure(Constraints constraints) => new(1, 1);

        public override void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }
    }

    [Test]
    public async Task QueryOne_ExactId_ReturnsTheRegisteredWidget()
    {
        var registry = new WidgetIdRegistry();
        var node = new FixedNode();

        registry.Register("composer", node);

        await Assert.That(registry.Count).IsEqualTo(1);
        await Assert.That(registry.QueryOne("composer")).IsSameReferenceAs(node);
    }

    [Test]
    public async Task QueryOne_IsExactAndCaseSensitive()
    {
        var registry = new WidgetIdRegistry();
        var node = new FixedNode();
        registry.Register("composer", node);

        await Assert.That(registry.QueryOne("Composer")).IsNull();
        await Assert.That(registry.QueryOne("compose")).IsNull();
        await Assert.That(registry.QueryOne("composer")).IsSameReferenceAs(node);
    }

    [Test]
    public async Task Register_DuplicateId_ThrowsAndKeepsTheOriginalWidget()
    {
        var registry = new WidgetIdRegistry();
        var first = new FixedNode();
        var second = new FixedNode();
        registry.Register("status", first);

        await Assert.That(() => registry.Register("status", second))
            .Throws<InvalidOperationException>();

        await Assert.That(registry.Count).IsEqualTo(1);
        await Assert.That(registry.QueryOne("status")).IsSameReferenceAs(first);
    }

    [Test]
    public async Task TryRegister_DuplicateId_ReturnsFalseAndKeepsTheOriginalWidget()
    {
        var registry = new WidgetIdRegistry();
        var first = new FixedNode();
        var second = new FixedNode();
        await Assert.That(registry.TryRegister("status", first)).IsTrue();

        await Assert.That(registry.TryRegister("status", second)).IsFalse();

        await Assert.That(registry.Count).IsEqualTo(1);
        await Assert.That(registry.QueryOne("status")).IsSameReferenceAs(first);
    }

    [Test]
    public async Task QueryOne_MissingId_ReturnsNull()
    {
        var registry = new WidgetIdRegistry();
        registry.Register("present", new FixedNode());

        await Assert.That(registry.QueryOne("absent")).IsNull();
        Assert.Throws<ArgumentException>(() => { _ = registry.QueryOne(""); });
    }

    [Test]
    public async Task Unregister_RemovesTheWidget_AndASecondRemoveReturnsFalse()
    {
        var registry = new WidgetIdRegistry();
        var node = new FixedNode();
        registry.Register("feedback", node);

        await Assert.That(registry.Unregister("feedback")).IsTrue();
        await Assert.That(registry.QueryOne("feedback")).IsNull();
        await Assert.That(registry.Count).IsEqualTo(0);
        await Assert.That(registry.Unregister("feedback")).IsFalse();
    }

    [Test]
    public async Task Unregister_ThenRegister_ReusesTheId()
    {
        var registry = new WidgetIdRegistry();
        var first = new FixedNode();
        var second = new FixedNode();
        registry.Register("slot", first);
        registry.Unregister("slot");

        registry.Register("slot", second);

        await Assert.That(registry.QueryOne("slot")).IsSameReferenceAs(second);
        await Assert.That(registry.Count).IsEqualTo(1);
    }
}
