using Harbor.Ui.Framework.Projection;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Setup-guide checklist model (KILLER_FEATURES §2.7 Feature 9, issue #383):
/// completion-state transitions, progress/ring clamping and the
/// immutable-on-update contract.
/// </summary>
public class SetupChecklistModelTests
{
    [Test]
    public async Task Empty_HasFivePendingTasks()
    {
        var model = SetupChecklistModel.Empty;

        await Assert.That(model.TotalCount).IsEqualTo(5);
        await Assert.That(model.CompletedCount).IsEqualTo(0);
        await Assert.That(model.PendingCount).IsEqualTo(5);
        await Assert.That(model.ProgressText).IsEqualTo("0/5");
        await Assert.That(model.Ratio).IsEqualTo(0d);
        await Assert.That(model.IsComplete).IsFalse();
    }

    [Test]
    public async Task WithCompletion_Partial_ReportsProgress()
    {
        SetupChecklistModel model = SetupChecklistModel.Empty.WithCompletion(
            new Dictionary<string, bool>
            {
                [SetupTaskIds.ConfigFile] = true,
                [SetupTaskIds.ProviderKey] = true,
                [SetupTaskIds.Workspace] = true,
            });

        await Assert.That(model.CompletedCount).IsEqualTo(3);
        await Assert.That(model.PendingCount).IsEqualTo(2);
        await Assert.That(model.ProgressText).IsEqualTo("3/5");
        await Assert.That(model.Ratio).IsEqualTo(0.6d);
        await Assert.That(model.IsComplete).IsFalse();
    }

    [Test]
    public async Task WithCompletion_AllDone_ClampsRingToOne()
    {
        SetupChecklistModel model = SetupChecklistModel.Empty.WithCompletion(
            SetupChecklistModel.Empty.ToCompletion().ToDictionary(kv => kv.Key, _ => true));

        await Assert.That(model.CompletedCount).IsEqualTo(model.TotalCount);
        await Assert.That(model.Ratio).IsEqualTo(1d);
        await Assert.That(model.ProgressText).IsEqualTo("5/5");
        await Assert.That(model.IsComplete).IsTrue();
    }

    [Test]
    public async Task WithCompletion_IsImmutable_SourceUntouched()
    {
        var before = SetupChecklistModel.Empty;
        SetupChecklistModel after = before.WithTask(SetupTaskIds.FirstPrompt, true);

        await Assert.That(after).IsNotSameReferenceAs(before);
        await Assert.That(before.CompletedCount).IsEqualTo(0);
        await Assert.That(before.Find(SetupTaskIds.FirstPrompt)?.IsDone).IsFalse();
        await Assert.That(after.Find(SetupTaskIds.FirstPrompt)?.IsDone).IsTrue();
    }

    [Test]
    public async Task WithCompletion_NoChange_ReturnsSameInstance()
    {
        var model = SetupChecklistModel.Empty;

        await Assert.That(model.WithCompletion(new Dictionary<string, bool>())).IsSameReferenceAs(model);
        await Assert.That(model.WithCompletion(null)).IsSameReferenceAs(model);
        await Assert.That(model.WithCompletion(new Dictionary<string, bool> { ["unknown-task"] = true }))
            .IsSameReferenceAs(model);
        await Assert.That(model.WithTask(SetupTaskIds.FirstPrompt, false)).IsSameReferenceAs(model);
        await Assert.That(model.WithTask("unknown-task", true)).IsSameReferenceAs(model);
    }

    [Test]
    public async Task WithCompletion_IgnoresUnknownIdsAndFoldsCasing()
    {
        var model = SetupChecklistModel.Empty.WithCompletion(
            new Dictionary<string, bool>
            {
                ["CONFIG-FILE"] = true,
                ["not-a-task"] = true,
            });

        await Assert.That(model.CompletedCount).IsEqualTo(1);
        await Assert.That(model.Find(SetupTaskIds.ConfigFile)?.IsDone).IsTrue();
        await Assert.That(model.TotalCount).IsEqualTo(5);
    }

    [Test]
    public async Task WithCompletion_CanUndoATask()
    {
        SetupChecklistModel done = SetupChecklistModel.Empty.WithTask(SetupTaskIds.ProviderKey, true);
        SetupChecklistModel undone = done.WithCompletion(new Dictionary<string, bool> { [SetupTaskIds.ProviderKey] = false });

        await Assert.That(undone.CompletedCount).IsEqualTo(0);
        await Assert.That(undone.ProgressText).IsEqualTo("0/5");
    }

    [Test]
    public async Task ToCompletion_RoundTripsThroughWithCompletion()
    {
        SetupChecklistModel model = SetupChecklistModel.Empty
            .WithTask(SetupTaskIds.ConfigFile, true)
            .WithTask(SetupTaskIds.FirstPrompt, true);

        SetupChecklistModel round = SetupChecklistModel.Empty.WithCompletion(model.ToCompletion());

        await Assert.That(round.CompletedCount).IsEqualTo(2);
        await Assert.That(round.ProgressText).IsEqualTo(model.ProgressText);
    }

    [Test]
    public async Task EmptyChecklist_ClampsRatioAndCompletion()
    {
        var model = new SetupChecklistModel(null);

        await Assert.That(model.TotalCount).IsEqualTo(0);
        await Assert.That(model.Ratio).IsEqualTo(0d);
        await Assert.That(model.ProgressText).IsEqualTo("0/0");
        await Assert.That(model.IsComplete).IsFalse();
    }

    [Test]
    public async Task TaskState_MarkersAndRowText()
    {
        var done = new SetupTaskState(SetupTaskIds.Workspace, "Workspace open", true);
        var pending = new SetupTaskState(SetupTaskIds.FirstPrompt, "First prompt sent", false);

        await Assert.That(done.Marker).IsEqualTo("✓");
        await Assert.That(pending.Marker).IsEqualTo("○");
        await Assert.That(done.RowText).IsEqualTo("✓ Workspace open");
        await Assert.That(pending.RowText).IsEqualTo("○ First prompt sent");
    }

    [Test]
    public async Task Find_UnknownId_ReturnsNull()
    {
        await Assert.That(SetupChecklistModel.Empty.Find("nope")).IsNull();
        await Assert.That(SetupChecklistModel.Empty.Find(string.Empty)).IsNull();
    }
}
