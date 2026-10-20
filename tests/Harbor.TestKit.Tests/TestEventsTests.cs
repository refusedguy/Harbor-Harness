// TestEventsTests.cs — what the shared event builders promise (#1821).
//
// The builders are the ONE place a test builds an event, and 885 call sites
// across 118 files now go through them. Two things therefore have to hold,
// and this file is where they are pinned:
//
//   1. THE DEFAULTS ARE THE VALUES THE DOCS PROMISE. A builder that silently
//      substituted a different session id or a different tool name would
//      change the meaning of every call site that relies on the default.
//
//   2. THE EVENT OWNS ITS DATA. `ToolExecutionStart` takes the tool arguments
//      as a JSON string and clones them, because a `JsonElement` that points
//      into a disposed `JsonDocument` is a use-after-free waiting for a
//      reader. The hand-written call sites spelled
//      `JsonDocument.Parse(...).RootElement.Clone()` for exactly that reason,
//      and the builder has to keep that guarantee.

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The shared event builders: the defaults they promise, and the ownership
///     they promise for the tool arguments.
/// </summary>
public class TestEventsTests
{
    [Test]
    public async Task A_Turn_Starts_At_One_And_Names_No_Session()
    {
        TurnStartEvent evt = TestEvents.TurnStart();

        await Assert.That(evt.TurnIndex).IsEqualTo(1)
            .Because("the turn index is 1-based, and 1 is the value 62 of the 123 hand-written TurnStartEvent call sites already used");
        await Assert.That(evt.SessionId).IsNull()
            .Because("a session id is optional on the event, and the builder does not invent one the caller did not ask for");
    }

    [Test]
    public async Task An_Agent_Starts_On_The_Default_Session_With_No_History()
    {
        AgentStartEvent evt = TestEvents.AgentStart();

        await Assert.That(evt.SessionId).IsEqualTo("s1")
            .Because("the default session id is the one the test tree already repeated in every hand-written call site");
        await Assert.That(evt.Messages).IsEmpty()
            .Because("an agent start with no history is the common case, and an empty list says so without a literal at the call site");
        await Assert.That(evt.Model).IsNull();
        await Assert.That(evt.Kind).IsEqualTo(SessionKind.User);
    }

    [Test]
    public async Task An_Agent_End_Carries_No_Messages_And_No_Limit()
    {
        AgentEndEvent evt = TestEvents.AgentEnd();

        await Assert.That(evt.NewMessages).IsEmpty();
        await Assert.That(evt.Cancelled).IsFalse()
            .Because("a run that ended is not a run that was cancelled, and the two are different facts the event carries separately");
        await Assert.That(evt.Limit).IsNull()
            .Because("a limit belongs to the run that hit one, and the builder does not invent one");
    }

    [Test]
    public async Task A_Message_Starts_And_Ends_On_An_Empty_Assistant()
    {
        MessageStartEvent start = TestEvents.MessageStart();
        MessageEndEvent end = TestEvents.MessageEnd();

        await Assert.That(start.Message.SessionId).IsEqualTo("s1")
            .Because("the default empty assistant belongs to the default session, so a test that does not care about the session does not have to name one");
        await Assert.That(start.Message.Model).IsEqualTo("m");
        await Assert.That(start.Message.Parts).IsEmpty();
        await Assert.That(end.Message.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task A_Tool_Starts_With_The_Default_Call_And_Tool()
    {
        ToolExecutionStartEvent evt = TestEvents.ToolExecutionStart();

        await Assert.That(evt.ToolCallId).IsEqualTo("tc1")
            .Because("the default correlation id is the one the test tree already repeated in every hand-written call site");
        await Assert.That(evt.ToolName).IsEqualTo("read");
        await Assert.That(evt.Args.ValueKind).IsEqualTo(JsonValueKind.Object)
            .Because("the arguments default to an empty object, which is what a tool call with no arguments carries");
    }

    [Test]
    public async Task A_Tool_Start_Owns_Its_Arguments()
    {
        ToolExecutionStartEvent evt = TestEvents.ToolExecutionStart(
            toolCallId: "tc1",
            toolName: "read",
            argsJson: "{\"path\":\"a.cs\"}");

        await Assert.That(evt.Args.GetProperty("path").GetString()).IsEqualTo("a.cs")
            .Because("the builder parses the JSON and clones it, so the event owns its arguments beyond the document's lifetime — the reason the hand-written call sites spelled JsonDocument.Parse(...).RootElement.Clone()");
    }

    [Test]
    public async Task A_Tool_Ends_Successfully_By_Default()
    {
        ToolExecutionEndEvent evt = TestEvents.ToolExecutionEnd();

        await Assert.That(evt.IsError).IsFalse();
        await Assert.That(evt.Result.Output).IsEmpty();
        await Assert.That(evt.Result.IsError).IsFalse();
    }

    [Test]
    public async Task A_Text_Delta_Update_Carries_The_Delta_And_The_Partial()
    {
        MessageUpdateEvent evt = TestEvents.TextDeltaUpdate("t1", "hello");

        await Assert.That(evt.LlmEvent).IsTypeOf<TextDeltaEvent>()
            .Because("the wrapped builder builds the provider-level event and puts it in the update, so the two cannot be swapped by hand");
        var delta = (TextDeltaEvent)evt.LlmEvent;
        await Assert.That(delta.Id).IsEqualTo("t1");
        await Assert.That(delta.Delta).IsEqualTo("hello");
        await Assert.That(evt.Partial.SessionId).IsEqualTo("s1")
            .Because("the partial defaults to the empty assistant, so a test that does not care about the partial does not have to name one");
    }

    [Test]
    public async Task A_Step_Finish_Update_Carries_The_Step()
    {
        MessageUpdateEvent evt = TestEvents.StepFinishUpdate();

        await Assert.That(evt.LlmEvent).IsTypeOf<StepFinishEvent>();
        var step = (StepFinishEvent)evt.LlmEvent;
        await Assert.That(step.Index).IsEqualTo(0);
        await Assert.That(step.FinishReason).IsEqualTo("stop");
    }

    [Test]
    public async Task A_Provider_Error_Carries_Its_Kind_And_Status()
    {
        ErrorEvent evt = TestEvents.LlmError("bad key", kind: ProviderErrorKind.Auth, statusCode: 401);

        await Assert.That(evt.Message).IsEqualTo("bad key");
        await Assert.That(evt.Kind).IsEqualTo(ProviderErrorKind.Auth)
            .Because("the transport classification is what the retry policy reads, and the builder passes it through unchanged");
        await Assert.That(evt.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task A_Compaction_Reports_What_It_Folded()
    {
        CompactionCompletedEvent evt = TestEvents.CompactionCompleted(prunedMessageCount: 5, tokensSaved: 100);

        await Assert.That(evt.SessionId).IsEqualTo("s1");
        await Assert.That(evt.Summary).IsEqualTo("summary");
        await Assert.That(evt.PrunedMessageCount).IsEqualTo(5);
        await Assert.That(evt.TokensSaved).IsEqualTo(100);
        await Assert.That(evt.Duration).IsEqualTo(TimeSpan.FromSeconds(1))
            .Because("the default duration is one second, so a test that does not measure the compaction does not have to name one");
    }
}
