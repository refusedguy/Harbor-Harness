using System.Text.Json;
using Harbor.Abstractions.Interaction;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Tests for <see cref="AskUserQuestionTool" /> (issue #1759): the
///     model-facing question channel. The four cases the channel has to get
///     right are covered one by one: an answer comes back, a dismissal comes
///     back, a timeout comes back, and several questions in a row each get
///     their own answer.
/// </summary>
public class AskUserQuestionToolTests
{
    private static AskUserQuestionTool Tool(IUserQuestionService questions) =>
        new(questions, NullLogger<AskUserQuestionTool>.Instance);

    private static ToolContext Context() => TestToolContext.Create();

    private static JsonElement Args(string json) => TestArgs.FromJson(json);

    private const string OneQuestion = """
                                       {
                                         "questions": [
                                           {
                                             "id": "db",
                                             "question": "Which database should we use?",
                                             "header": "database",
                                             "options": [
                                               { "label": "PostgreSQL", "description": "relational" },
                                               { "label": "SQLite", "description": "embedded" }
                                             ]
                                           }
                                         ]
                                       }
                                       """;

    /// <summary>
    ///     A channel that answers with whatever the test hands it, and records
    ///     what it was asked.
    /// </summary>
    private sealed class ScriptedChannel(UserQuestionOutcome outcome) : IUserQuestionService
    {
        public List<UserQuestionRequest> Requests { get; } = [];

        public Task<UserQuestionOutcome> AskAsync(UserQuestionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    [Test]
    public async Task Name_IsAskUserQuestion()
    {
        var tool = Tool(new ScriptedChannel(UserQuestionOutcome.Answered([])));
        await Assert.That(tool.Name.Value).IsEqualTo("ask_user_question");
        await Assert.That(tool.ExecutionMode == ExecutionMode.Sequential).IsTrue();
        await Assert.That(tool.SafetyProfile.ArgKind == ToolArgKind.Opaque).IsTrue();
    }

    [Test]
    public async Task Answer_Comes_Back_As_The_Tool_Result()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Answered(
        [
            new UserQuestionAnswer("db", ["PostgreSQL"], null),
        ]));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(Args(OneQuestion), Context());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("\"status\":\"ASK_ANSWERED\"");
        await Assert.That(result.Output).Contains("\"id\":\"db\"");
        await Assert.That(result.Output).Contains("PostgreSQL");
    }

    [Test]
    public async Task Free_Text_Answer_Comes_Back_As_The_Custom_Field()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Answered(
        [
            new UserQuestionAnswer("db", [], "whatever the team already runs"),
        ]));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(Args(OneQuestion), Context());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("\"custom\":\"whatever the team already runs\"");
    }

    [Test]
    public async Task Dismissal_Comes_Back_As_The_Aborted_Code()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Aborted("The user skipped the question."));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(Args(OneQuestion), Context());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("ASK_ABORTED");
        await Assert.That(result.Output).Contains("The user skipped the question.");
    }

    [Test]
    public async Task Timeout_Comes_Back_As_The_Pending_Code()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Pending("No answer within 120s."));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(Args(OneQuestion), Context());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("ASK_PENDING");
    }

    [Test]
    public async Task No_Human_To_Ask_Comes_Back_As_The_No_Channel_Code()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Unavailable("No interactive user."));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(Args(OneQuestion), Context());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("ASK_NO_CHANNEL");
    }

    [Test]
    public async Task Several_Questions_In_A_Row_Each_Get_Their_Own_Answer()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Answered(
        [
            new UserQuestionAnswer("first", ["A"], null),
            new UserQuestionAnswer("second", ["B"], null),
        ]));
        var tool = Tool(channel);

        ToolResult first = await tool.ExecuteAsync(
            Args("""
                 { "questions": [ { "id": "first", "question": "First?" } ] }
                 """),
            Context());
        ToolResult second = await tool.ExecuteAsync(
            Args("""
                 { "questions": [ { "id": "second", "question": "Second?" } ] }
                 """),
            Context());

        await Assert.That(first.IsError).IsFalse();
        await Assert.That(first.Output).Contains("\"id\":\"first\"");
        await Assert.That(second.IsError).IsFalse();
        await Assert.That(second.Output).Contains("\"id\":\"second\"");

        // Two calls, two questions asked, in the order they were asked.
        await Assert.That(channel.Requests.Count).IsEqualTo(2);
        await Assert.That(channel.Requests[0].Questions[0].Id).IsEqualTo("first");
        await Assert.That(channel.Requests[1].Questions[0].Id).IsEqualTo("second");
    }

    [Test]
    public async Task One_Call_May_Carry_Several_Questions()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Answered(
        [
            new UserQuestionAnswer("a", ["1"], null),
            new UserQuestionAnswer("b", [], "typed"),
        ]));
        var tool = Tool(channel);

        ToolResult result = await tool.ExecuteAsync(
            Args("""
                 {
                   "questions": [
                     { "id": "a", "question": "A?", "options": [ { "label": "1" } ] },
                     { "id": "b", "question": "B?" }
                   ]
                 }
                 """),
            Context());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(channel.Requests[0].Questions.Count).IsEqualTo(2);
        await Assert.That(result.Output).Contains("\"id\":\"a\"");
        await Assert.That(result.Output).Contains("\"id\":\"b\"");
    }

    [Test]
    public async Task The_Window_The_Model_Asks_For_Reaches_The_Channel()
    {
        var channel = new ScriptedChannel(UserQuestionOutcome.Answered([]));
        var tool = Tool(channel);

        await tool.ExecuteAsync(
            Args("""
                 { "questions": [ { "id": "a", "question": "A?" } ], "timeout_ms": 5000 }
                 """),
            Context());

        TimeSpan? asked = channel.Requests[0].Timeout;
        await Assert.That(asked.HasValue).IsTrue();
        await Assert.That(asked.GetValueOrDefault().TotalMilliseconds).IsEqualTo(5000d);
    }

    [Test]
    public async Task Validate_Rejects_Unusable_Arguments()
    {
        var tool = Tool(new ScriptedChannel(UserQuestionOutcome.Answered([])));

        // No questions at all.
        await Assert.That(tool.ValidateArguments(Args("{}")).IsFailure).IsTrue();
        // An empty batch.
        await Assert.That(tool.ValidateArguments(Args(""" { "questions": [] } """)).IsFailure).IsTrue();
        // A question without an id.
        await Assert.That(tool.ValidateArguments(
            Args(""" { "questions": [ { "question": "A?" } ] } """)).IsFailure).IsTrue();
        // A question without text.
        await Assert.That(tool.ValidateArguments(
            Args(""" { "questions": [ { "id": "a" } ] } """)).IsFailure).IsTrue();
        // Two questions with one id.
        await Assert.That(tool.ValidateArguments(
            Args("""
                 { "questions": [ { "id": "a", "question": "A?" }, { "id": "a", "question": "B?" } ] }
                 """)).IsFailure).IsTrue();
        // A timeout outside the window the tool allows.
        await Assert.That(tool.ValidateArguments(
            Args("""
                 { "questions": [ { "id": "a", "question": "A?" } ], "timeout_ms": 999999999 }
                 """)).IsFailure).IsTrue();
    }

    [Test]
    public async Task Validate_Accepts_A_Well_Formed_Batch()
    {
        var tool = Tool(new ScriptedChannel(UserQuestionOutcome.Answered([])));

        await Assert.That(tool.ValidateArguments(Args(OneQuestion)).IsSuccess).IsTrue();
    }
}
