using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;

namespace Harbor.E2E.Framework;

/// <summary>
///     Unit tests for <see cref="StateTestRunner" /> — the renderer-agnostic
///     state-based E2E harness. These tests verify the <see cref="StateTestRunner.ExtractExpectedText" />
///     extraction logic and the state factory methods without requiring a PTY or
///     display server, so they run in any CI environment.
/// </summary>
/// <remarks>
///     The harness was created in the e2e-state-coverage plan (Task 5.1) but had
///     no test coverage. These tests exercise the core extraction logic and
///     factory methods to ensure the state-to-text mapping is correct for all
///     renderer-agnostic <see cref="UiState" /> snapshots.
/// </remarks>
public class StateTestRunnerTests
{
    [Test]
    public async Task ExtractExpectedText_StreamingState_IncludesTextBuffer()
    {
        var state = StateTestRunner.StreamingState("Hello, world!");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Hello, world!");
        await Assert.That(text).Contains("running");
        await Assert.That(text).Contains("test-model");
        await Assert.That(text).Contains("mock");
    }

    [Test]
    public async Task ExtractExpectedText_ThinkingState_IncludesThinkBuffer()
    {
        var state = StateTestRunner.ThinkingState("Let me think about this...");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Let me think about this...");
        await Assert.That(text).Contains("running");
    }

    [Test]
    public async Task ExtractExpectedText_EmptyState_ReturnsOnlyDefaultStatus()
    {
        var state = new UiState();

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).IsEqualTo("idle");
    }

    [Test]
    public async Task ExtractExpectedText_ToolCallState_IncludesToolLine()
    {
        var state = StateTestRunner.ToolCallState("read", "{\"path\":\"/test.txt\"}");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("read: {\"path\":\"/test.txt\"}");
        await Assert.That(text).Contains("running");
    }

    [Test]
    public async Task ExtractExpectedText_ToolResultState_IncludesResultText()
    {
        var state = StateTestRunner.ToolResultState("File contents here");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("File contents here");
        await Assert.That(text).Contains("running");
    }

    [Test]
    public async Task ExtractExpectedText_ErrorState_IncludesErrorMessage()
    {
        var state = StateTestRunner.ErrorState("Something went wrong!");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Something went wrong!");
        await Assert.That(text).Contains("error");
    }

    [Test]
    public async Task ExtractExpectedText_CompactionState_IncludesCompactingStatus()
    {
        var state = StateTestRunner.CompactionState();

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("compacting");
    }

    [Test]
    public async Task ExtractExpectedText_AgentRunningState_IncludesRunningStatus()
    {
        var state = StateTestRunner.AgentRunningState();

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("running");
    }

    [Test]
    public async Task ExtractExpectedText_AgentIdleState_IncludesIdleStatus()
    {
        var state = StateTestRunner.AgentIdleState();

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("idle");
    }

    [Test]
    public async Task ExtractExpectedText_PanelFocusedState_IncludesPanelId()
    {
        var state = StateTestRunner.PanelFocusedState("logs");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("logs");
    }

    [Test]
    public async Task ExtractExpectedText_ScrolledState_IncludesScrollOffset()
    {
        var state = StateTestRunner.ScrolledState(scrollOffset: 5, totalLines: 30, viewportLines: 20);

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("idle");
    }

    [Test]
    public async Task ExtractExpectedText_HistoryNavigatedState_IncludesHistoryText()
    {
        var state = StateTestRunner.HistoryNavigatedState("first prompt", historyIndex: 0);

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("first prompt");
    }

    [Test]
    public async Task ExtractExpectedText_SlashAutocompleteState_IncludesPartialCommand()
    {
        var state = StateTestRunner.SlashAutocompleteState("/hel");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("/hel");
    }

    [Test]
    public async Task ExtractExpectedText_UserMessageState_IncludesMessageText()
    {
        var state = StateTestRunner.UserMessageState("Hello, agent!");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Hello, agent!");
        await Assert.That(text).Contains("idle");
    }

    [Test]
    public async Task ExtractExpectedText_AssistantMessageState_IncludesMessageText()
    {
        var state = StateTestRunner.AssistantMessageState("Hi there!");

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Hi there!");
        await Assert.That(text).Contains("idle");
    }

    [Test]
    public async Task ExtractExpectedText_TranscriptLines_AllRolesIncluded()
    {
        var state = new UiState
        {
            Lines = System.Collections.Immutable.ImmutableArray.Create(
                new ChatLine(ChatRole.User, "User message"),
                new ChatLine(ChatRole.Assistant, "Assistant reply"),
                new ChatLine(ChatRole.Tool, "read: {}"),
                new ChatLine(ChatRole.ToolResult, "Result data"),
                new ChatLine(ChatRole.Error, "Error occurred"),
                new ChatLine(ChatRole.System, "System note"),
                new ChatLine(ChatRole.Thinking, "Thinking block")
            ),
            Status = "idle",
            Model = "test-model",
            Provider = "mock",
            ViewportLines = 20,
            TotalLines = 7
        };

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("User message");
        await Assert.That(text).Contains("Assistant reply");
        await Assert.That(text).Contains("read: {}");
        await Assert.That(text).Contains("Result data");
        await Assert.That(text).Contains("Error occurred");
        await Assert.That(text).Contains("System note");
        await Assert.That(text).Contains("Thinking block");
    }

    [Test]
    public async Task ExtractExpectedText_StreamingAndThinkingTogether_BothIncluded()
    {
        var state = new UiState
        {
            IsStreaming = true,
            Active = new ActiveMessage("Streaming text", "Thinking text"),
            Status = "running",
            Model = "test-model",
            Provider = "mock",
            ViewportLines = 20,
            TotalLines = 1
        };

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).Contains("Streaming text");
        await Assert.That(text).Contains("Thinking text");
    }

    [Test]
    public async Task ExtractExpectedText_EmptyLines_OmitsEmptyText()
    {
        var state = new UiState
        {
            Lines = System.Collections.Immutable.ImmutableArray.Create(
                new ChatLine(ChatRole.User, ""),
                new ChatLine(ChatRole.Assistant, "Real text")
            ),
            Status = "idle"
        };

        string text = StateTestRunner.ExtractExpectedText(state);

        await Assert.That(text).DoesNotContain("\n\n");
        await Assert.That(text).Contains("Real text");
    }

    [Test]
    public async Task ExtractExpectedText_StateOrder_FollowsRendererDisplayOrder()
    {
        var state = new UiState
        {
            Active = new ActiveMessage("stream", "think"),
            Lines = System.Collections.Immutable.ImmutableArray.Create(
                new ChatLine(ChatRole.User, "user line")
            ),
            Status = "running",
            Model = "m",
            Provider = "p",
            AgentName = "agent"
        };

        string text = StateTestRunner.ExtractExpectedText(state);
        string[] parts = text.Split('\n');

        await Assert.That(parts[0]).IsEqualTo("stream");
        await Assert.That(parts[1]).IsEqualTo("think");
        await Assert.That(parts[2]).IsEqualTo("user line");
        await Assert.That(parts[3]).IsEqualTo("running");
        await Assert.That(parts[4]).IsEqualTo("m");
        await Assert.That(parts[5]).IsEqualTo("p");
        await Assert.That(parts[6]).IsEqualTo("agent");
    }

    [Test]
    public async Task StateFactory_StreamingState_SetsCorrectProperties()
    {
        var state = StateTestRunner.StreamingState("hello");

        await Assert.That(state.IsStreaming).IsTrue();
        await Assert.That(state.Active.TextBuffer).IsEqualTo("hello");
        await Assert.That(state.Active.ThinkBuffer).IsEmpty();
        await Assert.That(state.Status).IsEqualTo("running");
        await Assert.That(state.IsAgentRunning).IsTrue();
    }

    [Test]
    public async Task StateFactory_ThinkingState_SetsCorrectProperties()
    {
        var state = StateTestRunner.ThinkingState("thinking...");

        await Assert.That(state.IsStreaming).IsFalse();
        await Assert.That(state.Active.TextBuffer).IsEmpty();
        await Assert.That(state.Active.ThinkBuffer).IsEqualTo("thinking...");
        await Assert.That(state.Status).IsEqualTo("running");
        await Assert.That(state.IsAgentRunning).IsTrue();
    }

    [Test]
    public async Task StateFactory_ErrorState_SetsCorrectProperties()
    {
        var state = StateTestRunner.ErrorState("boom");

        await Assert.That(state.Status).IsEqualTo("error");
        await Assert.That(state.IsAgentRunning).IsFalse();
        await Assert.That(state.Lines.Length).IsEqualTo(1);
        await Assert.That(state.Lines[0].Role).IsEqualTo(ChatRole.Error);
        await Assert.That(state.Lines[0].Text).IsEqualTo("boom");
    }

    [Test]
    public async Task StateFactory_PanelFocusedState_SetsCorrectProperties()
    {
        var state = StateTestRunner.PanelFocusedState("logs");

        await Assert.That(state.Focus).IsEqualTo(FocusMode.Panel);
        await Assert.That(state.FocusedPanelId).IsEqualTo("logs");
        await Assert.That(state.PanelStates).ContainsKey("logs");
        await Assert.That(state.PanelStates["logs"]).IsEqualTo(TuiPanelState.Focused);
        await Assert.That(state.RegisteredPanelIds).Contains("logs");
    }

    [Test]
    public async Task StateFactory_ScrolledState_SetsCorrectProperties()
    {
        var state = StateTestRunner.ScrolledState(scrollOffset: 5, totalLines: 30, viewportLines: 20);

        await Assert.That(state.ScrollOffset).IsEqualTo(5);
        await Assert.That(state.TotalLines).IsEqualTo(30);
        await Assert.That(state.ViewportLines).IsEqualTo(20);
    }

    [Test]
    public async Task StateFactory_HistoryNavigatedState_SetsCorrectProperties()
    {
        var state = StateTestRunner.HistoryNavigatedState("current", historyIndex: 1);

        await Assert.That(state.Input.Text).IsEqualTo("current");
        await Assert.That(state.Input.HistoryIndex).IsEqualTo(1);
        await Assert.That(state.Input.History.Length).IsEqualTo(3);
    }

    [Test]
    public async Task StateFactory_SlashAutocompleteState_SetsCorrectProperties()
    {
        var state = StateTestRunner.SlashAutocompleteState("/help");

        await Assert.That(state.Input.Text).StartsWith("/");
        await Assert.That(state.Input.HistoryIndex).IsEqualTo(-1);
    }
}
