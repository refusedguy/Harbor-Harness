using System;
using System.Linq;
using System.Text;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2439: a mutation batch carries numeric ids, applies in three
///     phases, drops no-op payloads before enqueue and exposes one dirty flag.
/// </summary>
public class MutationBatchTests
{
    private static MutationStyle Style(params (string Name, string Value)[] properties) =>
        MutationStyle.Create(properties);

    // ── 1. Command set with numeric ids ───────────────────────────────────

    [Test]
    public async Task AllEightCommands_ApplyToANumericIdTree()
    {
        var applier = new MutationBatchApplier();
        var batch = new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.CreateElement(2, SpecKind.Text),
            MutationCommand.CreateElement(3, SpecKind.Text),
            MutationCommand.SetStyle(1, Style(("fg", "red"), ("bg", "black"))),
            MutationCommand.SetText(2, "hello"),
            MutationCommand.AppendChild(1, 2),
            MutationCommand.AppendChild(1, 3),
            MutationCommand.InsertBefore(1, 3, 2),
            MutationCommand.SetEventListener(2, "click", true),
            MutationCommand.SetRoot(1),
        ]);

        var result = applier.Apply(batch);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(applier.RootId).IsEqualTo(1);
        await Assert.That(applier.Contains(1)).IsTrue();
        await Assert.That(applier.Contains(3)).IsTrue();
        await Assert.That(applier.TextOf(2)).IsEqualTo("hello");
        await Assert.That(applier.StyleIdOf(1).HasValue).IsTrue();
        await Assert.That(applier.HasListener(2, "click")).IsTrue();
        await Assert.That(applier.ChildrenOf(1).SequenceEqual([3, 2])).IsTrue();
        await Assert.That(result.Value.Dirty).IsTrue();
    }

    [Test]
    public async Task InsertBefore_PlacesChildAheadOfSibling()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.CreateElement(2, SpecKind.Text),
            MutationCommand.CreateElement(3, SpecKind.Text),
            MutationCommand.AppendChild(1, 2),
            MutationCommand.AppendChild(1, 3),
        ]));

        var result = applier.Apply(new MutationBatch([MutationCommand.InsertBefore(1, 3, 2)]));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(applier.ChildrenOf(1).SequenceEqual([3, 2])).IsTrue();
    }

    [Test]
    public async Task DestroyElement_ReturnsSubtreeIdsAndClearsRoot()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.CreateElement(2, SpecKind.Panel),
            MutationCommand.CreateElement(3, SpecKind.Text),
            MutationCommand.AppendChild(1, 2),
            MutationCommand.AppendChild(2, 3),
            MutationCommand.SetRoot(1),
        ]));

        var result = applier.Apply(new MutationBatch([MutationCommand.DestroyElement(1)]));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.FreedIds.OrderBy(id => id).SequenceEqual([1, 2, 3])).IsTrue();
        await Assert.That(applier.RootId).IsEqualTo(0);
        await Assert.That(applier.Contains(2)).IsFalse();
        await Assert.That(applier.Contains(3)).IsFalse();
    }

    // ── 2. Three ordered phases ──────────────────────────────────────────

    [Test]
    public async Task Phase1_UnknownId_RejectsWholeBatchWithoutTouchingTree()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.SetText(1, "original"),
        ]));
        int revision = applier.Revision;
        applier.TakeDirty();

        var result = applier.Apply(new MutationBatch([
            MutationCommand.SetText(1, "changed"),
            MutationCommand.SetText(99, "missing"),
        ]));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Phase 1");
        await Assert.That(applier.TextOf(1)).IsEqualTo("original");
        await Assert.That(applier.Revision).IsEqualTo(revision);
        await Assert.That(applier.TakeDirty()).IsFalse();
    }

    [Test]
    public async Task Phase1_Cycle_RejectsBatch()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.CreateElement(2, SpecKind.Panel),
            MutationCommand.AppendChild(1, 2),
        ]));

        var result = applier.Apply(new MutationBatch([MutationCommand.AppendChild(2, 1)]));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("cycle");
        await Assert.That(applier.ChildrenOf(1).SequenceEqual([2])).IsTrue();
    }

    [Test]
    public async Task Phase2_StyleTableFull_SweepsInternedStylesAndLeavesTreeUntouched()
    {
        var applier = new MutationBatchApplier(styleLimit: 2);
        applier.Apply(new MutationBatch([MutationCommand.CreateElement(1, SpecKind.Panel)]));

        var result = applier.Apply(new MutationBatch([
            MutationCommand.SetStyle(1, Style(("k", "a"))),
            MutationCommand.SetStyle(1, Style(("k", "b"))),
            MutationCommand.SetStyle(1, Style(("k", "c"))),
        ]));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Phase 2");
        await Assert.That(applier.InternedStyleCount).IsEqualTo(0);
        await Assert.That(applier.StyleIdOf(1).HasValue).IsFalse();
        applier.Apply(new MutationBatch([MutationCommand.SetStyle(1, Style(("k", "fresh")))]));
        await Assert.That(applier.InternedStyleCount).IsEqualTo(1);
    }

    // ── 3. Structural equivalence drops no-ops before enqueue ───────────

    [Test]
    public async Task Queue_EquivalentStylePayload_IsDroppedBeforeEnqueue()
    {
        var queue = new MutationQueue();
        queue.Enqueue(MutationCommand.SetStyle(1, Style(("bg", "black"), ("fg", "red"))));
        queue.Enqueue(MutationCommand.SetStyle(1, Style(("fg", "red"), ("bg", "black"))));

        await Assert.That(queue.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Queue_ChangedPayload_IsEnqueued()
    {
        var queue = new MutationQueue();
        queue.Enqueue(MutationCommand.SetStyle(1, Style(("fg", "red"))));
        queue.Enqueue(MutationCommand.SetStyle(1, Style(("fg", "blue"))));

        await Assert.That(queue.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Queue_EquivalentTextAndListener_AreDropped()
    {
        var queue = new MutationQueue();
        queue.Enqueue(MutationCommand.SetText(2, "same"));
        queue.Enqueue(MutationCommand.SetText(2, "same"));
        queue.Enqueue(MutationCommand.SetEventListener(2, "click", true));
        queue.Enqueue(MutationCommand.SetEventListener(2, "click", true));
        queue.Enqueue(MutationCommand.SetEventListener(2, "click", false));

        await Assert.That(queue.Count).IsEqualTo(3);
    }

    // ── 4. One dirty/revision flag, no redraw on no-op ───────────────────

    [Test]
    public async Task NoOpBatch_DoesNotAdvanceRevisionOrRaiseDirty()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.SetRoot(1),
        ]));
        int revision = applier.Revision;
        applier.TakeDirty();

        var result = applier.Apply(new MutationBatch([MutationCommand.SetRoot(1)]));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Dirty).IsFalse();
        await Assert.That(applier.Revision).IsEqualTo(revision);
        await Assert.That(applier.TakeDirty()).IsFalse();
    }

    [Test]
    public async Task DirtyFlag_TakeDirtyClearsAfterOneChange()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([MutationCommand.CreateElement(1, SpecKind.Text)]));

        await Assert.That(applier.TakeDirty()).IsTrue();
        await Assert.That(applier.TakeDirty()).IsFalse();
        await Assert.That(applier.Revision).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatedStyleAcrossBatches_InternsOnceAndSecondUseIsNoOp()
    {
        var applier = new MutationBatchApplier();
        applier.Apply(new MutationBatch([
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.CreateElement(2, SpecKind.Panel),
        ]));
        applier.TakeDirty();
        var payload = Style(("fg", "green"));

        applier.Apply(new MutationBatch([MutationCommand.SetStyle(1, payload)]));
        int afterFirst = applier.Revision;
        applier.Apply(new MutationBatch([MutationCommand.SetStyle(2, payload)]));

        await Assert.That(applier.InternedStyleCount).IsEqualTo(1);
        await Assert.That(applier.Revision).IsEqualTo(afterFirst + 1);
    }

    // ── AOT-safe codec and the #2489 lowering seam ──────────────────────

    [Test]
    public async Task Codec_RoundTrip_PreservesEveryCommand()
    {
        var commands = new MutationCommand[]
        {
            MutationCommand.CreateElement(1, SpecKind.Panel),
            MutationCommand.SetStyle(1, Style(("bg", "black"), ("fg", "red"))),
            MutationCommand.SetText(1, "hello"),
            MutationCommand.AppendChild(1, 2),
            MutationCommand.InsertBefore(1, 2, 3),
            MutationCommand.SetEventListener(1, "click", true),
            MutationCommand.SetRoot(1),
            MutationCommand.DestroyElement(2),
        };

        var bytes = MutationBatchCodec.Write(new MutationBatch(commands));

        await Assert.That(MutationBatchCodec.TryRead(bytes, out var decoded, out var error)).IsTrue();
        await Assert.That(decoded.Count).IsEqualTo(commands.Length);
        for (int i = 0; i < commands.Length; i++)
            await Assert.That(decoded.Commands[i].IsEquivalentTo(commands[i])).IsTrue();
    }

    [Test]
    public async Task Codec_UnknownOperation_IsRejected()
    {
        var unknown = Encoding.UTF8.GetBytes("[[\"nope\",1]]");
        await Assert.That(MutationBatchCodec.TryRead(unknown, out _, out var unknownError)).IsFalse();
        await Assert.That(unknownError).Contains("unknown operation");
    }

    [Test]
    public async Task Lowering_FromSpecArena_ProducesCreateTextStyleAppendAndRoot()
    {
        var result = MarkupParser.Parse("""
            panel "main":
                .text "hello"
                .height 10
                text "done"
            """);
        await Assert.That(result.IsValid).IsTrue();

        var batch = SpecArenaMutationLowering.Lower(result.Arena);
        var operations = batch.Commands.Select(command => command.Op).ToArray();

        await Assert.That(operations).Contains(MutationOp.CreateElement);
        await Assert.That(operations).Contains(MutationOp.SetStyle);
        await Assert.That(operations).Contains(MutationOp.SetText);
        await Assert.That(operations).Contains(MutationOp.AppendChild);
        await Assert.That(operations).Contains(MutationOp.SetRoot);
        await Assert.That(batch.Commands.Count(command => command.Op == MutationOp.CreateElement)).IsEqualTo(2);

        var applier = new MutationBatchApplier();
        var applied = applier.Apply(batch);
        await Assert.That(applied.IsSuccess).IsTrue();
        await Assert.That(applier.RootId).IsEqualTo(1);
    }
}
