using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
/// Allocation-budget coverage for the publish/resolve hot paths (#186).
/// The zero-alloc cases below lock provably allocation-free fast paths
/// (synchronous early return / cached-array return); the N-subscriber fan-out
/// paths are bounded tripwires (generous, CI-safe) following the
/// <c>SpanParserTests</c> tripwire pattern — they catch pathological
/// regressions without flaking on timer/pool noise.
/// </summary>
public class EventBusAllocationTests
{
    private static InMemoryEventBus ZeroScrollbackBus(TimeSpan? handlerBudget = null) => new(
        NullLogger<InMemoryEventBus>.Instance,
        maxScrollback: 0,
        handlerBudget: handlerBudget ?? TimeSpan.Zero);

    [Test]
    public async Task PublishAsync_ZeroSubscribers_IsAllocationFree()
    {
        // Fast path (InMemoryEventBus.PublishAsync): no middleware, no
        // scrollback, no subscriptions → synchronous return before any await,
        // so the async builder hands back the cached completed task.
        var bus = ZeroScrollbackBus();
        var evt = new TurnStartEvent(1);

        for (int i = 0; i < 3_000; i++)
        {
            await bus.PublishAsync(evt);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int publishes = 5_000;
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task PublishAsync_SingleSubscriber_StaysBounded()
    {
        // handlerBudget disabled: no per-publish linked-CTS on this path.
        var bus = ZeroScrollbackBus();
        bus.Subscribe(static (_, _) => ValueTask.CompletedTask);
        var evt = new TurnStartEvent(1);

        for (int i = 0; i < 1_000; i++)
        {
            await bus.PublishAsync(evt);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int publishes = 2_000;
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"eventbus-alloc: 1 subscriber × {publishes} publishes = {allocated} B ({(double)allocated / publishes:F1} B/publish)");
        await Assert.That(allocated).IsLessThanOrEqualTo(publishes * 1_024L);
    }

    [Test]
    public async Task PublishAsync_TenSubscribers_StaysBounded()
    {
        var bus = ZeroScrollbackBus();
        for (int s = 0; s < 10; s++)
        {
            bus.Subscribe(static (_, _) => ValueTask.CompletedTask);
        }

        var evt = new TurnStartEvent(1);

        for (int i = 0; i < 1_000; i++)
        {
            await bus.PublishAsync(evt);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int publishes = 2_000;
        for (int i = 0; i < publishes; i++)
        {
            await bus.PublishAsync(evt);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"eventbus-alloc: 10 subscribers × {publishes} publishes = {allocated} B ({(double)allocated / publishes:F1} B/publish)");
        await Assert.That(allocated).IsLessThanOrEqualTo(publishes * 2_048L);
    }
}

/// <summary>
/// Allocation-budget coverage for the frozen <c>ToolRegistry</c> resolve path
/// (#186, #183 follow-up). Frozen repeat resolves return cached arrays
/// (unfiltered) or memoized per-ruleset arrays (filtered, same instance), so
/// both are asserted fully allocation-free after warmup.
/// </summary>
public class ToolRegistryAllocationTests
{
    private static readonly JsonDocument SharedSchema = JsonDocument.Parse("""{"type":"object"}""");

    private sealed class CachedSchemaStubTool : ITool
    {
        public CachedSchemaStubTool(string name) => Name = ToolName.Create(name);

        public ToolName Name { get; }

        /// <inheritdoc />
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => Name.Value;
        public string Description => "Allocation-budget stub (schema parsed once).";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema => SharedSchema;

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("stub"));
    }

    private static ToolRegistry CreateFrozenRegistry(params string[] names)
    {
        var registry = new ToolRegistry();
        foreach (var name in names)
        {
            registry.Register(new CachedSchemaStubTool(name));
        }

        registry.Freeze();
        return registry;
    }

    [Test]
    public async Task ResolveTools_FrozenUnfiltered_IsAllocationFree()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool", "gamma_tool", "delta_tool");

        for (int i = 0; i < 1_000; i++)
        {
            _ = registry.ResolveTools("code");
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int resolves = 2_000;
        for (int i = 0; i < resolves; i++)
        {
            _ = registry.ResolveTools("code");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveTools_FrozenWithPermission_IsAllocationFree()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool", "gamma_tool", "delta_tool");
        var permission = PermissionRuleset.Default;

        // First call populates the per-ruleset memo outside measurement.
        _ = registry.ResolveTools("code", permission);
        for (int i = 0; i < 1_000; i++)
        {
            _ = registry.ResolveTools("code", permission);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int resolves = 2_000;
        for (int i = 0; i < resolves; i++)
        {
            _ = registry.ResolveTools("code", permission);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }
}
