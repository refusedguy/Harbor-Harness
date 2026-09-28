using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.Terminal.Abstractions.Renderers;
namespace Harbor.Tui.Tests;

/// <summary>
/// ENG12 #284 (TGui snapshot pattern): draw paths iterate a snapshot of the
/// chat entries, never the live collection. These pins guard the snapshot
/// semantics — isolation from later appends and unchanged rendered output.
/// </summary>
public class Eng12SnapshotTests
{
    [Test]
    public async Task SnapshotEntries_IsolatesDrawIteration_FromLaterAppends()
    {
        var vm = new ChatHistoryViewModel();
        vm.AddEntry(new ChatEntry("user", "first", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("assistant", "second", DateTimeOffset.UtcNow));

        ChatEntry[] snapshot = vm.SnapshotEntries();

        vm.AddEntry(new ChatEntry("assistant", "third", DateTimeOffset.UtcNow));

        await Assert.That(snapshot.Length).IsEqualTo(2);
        await Assert.That(snapshot[0].Content).IsEqualTo("first");
        await Assert.That(snapshot[1].Content).IsEqualTo("second");
        await Assert.That(vm.Entries.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Render_RendersSnapshotEntries_UnchangedOutput()
    {
        var vm = new ChatHistoryViewModel();
        vm.AddEntry(new ChatEntry("user", "Hello", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("assistant", "Hi there", DateTimeOffset.UtcNow));
        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("Hello");
        await Assert.That(ctx.Output).Contains("Hi there");
        await Assert.That(ctx.Output).Contains("[user]");
        await Assert.That(ctx.Output).Contains("[assistant]");
    }
}
