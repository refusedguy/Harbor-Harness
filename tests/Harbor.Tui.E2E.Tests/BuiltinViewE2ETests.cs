using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
using Harbor.Tui.Abstractions.Views;
namespace Harbor.Tui.E2E.Tests;
/// <summary>
///     End-to-end view tests: instantiate each builtin view, bind a populated
///     view model, render into a <see cref="CaptureRenderContext" />, and assert
///     the rendered output contains the expected user-visible text.
///     These tests guard against regressions where a view silently produces an
///     empty output (e.g. early-return on a null ViewModel, or a switch case
///     that fails to write the role prefix).
/// </summary>
public class BuiltinViewE2ETests
{
    [Test]
    public async Task StatusBarView_RenderAsync_ProducesFormattedOutput()
    {
        var vm = new StatusBarViewModel
        {
            Provider = "anthropic",
            Model = "claude-opus-4-1",
            Agent = "code",
            Status = "running",
            Cost = 0.0123m,
            TokensIn = 1024,
            TokensOut = 256
        };
        var view = new StatusBarView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        await Assert.That(output).Contains("claude-opus-4-1");
        await Assert.That(output).Contains("running");
        await Assert.That(output).Contains("anthropic");
        await Assert.That(output).Contains("code");
    }

    [Test]
    public async Task ChatHistoryView_RenderAsync_ProducesEntries()
    {
        var vm = new ChatHistoryViewModel();
        vm.AddEntry(new ChatEntry("user", "Hello, can you help me with C#?", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("assistant", "Of course! What do you need help with?", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("tool", "→ read file.cs", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("tool-result", "✓ 42 lines", DateTimeOffset.UtcNow));
        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        await Assert.That(output).Contains("Hello, can you help me with C#?");
        await Assert.That(output).Contains("Of course! What do you need help with?");
        await Assert.That(output).Contains("→ read file.cs");
        await Assert.That(output).Contains("✓ 42 lines");
        await Assert.That(output).Contains("[user]");
        await Assert.That(output).Contains("[assistant]");
        await Assert.That(output).Contains("[tool]");
        await Assert.That(output).Contains("[result]");
    }

    [Test]
    public async Task InputView_RenderAsync_ProducesPrompt()
    {
        var vm = new InputViewModel
        {
            Text = "explain this code",
            CursorPosition = 4,
            Placeholder = "Type your message..."
        };
        var view = new InputView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        // The "> " prompt is the InputView's signature — it must appear even
        // when text is non-empty.
        await Assert.That(output).Contains("> ");
        await Assert.That(output).Contains("explain this code");
    }

    [Test]
    public async Task InputView_RenderAsync_ProducesPlaceholder_WhenEmpty()
    {
        var vm = new InputViewModel
        {
            Text = "",
            Placeholder = "Type your message..."
        };
        var view = new InputView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        await Assert.That(output).Contains("> ");
        await Assert.That(output).Contains("Type your message...");
    }

    [Test]
    public async Task DiffPreviewView_RenderAsync_ProducesDiff()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry(
            "write",
            "Wrote 142 chars to /tmp/test.cs",
            DateTimeOffset.UtcNow));
        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        await Assert.That(output).Contains("Diff 1 of 1");
        await Assert.That(output).Contains("[write]");
        await Assert.That(output).Contains("Wrote 142 chars");
    }

    [Test]
    public async Task DiffPreviewView_RenderAsync_ShowsNavigationHint_WhenMultipleDiffs()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1.cs", DateTimeOffset.UtcNow));
        vm.AddDiff(new DiffEntry("edit", "file2.cs", DateTimeOffset.UtcNow));
        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        string output = ctx.Output;
        await Assert.That(output).Contains("Diff 1 of 2");
        await Assert.That(output).Contains("next");
        await Assert.That(output).Contains("previous");
    }
}
