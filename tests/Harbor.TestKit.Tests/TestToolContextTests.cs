// TestToolContextTests.cs — the ToolContext factory is pinned, so it cannot
// drift (#1775).
//
// WHY THIS FILE EXISTS
// --------------------
// `TestToolContext.Create()` is the ONE place a test builds a `ToolContext`.
// Before it, the 8-argument literal was copied into every test file that
// exercised a tool — 24 copies in 19 files, each one a hand-maintained
// contract that drifted from the record silently.
//
// A factory has the same failure mode if nobody pins it: change the record's
// signature, or one default, and every call site changes meaning at once.
// So this file holds the factory against the literal it replaced:
//
//   1. THE FACTORY BUILDS THE CONTEXT THE HAND-WRITTEN CALL BUILT. The seven
//      value members are compared one by one, and the two callbacks are
//      compared by WHAT THEY ANSWER — a delegate is not comparable by value,
//      and a record compares a delegate member by reference, so a whole-record
//      comparison would say nothing about them.
//   2. THE DEFAULTS ARE THE VALUES THE DOCS PROMISE.
//   3. EVERY PARAMETER REACHES THE RECORD. A parameter that is accepted and
//      dropped is the quietest bug a factory can have.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The <see cref="ToolContext" /> factory: the context it builds, and the
///     defaults it promises.
/// </summary>
public class TestToolContextTests
{
    [Test]
    public async Task A_Context_Is_The_One_The_Hand_Written_Call_Built()
    {
        ToolContext built = TestToolContext.Create();
        ToolContext handwritten = new(
            "test-session",
            "test-message",
            "test-call",
            "code",
            CancellationToken.None,
            Array.Empty<AgentMessage>(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));

        await Assert.That(built.SessionId).IsEqualTo(handwritten.SessionId)
            .Because("the factory is a factory for the very record the product hands a tool, and a difference in any member is a difference in what the test means");
        await Assert.That(built.MessageId).IsEqualTo(handwritten.MessageId);
        await Assert.That(built.CallId).IsEqualTo(handwritten.CallId);
        await Assert.That(built.Agent).IsEqualTo(handwritten.Agent);
        await Assert.That(built.Abort).IsEqualTo(handwritten.Abort)
            .Because("the abort token is the one the tool reads to stop, and a factory that swapped it for a fresh one would defeat every cancellation test");
        await Assert.That(built.Messages.Count).IsEqualTo(handwritten.Messages.Count)
            .Because("a record compares its list member by reference, so the two empty lists are not equal to each other and the count is what can be compared");
        await Assert.That(built.WorkingDirectory).IsNull()
            .Because("the hand-written copy left the ninth member out, and the factory must not fill it in");

        // The two callbacks are DELEGATES. A whole-record comparison cannot
        // see them, so what is compared is what they answer.
        PermissionRequest request = PermissionRequest.Create("read", "file", default, Array.Empty<string>());

        PermissionResponse builtAnswer = await built.Ask(request, CancellationToken.None);
        PermissionResponse handwrittenAnswer = await handwritten.Ask(request, CancellationToken.None);
        await Assert.That(builtAnswer).IsEqualTo(handwrittenAnswer)
            .Because("the permission answer is the one the tool acts on, and the canonical copy allowed the call without persisting the decision");
        await Assert.That(builtAnswer.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(builtAnswer.PersistDecision).IsFalse();

        // The progress callback drops every update. The observable behaviour is
        // that it completes and reports nothing, and that is what is compared.
        await built.ReportProgress(new ToolProgressUpdate("half"), CancellationToken.None);
        await handwritten.ReportProgress(new ToolProgressUpdate("half"), CancellationToken.None);
    }

    [Test]
    public async Task A_Context_Takes_The_Defaults_The_Docs_Promise()
    {
        ToolContext context = TestToolContext.Create();

        await Assert.That(context.SessionId).IsEqualTo("test-session")
            .Because("the default session id is the one the hand-written copies used, so the call sites written before the migration still mean what they meant");
        await Assert.That(context.MessageId).IsEqualTo("test-message");
        await Assert.That(context.CallId).IsEqualTo("test-call");
        await Assert.That(context.Agent).IsEqualTo("code");
        await Assert.That(context.Abort.IsCancellationRequested).IsFalse();
        await Assert.That(context.Messages.Count).IsEqualTo(0)
            .Because("a tool that is handed a conversation snapshot is handed none by default, exactly as the hand-written copies did");
        await Assert.That(context.WorkingDirectory).IsNull()
            .Because("a null working directory means the process directory, which is what the hand-written copies meant by leaving the ninth member out");
    }

    [Test]
    public async Task Every_Parameter_Reaches_The_Record()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        AgentMessage message = TestMessages.User("hi");
        ToolProgressUpdate? reported = null;

        ToolContext context = TestToolContext.Create(
            sessionId: "s1",
            messageId: "m1",
            callId: "c1",
            agent: "plan",
            abort: cts.Token,
            messages: [message],
            reportProgress: (update, _) =>
            {
                reported = update;
                return Task.CompletedTask;
            },
            ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Deny, false)),
            workingDirectory: "/tmp/work");

        await Assert.That(context.SessionId).IsEqualTo("s1");
        await Assert.That(context.MessageId).IsEqualTo("m1");
        await Assert.That(context.CallId).IsEqualTo("c1");
        await Assert.That(context.Agent).IsEqualTo("plan");
        await Assert.That(context.Abort).IsEqualTo(cts.Token)
            .Because("the exact token has to reach the tool, not merely a cancelled one, or a test could not tell two cancellations apart");
        await Assert.That(context.Messages.Count).IsEqualTo(1);
        await Assert.That(context.Messages[0]).IsEqualTo(message);
        await Assert.That(context.WorkingDirectory).IsEqualTo("/tmp/work");

        await context.ReportProgress(new ToolProgressUpdate("half"), CancellationToken.None);
        await Assert.That(reported).IsNotNull();
        await Assert.That(reported!.Status).IsEqualTo("half")
            .Because("a progress callback that is accepted and never called would make a tool that reports progress look like one that does not");

        PermissionResponse answer = await context.Ask(
            PermissionRequest.Create("read", "file", default, Array.Empty<string>()),
            CancellationToken.None);
        await Assert.That(answer.Action).IsEqualTo(PermissionAction.Deny)
            .Because("a permission callback that is accepted and ignored would make every tool look permitted");
        await Assert.That(answer.PersistDecision).IsFalse();
    }

    [Test]
    public async Task A_Call_Id_May_Be_Null()
    {
        ToolContext context = TestToolContext.Create(callId: null);

        await Assert.That(context.CallId).IsNull()
            .Because("a call that has no id yet is a real case, and the record allows it");
    }

    [Test]
    public async Task The_Two_Permission_Answers_Name_Themselves()
    {
        PermissionResponse allow = TestToolContext.AllowWithoutPersist();
        PermissionResponse deny = TestToolContext.DenyWithoutPersist();

        await Assert.That(allow.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(allow.PersistDecision).IsFalse();
        await Assert.That(deny.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(deny.PersistDecision).IsFalse();
    }
}
