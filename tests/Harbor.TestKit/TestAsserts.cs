using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;

namespace Harbor.TestKit;

public static class TestAsserts
{
    public static async Task Succeeded<T>(this Result<T> result, string? context = null)
    {
        if (result.IsSuccess) return;
        var ctx = context != null ? $" ({context})" : "";
        await Assert.That(false).IsTrue().Because($"Expected success{ctx}. Error: {result.Error}");
    }

    public static async Task Failed<T>(this Result<T> result, string? expectedSubstring = null, string? context = null)
    {
        if (result.IsFailure)
        {
            if (expectedSubstring == null || result.Error.Contains(expectedSubstring))
                return;
        }
        var ctx = context != null ? $" ({context})" : "";
        var expected = expectedSubstring != null ? $" containing '{expectedSubstring}'" : "";
        var actual = result.IsSuccess ? "success" : result.Error;
        await Assert.That(false).IsTrue().Because($"Expected failure{expected}{ctx}. Actual: {actual}");
    }

    public static async Task Succeeded(this ToolResult result, string? context = null)
    {
        if (!result.IsError) return;
        var ctx = context != null ? $" ({context})" : "";
        await Assert.That(false).IsTrue().Because($"Expected tool success{ctx}. Output: {result.Output}");
    }

    public static async Task Failed(this ToolResult result, string? expectedSubstring = null, string? context = null)
    {
        if (result.IsError)
        {
            if (expectedSubstring == null || result.Output.Contains(expectedSubstring))
                return;
        }
        var ctx = context != null ? $" ({context})" : "";
        var expected = expectedSubstring != null ? $" containing '{expectedSubstring}'" : "";
        var actual = result.IsError ? result.Output : "success";
        await Assert.That(false).IsTrue().Because($"Expected tool error{expected}{ctx}. Actual: {actual}");
    }

    public static async Task HasOutput(this ToolResult result, string expected, string? context = null)
    {
        if (result.Output.Contains(expected)) return;
        var ctx = context != null ? $" ({context})" : "";
        await Assert.That(false).IsTrue().Because($"Expected tool output to contain '{expected}'{ctx}. Actual: {result.Output}");
    }

    public static async Task Throws<TEx>(this Func<Task> action, string? expectedMessageSubstring = null, string? context = null)
        where TEx : Exception
    {
        try
        {
            await action();
            var ctx = context != null ? $" ({context})" : "";
            await Assert.That(false).IsTrue().Because($"Expected {typeof(TEx).Name} to be thrown{ctx}.");
        }
        catch (TEx ex) when (expectedMessageSubstring != null)
        {
            if (ex.Message.Contains(expectedMessageSubstring)) return;
            var ctx = context != null ? $" ({context})" : "";
            await Assert.That(false).IsTrue().Because($"Expected {typeof(TEx).Name} with message containing '{expectedMessageSubstring}'{ctx}. Got: {ex.Message}");
        }
    }

    public static async Task Throws<TEx>(this Action action, string? expectedMessageSubstring = null, string? context = null)
        where TEx : Exception
    {
        try
        {
            action();
            var ctx = context != null ? $" ({context})" : "";
            await Assert.That(false).IsTrue().Because($"Expected {typeof(TEx).Name} to be thrown{ctx}.");
        }
        catch (TEx ex) when (expectedMessageSubstring != null)
        {
            if (ex.Message.Contains(expectedMessageSubstring)) return;
            var ctx = context != null ? $" ({context})" : "";
            await Assert.That(false).IsTrue().Because($"Expected {typeof(TEx).Name} with message containing '{expectedMessageSubstring}'{ctx}. Got: {ex.Message}");
        }
    }
}
