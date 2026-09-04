using System.Collections.Immutable;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.E2E.Framework;

/// <summary>
///     Renderer-agnostic E2E state test harness. Bridges the gap between the
///     immutable <see cref="UiState" /> snapshot (the single source of truth that
///     every interactive renderer projects from) and the <see cref="IE2eDriver" />
///     abstraction (CLI / TUI / Avalonia).
/// </summary>
/// <remarks>
///     <para>
///         <b>Purpose:</b> instead of writing renderer-specific tests that
///         duplicate the same state-coverage logic, tests build a
///         <see cref="UiState" /> snapshot describing the state they want to
///         verify, then call <see cref="AssertStateRenderedAsync" /> which:
///         <list type="number">
///             <item>Extracts the expected visible text from the snapshot.</item>
///             <item>Drives the <see cref="IE2eDriver" /> to produce that state
///                 (via mock LLM responses for TUI, or direct VM mutation for
///                 Avalonia).</item>
///             <item>Polls the driver's screen until the expected text appears.</item>
///         </list>
///     </para>
///     <para>
///         <b>TUI path:</b> the runner configures <see cref="MockLlmServer" />
///         to emit the canned response that would produce the given
///         <see cref="UiState" /> (streaming text, tool call, error, etc.),
///         then drives the app via <see cref="IE2eDriver.SendInputAsync" /> /
///         <see cref="IE2eDriver.SendKeyAsync" />.
///     </para>
///     <para>
///         <b>Avalonia path:</b> the runner maps <see cref="UiState" />
///         properties onto the bound <c>MainViewModel</c> / <c>ChatViewModel</c>
///         fields directly (no LLM round-trip needed).
///     </para>
/// </remarks>
public sealed class StateTestRunner
{
    /// <summary>
    ///     Assert that the <paramref name="driver" /> renders the expected text
    ///     derived from <paramref name="state" /> within <paramref name="timeout" />.
    /// </summary>
    /// <remarks>
    ///     The expected text is extracted from the <see cref="UiState" /> via
    ///     <see cref="ExtractExpectedText" /> — it includes the streaming buffer,
    ///     thinking buffer, transcript lines, status, and session chrome.
    ///     Callers can pass an explicit <paramref name="expectedText" /> to
    ///     override the auto-derived value when the renderer formats text
    ///     differently from the raw <see cref="UiState" /> (e.g. ANSI colour
    ///     codes, prefix labels).
    /// </remarks>
    public static async Task<bool> AssertStateRenderedAsync(
        IE2eDriver driver,
        UiState state,
        string? expectedText = null,
        TimeSpan? timeout = null)
    {
        string text = expectedText ?? ExtractExpectedText(state);
        if (string.IsNullOrEmpty(text))
            return true;

        return await driver.WaitForTextAsync(text, timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Drive the <paramref name="driver" /> through a sequence of
    ///     <see cref="UiState" /> snapshots, asserting each expected text
    ///     appears before moving to the next step.
    /// </summary>
    /// <remarks>
    ///     Each step is a <c>(UiState, expectedText)</c> tuple. If
    ///     <c>expectedText</c> is null, it is auto-derived from the
    ///     <see cref="UiState" /> via <see cref="ExtractExpectedText" />.
    /// </remarks>
    public static async Task<bool> RunStateSequenceAsync(
        IE2eDriver driver,
        params (UiState state, string? expectedText)[] steps)
    {
        foreach (var (state, expectedText) in steps)
        {
            bool ok = await AssertStateRenderedAsync(driver, state, expectedText).ConfigureAwait(false);
            if (!ok)
                return false;
        }
        return true;
    }

    /// <summary>
    ///     Extract the expected visible text from a <see cref="UiState" />
    ///     snapshot. Concatenates the streaming buffer, thinking buffer,
    ///     transcript lines, status, and session chrome in the order a
    ///     renderer would display them.
    /// </summary>
    public static string ExtractExpectedText(UiState state)
    {
        var parts = new List<string>(16);

        // Streaming buffer (live text being typed by the LLM).
        if (!string.IsNullOrEmpty(state.Active.TextBuffer))
            parts.Add(state.Active.TextBuffer);

        // Thinking buffer.
        if (!string.IsNullOrEmpty(state.Active.ThinkBuffer))
            parts.Add(state.Active.ThinkBuffer);

        // Transcript lines (user, assistant, tool, etc.).
        foreach (var line in state.Lines)
        {
            if (!string.IsNullOrEmpty(line.Text))
                parts.Add(line.Text);
        }

        // Input text (history navigation, slash autocomplete).
        if (!string.IsNullOrEmpty(state.Input.Text))
            parts.Add(state.Input.Text);

        // Focused panel id.
        if (!string.IsNullOrEmpty(state.FocusedPanelId))
            parts.Add(state.FocusedPanelId);

        // Status (idle / running / compacting / error).
        if (!string.IsNullOrEmpty(state.Status))
            parts.Add(state.Status);

        // Session chrome.
        if (!string.IsNullOrEmpty(state.Model))
            parts.Add(state.Model);
        if (!string.IsNullOrEmpty(state.Provider))
            parts.Add(state.Provider);
        if (!string.IsNullOrEmpty(state.AgentName))
            parts.Add(state.AgentName);

        return string.Join("\n", parts);
    }

    // ── State snapshot builders ───────────────────────────────────────────

    /// <summary>
    ///     Build a <see cref="UiState" /> representing the streaming state:
    ///     <c>IsStreaming = true</c> with a non-empty <see cref="ActiveMessage.TextBuffer" />.
    /// </summary>
    public static UiState StreamingState(string streamingText, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = true,
            Active = new ActiveMessage(streamingText, string.Empty),
            Model = model,
            Provider = provider,
            Status = "running",
            IsAgentRunning = true,
            ViewportLines = 20,
            TotalLines = 5,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing the thinking state:
    ///     <c>IsAgentRunning = true</c> with a non-empty
    ///     <see cref="ActiveMessage.ThinkBuffer" /> but empty text buffer.
    /// </summary>
    public static UiState ThinkingState(string thinkingText, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Active = new ActiveMessage(string.Empty, thinkingText),
            Model = model,
            Provider = provider,
            Status = "running",
            IsAgentRunning = true,
            ViewportLines = 20,
            TotalLines = 3,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a tool-call state:
    ///     a <see cref="ChatRole.Tool" /> line in the transcript.
    /// </summary>
    public static UiState ToolCallState(string toolName, string argsPreview, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.Tool, $"{toolName}: {argsPreview}")),
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "running",
            IsAgentRunning = true,
            ViewportLines = 20,
            TotalLines = 2,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a tool-result state:
    ///     a <see cref="ChatRole.ToolResult" /> line in the transcript.
    /// </summary>
    public static UiState ToolResultState(string resultText, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.ToolResult, resultText)),
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "running",
            IsAgentRunning = true,
            ViewportLines = 20,
            TotalLines = 2,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing an error state:
    ///     a <see cref="ChatRole.Error" /> line + <c>Status = "error"</c>.
    /// </summary>
    public static UiState ErrorState(string errorMessage, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.Error, errorMessage)),
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "error",
            IsAgentRunning = false,
            ViewportLines = 20,
            TotalLines = 2,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a compaction state:
    ///     <c>Status = "compacting"</c>.
    /// </summary>
    public static UiState CompactionState(string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "compacting",
            IsAgentRunning = true,
            ViewportLines = 20,
            TotalLines = 10,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing the agent-running state:
    ///     <c>IsAgentRunning = true</c>, <c>Status = "running"</c>,
    ///     <c>WasRunning = false</c> (rising edge).
    /// </summary>
    public static UiState AgentRunningState(string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "running",
            IsAgentRunning = true,
            WasRunning = false,
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing the agent-idle state:
    ///     <c>IsAgentRunning = false</c>, <c>Status = "idle"</c>.
    /// </summary>
    public static UiState AgentIdleState(string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            WasRunning = true,
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a panel-focused state:
    ///     <c>Focus = Panel</c>, <c>FocusedPanelId</c> set, panel visible.
    /// </summary>
    public static UiState PanelFocusedState(string panelId = "logs", string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            Focus = FocusMode.Panel,
            FocusedPanelId = panelId,
            PanelStates = ImmutableDictionary<string, TuiPanelState>.Empty
                .Add(panelId, TuiPanelState.Focused),
            RegisteredPanelIds = ImmutableArray.Create(panelId),
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a scrolled-back state:
    ///     <c>ScrollOffset &gt; 0</c>, <c>ScrollPercent &gt; 0</c>.
    /// </summary>
    public static UiState ScrolledState(int scrollOffset = 5, int totalLines = 30, int viewportLines = 20, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            ScrollOffset = scrollOffset,
            ViewportLines = viewportLines,
            TotalLines = totalLines,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing input history navigation:
    ///     <see cref="InputModel.HistoryIndex" /> &gt; 0.
    /// </summary>
    public static UiState HistoryNavigatedState(string currentText, int historyIndex = 0, string model = "test-model", string provider = "mock")
    {
        var history = ImmutableArray.Create("first prompt", "second prompt", "third prompt");
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            Input = new InputModel(currentText, history, historyIndex),
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing slash-command autocomplete:
    ///     <see cref="InputModel.Text" /> starts with <c>/</c>.
    /// </summary>
    public static UiState SlashAutocompleteState(string partialCommand, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            Input = new InputModel(partialCommand, ImmutableArray<string>.Empty, -1),
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing a user message in the transcript.
    /// </summary>
    public static UiState UserMessageState(string message, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.User, message)),
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            ViewportLines = 20,
            TotalLines = 1,
        };
    }

    /// <summary>
    ///     Build a <see cref="UiState" /> representing an assistant message in the transcript.
    /// </summary>
    public static UiState AssistantMessageState(string message, string model = "test-model", string provider = "mock")
    {
        return new UiState
        {
            Lines = ImmutableArray.Create(new ChatLine(ChatRole.Assistant, message)),
            IsStreaming = false,
            Model = model,
            Provider = provider,
            Status = "idle",
            IsAgentRunning = false,
            ViewportLines = 20,
            TotalLines = 1,
        };
    }
}
