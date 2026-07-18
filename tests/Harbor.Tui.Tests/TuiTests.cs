using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
namespace Harbor.Tui.Tests;
public class StatusBarViewModelTests
{
    [Test]
    public async Task Initial_Values_Are_Defaults()
    {
        var vm = new StatusBarViewModel();
        await Assert.That(vm.Status).IsEqualTo("idle");
        await Assert.That(vm.Agent).IsEqualTo("code");
        await Assert.That(vm.Cost).IsEqualTo(0m);
        await Assert.That(vm.TokensIn).IsEqualTo(0);
        await Assert.That(vm.TokensOut).IsEqualTo(0);
    }

    [Test]
    public async Task AgentStartEvent_SetsStatusRunning()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await Assert.That(vm.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEndEvent_SetsStatusIdle()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(new AgentStartEvent("s1", Array.Empty<AgentMessage>()));
        await vm.UpdateFromEventAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));
        await Assert.That(vm.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task StepFinishEvent_AccumulatesTokens()
    {
        var vm = new StatusBarViewModel();
        var usage = new Usage(100, 50);
        var evt = new MessageUpdateEvent(new StepFinishEvent(0, "stop", usage), AssistantMessage.Empty("s1", "m"));
        await vm.UpdateFromEventAsync(evt);
        await Assert.That(vm.TokensIn).IsEqualTo(100);
        await Assert.That(vm.TokensOut).IsEqualTo(50);

        var usage2 = new Usage(200, 75);
        var evt2 = new MessageUpdateEvent(new StepFinishEvent(0, "stop", usage2), AssistantMessage.Empty("s1", "m"));
        await vm.UpdateFromEventAsync(evt2);
        await Assert.That(vm.TokensIn).IsEqualTo(300);
        await Assert.That(vm.TokensOut).IsEqualTo(125);
    }

    [Test]
    public async Task AgentErrorEvent_SetsStatusError()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(new AgentErrorEvent("boom"));
        await Assert.That(vm.Status).IsEqualTo("error");
    }

    [Test]
    public async Task CompactionStartedEvent_SetsStatusCompacting()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(new CompactionStartedEvent("s1"));
        await Assert.That(vm.Status).IsEqualTo("compacting");
    }

    [Test]
    public async Task CompactionCompletedEvent_SetsStatusRunning()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(new CompactionStartedEvent("s1"));
        await vm.UpdateFromEventAsync(new CompactionCompletedEvent("s1", "summary", 10, 1000, TimeSpan.FromSeconds(1)));
        await Assert.That(vm.Status).IsEqualTo("running");
    }

    [Test]
    public async Task Formatted_Contains_Model_And_Status()
    {
        var vm = new StatusBarViewModel
        {
            Model = "claude-opus-4",
            Provider = "anthropic",
            Status = "running"
        };
        string formatted = vm.Formatted;
        await Assert.That(formatted).Contains("claude-opus-4");
        await Assert.That(formatted).Contains("running");
        await Assert.That(formatted).Contains("anthropic");
    }

    [Test]
    public async Task ResetCommand_ClearsCounters()
    {
        var vm = new StatusBarViewModel();
        var usage = new Usage(100, 50);
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new StepFinishEvent(0, "stop", usage), AssistantMessage.Empty("s1", "m")));
        vm.ResetCommand.Execute(null);
        await Assert.That(vm.TokensIn).IsEqualTo(0);
        await Assert.That(vm.TokensOut).IsEqualTo(0);
        await Assert.That(vm.Status).IsEqualTo("idle");
    }
}

public class ChatHistoryViewModelTests
{
    [Test]
    public async Task MessageStartEvent_SetsStreaming()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await Assert.That(vm.IsStreaming).IsTrue();
        await Assert.That(vm.StreamingText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextDeltaEvent_AppendsToStreamingText()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello"), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new TextDeltaEvent("0", ", "), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "World!"), AssistantMessage.Empty("s1", "m")));
        await Assert.That(vm.StreamingText).IsEqualTo("Hello, World!");
    }

    [Test]
    public async Task ThinkingDeltaEvent_AppendsToThinkingText()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new ThinkingDeltaEvent("0", "Let me think..."), AssistantMessage.Empty("s1", "m")));
        await Assert.That(vm.IsThinking).IsTrue();
        await Assert.That(vm.ThinkingText).IsEqualTo("Let me think...");
    }

    [Test]
    public async Task MessageEndEvent_AddsEntryAndResets()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageStartEvent(AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(new TextDeltaEvent("0", "Hello!"), AssistantMessage.Empty("s1", "m")));
        await vm.UpdateFromEventAsync(new MessageEndEvent(AssistantMessage.Empty("s1", "m")));

        await Assert.That(vm.IsStreaming).IsFalse();
        await Assert.That(vm.StreamingText).IsEqualTo(string.Empty);
        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("assistant");
        await Assert.That(vm.Entries[0].Content).IsEqualTo("Hello!");
    }

    [Test]
    public async Task ToolCallStartEvent_AddsToolEntry()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(new MessageUpdateEvent(
            new ToolCallStartEvent("tc1", "read"),
            AssistantMessage.Empty("s1", "m")));

        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("tool");
        await Assert.That(vm.Entries[0].Content).Contains("read");
    }

    [Test]
    public async Task AgentStartEvent_AddsUserEntries()
    {
        var vm = new ChatHistoryViewModel();
        var userMsg = new UserMessage("u1", "s1", DateTimeOffset.UtcNow, "Hello agent", "code", "claude");
        await vm.UpdateFromEventAsync(new AgentStartEvent("s1", new AgentMessage[] { userMsg }));

        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("user");
        await Assert.That(vm.Entries[0].Content).IsEqualTo("Hello agent");
    }

    [Test]
    public async Task ClearCommand_RemovesAllEntries()
    {
        var vm = new ChatHistoryViewModel();
        vm.AddEntry(new ChatEntry("user", "test", DateTimeOffset.UtcNow));
        vm.AddEntry(new ChatEntry("assistant", "response", DateTimeOffset.UtcNow));
        await Assert.That(vm.Entries.Count).IsEqualTo(2);

        vm.ClearHistoryCommand.Execute(null);
        await Assert.That(vm.Entries.Count).IsEqualTo(0);
        await Assert.That(vm.StreamingText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ToolExecutionEndEvent_AddsResultEntry()
    {
        var vm = new ChatHistoryViewModel();
        var result = new ToolResult("output text", false);
        await vm.UpdateFromEventAsync(new ToolExecutionEndEvent("tc1", result, false));

        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("tool-result");
        await Assert.That(vm.Entries[0].Content).Contains("output text");
    }
}

public class InputViewModelTests
{
    [Test]
    public async Task Initial_Values_Are_Defaults()
    {
        var vm = new InputViewModel();
        await Assert.That(vm.Text).IsEqualTo(string.Empty);
        await Assert.That(vm.Placeholder).Contains("Type");
        await Assert.That(vm.IsMultiline).IsFalse();
        await Assert.That(vm.CursorPosition).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitCommand_ClearsText()
    {
        var vm = new InputViewModel { Text = "hello", CursorPosition = 5 };
        vm.SubmitCommand.Execute(null);
        await Assert.That(vm.Text).IsEqualTo(string.Empty);
        await Assert.That(vm.CursorPosition).IsEqualTo(0);
    }

    [Test]
    public async Task CancelCommand_ResetsCursor_KeepsText()
    {
        var vm = new InputViewModel { Text = "hello", CursorPosition = 5 };
        vm.CancelCommand.Execute(null);
        await Assert.That(vm.Text).IsEqualTo("hello");
        await Assert.That(vm.CursorPosition).IsEqualTo(0);
    }
}

public class DiffPreviewViewModelTests
{
    [Test]
    public async Task Initial_Values_Are_Defaults()
    {
        var vm = new DiffPreviewViewModel();
        await Assert.That(vm.CurrentIndex).IsEqualTo(-1);
        await Assert.That(vm.Diffs.Count).IsEqualTo(0);
        await Assert.That(vm.Current).IsNull();
    }

    [Test]
    public async Task AddDiff_SetsCurrentIndex()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "Wrote file.txt", DateTimeOffset.UtcNow));
        await Assert.That(vm.Diffs.Count).IsEqualTo(1);
        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
        await Assert.That(vm.Current).IsNotNull();
    }

    [Test]
    public async Task NextDiffCommand_MovesToNext()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));
        vm.AddDiff(new DiffEntry("edit", "file2", DateTimeOffset.UtcNow));
        await Assert.That(vm.CurrentIndex).IsEqualTo(0);

        vm.NextDiffCommand.Execute(null);
        await Assert.That(vm.CurrentIndex).IsEqualTo(1);
    }

    [Test]
    public async Task NextDiffCommand_CannotExecute_AtLastIndex()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));
        await Assert.That(vm.NextDiffCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task PreviousDiffCommand_MovesToPrevious()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));
        vm.AddDiff(new DiffEntry("edit", "file2", DateTimeOffset.UtcNow));
        vm.NextDiffCommand.Execute(null);
        await Assert.That(vm.CurrentIndex).IsEqualTo(1);

        vm.PreviousDiffCommand.Execute(null);
        await Assert.That(vm.CurrentIndex).IsEqualTo(0);
    }

    [Test]
    public async Task PreviousDiffCommand_CannotExecute_AtFirstIndex()
    {
        var vm = new DiffPreviewViewModel();
        vm.AddDiff(new DiffEntry("write", "file1", DateTimeOffset.UtcNow));
        await Assert.That(vm.PreviousDiffCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task ToolExecutionEndEvent_AddsDiffForFileChanges()
    {
        var vm = new DiffPreviewViewModel();
        var result = new ToolResult("Wrote 100 chars to /tmp/test.cs", false);
        await vm.UpdateFromEventAsync(new ToolExecutionEndEvent("tc1", result, false));
        await Assert.That(vm.Diffs.Count).IsEqualTo(1);
    }
}

public class CaptureRenderContextTests
{
    [Test]
    public async Task Write_AppendsText()
    {
        var ctx = new CaptureRenderContext();
        ctx.Write("hello");
        ctx.Write(" world");
        await Assert.That(ctx.Output).IsEqualTo("hello world");
    }

    [Test]
    public async Task WriteLine_AppendsLine()
    {
        var ctx = new CaptureRenderContext();
        ctx.WriteLine("line1");
        ctx.WriteLine("line2");
        await Assert.That(ctx.Output).Contains("line1");
        await Assert.That(ctx.Output).Contains("line2");
    }

    [Test]
    public async Task WriteColored_AppendsText_WithoutColor()
    {
        var ctx = new CaptureRenderContext();
        ctx.WriteColored("text", TuiColor.Red);
        await Assert.That(ctx.Output).IsEqualTo("text");
    }

    [Test]
    public async Task WriteStyled_AppendsText_WithoutStyle()
    {
        var ctx = new CaptureRenderContext();
        ctx.WriteStyled("text", TuiStyle.Bold | TuiStyle.Italic);
        await Assert.That(ctx.Output).IsEqualTo("text");
    }

    [Test]
    public async Task Clear_EmptiesOutput()
    {
        var ctx = new CaptureRenderContext();
        ctx.Write("data");
        ctx.Clear();
        await Assert.That(ctx.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SupportsColor_IsFalse()
    {
        var ctx = new CaptureRenderContext();
        await Assert.That(ctx.SupportsColor).IsFalse();
    }

    [Test]
    public async Task Width_And_Height_AreNonZero()
    {
        var ctx = new CaptureRenderContext();
        await Assert.That(ctx.Width).IsEqualTo(80);
        await Assert.That(ctx.Height).IsEqualTo(24);
    }
}

public class ViewRegistryTests
{
    [Test]
    public async Task Register_AddsView()
    {
        var registry = new ViewRegistry();
        var view = new TestView("test-view", "Test View", TuiViewPlacement.StatusBar);
        registry.Register(view);

        var found = registry.Get("test-view");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo("test-view");
    }

    [Test]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var registry = new ViewRegistry();
        var found = registry.Get("non-existent");
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task Unregister_RemovesView()
    {
        var registry = new ViewRegistry();
        registry.Register(new TestView("test-view", "Test", TuiViewPlacement.StatusBar));
        bool removed = registry.Unregister("test-view");
        await Assert.That(removed).IsTrue();
        await Assert.That(registry.Get("test-view")).IsNull();
    }

    [Test]
    public async Task GetByPlacement_ReturnsCorrectViews()
    {
        var registry = new ViewRegistry();
        registry.Register(new TestView("status-1", "Status 1", TuiViewPlacement.StatusBar));
        registry.Register(new TestView("status-2", "Status 2", TuiViewPlacement.StatusBar));
        registry.Register(new TestView("chat-1", "Chat", TuiViewPlacement.ChatHistory));

        var statusViews = registry.GetByPlacement(TuiViewPlacement.StatusBar);
        await Assert.That(statusViews.Count).IsEqualTo(2);

        var chatViews = registry.GetByPlacement(TuiViewPlacement.ChatHistory);
        await Assert.That(chatViews.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetAll_ReturnsAllRegisteredViews()
    {
        var registry = new ViewRegistry();
        registry.Register(new TestView("v1", "V1", TuiViewPlacement.StatusBar));
        registry.Register(new TestView("v2", "V2", TuiViewPlacement.ChatHistory));
        registry.Register(new TestView("v3", "V3", TuiViewPlacement.Input));

        var all = registry.GetAll();
        await Assert.That(all.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Register_ReplacesExistingView_WithSameId()
    {
        var registry = new ViewRegistry();
        registry.Register(new TestView("v1", "Original", TuiViewPlacement.StatusBar));
        registry.Register(new TestView("v1", "Replaced", TuiViewPlacement.StatusBar));

        var all = registry.GetByPlacement(TuiViewPlacement.StatusBar);
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].DisplayName).IsEqualTo("Replaced");
    }

    [Test]
    public async Task Freeze_EnablesFastLookup()
    {
        var registry = new ViewRegistry();
        registry.Register(new TestView("v1", "V1", TuiViewPlacement.StatusBar));
        registry.Freeze();

        var found = registry.Get("v1");
        await Assert.That(found).IsNotNull();
    }
}

public class ViewModelRegistryTests
{
    [Test]
    public async Task Register_AddsViewModel()
    {
        var registry = new ViewModelRegistry();
        var vm = new StatusBarViewModel();
        registry.Register(vm);

        var found = registry.Get<StatusBarViewModel>("status-bar");
        await Assert.That(found).IsNotNull();
    }

    [Test]
    public async Task Get_Typed_ReturnsCorrectType()
    {
        var registry = new ViewModelRegistry();
        var vm = new ChatHistoryViewModel();
        registry.Register(vm);

        var found = registry.Get<ChatHistoryViewModel>("chat-history");
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo("chat-history");
    }

    [Test]
    public async Task Unregister_RemovesViewModel()
    {
        var registry = new ViewModelRegistry();
        registry.Register(new StatusBarViewModel());
        bool removed = registry.Unregister("status-bar");
        await Assert.That(removed).IsTrue();
        await Assert.That(registry.Get("status-bar")).IsNull();
    }

    [Test]
    public async Task GetAll_ReturnsAllRegisteredViewModels()
    {
        var registry = new ViewModelRegistry();
        registry.Register(new StatusBarViewModel());
        registry.Register(new ChatHistoryViewModel());
        registry.Register(new InputViewModel());

        var all = registry.GetAll();
        await Assert.That(all.Count).IsEqualTo(3);
    }
}

internal sealed class TestView : ITuiView
{
    public TestView(string id, string displayName, TuiViewPlacement placement)
    {
        Id = id;
        DisplayName = displayName;
        Placement = placement;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public TuiViewPlacement Placement { get; }
    public ITuiViewModel? ViewModel { get; set; }

    public Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) => Task.CompletedTask;
    public void Dispose() { }
}
