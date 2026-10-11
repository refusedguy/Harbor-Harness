using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Send-back preamble for issue #402 slice 2/2: the user turn carrying the
///     baked PNG names the source screenshot, the annotation count and the
///     saved file path (a bare image is ambiguous to the model), and the
///     send gate keeps baking silent while sending stays explicit.
/// </summary>
public class MarkupSendPreambleTests
{
    [Test]
    public async Task Build_NamesSource_Count_AndPath()
    {
        string preamble = MarkupSendPreamble.Build("broken.png", 3, "/shots/broken.annotated.png");

        await Assert.That(preamble).Contains("broken.png");
        await Assert.That(preamble).Contains("3 annotations");
        await Assert.That(preamble).Contains("/shots/broken.annotated.png");
    }

    [Test]
    public async Task Build_SingleAnnotation_UsesSingular()
    {
        string preamble = MarkupSendPreamble.Build("shot.png", 1, "/shots/shot.annotated.png");

        await Assert.That(preamble).Contains("1 annotation");
        await Assert.That(preamble).DoesNotContain("1 annotations");
    }

    [Test]
    public async Task Build_BlankName_FallsBackToImage()
    {
        string preamble = MarkupSendPreamble.Build("  ", 2, "/shots/x.annotated.png");

        await Assert.That(preamble).Contains("\"image\"");
    }

    [Test]
    public async Task IsSendable_ClosedOverlay_IsFalse()
    {
        await Assert.That(MarkupSendPreamble.IsSendable(MarkupOverlayState.Closed)).IsFalse();
    }

    [Test]
    public async Task IsSendable_OpenButUnsaved_IsFalse()
    {
        var markup = MarkupOverlayState.Open("/shots/b.png", "b.png", 800, 600, scrollOffset: 0) with
        {
            Model = MarkupAnnotationModel.Empty.Add(
                MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.5, 0.5)),
        };

        await Assert.That(MarkupSendPreamble.IsSendable(markup)).IsFalse();
    }

    [Test]
    public async Task IsSendable_OpenSavedButEmpty_IsFalse()
    {
        var markup = MarkupOverlayState.Open("/shots/b.png", "b.png", 800, 600, scrollOffset: 0) with
        {
            SavedPath = "/shots/b.annotated.png",
        };

        await Assert.That(MarkupSendPreamble.IsSendable(markup)).IsFalse();
    }

    [Test]
    public async Task IsSendable_OpenSavedWithItems_IsTrue()
    {
        var markup = MarkupOverlayState.Open("/shots/b.png", "b.png", 800, 600, scrollOffset: 0) with
        {
            Model = MarkupAnnotationModel.Empty.Add(
                MarkupKind.Rectangle, NormalizedPoint.Create(0.2, 0.2), NormalizedPoint.Create(0.6, 0.6)),
            SavedPath = "/shots/b.annotated.png",
        };

        await Assert.That(MarkupSendPreamble.IsSendable(markup)).IsTrue();
    }
}
