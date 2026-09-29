using System.Collections.Immutable;
using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #680: the diff payload of a tool call must REACH the UI, and the UI
///     must not rebuild a call by parsing its own rendered line.
/// </summary>
/// <remarks>
///     <para>
///         The defect the issue names is a dead branch. <c>ChatViewModelBase</c>
///         rebuilt every tool card by parsing the transcript string
///         <c>"→ edit {…}"</c> back into a structured call, and its parser never
///         set <c>IsDiffTool</c> — so the <c>if (parsed.IsDiffTool)</c> arm that
///         should have carried the path, preview and full diff into the card could
///         never run. The structure was in the event; the state did not carry it; so
///         the UI lost it and tried to reconstruct it from its own output.
///     </para>
///     <para>
///         These tests drive the whole path the way the app does — event through
///         reducer through store through view-model — rather than hand-building a
///         view-model, so a break anywhere in the chain is a failure here.
///     </para>
/// </remarks>
public class ToolCallStructureReachesUiTests
{
    private const string EditCallId = "tc_edit";

    private const string EditArgs =
        """{"file_path":"src/app.cs","oldString":"var a = 1;","newString":"var a = 2;"}""";

    /// <summary>
    ///     A <c>ToolExecutionStartEvent</c> carrying the arguments and the tool's
    ///     own glyph, exactly as <c>ToolDispatcher</c> publishes it.
    /// </summary>
    private static ToolExecutionStartEvent EditStarted() =>
        ToolExecutionStartEvent.Create(
            EditCallId,
            "edit",
            JsonDocument.Parse(EditArgs).RootElement.Clone(),
            "✎");

    /// <summary>Drives a state through the real reducer for the given agent events.</summary>
    private static UiState Reduce(params AgentEvent[] events)
    {
        UiState state = new();
        foreach (AgentEvent evt in events)
        {
            state = ChatAppReducer.Update(state, new ChatAppMsg.Agent(evt)).State;
        }

        return state;
    }

    /// <summary>Runs a state through the desktop chat view-model's projection.</summary>
    private static TestChatViewModel Project(UiState state)
    {
        var vm = new TestChatViewModel(new TestDispatcherAdapter(), NullLogger.Instance);
        vm.TriggerStoreChanged(state);
        return vm;
    }

    // =====================================================================
    // The payload arrives.
    // =====================================================================

    /// <summary>
    ///     THE TEST THE ISSUE ASKS FOR. An <c>edit</c> call's diff path, preview
    ///     and full diff survive the whole chain and land on the tool card. Before
    ///     the fix this is where the branch died: the card's <c>IsDiffTool</c> was
    ///     always false and all three diff fields were null.
    /// </summary>
    [Test]
    public async Task DiffPayload_ReachesTheToolCard()
    {
        TestChatViewModel vm = Project(Reduce(EditStarted()));

        await Assert.That(vm.ToolCalls.Count).IsEqualTo(1);

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = vm.ToolCalls[0];

        await Assert.That(card.Id).IsEqualTo(EditCallId);
        await Assert.That(card.ToolName).IsEqualTo("edit");
        await Assert.That(card.IsDiffTool).IsTrue()
            .Because("the reducer publishes the diff payload and the card reads it. This flag was "
                   + "dead before #680: the parser in ChatViewModelBase never set it, so the "
                   + "branch carrying the diff into the UI never ran.");

        await Assert.That(card.DiffFilePath).IsEqualTo("src/app.cs")
            .Because("the file path is read from the published structure, not recovered by parsing "
                   + "the rendered transcript line");

        await Assert.That(card.DiffPreview).IsNotNull()
            .Because("the inline preview is part of the payload that used to be lost");

        await Assert.That(card.DiffFull).IsNotNull()
            .Because("the full diff behind the expand path is part of the same payload");

        // The preview must actually be the diff, not merely non-null: an empty
        // string would satisfy a null-check while showing the user nothing.
        await Assert.That(card.DiffPreview!).Contains("- var a = 1;");
        await Assert.That(card.DiffPreview!).Contains("+ var a = 2;");
    }

    /// <summary>
    ///     The result half of the payload reaches the card too — including the
    ///     failure status, which used to be recovered by testing the transcript
    ///     line's leading "✗" and then stripping it back off again.
    /// </summary>
    [Test]
    public async Task ResultPayload_ReachesTheToolCard()
    {
        UiState state = Reduce(
            EditStarted(),
            new ToolExecutionEndEvent(EditCallId, ToolResult.Success("edited 1 line"), IsError: false));

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = Project(state).ToolCalls[0];

        await Assert.That(card.Status).IsEqualTo(ToolCallState.Success);
        await Assert.That(card.ResultPreview).IsEqualTo("edited 1 line")
            .Because("the preview is the result text as published, without the status glyph the "
                   + "transcript line carries");

        await Assert.That(card.ResultPreview.StartsWith("✓", StringComparison.Ordinal)).IsFalse()
            .Because("the glyph belongs to the rendered line, not to the structured result");
    }

    [Test]
    public async Task FailedCall_ReachesTheToolCardAsError()
    {
        UiState state = Reduce(
            EditStarted(),
            new ToolExecutionEndEvent(EditCallId, ToolResult.Error("no such file"), IsError: true));

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = Project(state).ToolCalls[0];

        await Assert.That(card.Status).IsEqualTo(ToolCallState.Error);
        await Assert.That(card.ResultPreview).IsEqualTo("no such file");
    }

    // =====================================================================
    // The UI reads; it does not reconstruct.
    // =====================================================================

    /// <summary>
    ///     The transcript still carries the DISPLAY string — that is what the chat
    ///     list draws, and it is unchanged. What changed is that it is no longer
    ///     also the only copy: the structure rides beside it.
    /// </summary>
    [Test]
    public async Task DisplayLine_IsStillPublished_AlongsideTheStructure()
    {
        UiState state = Reduce(EditStarted());

        await Assert.That(state.Chat.Lines.Length).IsEqualTo(1);
        await Assert.That(state.Chat.Lines[0].Role).IsEqualTo(ChatRole.Tool);
        await Assert.That(state.Chat.Lines[0].ToolCallId).IsEqualTo(EditCallId);

        // The rendering of a tool start is unchanged by this issue, and the
        // history/grep surfaces depend on it. Pinning it says so out loud rather
        // than leaving a reviewer to wonder.
        await Assert.That(state.Chat.Lines[0].Text).IsEqualTo($"→ edit  {EditArgs}");
    }

    /// <summary>
    ///     A tool card is NOT fabricated from a display line alone. This is the
    ///     property the old parser broke in the other direction: it produced a card
    ///     whose structure it had guessed out of the text, so a card could exist
    ///     with fields no producer ever set.
    /// </summary>
    [Test]
    public async Task DisplayLineAlone_DoesNotProduceACard()
    {
        // A transcript line with the tool-call id, but no structured call behind
        // it — the shape a hand-built or legacy-replayed state has.
        UiState state = new()
        {
            Chat = ChatDomainState.Empty with
            {
                Lines = ImmutableArray.Create(
                    new ChatLine(ChatRole.Tool, $"→ edit  {EditArgs}", EditCallId)),
            },
        };

        await Assert.That(Project(state).ToolCalls).IsEmpty()
            .Because("a tool card is drawn from ChatDomainState.ToolCalls. Recovering one by parsing "
                   + "the transcript is what lost the diff payload in the first place.");
    }

    // =====================================================================
    // Lifecycle coherence.
    // =====================================================================

    /// <summary>
    ///     Start and result for one call stay ONE card, keyed by the tool-call id
    ///     — a pre-execution placeholder and the real start share an id.
    /// </summary>
    [Test]
    public async Task StartAndResult_AreOneCard()
    {
        UiState state = Reduce(
            new ToolExecutionStartEvent(EditCallId, "edit", JsonDocument.Parse(EditArgs).RootElement.Clone()),
            EditStarted(),
            new ToolExecutionEndEvent(EditCallId, ToolResult.Success("ok"), IsError: false));

        await Assert.That(state.Chat.ToolCalls.Length).IsEqualTo(1)
            .Because("a second start for the same call id updates the entry rather than appending "
                   + "another, so the transcript's two tool lines still yield one card");

        await Assert.That(Project(state).ToolCalls.Count).IsEqualTo(1);
    }

    /// <summary>An end event with no start adds nothing rather than an empty card.</summary>
    [Test]
    public async Task OrphanResult_DoesNotProduceACard()
    {
        UiState state = Reduce(
            new ToolExecutionEndEvent("tc_never_started", ToolResult.Success("ok"), IsError: false));

        await Assert.That(state.Chat.ToolCalls).IsEmpty()
            .Because("an end event carries no name, no glyph and no arguments; a card built from it "
                   + "would show the user an empty one");
    }

    /// <summary>
    ///     Cards keep their position and identity across reconciles, so a card the
    ///     user expanded stays expanded while other state churns underneath.
    /// </summary>
    [Test]
    public async Task Reconcile_ReusesCardInstances()
    {
        var vm = new TestChatViewModel(new TestDispatcherAdapter(), NullLogger.Instance);

        vm.TriggerStoreChanged(Reduce(EditStarted()));
        Harbor.Ui.Framework.ViewModels.ToolCallViewModel first = vm.ToolCalls[0];
        first.IsExpanded = true;

        // A later transition that does not touch the tool calls.
        vm.TriggerStoreChanged(Reduce(
            EditStarted(),
            new AgentStartEvent("s1", [])));

        await Assert.That(vm.ToolCalls.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(vm.ToolCalls[0], first)).IsTrue()
            .Because("cards are matched by tool-call id and updated in place; rebuilding them per "
                   + "transition would drop the user's expand state on every streamed token");

        await Assert.That(vm.ToolCalls[0].IsExpanded).IsTrue();
    }

    /// <summary>
    ///     The card appears the moment the model NAMES the tool, before it
    ///     executes — a name-only placeholder, with no glyph and no diff payload
    ///     because none is known yet.
    /// </summary>
    [Test]
    public async Task NamedButNotExecuting_ShowsAPlaceholderCard()
    {
        UiState state = Reduce(
            new MessageUpdateEvent(
                new ToolCallStartEvent("tc_named", "edit"),
                AssistantMessage.Empty("s1", "test-model")));

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = Project(state).ToolCalls[0];

        await Assert.That(card.ToolName).IsEqualTo("edit")
            .Because("the model has named the tool, so the user should see the call begin — this is "
                   + "what the old parser did by scraping the display line, and what the reducer now "
                   + "publishes outright");

        await Assert.That(card.Status).IsEqualTo(ToolCallState.Pending)
            .Because("nothing is executing yet; Pending is the documented member for exactly this "
                   + "(no start event seen)");

        await Assert.That(card.IsDiffTool).IsFalse()
            .Because("no arguments have arrived, so there is nothing to diff");
    }

    /// <summary>
    ///     The placeholder is UPGRADED, not duplicated: the execution event brings
    ///     the arguments, the tool's glyph and the diff payload.
    /// </summary>
    [Test]
    public async Task ExecutionUpgradesTheNamedPlaceholder()
    {
        UiState state = Reduce(
            new MessageUpdateEvent(
                new ToolCallStartEvent(EditCallId, "edit"),
                AssistantMessage.Empty("s1", "test-model")),
            EditStarted());

        await Assert.That(state.Chat.ToolCalls.Length).IsEqualTo(1)
            .Because("both events share one tool-call id, so the call is one entry upgraded in place");

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = Project(state).ToolCalls[0];

        await Assert.That(card.Status).IsEqualTo(ToolCallState.Running);
        await Assert.That(card.IconText).IsEqualTo("✎")
            .Because("the placeholder's generic wrench is replaced by the tool's own declared glyph "
                   + "once the dispatcher resolves the tool");

        await Assert.That(card.IsDiffTool).IsTrue();
        await Assert.That(card.DiffFilePath).IsEqualTo("src/app.cs");
    }

    /// <summary>A non-diff tool publishes no diff payload at all.</summary>
    [Test]
    public async Task NonDiffTool_CarriesNoDiffPayload()
    {
        ToolExecutionStartEvent started = ToolExecutionStartEvent.Create(
            "tc_bash",
            "bash",
            JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone(),
            "$");

        Harbor.Ui.Framework.ViewModels.ToolCallViewModel card = Project(Reduce(started)).ToolCalls[0];

        await Assert.That(card.IsDiffTool).IsFalse();
        await Assert.That(card.DiffFilePath).IsNull();
        await Assert.That(card.DiffPreview).IsNull();
        await Assert.That(card.DiffFull).IsNull();
        await Assert.That(card.ArgsPreview).IsEqualTo("""{"command":"ls"}""");
    }
}
