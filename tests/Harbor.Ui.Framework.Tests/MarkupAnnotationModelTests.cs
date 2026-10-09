using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Pure-model acceptance for issue #400 slice 1/2: add / select / move /
///     delete / undo / redo exactness, the bounded stack, and the normalized
///     coordinate round-trip (same model, any terminal size).
/// </summary>
public class MarkupAnnotationModelTests
{
    [Test]
    public async Task Add_SelectsNew_AndAssignsSequenceIds()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.5, 0.5))
            .Add(MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.6, 0.6));

        await Assert.That(model.Items.Length).IsEqualTo(2);
        await Assert.That(model.Items[0].Id).IsEqualTo(1);
        await Assert.That(model.Items[1].Id).IsEqualTo(2);
        await Assert.That(model.SelectedId).IsEqualTo(2);
        await Assert.That(model.NextId).IsEqualTo(3);
    }

    [Test]
    public async Task Points_AreClamped_ToUnitSquare()
    {
        var point = NormalizedPoint.Create(-0.5, 1.5);

        await Assert.That(point.X).IsEqualTo(0);
        await Assert.That(point.Y).IsEqualTo(1);
    }

    [Test]
    public async Task Move_ClampsEndpoints_AndKeepsSelection()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Rectangle, NormalizedPoint.Create(0.8, 0.8), NormalizedPoint.Create(0.9, 0.9))
            .MoveSelected(0.5, 0.5);

        await Assert.That(model.Items[0].From.X).IsEqualTo(1);
        await Assert.That(model.Items[0].To.Y).IsEqualTo(1);
        await Assert.That(model.SelectedId).IsEqualTo(1);
    }

    [Test]
    public async Task Delete_Removes_AndKeepsLiveNeighbourSelected()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0, 0), NormalizedPoint.Create(0.1, 0.1))
            .Add(MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.3, 0.3))
            .Select(1)
            .Delete();

        await Assert.That(model.Items.Length).IsEqualTo(1);
        await Assert.That(model.Items[0].Id).IsEqualTo(2);
        await Assert.That(model.SelectedId).IsEqualTo(2);
    }

    [Test]
    public async Task Delete_UnknownId_IsNoOp()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0, 0), NormalizedPoint.Create(0.1, 0.1));
        var next = model.Delete(99);

        await Assert.That(next).IsSameReferenceAs(model);
    }

    [Test]
    public async Task Undo_Redo_AreExactInverses()
    {
        var afterAdds = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.5, 0.5), weight: 3)
            .Add(MarkupKind.Text, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.2, 0.2), text: "broken");
        var afterMove = afterAdds.MoveSelected(0.1, 0);

        var undone = afterMove.UndoFrame().UndoFrame().UndoFrame();
        await Assert.That(undone.Items.Length).IsEqualTo(0);
        await Assert.That(undone.SelectedId is null).IsTrue();
        await Assert.That(undone.NextId).IsEqualTo(1);

        var redone = undone.RedoFrame().RedoFrame().RedoFrame();
        await Assert.That(redone.Items.Length).IsEqualTo(afterMove.Items.Length);
        await Assert.That(redone.Items[0]).IsEqualTo(afterMove.Items[0]);
        await Assert.That(redone.Items[1]).IsEqualTo(afterMove.Items[1]);
        await Assert.That(redone.SelectedId).IsEqualTo(afterMove.SelectedId);
        await Assert.That(redone.NextId).IsEqualTo(afterMove.NextId);
    }

    [Test]
    public async Task UndoStack_IsBounded_OldestDropped()
    {
        var model = MarkupAnnotationModel.Empty;
        for (int i = 0; i < MarkupAnnotationModel.MaxUndoDepth + 10; i++)
        {
            double at = i / 100.0;
            model = model.Add(MarkupKind.Rectangle, NormalizedPoint.Create(at, at), NormalizedPoint.Create(at, at));
        }

        await Assert.That(model.Undo.Length).IsEqualTo(MarkupAnnotationModel.MaxUndoDepth);

        for (int i = 0; i < MarkupAnnotationModel.MaxUndoDepth; i++)
        {
            model = model.UndoFrame();
        }

        // The 10 oldest frames were dropped: 10 annotations survive every undo.
        await Assert.That(model.Items.Length).IsEqualTo(10);
        var stuck = model.UndoFrame();
        await Assert.That(stuck).IsSameReferenceAs(model);
    }

    [Test]
    public async Task Resize_MovesHead_AndIgnoresText()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.5, 0.5))
            .ResizeSelected(0.2, 0);

        await Assert.That(model.Items[0].From.X).IsEqualTo(0.1);
        await Assert.That(model.Items[0].To.X).IsEqualTo(0.7);

        var text = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Text, NormalizedPoint.Create(0.3, 0.3), NormalizedPoint.Create(0.3, 0.3), text: "hi");
        var resized = text.ResizeSelected(0.2, 0.2);
        await Assert.That(resized).IsSameReferenceAs(text);
    }

    [Test]
    public async Task SelectNext_Cycles_InOrder()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Arrow, NormalizedPoint.Create(0, 0), NormalizedPoint.Create(0.1, 0.1))
            .Add(MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.3, 0.3));

        var first = model.Select(null).SelectNext();
        await Assert.That(first.SelectedId).IsEqualTo(1);
        var second = first.SelectNext();
        await Assert.That(second.SelectedId).IsEqualTo(2);
        var wrapped = second.SelectNext();
        await Assert.That(wrapped.SelectedId).IsEqualTo(1);
    }

    [Test]
    public async Task Nudge_DragsWithoutUndoFrame_ButCheckpointGroupsIt()
    {
        var model = MarkupAnnotationModel.Empty
            .Add(MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.4, 0.4));
        int framesAfterAdd = model.Undo.Length;

        var dragged = model.Checkpoint()
            .NudgeSelected(0.01, 0)
            .NudgeSelected(0.01, 0)
            .NudgeSelected(0.01, 0);

        await Assert.That(dragged.Undo.Length).IsEqualTo(framesAfterAdd + 1);

        var undone = dragged.UndoFrame();
        await Assert.That(undone.Items[0].From.X).IsEqualTo(0.2);
    }

    [Test]
    public async Task Projection_MapsUnitSquare_ToRectCorners_AtAnySize()
    {
        var small = new Rect(0, 0, 80, 24);
        var large = new Rect(0, 0, 160, 48);

        var origin = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(0, 0), small);
        var far = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(1, 1), small);
        await Assert.That(origin).IsEqualTo((0, 0));
        await Assert.That(far).IsEqualTo((79, 23));

        // Same model, doubled terminal: the projection scales, the model does not move.
        var centerSmall = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(0.5, 0.5), small);
        var centerLarge = MarkupAnnotationModel.ProjectToCells(NormalizedPoint.Create(0.5, 0.5), large);
        await Assert.That(centerSmall).IsEqualTo((40, 12));
        await Assert.That(centerLarge).IsEqualTo((80, 24));
    }
}
