using System.ComponentModel;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using TUnit.Assertions.Attributes;

namespace Harbor.TestKit;

/// <summary>
///     Source-generated fluent assertions for <see cref="Result{T}"/> and
///     <see cref="ToolResult"/> (#1087). Each <c>[GenerateAssertion]</c> method
///     below becomes an <c>Assert.That(...)</c> chain link, so failures point at
///     the asserted value instead of a helper frame:
///     <c>await Assert.That(result).IsSuccessful();</c>
/// </summary>
/// <remarks>
///     Prefer these over spelling out the boolean property
///     (<c>await Assert.That(result.IsSuccess).IsTrue();</c>): the property
///     form reports the failure at the <c>bool</c>, losing which result failed
///     and its error text. The hand-written <c>TestAsserts</c> helpers solve the
///     same problem with richer messages but break the fluent chain; these are
///     the chain-preserving complement for the two most common checks.
/// </remarks>
public static partial class ResultAssertions
{
    /// <summary>Asserts a <see cref="Result{T}"/> succeeded.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static bool IsSuccessful<T>(this Result<T> result) => result.IsSuccess;

    /// <summary>Asserts a <see cref="Result{T}"/> failed.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static bool IsFailed<T>(this Result<T> result) => result.IsFailure;

    /// <summary>Asserts a <see cref="ToolResult"/> is not an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static bool HasSucceeded(this ToolResult result) => !result.IsError;

    /// <summary>Asserts a <see cref="ToolResult"/> is an error.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [GenerateAssertion]
    public static bool HasFailed(this ToolResult result) => result.IsError;
}
