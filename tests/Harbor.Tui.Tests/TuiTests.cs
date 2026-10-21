using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Terminal.Abstractions.ViewModels;
using Harbor.Terminal.Abstractions.Views;
using Harbor.TestKit;
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
        await vm.UpdateFromEventAsync(TestEvents.AgentStart());
        await Assert.That(vm.Status).IsEqualTo("running");
    }

    [Test]
    public async Task AgentEndEvent_SetsStatusIdle()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.AgentStart());
        await vm.UpdateFromEventAsync(TestEvents.AgentEnd());
        await Assert.That(vm.Status).IsEqualTo("idle");
    }

    [Test]
    public async Task StepFinishEvent_AccumulatesTokens()
    {
        var vm = new StatusBarViewModel();
        var usage = new Usage(100, 50);
        var evt = TestEvents.StepFinishUpdate(usage: usage);
        await vm.UpdateFromEventAsync(evt);
        await Assert.That(vm.TokensIn).IsEqualTo(100);
        await Assert.That(vm.TokensOut).IsEqualTo(50);

        var usage2 = new Usage(200, 75);
        var evt2 = TestEvents.StepFinishUpdate(usage: usage2);
        await vm.UpdateFromEventAsync(evt2);
        await Assert.That(vm.TokensIn).IsEqualTo(300);
        await Assert.That(vm.TokensOut).IsEqualTo(125);
    }

    [Test]
    public async Task AgentErrorEvent_SetsStatusError()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.AgentError());
        await Assert.That(vm.Status).IsEqualTo("error");
    }

    [Test]
    public async Task CompactionStartedEvent_SetsStatusCompacting()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.CompactionStarted());
        await Assert.That(vm.Status).IsEqualTo("compacting");
    }

    [Test]
    public async Task CompactionCompletedEvent_SetsStatusRunning()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.CompactionStarted());
        await vm.UpdateFromEventAsync(TestEvents.CompactionCompleted(prunedMessageCount: 10, tokensSaved: 1000));
        await Assert.That(vm.Status).IsEqualTo("running");
    }

    /// <summary>
    ///     #773 — the legacy VM path's half of the compaction gap. This VM has
    ///     no <c>MessageStartEvent</c> arm, so unlike the store projection (which
    ///     self-corrects on the next message start) it has nothing that would
    ///     clear a stale "compacting". The status must leave "compacting" the
    ///     moment the failure lands.
    ///     <para>
    ///         "running", not "error": the core engaged the truncation fallback
    ///         and continued the turn. A status bar is one collapsed line, so
    ///         the value has to be the true one — the degradation itself is
    ///         narrated by the renderers that have a transcript (see the sibling
    ///         issue filed with #773).
    ///     </para>
    /// </summary>
    [Test]
    public async Task CompactionFailedEvent_LeavesStatusCompacting()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.CompactionStarted());
        await Assert.That(vm.Status).IsEqualTo("compacting");

        await vm.UpdateFromEventAsync(TestEvents.CompactionFailed(error: "summarizer timed out"));

        await Assert.That(vm.Status).IsEqualTo("running")
            .Because("compaction is over and the turn continues; \"compacting\" would be a "
                   + "stale label with no MessageStartEvent arm in this VM to correct it");
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
        await vm.UpdateFromEventAsync(TestEvents.StepFinishUpdate(usage: usage));
        vm.ResetCommand.Execute(null);
        await Assert.That(vm.TokensIn).IsEqualTo(0);
        await Assert.That(vm.TokensOut).IsEqualTo(0);
        await Assert.That(vm.Status).IsEqualTo("idle");
    }

    /// <summary>
    ///     #623/#641 — the ctx cell is context-window <em>occupancy</em>, not
    ///     session-cumulative spend. <see cref="SessionMetadata.AddUsage" />
    ///     accumulates <c>TokensInput</c> for the whole session, so
    ///     <c>SessionStatsEvent.Metadata</c> is a running total; reading it as
    ///     occupancy made the cell climb 30k → 60k → 90k → 120k → 150k while
    ///     every request stayed 30k, pinning it at 100% by turn 4 on a 128k
    ///     window. Occupancy is the prompt-token count of the request the
    ///     provider just accepted (<c>StepFinishEvent.Usage.InputTokens</c>) —
    ///     the same fix #630 landed for the CellForge ctx bar.
    /// </summary>
    [Test]
    public async Task ContextPct_Is_Last_Request_Size_Not_Cumulative_Spend()
    {
        // Window and per-turn request size from the issue report. The window is
        // small enough that the cumulative total blows past it by turn 4.
        const int Window = 128_000;
        const int RequestTokens = 30_000;

        var vm = new StatusBarViewModel();
        vm.SetModel(new ModelInfo("hy3", "kilocode", "Kilocode Hy3", Window, 4096, false, false, true, Pricing.Unknown, "openai"));

        for (int turn = 1; turn <= 5; turn++)
        {
            // The request actually sent this turn: unchanged at 30k. This is
            // the only event that carries the occupancy reading.
            await vm.UpdateFromEventAsync(TestEvents.StepFinishUpdate(usage: new Usage(RequestTokens, 500)));

            // Then the core publishes the session-cumulative spend — the running
            // total that used to be rendered as occupancy (30k, 60k, …, 150k).
            // The arm assigns TokensIn/TokensOut, so the last write wins.
            await vm.UpdateFromEventAsync(TestEvents.SessionStats(metadata: new SessionMetadata(0.01m * turn, RequestTokens * turn, 500, 0, 0, 0, turn, null)));

            // Occupancy is the request just sent, never the cumulative total.
            await Assert.That(vm.RequestTokens).IsEqualTo(RequestTokens);
            await Assert.That(vm.ContextPct).IsEqualTo(ContextUsage.PercentUsed(RequestTokens, Window));
        }

        // 30k of 128k = 23%. The cumulative total (150k) exceeds the whole
        // window, so the old reading saturated at 100%.
        await Assert.That(vm.ContextPct).IsEqualTo(23);
        await Assert.That(vm.Formatted).Contains("ctx: 30k/23%");

        // The spend segments still track cumulative totals — only ctx moved.
        await Assert.That(vm.TokensIn).IsEqualTo(150_000);
        await Assert.That(vm.TokensOut).IsEqualTo(500);
        await Assert.That(vm.Formatted).Contains("150000↑ 500↓");
    }

    /// <summary>
    ///     The other half of the guard: a stats event alone must not move the
    ///     occupancy reading (there was no request to measure), while the
    ///     cumulative totals and the #653 core-owned cost must still land.
    /// </summary>
    [Test]
    public async Task SessionStats_Alone_Does_Not_Move_Occupancy()
    {
        const int Window = 128_000;
        const int RequestTokens = 30_000;

        var vm = new StatusBarViewModel();
        vm.SetModel(new ModelInfo("hy3", "kilocode", "Kilocode Hy3", Window, 4096, false, false, true, Pricing.Unknown, "openai"));
        await vm.UpdateFromEventAsync(TestEvents.StepFinishUpdate(usage: new Usage(RequestTokens, 500)));

        int occupancy = vm.ContextPct;
        await vm.UpdateFromEventAsync(TestEvents.SessionStats(metadata: new SessionMetadata(1.5m, 150_000, 500, 0, 0, 0, 5, null)));

        // A running total is not an occupancy reading.
        await Assert.That(vm.ContextPct).IsEqualTo(occupancy);
        await Assert.That(vm.ContextPct).IsEqualTo(23);

        // #653: the core owns cost and the token totals — assign, never re-derive.
        await Assert.That(vm.Cost).IsEqualTo(1.5m);
        await Assert.That(vm.IsCostKnown).IsTrue();
        await Assert.That(vm.TokensIn).IsEqualTo(150_000);
    }

    [Test]
    public async Task ContextPct_Zero_When_Window_Unknown()
    {
        var vm = new StatusBarViewModel();
        await vm.UpdateFromEventAsync(TestEvents.StepFinishUpdate(usage: new Usage(3000, 500)));
        await Assert.That(vm.ContextPct).IsEqualTo(0);
    }
}

public class ChatHistoryViewModelTests
{
    [Test]
    public async Task MessageStartEvent_SetsStreaming()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(TestEvents.MessageStart());
        await Assert.That(vm.IsStreaming).IsTrue();
        await Assert.That(vm.StreamingText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TextDeltaEvent_AppendsToStreamingText()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(TestEvents.MessageStart());
        await vm.UpdateFromEventAsync(TestEvents.TextDeltaUpdate("0", "Hello"));
        await vm.UpdateFromEventAsync(TestEvents.TextDeltaUpdate("0", ", "));
        await vm.UpdateFromEventAsync(TestEvents.TextDeltaUpdate("0", "World!"));
        await Assert.That(vm.StreamingText).IsEqualTo("Hello, World!");
    }

    [Test]
    public async Task ThinkingDeltaEvent_AppendsToThinkingText()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(TestEvents.MessageStart());
        await vm.UpdateFromEventAsync(TestEvents.ThinkingDeltaUpdate("0", "Let me think..."));
        await Assert.That(vm.IsThinking).IsTrue();
        await Assert.That(vm.ThinkingText).IsEqualTo("Let me think...");
    }

    [Test]
    public async Task MessageEndEvent_AddsEntryAndResets()
    {
        var vm = new ChatHistoryViewModel();
        await vm.UpdateFromEventAsync(TestEvents.MessageStart());
        await vm.UpdateFromEventAsync(TestEvents.TextDeltaUpdate("0", "Hello!"));
        await vm.UpdateFromEventAsync(TestEvents.MessageEnd());

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
        await vm.UpdateFromEventAsync(TestEvents.ToolCallStartUpdate("tc1", "read"));

        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Role).IsEqualTo("tool");
        await Assert.That(vm.Entries[0].Content).Contains("read");
    }

    [Test]
    public async Task AgentStartEvent_AddsUserEntries()
    {
        var vm = new ChatHistoryViewModel();
        var userMsg = TestMessages.User("Hello agent", id: "u1", sessionId: "s1", model: "claude");
        await vm.UpdateFromEventAsync(TestEvents.AgentStart(messages: new AgentMessage[] { userMsg }));

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
        await vm.UpdateFromEventAsync(TestEvents.ToolExecutionEnd(result: result));

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
        await vm.UpdateFromEventAsync(TestEvents.ToolExecutionEnd(result: result));
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
