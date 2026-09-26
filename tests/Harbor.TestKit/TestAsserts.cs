using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.TestKit;

/// <summary>
///     Diagnostic assertion helpers for <see cref="Result"/>, <see cref="Result{T}"/>
///     and <see cref="ToolResult"/> in TUnit tests. Each helper verifies the expected
///     outcome and, on mismatch, fails with a detailed "because" message describing
///     what was expected, what was actually observed, and — when supplied — the
///     <paramref name="context"/> label, so failures read clearly at the call site.
/// </summary>
public static class TestAsserts
{
    /// <summary>
    ///     Asserts that <paramref name="result"/> succeeded.
    /// </summary>
    /// <typeparam name="T">The success value type of the result.</typeparam>
    /// <param name="result">The result to assert on.</param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task Succeeded<T>(this Result<T> result, string? context = null)
    {
        if (result.IsSuccess) return;

        var reason = Describe(
            "result to succeed",
            $"result failed with error: {result.Error}",
            context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Asserts that <paramref name="result"/> failed, optionally requiring the error
    ///     message to contain <paramref name="expectedSubstring"/>.
    /// </summary>
    /// <typeparam name="T">The success value type of the result.</typeparam>
    /// <param name="result">The result to assert on.</param>
    /// <param name="expectedSubstring">
    ///     When non-null, the failure error message must contain this substring (ordinal).
    /// </param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task Failed<T>(this Result<T> result, string? expectedSubstring = null, string? context = null)
    {
        if (result.IsFailure &&
            (expectedSubstring is null || result.Error.Contains(expectedSubstring, StringComparison.Ordinal)))
            return;

        string expected, actual;
        if (result.IsSuccess)
        {
            expected = "result to fail";
            actual = $"result succeeded with value: {result.Value}";
        }
        else
        {
            expected = $"failure with error containing \"{expectedSubstring}\"";
            actual = $"error was: {result.Error}";
        }

        var reason = Describe(expected, actual, context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Asserts that <paramref name="result"/> failed, optionally requiring the error
    ///     message to contain <paramref name="expectedSubstring"/>.
    /// </summary>
    /// <param name="result">The result to assert on.</param>
    /// <param name="expectedSubstring">
    ///     When non-null, the failure error message must contain this substring (ordinal).
    /// </param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task Failed(this Result result, string? expectedSubstring = null, string? context = null)
    {
        if (result.IsFailure &&
            (expectedSubstring is null || result.Error.Contains(expectedSubstring, StringComparison.Ordinal)))
            return;

        string expected, actual;
        if (result.IsSuccess)
        {
            expected = "result to fail";
            actual = "result succeeded";
        }
        else
        {
            expected = $"failure with error containing \"{expectedSubstring}\"";
            actual = $"error was: {result.Error}";
        }

        var reason = Describe(expected, actual, context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Asserts that <paramref name="result"/> represents a successful tool execution
    ///     (i.e. <see cref="ToolResult.IsError"/> is <see langword="false"/>).
    /// </summary>
    /// <param name="result">The tool result to assert on.</param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task Succeeded(this ToolResult result, string? context = null)
    {
        if (!result.IsError) return;

        var reason = Describe(
            "tool result to succeed (IsError = false)",
            $"tool result was an error: {result.Output}",
            context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Asserts that <paramref name="result"/> represents a failed tool execution
    ///     (i.e. <see cref="ToolResult.IsError"/> is <see langword="true"/>), optionally
    ///     requiring the output to contain <paramref name="expectedSubstring"/>.
    /// </summary>
    /// <param name="result">The tool result to assert on.</param>
    /// <param name="expectedSubstring">
    ///     When non-null, the tool output must contain this substring (ordinal).
    /// </param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task Failed(this ToolResult result, string? expectedSubstring = null, string? context = null)
    {
        if (result.IsError &&
            (expectedSubstring is null || result.Output.Contains(expectedSubstring, StringComparison.Ordinal)))
            return;

        string expected, actual;
        if (!result.IsError)
        {
            expected = "tool result to fail (IsError = true)";
            actual = $"tool output was: {result.Output}";
        }
        else
        {
            expected = $"tool error output containing \"{expectedSubstring}\"";
            actual = $"tool output was: {result.Output}";
        }

        var reason = Describe(expected, actual, context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Asserts that <paramref name="result"/>'s <see cref="ToolResult.Output"/>
    ///     contains <paramref name="expected"/> (ordinal, case-sensitive).
    /// </summary>
    /// <param name="result">The tool result to assert on.</param>
    /// <param name="expected">The substring expected to appear in the tool output.</param>
    /// <param name="context">Optional context label surfaced in the failure message.</param>
    public static async Task HasOutput(this ToolResult result, string expected, string? context = null)
    {
        if (result.Output.Contains(expected, StringComparison.Ordinal)) return;

        var reason = Describe(
            $"output to contain \"{expected}\"",
            $"output was: {result.Output}",
            context);
        await Assert.That(false).IsTrue().Because(reason);
    }

    /// <summary>
    ///     Formats a "because" reason describing the expected outcome, the actual
    ///     observation, and an optional context label.
    /// </summary>
    /// <param name="expected">A description of what was expected.</param>
    /// <param name="actual">A description of what was actually observed.</param>
    /// <param name="context">Optional context label; omitted from the message when blank.</param>
    /// <returns>A single reason string passed to <c>Assert.That(false).IsTrue().Because(...)</c>.</returns>
    private static string Describe(string expected, string actual, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return $"Expected: {expected}. Actual: {actual}.";

        return $"Expected: {expected}. Actual: {actual}. Context: {context}.";
    }
}
