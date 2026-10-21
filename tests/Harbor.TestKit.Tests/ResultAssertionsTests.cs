// ResultAssertionsTests.cs — the kit's result assertions, pinned (#1569).
//
// WHY THIS FILE EXISTS
// --------------------
// The assertions are SOURCE-GENERATED: `[GenerateAssertion]` on a static
// method becomes a chain link on `Assert.That(...)`, and the generator writes
// the class name, the expectation and the failure message. A generator that
// silently produced a link for the wrong overload would be invisible until a
// test failed in the wrong place — so each family is exercised here, on the
// real values, in both directions.
//
// The failure-direction assertions catch the exception BY HAND rather than
// through `Assert.ThrowsAsync`, because the condition is not "did it throw"
// but "did the message name the reason" — which is the whole point of the
// helper: the old boolean form said "found False" and lost the reason.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Contracts.Events;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Exceptions;

namespace Harbor.TestKit.Tests;

public sealed class ResultAssertionsTests
{
    [Test]
    public async Task A_Successful_Result_Passes_IsSuccessful()
    {
        await Assert.That(Result.Success(42)).IsSuccessful();
        await Assert.That(Result.Success()).IsSuccessful();
        await Assert.That(Result.Success<string, string>("streamed")).IsSuccessful();
    }

    [Test]
    public async Task A_Failed_Result_Passes_IsFailed()
    {
        await Assert.That(Result.Failure<int>("boom")).IsFailed();
        await Assert.That(Result.Failure("boom")).IsFailed();
        await Assert.That(Result.Failure<string, string>("boom")).IsFailed();
    }

    [Test]
    public async Task A_Failed_Result_Says_Why_It_Failed()
    {
        string message = await FailureMessageOf(
            async () => await Assert.That(Result.Failure<int>("the reason")).IsSuccessful());

        await Assert.That(message).Contains("the reason")
            .Because("the old boolean form reported the flag, not the reason");
    }

    [Test]
    public async Task A_Successful_Result_Fails_IsFailed()
    {
        string message = await FailureMessageOf(
            async () => await Assert.That(Result.Success(1)).IsFailed());

        await Assert.That(message).IsNotEmpty()
            .Because("a result that succeeded must not pass the failed-assertion");
    }

    [Test]
    public async Task A_Successful_Result_Passes_Without_Reading_Its_Error()
    {
        // `Result.Error` THROWS on a successful result, so the helper must
        // not read it before it knows the result failed. If it did, this
        // assertion would throw that exception instead of passing.
        await Assert.That(Result.Success(1)).IsSuccessful();
        await Assert.That(Result.Success()).IsSuccessful();
    }

    [Test]
    public async Task A_Tool_Result_Passes_HasSucceeded_And_HasFailed()
    {
        await Assert.That(ToolResult.Success("out")).HasSucceeded();
        await Assert.That(ToolResult.Error("boom")).HasFailed();
    }

    [Test]
    public async Task A_Tool_Result_Entry_Passes_HasSucceeded_And_HasFailed()
    {
        await Assert.That(new ToolResultEntry("c1", "read", "out", false)).HasSucceeded();
        await Assert.That(new ToolResultEntry("c1", "read", "boom", true)).HasFailed();
    }

    [Test]
    public async Task A_Tool_Call_Link_Passes_HasSucceeded_And_HasFailed()
    {
        await Assert.That(new ToolCallLink("c1", "read", false)).HasSucceeded();
        await Assert.That(new ToolCallLink("c1", "read", true)).HasFailed();
    }

    [Test]
    public async Task A_Tool_Execution_End_Event_Passes_HasSucceeded_And_HasFailed()
    {
        await Assert.That(new ToolExecutionEndEvent("c1", ToolResult.Success("out"), false)).HasSucceeded();
        await Assert.That(new ToolExecutionEndEvent("c1", ToolResult.Error("boom"), true)).HasFailed();
    }

    /// <summary>
    ///     Runs the assertion and returns the message of the failure it
    ///     produced, or an empty string when it did not fail.
    /// </summary>
    private static async Task<string> FailureMessageOf(Func<Task> action)
    {
        try
        {
            await action();
            return string.Empty;
        }
        catch (AssertionException ex)
        {
            return ex.Message;
        }
    }
}
