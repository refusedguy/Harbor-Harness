// ResultAssertions.More.cs — the two families that are NOT plain results.
//
// They live in their own file for one reason: the five families in
// `ResultAssertions.cs` are the ones every result-shaped value in the tree
// uses, and this file holds the two that only a few sites reach. Splitting
// them keeps the common file's shape (and its merge surface) small.
//
//   Result<TValue, TError>   the value-shaped retry verdict
//                            (`RetryPolicy.ExecuteValueAsync`)
//   ToolExecutionEndEvent    the bus event that carries a tool's error flag
//
// Both carry the same flag the other families carry (`IsSuccess` /
// `IsFailure`, and `IsError`), so they get the same chain links.

using System.ComponentModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Contracts.Events;
using TUnit.Assertions.Attributes;
using TUnit.Assertions.Core;

namespace Harbor.TestKit;

public static partial class ResultAssertions
{
    /// <summary>Asserts a value-shaped <see cref="Result{TValue, TError}" /> succeeded.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static AssertionResult IsSuccessful<TValue, TError>(this Result<TValue, TError> result) =>
        result.IsSuccess
            ? AssertionResult.Passed
            : AssertionResult.Failed($"it failed with: {result.Error}");

    /// <summary>Asserts a value-shaped <see cref="Result{TValue, TError}" /> failed.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static AssertionResult IsFailed<TValue, TError>(this Result<TValue, TError> result) =>
        result.IsFailure
            ? AssertionResult.Passed
            : AssertionResult.Failed("it succeeded");

    /// <summary>Asserts a <see cref="ToolExecutionEndEvent" /> is not an error.</summary>
    /// <remarks>
    ///     The event carries the tool's own result and its error flag beside
    ///     it; the flag is what the bus publishes, so it is the flag a
    ///     consumer of the event reads.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static AssertionResult HasSucceeded(this ToolExecutionEndEvent result) =>
        result.IsError
            ? AssertionResult.Failed("it carries an error")
            : AssertionResult.Passed;

    /// <summary>Asserts a <see cref="ToolExecutionEndEvent" /> is an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static AssertionResult HasFailed(this ToolExecutionEndEvent result) =>
        result.IsError
            ? AssertionResult.Passed
            : AssertionResult.Failed("it carries no error");
}
