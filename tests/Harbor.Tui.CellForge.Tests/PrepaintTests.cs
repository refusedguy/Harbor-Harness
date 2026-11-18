// #2361: the prepaint phase must reuse records by stable identity, reprepare
// when the identity or the version key changes, and must not re-read state in
// Paint. The test node records a tiny display-list range in prepaint and only
// replays it in Paint, which is the contract the engine now exposes.

namespace Harbor.Tui.CellForge.Tests;

public class PrepaintTests
{
    private static EngineCells.Constraints Box => new(20, 4);

    private static EngineCells.Rect Screen => new(0, 0, 20, 4);

    private sealed class TextNode : EngineCells.PrepaintNode
    {
        private readonly int _x;
        private readonly int _y;
        private readonly string _text;
        private readonly string _read;

        public TextNode(string text, int x, int y, string read = "text.state")
        {
            _text = text;
            _x = x;
            _y = y;
            _read = read;

            // Each node carries its own content version. Two different nodes
            // that swap indexed paths must not adopt each other's cache entry:
            // the record holds absolute ops for different text.
            Version = (ulong)(uint)text.GetHashCode(StringComparison.Ordinal);
        }

        public ulong Version { get; set; }

        public int Prepares { get; private set; }

        public List<string> Events { get; } = [];

        protected override ulong PrepaintKey => Version;

        public override EngineCells.Size Measure(EngineCells.Constraints constraints) => new(_text.Length, 1);

        protected override EngineCells.PrepaintRecord OnPrepaint(
            EngineCells.PrepaintContext context,
            EngineCells.ElementId id,
            EngineCells.Rect bounds)
        {
            Prepares++;
            Events.Add($"prepaint:{id}");
            context.MarkRead(_read);
            var mark = context.Scene.Mark();
            context.Scene.SetText(_x, _y, _text, EngineCells.CellStyle.Plain);
            return new EngineCells.PrepaintRecord(
                bounds,
                EngineCells.CellStyle.Plain,
                context.Scene.Close(mark),
                null);
        }

        protected override void OnPaint(EngineCells.ScreenBuffer buffer, EngineCells.Rect area) =>
            Events.Add("paint");
    }

    [Test]
    public async Task ElementId_StablePathAndPrefixHash_ChangeWithEveryStep()
    {
        var a = EngineCells.ElementId.Root.Child("row").Child(0).Child("text");
        var same = EngineCells.ElementId.Root.Child("row").Child(0).Child("text");
        var sibling = EngineCells.ElementId.Root.Child("row").Child(1).Child("text");
        var otherBranch = EngineCells.ElementId.Root.Child("column").Child(0).Child("text");

        await Assert.That(a).IsEqualTo(same);
        await Assert.That(a.PrefixHash).IsEqualTo(same.PrefixHash);
        await Assert.That(a != sibling).IsTrue();
        await Assert.That(a != otherBranch).IsTrue();
        await Assert.That(a.Depth).IsEqualTo(3);
        await Assert.That(a.ToString()).IsEqualTo("/row/0/text");
        await Assert.That(EngineCells.ElementId.Root.ToString()).IsEqualTo("/");
    }

    [Test]
    public async Task SecondFrame_SameIdAndKey_ReusesRecordAndReplaysRange()
    {
        var context = new EngineCells.PrepaintContext();
        var node = new TextNode("HELLO", 1, 1);

        var first = new EngineCells.ScreenBuffer(20, 4);
        EngineCells.NodeFrame.Render(node, context, first, Box, Screen);

        await Assert.That(node.Prepares).IsEqualTo(1);
        await Assert.That(context.Misses).IsEqualTo(1);
        await Assert.That(context.Hits).IsEqualTo(0);
        await Assert.That(node.CurrentRecord.ReadEntities).IsNotNull();
        await Assert.That(node.CurrentRecord.ReadEntities![0]).IsEqualTo("text.state");
        await Assert.That(node.Events[0]).IsEqualTo("prepaint:/");
        await Assert.That(node.Events[1]).IsEqualTo("paint");

        var second = new EngineCells.ScreenBuffer(20, 4);
        EngineCells.NodeFrame.Render(node, context, second, Box, Screen);

        // OnPrepaint did not run again, yet Paint still produced the same grid
        // from the cached display-list range.
        await Assert.That(node.Prepares).IsEqualTo(1);
        await Assert.That(context.Hits).IsEqualTo(1);
        await Assert.That(context.Misses).IsEqualTo(1);
        await Assert.That(node.Events.Count).IsEqualTo(3);
        await Assert.That(node.Events[2]).IsEqualTo("paint");
        await Assert.That(second.ContentEquals(first)).IsTrue();
    }

    [Test]
    public async Task VersionKeyChange_Reprepares_ThenReusesAgain()
    {
        var context = new EngineCells.PrepaintContext();
        var node = new TextNode("V1", 2, 0);

        EngineCells.NodeFrame.Render(node, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        node.Version = 2;
        EngineCells.NodeFrame.Render(node, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);

        await Assert.That(node.Prepares).IsEqualTo(2);
        await Assert.That(context.Misses).IsEqualTo(2);
        await Assert.That(context.Hits).IsEqualTo(0);

        EngineCells.NodeFrame.Render(node, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        await Assert.That(node.Prepares).IsEqualTo(2);
        await Assert.That(context.Hits).IsEqualTo(1);
    }

    [Test]
    public async Task ChangedId_DropsStaleEntry_AndForcesFreshRecord()
    {
        var context = new EngineCells.PrepaintContext();
        var node = new TextNode("X", 0, 0);

        context.BeginFrame();
        _ = node.Prepaint(context, EngineCells.ElementId.Root.Child("left"));
        context.EndFrame();
        await Assert.That(context.Count).IsEqualTo(1);

        context.BeginFrame();
        _ = node.Prepaint(context, EngineCells.ElementId.Root.Child("right"));
        context.EndFrame();
        await Assert.That(node.Prepares).IsEqualTo(2);
        await Assert.That(context.Count).IsEqualTo(1);
        await Assert.That(context.Misses).IsEqualTo(2);

        // The old id was pruned: coming back to it cannot reuse frame one's state.
        context.BeginFrame();
        _ = node.Prepaint(context, EngineCells.ElementId.Root.Child("left"));
        context.EndFrame();
        await Assert.That(node.Prepares).IsEqualTo(3);
        await Assert.That(context.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Container_ForwardsStableChildIds_AndReordersInvalidate()
    {
        var context = new EngineCells.PrepaintContext();
        var a = new TextNode("A", 0, 0);
        var b = new TextNode("B", 1, 0);
        var root = new EngineCells.FlexSplitNode(EngineCells.SplitDir.Horizontal);
        root.Add(a, EngineCells.FlexTrack.Fill());
        root.Add(b, EngineCells.FlexTrack.Fill());

        EngineCells.NodeFrame.Render(root, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        await Assert.That(a.Events[0]).IsEqualTo("prepaint:/0");
        await Assert.That(b.Events[0]).IsEqualTo("prepaint:/1");
        await Assert.That(context.Hits).IsEqualTo(0);

        EngineCells.NodeFrame.Render(root, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        await Assert.That(context.Hits).IsEqualTo(2);

        // Swapping the children changes their indexed paths; both must reprepare.
        _ = root.Remove(a);
        _ = root.Remove(b);
        root.Add(b, EngineCells.FlexTrack.Fill());
        root.Add(a, EngineCells.FlexTrack.Fill());
        EngineCells.NodeFrame.Render(root, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);

        await Assert.That(a.Prepares).IsEqualTo(2);
        await Assert.That(b.Prepares).IsEqualTo(2);
        await Assert.That(a.Events[^1]).IsEqualTo("paint");
        await Assert.That(a.Events[^2]).IsEqualTo("prepaint:/1");
        await Assert.That(b.Events[^2]).IsEqualTo("prepaint:/0");
        await Assert.That(context.Hits).IsEqualTo(2);
        await Assert.That(context.Misses).IsEqualTo(4);
    }

    [Test]
    public async Task KeyedContainer_Reorder_KeepsRecordsBoundToKey()
    {
        var context = new EngineCells.PrepaintContext();
        var a = new TextNode("A", 0, 0);
        var b = new TextNode("B", 1, 0);
        var root = new EngineCells.FlexSplitNode(EngineCells.SplitDir.Horizontal);
        root.Add(a, "alpha", EngineCells.FlexTrack.Fill());
        root.Add(b, "beta", EngineCells.FlexTrack.Fill());

        // Prepaint directly so the arranged bounds (part of the PrepaintKey)
        // stay fixed: the only thing that changes between frames is the
        // children's position in the container.
        context.BeginFrame();
        _ = root.Prepaint(context, EngineCells.ElementId.Root);
        context.EndFrame();

        await Assert.That(a.Events[0]).IsEqualTo("prepaint:/alpha");
        await Assert.That(b.Events[0]).IsEqualTo("prepaint:/beta");
        await Assert.That(a.Prepares).IsEqualTo(1);
        await Assert.That(b.Prepares).IsEqualTo(1);

        // Swap positions; the keys travel with their children.
        _ = root.Remove(a);
        _ = root.Remove(b);
        root.Add(b, "beta", EngineCells.FlexTrack.Fill());
        root.Add(a, "alpha", EngineCells.FlexTrack.Fill());

        context.BeginFrame();
        _ = root.Prepaint(context, EngineCells.ElementId.Root);
        context.EndFrame();

        // Both records are reused under their keys. With positional ids the
        // same swap forces a reprepare of both children.
        await Assert.That(a.Prepares).IsEqualTo(1);
        await Assert.That(b.Prepares).IsEqualTo(1);
        await Assert.That(context.Hits).IsEqualTo(2);
        await Assert.That(context.Misses).IsEqualTo(2);
    }

    [Test]
    public async Task NamedForward_KeyedId_EqualsStackPushAndKeepsState()
    {
        var context = new EngineCells.PrepaintContext();
        var a = new IdProbe();
        var b = new IdProbe();
        var stack = new EngineCells.ElementIdStack();
        stack.Push("alpha");
        var alphaId = stack.GlobalId;
        stack.Pop();
        stack.Push("beta");
        var betaId = stack.GlobalId;

        EngineCells.PrepaintForward.Named(
            context,
            EngineCells.ElementId.Root,
            [a, b],
            ["alpha", "beta"]);

        await Assert.That(a.Ids[0]).IsEqualTo(alphaId);
        await Assert.That(a.Ids[0].PrefixHash).IsEqualTo(alphaId.PrefixHash);
        await Assert.That(b.Ids[0]).IsEqualTo(betaId);
        await Assert.That(b.Ids[0].ToString()).IsEqualTo("/beta");

        // State stored under the stack-built id is read back through the id
        // the named forwarder produced: no regression to #2436.
        var store = new EngineCells.ElementStateStore();
        store.BeginFrame();
        var slot = store.GetOrCreate<Counter>(alphaId);
        slot.Value = 42;
        store.EndFrame();

        store.BeginFrame();
        await Assert.That(store.TryGet<Counter>(a.Ids[0], out var found)).IsTrue();
        await Assert.That(found!.Value).IsEqualTo(42);
        store.EndFrame();
    }

    private sealed class IdProbe : EngineCells.INode
    {
        public List<EngineCells.ElementId> Ids { get; } = [];

        public EngineCells.Rect Arranged { get; private set; }

        public EngineCells.Size Measure(EngineCells.Constraints constraints) => new(1, 1);

        public void Arrange(EngineCells.Rect rect) => Arranged = rect;

        public void Paint(EngineCells.ScreenBuffer buffer, EngineCells.Rect clip)
        {
        }

        public EngineCells.PrepaintRecord Prepaint(EngineCells.PrepaintContext context, EngineCells.ElementId id)
        {
            Ids.Add(id);
            return EngineCells.PrepaintRecord.Empty;
        }
    }

    private sealed class Counter
    {
        internal int Value;
    }

    [Test]
    public async Task NamedOverlayLayerId_KeepsIdentityAcrossZReorder()
    {
        var context = new EngineCells.PrepaintContext();
        var toast = new TextNode("TOAST", 0, 0);
        var dialog = new TextNode("DIALOG", 0, 1);
        var layers = new EngineCells.OverlayLayer();
        layers.Push(new EngineCells.ZLayer("toast", toast, Screen, z: 1));
        layers.Push(new EngineCells.ZLayer("dialog", dialog, Screen, z: 10));

        EngineCells.NodeFrame.Render(layers, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        await Assert.That(toast.Events[0]).IsEqualTo("prepaint:/toast");
        await Assert.That(dialog.Events[0]).IsEqualTo("prepaint:/dialog");

        layers.Get("toast")!.Z = 20;
        EngineCells.NodeFrame.Render(layers, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);
        await Assert.That(context.Hits).IsEqualTo(2);
        await Assert.That(toast.Prepares).IsEqualTo(1);
        await Assert.That(dialog.Prepares).IsEqualTo(1);
    }

    [Test]
    public async Task EntityVersionChange_InvalidatesOnlyTheReader_WhileUnchangedVersionReuses()
    {
        var context = new EngineCells.PrepaintContext();
        var versions = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["shared"] = 1 };
        context.EntityVersion = name => versions.TryGetValue(name, out var v) ? v : 0;

        var reader = new TextNode("R", 0, 0, "shared");
        var bystander = new TextNode("B", 1, 0, "unrelated");
        reader.Version = 7;
        bystander.Version = 7;

        var root = new EngineCells.FlexSplitNode(EngineCells.SplitDir.Horizontal);
        root.Add(reader, EngineCells.FlexTrack.Fill());
        root.Add(bystander, EngineCells.FlexTrack.Fill());

        void Frame() => EngineCells.NodeFrame.Render(
            root, context, new EngineCells.ScreenBuffer(20, 4), Box, Screen);

        Frame();
        await Assert.That(reader.Prepares).IsEqualTo(1);
        await Assert.That(bystander.Prepares).IsEqualTo(1);
        await Assert.That(context.Misses).IsEqualTo(2);

        // Neither entity changed: both records come back from the cache.
        Frame();
        await Assert.That(reader.Prepares).IsEqualTo(1);
        await Assert.That(bystander.Prepares).IsEqualTo(1);
        await Assert.That(context.Hits).IsEqualTo(2);

        // Only "shared" changes. Its reader reprepares even though its own
        // PrepaintKey and id are untouched; the other node still hits.
        versions["shared"] = 2;
        Frame();
        await Assert.That(reader.Prepares).IsEqualTo(2);
        await Assert.That(bystander.Prepares).IsEqualTo(1);
        await Assert.That(context.Hits).IsEqualTo(3);
        await Assert.That(context.Misses).IsEqualTo(3);

        // The new version is the baseline again.
        Frame();
        await Assert.That(reader.Prepares).IsEqualTo(2);
        await Assert.That(context.Hits).IsEqualTo(5);
        await Assert.That(context.Misses).IsEqualTo(3);
    }
}
