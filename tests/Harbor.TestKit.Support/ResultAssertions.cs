// ResultAssertions.cs — the ONE way to assert a Result in this tree.
//
// WHAT THIS IS
// ------------
// The tree asserts results in two spellings today, and the losing one is
// everywhere:
//
//     await Assert.That(result.IsSuccess).IsTrue();     ← 1551 sites (measured)
//     await Assert.That(result).IsSuccessful();          ← 78 sites
//
// The first spelling asserts a BOOL, so the failure says "found False" and
// the reader is left with two questions the assertion cannot answer: WHICH
// result failed, and WHY. The second spelling asserts the RESULT, so the
// failure names it — and this file is what puts the reason (the error text)
// into the message as well.
//
// WHY IT RETURNS AssertionResult AND NOT bool
// -------------------------------------------
// `bool` is the shape TUnit's own assertions use, and the generated failure
// message is then "found <value>" — where <value> is the asserted value's
// ToString(). `Result` and `Result<T>` are readonly structs with NO ToString
// override (CSharpFunctionalExtensions 3.7.0), so that message is the TYPE
// NAME, not the reason. Returning `AssertionResult` lets the helper put the
// reason in the message itself, and the reason is only read on the failure
// path — `Result.Error` THROWS on a successful result
// (`ErrorIsInaccessibleForSuccess`), so the helper must not touch it before
// it knows the result failed.
//
// (The XML doc of the previous home of this file said "put the error text in
// `.Because(result.Error)` when the message matters". That advice is a
// landmine: `Because` takes its argument EAGERLY, so on a SUCCESSFUL result
// `result.Error` throws and the test fails with an exception instead of
// passing. The message is built here instead, where the failure path is
// known.)
//
// WHERE IT LIVES AND WHY
// ----------------------
// tests/Harbor.TestKit.Support, NOT tests/Harbor.TestKit.
//
// The support part is the one every test project can take "without widening
// its product-assembly closure" (its own csproj says so), and this helper
// needs no product assembly beyond the two contract assemblies: `Result` /
// `Result<T>` come from the CSharpFunctionalExtensions PACKAGE, and
// `ToolResult` / `ToolResultEntry` / `ToolCallLink` live in
// `Harbor.Abstractions.Contracts`.
//
// The core kit (tests/Harbor.TestKit) is where it sat before, and that is
// exactly why the 24 test projects that take only the support part could not
// use it: they would have had to pull the whole core kit (the doubles, the
// two cost gates — 1400 lines they do not need) into their closure to reach
// one assertion helper. The core kit still reaches this file (it references
// the support part), so the ten projects that take the core keep it too, and
// the NAMESPACE is still Harbor.TestKit — a consumer's `using` line does not
// change.
//
// The `TestKitCompositionRules` closure pin is unaffected: the two new
// dependencies are PACKAGE references, and the rule walks ProjectReferences.
//
// THE FIVE FAMILIES
// -----------------
// The types in this tree that carry a success/failure flag:
//
//   Result<T>          IsSuccess / IsFailure   → IsSuccessful / IsFailed
//   Result             IsSuccess / IsFailure   → IsSuccessful / IsFailed
//   ToolResult         IsError                 → HasSucceeded / HasFailed
//   ToolResultEntry    IsError                 → HasSucceeded / HasFailed
//   ToolCallLink       IsError                 → HasSucceeded / HasFailed
//
// The generic and the non-generic `Result` are two unrelated types in
// CSharpFunctionalExtensions (`Result<T>` does not derive from `Result`), so
// each needs its own pair.
//
// `LlmToolResultBlock` also carries an `IsError`, and it is deliberately NOT
// here: it is a wire block from the provider, not a result the tree owns, and
// its two sites assert a different thing (that the MODEL was told the call
// failed).

using System.ComponentModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using TUnit.Assertions.Attributes;
using TUnit.Assertions.Core;

namespace Harbor.TestKit;

/// <summary>
///     Source-generated fluent assertions for <see cref="Result{T}" />,
///     <see cref="Result" />, <see cref="ToolResult" />,
///     <see cref="ToolResultEntry" /> and <see cref="ToolCallLink" /> (#1087,
///     #1569). Each <c>[GenerateAssertion]</c> method below becomes an
///     <c>Assert.That(...)</c> chain link, so failures point at the asserted
///     value instead of a helper frame:
///     <c>await Assert.That(result).IsSuccessful();</c>
/// </summary>
/// <remarks>
///     <para>
///         Prefer these over spelling out the boolean property
///         (<c>await Assert.That(result.IsSuccess).IsTrue();</c>): the property
///         form reports the failure at the <c>bool</c>, losing which result
///         failed and its error text. #1774 removed the hand-written helper
///         family that used to solve the same problem with richer messages but
///         a broken fluent chain — two competing APIs for one job. This is
///         now the only shape: assert through the chain, and the error text
///         is in the failure message because the helper puts it there.
///     </para>
/// </remarks>
public static partial class ResultAssertions
{
    /// <summary>Asserts a <see cref="Result{T}" /> succeeded.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to be successful")]
    public static AssertionResult IsSuccessful<T>(this Result<T> result) =>
        result.IsSuccess
            ? AssertionResult.Passed
            : AssertionResult.Failed($"it failed with: {result.Error}");

    /// <summary>Asserts a <see cref="Result{T}" /> failed.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to be failed")]
    public static AssertionResult IsFailed<T>(this Result<T> result) =>
        result.IsFailure
            ? AssertionResult.Passed
            : AssertionResult.Failed("it succeeded");

    /// <summary>Asserts a non-generic <see cref="Result" /> succeeded.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to be successful")]
    public static AssertionResult IsSuccessful(this Result result) =>
        result.IsSuccess
            ? AssertionResult.Passed
            : AssertionResult.Failed($"it failed with: {result.Error}");

    /// <summary>Asserts a non-generic <see cref="Result" /> failed.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to be failed")]
    public static AssertionResult IsFailed(this Result result) =>
        result.IsFailure
            ? AssertionResult.Passed
            : AssertionResult.Failed("it succeeded");

    /// <summary>Asserts a <see cref="ToolResult" /> is not an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have succeeded")]
    public static AssertionResult HasSucceeded(this ToolResult result) =>
        result.IsError
            ? AssertionResult.Failed("it carries an error")
            : AssertionResult.Passed;

    /// <summary>Asserts a <see cref="ToolResult" /> is an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have failed")]
    public static AssertionResult HasFailed(this ToolResult result) =>
        result.IsError
            ? AssertionResult.Passed
            : AssertionResult.Failed("it carries no error");

    /// <summary>Asserts a <see cref="ToolResultEntry" /> is not an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have succeeded")]
    public static AssertionResult HasSucceeded(this ToolResultEntry result) =>
        result.IsError
            ? AssertionResult.Failed("it carries an error")
            : AssertionResult.Passed;

    /// <summary>Asserts a <see cref="ToolResultEntry" /> is an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have failed")]
    public static AssertionResult HasFailed(this ToolResultEntry result) =>
        result.IsError
            ? AssertionResult.Passed
            : AssertionResult.Failed("it carries no error");

    /// <summary>Asserts a <see cref="ToolCallLink" /> is not an error.</summary>
    /// <remarks>
    ///     A link's <see cref="ToolCallLink.Completion" /> is a separate fact
    ///     and is not judged here: an ABANDONED call still carries
    ///     <c>IsError</c>, and this assertion says only that the call did not
    ///     error.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have succeeded")]
    public static AssertionResult HasSucceeded(this ToolCallLink result) =>
        result.IsError
            ? AssertionResult.Failed("it carries an error")
            : AssertionResult.Passed;

    /// <summary>Asserts a <see cref="ToolCallLink" /> is an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion(ExpectationMessage = "to have failed")]
    public static AssertionResult HasFailed(this ToolCallLink result) =>
        result.IsError
            ? AssertionResult.Passed
            : AssertionResult.Failed("it carries no error");
}
