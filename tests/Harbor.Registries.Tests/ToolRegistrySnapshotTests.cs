using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
using TUnit.Assertions;

using FakeToolBase = Harbor.TestKit.Generated.FakeTool; // #1769: generated double

namespace Harbor.Registries.Tests;

/// <summary>
///     Snapshot slice (#183): the frozen <c>ResolveTools</c> / <c>GetAllTools</c> path
///     returns cached arrays instead of allocating per call, with resolution
///     semantics unchanged (permissions filtering identical to manual evaluation).
/// </summary>
public class ToolRegistrySnapshotTests
{
    private sealed class NamedStubTool : FakeToolBase
    {
        public NamedStubTool(string name)
        {
            Name = ToolName.Create(name);
            DisplayName = Name.Value;
            Description = "Snapshot test stub.";
            ExecuteAsyncValue = ToolResult.Success("stub");
        }
    }

    private sealed class StubSource : IToolSource
    {
        private readonly ToolRegistry _inner = new();

        public StubSource(params string[] names)
        {
            foreach (var name in names)
            {
                _inner.Register(new NamedStubTool(name));
            }

            _inner.Freeze();
        }

        public IReadOnlyList<ToolDescriptor> GetAllTools() => _inner.GetAllTools();

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
            => _inner.ResolveTools(agentName, sessionPermission);

        public Result<ITool> GetTool(ToolName name) => _inner.GetTool(name);
    }

    private static ToolRegistry CreateFrozenRegistry(params string[] names)
    {
        var registry = new ToolRegistry();
        foreach (var name in names)
        {
            registry.Register(new NamedStubTool(name));
        }

        registry.Freeze();
        return registry;
    }

    private static List<string> SortedNames(IReadOnlyList<ToolDescriptor> descriptors)
    {
        var names = new List<string>(descriptors.Count);
        for (int i = 0; i < descriptors.Count; i++)
        {
            names.Add(descriptors[i].Name.Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task AssertSameNames(IReadOnlyList<ToolDescriptor> actual, params string[] expected)
    {
        var actualNames = SortedNames(actual);
        var expectedNames = new List<string>(expected);
        expectedNames.Sort(StringComparer.Ordinal);
        await Assert.That(actualNames.Count).IsEqualTo(expectedNames.Count);
        for (int i = 0; i < expectedNames.Count; i++)
        {
            await Assert.That(actualNames[i]).IsEqualTo(expectedNames[i]);
        }
    }

    [Test]
    public async Task ResolveTools_Unfiltered_FrozenMatchesUnfrozen()
    {
        var frozen = CreateFrozenRegistry("alpha_tool", "beta_tool");
        var unfrozen = new ToolRegistry();
        unfrozen.Register(new NamedStubTool("alpha_tool"));
        unfrozen.Register(new NamedStubTool("beta_tool"));

        await AssertSameNames(frozen.ResolveTools("code"), "alpha_tool", "beta_tool");
        await AssertSameNames(unfrozen.ResolveTools("code"), "alpha_tool", "beta_tool");
    }

    [Test]
    public async Task ResolveTools_Filtered_FrozenMatchesManualEvaluation()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool");
        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("alpha_tool", "*", PermissionAction.Allow),
            new PermissionRule("beta_tool", "*", PermissionAction.Deny)
        });

        var resolved = registry.ResolveTools("code", permission);

        // Manual evaluation over the unfiltered set must agree with ResolveTools.
        var all = registry.ResolveTools("code");
        var expected = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            if (permission.Evaluate(all[i].Name.Value, "*") == PermissionAction.Allow)
            {
                expected.Add(all[i].Name.Value);
            }
        }

        await AssertSameNames(resolved, expected.ToArray());
        await AssertSameNames(resolved, "alpha_tool");
    }

    [Test]
    public async Task ResolveTools_Unfiltered_RepeatCalls_ReturnSameSnapshot()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool");

        var first = registry.ResolveTools("code");
        var second = registry.ResolveTools("code");

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await AssertSameNames(first, "alpha_tool", "beta_tool");
    }

    [Test]
    public async Task ResolveTools_Filtered_RepeatCalls_ReturnSameSnapshot()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool");
        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("alpha_tool", "*", PermissionAction.Allow)
        });

        var first = registry.ResolveTools("code", permission);
        var second = registry.ResolveTools("code", permission);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await AssertSameNames(first, "alpha_tool");
    }

    [Test]
    public async Task GetAllTools_Frozen_RepeatCalls_ReturnSameSnapshot()
    {
        var registry = CreateFrozenRegistry("alpha_tool");

        var first = registry.GetAllTools();
        var second = registry.GetAllTools();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task ResolveTools_Frozen_RepeatCalls_AllocateNothing()
    {
        var registry = CreateFrozenRegistry("alpha_tool", "beta_tool");
        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("*", "*", PermissionAction.Allow)
        });

        // Warmup: populate the per-ruleset cache and tier up the loop.
        for (int i = 0; i < 50; i++)
        {
            _ = registry.ResolveTools("code");
            _ = registry.ResolveTools("code", permission);
            _ = registry.GetAllTools();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            _ = registry.ResolveTools("code");
            _ = registry.ResolveTools("code", permission);
            _ = registry.GetAllTools();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        // Cached arrays are returned as-is — no List/array wrapper per call.
        await Assert.That(after - before).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveTools_AfterRegisterAndFreeze_ReflectsNewTools()
    {
        var registry = CreateFrozenRegistry("alpha_tool");
        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("*", "*", PermissionAction.Allow)
        });

        // Populate both caches, then invalidate and re-freeze with a new tool.
        _ = registry.ResolveTools("code");
        _ = registry.ResolveTools("code", permission);
        registry.Register(new NamedStubTool("beta_tool"));
        registry.Freeze();

        await AssertSameNames(registry.ResolveTools("code"), "alpha_tool", "beta_tool");
        await AssertSameNames(registry.ResolveTools("code", permission), "alpha_tool", "beta_tool");

        // The post-freeze snapshot is cached again.
        await Assert.That(ReferenceEquals(registry.ResolveTools("code"), registry.ResolveTools("code"))).IsTrue();
    }

    [Test]
    public async Task CompositeToolRegistry_Frozen_RepeatCalls_ReturnSameSnapshot()
    {
        var composite = new CompositeToolRegistry();
        composite.AddSource(new StubSource("alpha_tool", "beta_tool"));
        composite.Freeze();

        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("*", "*", PermissionAction.Allow)
        });

        var first = composite.ResolveTools("code");
        var second = composite.ResolveTools("code");
        var filteredFirst = composite.ResolveTools("code", permission);
        var filteredSecond = composite.ResolveTools("code", permission);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(ReferenceEquals(filteredFirst, filteredSecond)).IsTrue();
        await AssertSameNames(first, "alpha_tool", "beta_tool");
        await AssertSameNames(filteredFirst, "alpha_tool", "beta_tool");
    }

    /// <summary>
    ///     #1101 dual-path: unfrozen composite fans out to sources with the
    ///     same names a frozen snapshot serves (single IToolSource path).
    /// </summary>
    [Test]
    public async Task CompositeToolRegistry_Unfrozen_MatchesFrozen()
    {
        var unfrozen = new CompositeToolRegistry();
        unfrozen.AddSource(new StubSource("alpha_tool", "beta_tool"));

        var frozen = new CompositeToolRegistry();
        frozen.AddSource(new StubSource("alpha_tool", "beta_tool"));
        frozen.Freeze();

        await AssertSameNames(unfrozen.ResolveTools("code"), "alpha_tool", "beta_tool");
        await AssertSameNames(frozen.ResolveTools("code"), "alpha_tool", "beta_tool");
        await AssertSameNames(unfrozen.GetAllTools(), "alpha_tool", "beta_tool");

        var found = unfrozen.GetTool(ToolName.Create("alpha_tool"));
        await Assert.That(found.IsSuccess).IsTrue();
        var missing = unfrozen.GetTool(ToolName.Create("nope_tool"));
        await Assert.That(missing.IsFailure).IsTrue();
    }

    /// <summary>
    ///     #1240: frozen arrays are pre-sorted by name (ordinal) — the per-turn
    ///     consumer iterates them as-is instead of paying an OrderBy per turn.
    ///     Registration order must not leak into the snapshot order.
    /// </summary>
    [Test]
    public async Task FrozenArrays_ArePreSortedByNameOrdinal()
    {
        var registry = CreateFrozenRegistry("zeta_tool", "beta_tool", "alpha_tool", "delta_tool");
        var permission = new PermissionRuleset(new[]
        {
            new PermissionRule("*", "*", PermissionAction.Allow)
        });

        await AssertNamesInOrder(registry.GetAllTools(), "alpha_tool", "beta_tool", "delta_tool", "zeta_tool");
        await AssertNamesInOrder(registry.ResolveTools("code"), "alpha_tool", "beta_tool", "delta_tool", "zeta_tool");
        await AssertNamesInOrder(registry.ResolveTools("code", permission), "alpha_tool", "beta_tool", "delta_tool", "zeta_tool");
    }

    private static async Task AssertNamesInOrder(IReadOnlyList<ToolDescriptor> actual, params string[] expected)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i].Name.Value).IsEqualTo(expected[i]);
        }
    }
}
