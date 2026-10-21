using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     #1760: a tool's own execution budget is validated at the door.
/// </summary>
/// <remarks>
///     A zero budget would cancel the call before it started, and a negative
///     one is a typo that would otherwise read as "no deadline". Either way
///     the failure would surface at RUN time as a mysterious instant
///     cancellation, far from the registration that caused it — so the
///     registration refuses it instead.
/// </remarks>
public class ToolTimeoutRegistrationTests
{
    private sealed class BudgetedTool(int? timeoutMs) : ITool
    {
        public int? TimeoutMs => timeoutMs;

        public ToolName Name => ToolName.Create("budgeted");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Budgeted";

        public string Description => "Declares an execution budget.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    [Test]
    public async Task Register_PositiveBudget_IsAccepted()
    {
        var registry = new ToolRegistry();

        Result result = registry.Register(new BudgetedTool(timeoutMs: 1_000));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Register_NoBudget_IsAccepted()
    {
        // "No opinion" is the default and is not a defect: the agent's common
        // default applies.
        var registry = new ToolRegistry();

        Result result = registry.Register(new BudgetedTool(timeoutMs: null));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Register_ZeroBudget_IsRefused()
    {
        var registry = new ToolRegistry();

        Result result = registry.Register(new BudgetedTool(timeoutMs: 0));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("TimeoutMs");
    }

    [Test]
    public async Task Register_NegativeBudget_IsRefused()
    {
        var registry = new ToolRegistry();

        Result result = registry.Register(new BudgetedTool(timeoutMs: -1));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("TimeoutMs");
    }

    [Test]
    public async Task Resolve_ToolBudgetWins_TheCommonDefaultIsTheFallback()
    {
        // The precedence, pinned where it is decided.
        await Assert.That(ToolTimeouts.Resolve(1_000, TimeSpan.FromSeconds(9))!.Value)
            .IsEqualTo(TimeSpan.FromMilliseconds(1_000));
        await Assert.That(ToolTimeouts.Resolve(null, TimeSpan.FromSeconds(9))!.Value)
            .IsEqualTo(TimeSpan.FromSeconds(9));
        await Assert.That(ToolTimeouts.Resolve(null, null).HasValue).IsFalse();
        // A value the registration path refuses never reaches the dispatcher —
        // and if it somehow did, it must not become a zero budget.
        await Assert.That(ToolTimeouts.Resolve(0, TimeSpan.FromSeconds(9))!.Value)
            .IsEqualTo(TimeSpan.FromSeconds(9));
    }
}
