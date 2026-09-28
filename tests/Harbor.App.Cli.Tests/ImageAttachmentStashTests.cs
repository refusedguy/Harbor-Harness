using Harbor.Abstractions.Models;
using Harbor.App.Cli.Repl;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #386 — the staged-image stash behind <c>/attach</c>. Draining must
///     be destructive: an image belongs to exactly one turn, and re-sending it
///     on every later prompt would silently re-bill the user for it forever.
/// </summary>
public class ImageAttachmentStashTests
{
    private static ImageAttachment Image(string path, string mime = "image/png") =>
        new(path, mime, 10, 20, [0x01, 0x02]);

    [Test]
    public async Task FreshStash_IsEmptyAndDescribesNothing()
    {
        var stash = new ImageAttachmentStash();

        await Assert.That(stash.Count).IsEqualTo(0);
        await Assert.That(stash.Drain()).IsNull();
        await Assert.That(stash.Describe()).IsNull();
    }

    [Test]
    public async Task TryStage_ThenDrain_ReturnsEverythingAndEmptiesTheStash()
    {
        var stash = new ImageAttachmentStash();
        await Assert.That(stash.TryStage(Image("/a.png"))).IsTrue();
        await Assert.That(stash.TryStage(Image("/b.jpg", "image/jpeg"))).IsTrue();
        await Assert.That(stash.Count).IsEqualTo(2);

        var drained = stash.Drain();
        await Assert.That(drained).IsNotNull();
        await Assert.That(drained!.Count).IsEqualTo(2);
        await Assert.That(drained[0].Path).IsEqualTo("/a.png");
        await Assert.That(drained[1].MimeType).IsEqualTo("image/jpeg");

        // Second drain is empty — the images went to the turn that asked for them.
        await Assert.That(stash.Count).IsEqualTo(0);
        await Assert.That(stash.Drain()).IsNull();
    }

    [Test]
    public async Task TryStage_BeyondTheCap_Refuses()
    {
        var stash = new ImageAttachmentStash();
        for (int i = 0; i < ImageAttachmentStash.MaxPending; i++)
            await Assert.That(stash.TryStage(Image($"/{i}.png"))).IsTrue();

        await Assert.That(stash.TryStage(Image("/overflow.png"))).IsFalse();
        await Assert.That(stash.Count).IsEqualTo(ImageAttachmentStash.MaxPending);
    }

    [Test]
    public async Task Clear_DropsEverything()
    {
        var stash = new ImageAttachmentStash();
        stash.TryStage(Image("/a.png"));

        stash.Clear();

        await Assert.That(stash.Count).IsEqualTo(0);
        await Assert.That(stash.Drain()).IsNull();
    }

    [Test]
    public async Task Describe_ListsNameAndDimensions()
    {
        var stash = new ImageAttachmentStash();
        stash.TryStage(new ImageAttachment("/tmp/screenshot.png", "image/png", 1024, 768, [0x01]));

        await Assert.That(stash.Describe()).IsEqualTo("screenshot.png (1024×768)");
    }

    [Test]
    public async Task Describe_FallsBackToMimeTypeWhenDimensionsAreUnknown()
    {
        var stash = new ImageAttachmentStash();
        stash.TryStage(new ImageAttachment("/tmp/x.png", "image/png", 0, 0, [0x01]));

        await Assert.That(stash.Describe()).IsEqualTo("x.png (image/png)");
    }
}
