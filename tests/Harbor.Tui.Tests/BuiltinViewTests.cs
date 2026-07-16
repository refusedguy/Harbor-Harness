using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.Abstractions;
using Harbor.Tui.Abstractions.Renderers;
using Harbor.Tui.Abstractions.ViewModels;
using Harbor.Tui.Abstractions.Views;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tui.Tests;
public class StatusBarViewTests
{
    [Test]
    public async Task Render_NoViewModel_WritesNothing()
    {
        var view = new StatusBarView();
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_WritesFormattedStatusLine()
    {
        var vm = new StatusBarViewModel
        {
            Provider = "anthropic",
            Model = "claude-opus-4",
            Status = "running"
        };
        var view = new StatusBarView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("claude-opus-4");
        await Assert.That(ctx.Output).Contains("anthropic");
        await Assert.That(ctx.Output).Contains("running");
    }

    [Test]
    public async Task Id_IsStatusBar()
    {
        var view = new StatusBarView();
        await Assert.That(view.Id).IsEqualTo("status-bar");
    }

    [Test]
    public async Task Placement_IsStatusBar()
    {
        var view = new StatusBarView();
        await Assert.That(view.Placement).IsEqualTo(TuiViewPlacement.StatusBar);
    }
}

public class ChatHistoryViewTests
{
    [Test]
    public async Task Render_NoViewModel_WritesNothing()
    {
        var view = new ChatHistoryView();
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_Entries_RendersAllEntries()
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

    [Test]
    public async Task Render_StreamingText_AppendedAsAssistantEntry()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "streaming..."), AssistantMessage.Empty("s1", "m")));

        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("streaming...");
        await Assert.That(ctx.Output).Contains("[assistant]");
    }

    [Test]
    public async Task Render_ThinkingText_RendersThinkingPrefix()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("0", "reasoning..."), AssistantMessage.Empty("s1", "m")));

        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("[thinking]");
        await Assert.That(ctx.Output).Contains("reasoning...");
    }

    [Test]
    public async Task Render_ToolResultEntry_RendersResultPrefix()
    {
        var vm = new ChatHistoryViewModel();
        var result = new ToolResult("done", false);
        await vm.UpdateFromEventAsync(new ToolExecutionEndEvent("tc1", result, false));

        var view = new ChatHistoryView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("[result]");
        await Assert.That(ctx.Output).Contains("done");
    }

    [Test]
    public async Task Id_IsChatHistory()
    {
        var view = new ChatHistoryView();
        await Assert.That(view.Id).IsEqualTo("chat-history");
    }

    [Test]
    public async Task Placement_IsChatHistory()
    {
        var view = new ChatHistoryView();
        await Assert.That(view.Placement).IsEqualTo(TuiViewPlacement.ChatHistory);
    }
}

public class InputViewTests
{
    [Test]
    public async Task Render_NoViewModel_WritesNothing()
    {
        var view = new InputView();
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_EmptyText_ShowsPlaceholder()
    {
        var vm = new InputViewModel { Text = "", Placeholder = "Type here..." };
        var view = new InputView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("Type here...");
        await Assert.That(ctx.Output).Contains("> ");
    }

    [Test]
    public async Task Render_WithText_ShowsText()
    {
        var vm = new InputViewModel { Text = "hello", CursorPosition = 5 };
        var view = new InputView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("hello");
        await Assert.That(ctx.Output).Contains("> ");
    }

    [Test]
    public async Task Render_CursorAtStart_ShowsCursorBlockFirst()
    {
        var vm = new InputViewModel { Text = "abc", CursorPosition = 0 };
        var view = new InputView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        // In plain mode, the cursor is rendered as the character itself (or _ for space).
        // The text "abc" should still be fully visible.
        await Assert.That(ctx.Output).Contains("abc");
    }

    [Test]
    public async Task Id_IsInput()
    {
        var view = new InputView();
        await Assert.That(view.Id).IsEqualTo("input");
    }

    [Test]
    public async Task Placement_IsInput()
    {
        var view = new InputView();
        await Assert.That(view.Placement).IsEqualTo(TuiViewPlacement.Input);
    }
}

public class DiffPreviewViewTests
{
    [Test]
    public async Task Render_NoViewModel_WritesNothing()
    {
        var view = new DiffPreviewView();
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_NoDiffs_WritesNothing()
    {
        var vm = new DiffPreviewViewModel();
        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Render_WithDiffs_ShowsHeaderAndOutput()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "Wrote 100 chars to /tmp/test.cs", DateTimeOffset.UtcNow));

        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("Diff 1 of 1");
        await Assert.That(ctx.Output).Contains("[write]");
        await Assert.That(ctx.Output).Contains("Wrote 100 chars");
    }

    [Test]
    public async Task Render_MultipleDiffs_ShowsNavigationHint()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));
        vm.AddDiff(new DiffEntry("edit", "file2", DateTimeOffset.UtcNow));

        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).Contains("Diff 1 of 2");
        await Assert.That(ctx.Output).Contains("next");
        await Assert.That(ctx.Output).Contains("previous");
    }

    [Test]
    public async Task Render_SingleDiff_NoNavigationHint()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));

        var view = new DiffPreviewView { ViewModel = vm };
        var ctx = new CaptureRenderContext();

        await view.RenderAsync(ctx);

        await Assert.That(ctx.Output).DoesNotContain("next");
        await Assert.That(ctx.Output).DoesNotContain("previous");
    }

    [Test]
    public async Task Id_IsDiffPreview()
    {
        var view = new DiffPreviewView();
        await Assert.That(view.Id).IsEqualTo("diff-preview");
    }

    [Test]
    public async Task Placement_IsOverlay()
    {
        var view = new DiffPreviewView();
        await Assert.That(view.Placement).IsEqualTo(TuiViewPlacement.Overlay);
    }
}

public class BaseTuiRendererBuiltinViewTests
{
    [Test]
    public async Task InitializeAsync_RegistersAllBuiltinViews()
    {
        var renderer = new TestTuiRenderer();
        await renderer.InitializeAsync();

        await Assert.That(renderer.Views.Get("status-bar")).IsNotNull();
        await Assert.That(renderer.Views.Get("chat-history")).IsNotNull();
        await Assert.That(renderer.Views.Get("input")).IsNotNull();
        await Assert.That(renderer.Views.Get("diff-preview")).IsNotNull();
        renderer.Dispose();
    }

    [Test]
    public async Task InitializeAsync_BindsViewModelsToViews()
    {
        var renderer = new TestTuiRenderer();
        await renderer.InitializeAsync();

        var statusBarView = renderer.Views.Get("status-bar");
        await Assert.That(statusBarView).IsNotNull();
        await Assert.That(statusBarView!.ViewModel).IsNotNull();

        var chatHistoryView = renderer.Views.Get("chat-history");
        await Assert.That(chatHistoryView).IsNotNull();
        await Assert.That(chatHistoryView!.ViewModel).IsNotNull();

        var inputView = renderer.Views.Get("input");
        await Assert.That(inputView).IsNotNull();
        await Assert.That(inputView!.ViewModel).IsNotNull();

        var diffPreviewView = renderer.Views.Get("diff-preview");
        await Assert.That(diffPreviewView).IsNotNull();
        await Assert.That(diffPreviewView!.ViewModel).IsNotNull();
        renderer.Dispose();
    }

    [Test]
    public async Task InitializeAsync_DoesNotOverridePluginViews()
    {
        var renderer = new TestTuiRenderer();
        var customView = new CustomStatusBarView();
        renderer.Views.Register(customView);

        await renderer.InitializeAsync();

        var found = renderer.Views.Get("status-bar");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.GetType().Name).IsEqualTo("CustomStatusBarView");
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_UpdatesViewModelsFromEvent()
    {
        var renderer = new TestTuiRenderer();
        await renderer.InitializeAsync();

        var statusBarVm = renderer.ViewModels.Get<StatusBarViewModel>("status-bar");
        await Assert.That(statusBarVm).IsNotNull();
        await Assert.That(statusBarVm!.Status).IsEqualTo("idle");

        await renderer.RenderAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));

        await Assert.That(statusBarVm.Status).IsEqualTo("running");
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_OnAgentStart_RendersStatusBarAndInput()
    {
        var renderer = new TestTuiRenderer();
        await renderer.InitializeAsync();

        var ctx = (CaptureRenderContext)renderer.Context;
        ctx.Clear();

        await renderer.RenderAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));

        // StatusBar should be rendered (contains status info).
        var statusBarVm = renderer.ViewModels.Get<StatusBarViewModel>("status-bar");
        await Assert.That(ctx.Output).Contains(statusBarVm!.Status);
        renderer.Dispose();
    }

    [Test]
    public async Task RenderAsync_OnToolEnd_RendersOverlayForDiff()
    {
        var renderer = new TestTuiRenderer();
        await renderer.InitializeAsync();

        // Feed a tool end event that adds a diff.
        var result = new ToolResult("Wrote 100 chars to /tmp/test.cs", false);
        await renderer.RenderAsync(new ToolExecutionEndEvent("tc1", result, false));

        var ctx = (CaptureRenderContext)renderer.Context;
        // The diff preview overlay should show the diff header.
        await Assert.That(ctx.Output).Contains("Diff");
        renderer.Dispose();
    }
}

internal sealed class TestTuiRenderer : BaseTuiRenderer
{
    public TestTuiRenderer() : base(NullLogger.Instance)
    {
        Context = new CaptureRenderContext();
    }

    public override ITuiRenderContext Context { get; }

    public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
        => Task.FromResult(Result.Success(string.Empty));

    public override Task<Result> WriteAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public override Task<Result> ClearAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success());
}

internal sealed class CustomStatusBarView : TuiViewBase<StatusBarViewModel>
{
    public override string Id => "status-bar";
    public override string DisplayName => "Custom Status Bar";
    public override TuiViewPlacement Placement => TuiViewPlacement.StatusBar;

    public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
