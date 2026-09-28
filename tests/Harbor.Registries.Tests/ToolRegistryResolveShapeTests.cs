using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
using System.Text.Json;

namespace Harbor.Registries.Tests;

/// <summary>
///     #408: the result-shape difference in <see cref="ToolRegistry.ResolveTools" /> that
///     made <c>ToolRegistryBenchmark</c> incomparable.
///     <para>
///         The unfiltered call resolves every registered tool; a permission-filtered call
///         resolves only what the ruleset <c>Allow</c>s. Benchmarking
///         <c>PermissionRuleset.Default</c> against "no permission" therefore measured a
///         <b>shape change</b> (N descriptors vs 0), not a performance change — a deny-all
///         filter short-circuits on an empty cached snapshot, so the "with permission" row
///         looked like an optimization. These tests pin the shape so the benchmark fixture
///         cannot silently reintroduce it, and so the fix (an explicit allow-all ruleset)
///         stays comparable.
///     </para>
/// </summary>
public class ToolRegistryResolveShapeTests
{
    private sealed class NamedStubTool(string name) : ITool
    {
        public ToolName Name { get; } = ToolName.Create(name);

        public string DisplayName => Name.Value;
        public string Description => "Resolve-shape test stub.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema => JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("stub"));
    }

    private static ToolRegistry CreateRegistry(bool frozen, params string[] names)
    {
        var registry = new ToolRegistry();
        foreach (var name in names)
        {
            registry.Register(new NamedStubTool(name));
        }

        if (frozen)
        {
            registry.Freeze();
        }

        return registry;
    }

    private static PermissionRuleset AllowAll(params string[] names) =>
        new(Array.ConvertAll(names, n => new PermissionRule(n, "*", PermissionAction.Allow)),
            Array.Empty<IArgSafetyPolicy>());

    private static PermissionRuleset DenyAll(params string[] names) =>
        new(Array.ConvertAll(names, n => new PermissionRule(n, "*", PermissionAction.Deny)),
            Array.Empty<IArgSafetyPolicy>());

    private static List<string> Names(IReadOnlyList<ToolDescriptor> descriptors)
    {
        var names = new List<string>(descriptors.Count);
        for (int i = 0; i < descriptors.Count; i++)
        {
            names.Add(descriptors[i].Name.Value);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    ///     The shape difference itself: the unfiltered call resolves every tool, the
    ///     deny-all call resolves none. A benchmark that puts these two rows side by side
    ///     is comparing a shape change, which is why the bench now uses an allow-all
    ///     ruleset for its comparable pair.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveTools_DenyAll_HasADifferentResultShapeThanUnfiltered(bool frozen)
    {
        var registry = CreateRegistry(frozen, "tool_0", "tool_1", "tool_2", "tool_3");

        var unfiltered = registry.ResolveTools("code");
        var denied = registry.ResolveTools("code", DenyAll("tool_0", "tool_1", "tool_2", "tool_3"));

        await Assert.That(unfiltered.Count).IsEqualTo(4);
        await Assert.That(denied.Count).IsEqualTo(0);
        await Assert.That(denied).IsNotNull()
            .Because("a deny-all filter must return an empty list, never null — a null would be indistinguishable from 'not resolved yet'.");

        // Same registry, same agent, same ruleset count — only the outcome differs.
        await Assert.That(denied.Count).IsNotEqualTo(unfiltered.Count);
    }

    /// <summary>
    ///     The default ruleset denies unknown tool names (no rule matches → Ask → filtered
    ///     out), so it is also a deny-all shape for stub tools. Pinned because the original
    ///     benchmark fixture used it as the "with permission" case.
    /// </summary>
    [Test]
    public async Task ResolveTools_DefaultRuleset_FiltersOutUnregisteredToolNames()
    {
        var registry = CreateRegistry(frozen: true, "tool_0", "tool_1");

        var unfiltered = registry.ResolveTools("code");
        var withDefault = registry.ResolveTools("code", PermissionRuleset.Default);

        await Assert.That(unfiltered.Count).IsEqualTo(2);
        await Assert.That(withDefault.Count).IsEqualTo(0);
    }

    /// <summary>
    ///     The comparable shape: an explicit allow-all ruleset resolves exactly the same
    ///     descriptors as the unfiltered call, on both the frozen and the unfrozen path.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveTools_AllowAll_ResolvesTheSameDescriptorsAsUnfiltered(bool frozen)
    {
        string[] names = ["tool_0", "tool_1", "tool_2", "tool_3"];
        var registry = CreateRegistry(frozen, names);

        var unfiltered = registry.ResolveTools("code");
        var allowed = registry.ResolveTools("code", AllowAll(names));

        await Assert.That(allowed.Count).IsEqualTo(unfiltered.Count)
            .Because("the comparable pair must resolve the same number of tools, otherwise the benchmark measures a shape change.");

        var expected = Names(unfiltered);
        var actual = Names(allowed);
        await Assert.That(string.Join(",", actual)).IsEqualTo(string.Join(",", expected));

        for (int i = 0; i < actual.Count; i++)
        {
            await Assert.That(allowed[i].GetType()).IsEqualTo(unfiltered[i].GetType())
                .Because("descriptor type must not differ between the compared cases.");
        }
    }

    /// <summary>
    ///     A partial filter is the middle shape: same container contract, fewer entries.
    ///     Documented here so nobody "fixes" a benchmark by comparing a partial filter
    ///     against an unfiltered call.
    /// </summary>
    [Test]
    public async Task ResolveTools_PartialFilter_ResolvesTheSubsetOnly()
    {
        var registry = CreateRegistry(frozen: true, "tool_0", "tool_1", "tool_2");

        var resolved = registry.ResolveTools("code", AllowAll("tool_0", "tool_2"));

        await Assert.That(string.Join(",", Names(resolved))).IsEqualTo("tool_0,tool_2");
    }

    /// <summary>
    ///     The frozen path memoizes per ruleset instance; the same denied ruleset must keep
    ///     returning the same empty snapshot rather than re-resolving.
    /// </summary>
    [Test]
    public async Task ResolveTools_DenyAll_RepeatCalls_ReturnTheSameSnapshot()
    {
        var registry = CreateRegistry(frozen: true, "tool_0", "tool_1");
        var denied = DenyAll("tool_0", "tool_1");

        var first = registry.ResolveTools("code", denied);
        var second = registry.ResolveTools("code", denied);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first.Count).IsEqualTo(0);
    }
}
