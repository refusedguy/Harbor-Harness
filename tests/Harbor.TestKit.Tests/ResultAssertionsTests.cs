// ResultAssertionsTests.cs — what the shared result assertions promise.
//
// WHY THESE EXIST
// ----------------
// The helper family is used by 400+ call sites after #1569, and every one of
// them is a test that stops asserting anything if the helper stops deciding.
// A helper that always passes is worse than no helper: the tree would keep
// its green and lose its teeth.
//
// So the properties below are pinned:
//
//   1. a SUCCESSFUL result passes, and a FAILED one fails;
//   2. the failure message carries the ERROR TEXT — the whole reason the
//      helper replaced `Assert.That(result.IsSuccess).IsTrue()`, which reports
//      a bool and loses the reason;
//   3. the chain (`Assert.That(result).IsSuccessful()`) fails for a failed
//      result, and its message carries the error text TOO — the plain method
//      and the chain are two different code paths, and only the chain is
//      what the tree calls;
//   4. the non-generic `Result` is a DIFFERENT TYPE from `Result<T>` and
//      needs its own pair, and the three `IsError` carriers (ToolResult,
//      ToolResultEntry, ToolCallLink) each need theirs;
//   5. the two families that live in `ResultAssertions.More.cs` — the
//      value-shaped `Result<TValue, TError>` and the `ToolExecutionEndEvent`
//      — get their own pairs, and the chain reaches them as well.
//
// The error text is read on the FAILURE path only, and that is not an
// accident: `Result.Error` THROWS on a successful result
// (`ErrorIsInaccessibleForSuccess` in CSharpFunctionalExtensions 3.7.0), so a
// helper that read it unconditionally would turn every passing test into an
// exception. The passing cases below are the ones that would catch that.
//
// The helper is a PARTIAL class in two files: the five families every
// result-shaped value in the tree uses live in
// `tests/Harbor.TestKit.Support/ResultAssertions.cs`, and the two that only a
// few sites reach live in `ResultAssertions.More.cs`. One `using
// Harbor.TestKit;` reaches all seven, and the chain links are generated from
// the same attribute — so the chain is exercised for all seven here.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using TUnit.Assertions;
using TUnit.Assertions.Core;

namespace Harbor.TestKit.Tests;

public sealed class ResultAssertionsTests
{
    [Test]
    public async Task A_Successful_Result_Passes()
    {
        Result<string> ok = Result.Success("value");

        await Assert.That(ok).IsSuccessful();
    }

    [Test]
    public async Task A_Failed_Result_Fails()
    {
        Result<string> failed = Result.Failure<string>("Session 's1' not found.");

        await Assert.That(ResultAssertions.IsSuccessful(failed).IsPassed).IsFalse();
        await Assert.That(ResultAssertions.IsFailed(failed).IsPassed).IsTrue();
    }

    [Test]
    public async Task The_Failure_Message_Carries_The_Error_Text()
    {
        Result<string> failed = Result.Failure<string>("Session 's1' not found.");

        AssertionResult outcome = ResultAssertions.IsSuccessful(failed);

        await Assert.That(outcome.IsPassed).IsFalse();
        await Assert.That(outcome.Message ?? string.Empty).Contains("Session 's1' not found.");
    }

    [Test]
    public async Task The_Chain_Fails_And_Names_The_Error()
    {
        Result<string> failed = Result.Failure<string>("Session 's1' not found.");

        string? message = null;
        try
        {
            await Assert.That(failed).IsSuccessful();
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        await Assert.That(message ?? string.Empty).Contains("Session 's1' not found.")
            .Because("the chain is what 400+ call sites use, and the error text in its failure "
                + "message is the reason the helper replaced the bool form. A chain that failed "
                + "without the text would be no better than `Assert.That(result.IsSuccess)`.");
    }

    [Test]
    public async Task The_Non_Generic_Result_Has_Its_Own_Pair()
    {
        Result failed = Result.Failure("Session 's1' not found.");

        await Assert.That(ResultAssertions.IsSuccessful(failed).IsPassed).IsFalse();
        await Assert.That(ResultAssertions.IsFailed(failed).IsPassed).IsTrue();
        await Assert.That(ResultAssertions.IsSuccessful(Result.Success()).IsPassed).IsTrue();
    }

    [Test]
    public async Task A_Tool_Result_Carries_Its_Error_Verdict()
    {
        ToolResult ok = ToolResult.Success("done");
        ToolResult failed = ToolResult.Error("boom");

        await Assert.That(ResultAssertions.HasSucceeded(ok).IsPassed).IsTrue();
        await Assert.That(ResultAssertions.HasFailed(ok).IsPassed).IsFalse();
        await Assert.That(ResultAssertions.HasSucceeded(failed).IsPassed).IsFalse();
        await Assert.That(ResultAssertions.HasFailed(failed).IsPassed).IsTrue();
    }

    [Test]
    public async Task A_Tool_Result_Entry_Carries_Its_Error_Verdict()
    {
        ToolResultEntry ok = new("tc-1", "read", "output", IsError: false);
        ToolResultEntry failed = new("tc-2", "read", "output", IsError: true);

        await Assert.That(ResultAssertions.HasSucceeded(ok).IsPassed).IsTrue();
        await Assert.That(ResultAssertions.HasFailed(failed).IsPassed).IsTrue();
    }

    [Test]
    public async Task A_Tool_Call_Link_Carries_Its_Error_Verdict()
    {
        ToolCallLink ok = new("tc-1", "read", IsError: false);
        ToolCallLink failed = new("tc-2", "read", IsError: true);

        await Assert.That(ResultAssertions.HasSucceeded(ok).IsPassed).IsTrue();
        await Assert.That(ResultAssertions.HasFailed(failed).IsPassed).IsTrue();
    }

    // --- the chain path, which is the path the tree calls ---

    [Test]
    public async Task The_Chain_Passes_For_Every_Result_Family()
    {
        await Assert.That(Result.Success(42)).IsSuccessful();
        await Assert.That(Result.Success()).IsSuccessful();
        await Assert.That(Result.Success<string, string>("streamed")).IsSuccessful();
        await Assert.That(Result.Failure("boom")).IsFailed();
        await Assert.That(Result.Failure<string, string>("boom")).IsFailed();
    }

    [Test]
    public async Task The_Chain_Passes_For_Every_Error_Carrier()
    {
        await Assert.That(ToolResult.Success("out")).HasSucceeded();
        await Assert.That(ToolResult.Error("boom")).HasFailed();
        await Assert.That(new ToolResultEntry("c1", "read", "out", false)).HasSucceeded();
        await Assert.That(new ToolResultEntry("c2", "read", "boom", true)).HasFailed();
        await Assert.That(new ToolCallLink("c1", "read", false)).HasSucceeded();
        await Assert.That(new ToolCallLink("c2", "read", true)).HasFailed();
    }

    [Test]
    public async Task The_Chain_Passes_For_The_Two_Families_In_ResultAssertions_More()
    {
        await Assert.That(new ToolExecutionEndEvent("c1", ToolResult.Success("out"), false)).HasSucceeded();
        await Assert.That(new ToolExecutionEndEvent("c2", ToolResult.Error("boom"), true)).HasFailed();
    }

    [Test]
    public async Task The_Chain_Fails_For_A_Successful_Result()
    {
        string? message = null;
        try
        {
            await Assert.That(Result.Success(1)).IsFailed();
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        await Assert.That(message ?? string.Empty).IsNotEmpty()
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
}
