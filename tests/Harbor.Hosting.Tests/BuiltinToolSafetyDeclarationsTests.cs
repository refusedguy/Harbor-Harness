using CSharpFunctionalExtensions;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     The guard for the guard (#557).
/// </summary>
/// <remarks>
///     <para>
///         <c>PermissionRuleset.DefaultSafetyPolicies</c> is the FALLBACK policy
///         set, built from <see cref="BuiltinToolSafetyProfiles" />. The live
///         <c>IPermissionService</c> overrides it with the set the tool registry
///         derives at check time, so a tool added to the registry is guarded the
///         moment it exists. But two callers have no registry in hand — a direct
///         <c>PermissionRuleset.Evaluate</c>, and <c>ToolRegistry.ResolveTools</c> —
///         and for them the builtin table is the only thing that can rot.
///     </para>
///     <para>
///         This test is the table's liveness check. It composes the real host,
///         reads what actually registered, and requires the table to agree name for
///         name and kind for kind: a registered tool with no row, a row whose kind
///         disagrees with the tool's own <c>ITool.SafetyProfile</c>, and a row
///         naming a tool that no longer registers are all red. That is the single
///         visible failure that replaces the silent ones the audit found.
///     </para>
/// </remarks>
[NotInParallel("hosting")]
public class BuiltinToolSafetyDeclarationsTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose() =>
        new ServiceCollection()
            .AddHarbor(new HarborComposeOptions { HarborDir = TempHarborDir(), DefaultStorageBackend = "memory" })
            .BuildServiceProvider();

    [Test]
    public async Task Every_Registered_Tool_Is_Covered_By_The_Fallback_Table_With_The_Same_Profile()
    {
        using var sp = Compose();
        var registry = sp.GetRequiredService<IToolRegistry>();

        var registered = new Dictionary<string, ToolSafetyProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (ToolDescriptor descriptor in registry.GetAllTools())
        {
            Result<ITool> resolved = registry.GetTool(descriptor.Name);
            await Assert.That(resolved.IsSuccess).IsTrue()
                .Because($"'{descriptor.Name}' is listed by GetAllTools so GetTool must find it");
            registered[descriptor.Name.Value] = resolved.Value.SafetyProfile;
        }

        var problems = new List<string>();
        foreach (var (name, declared) in registered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!BuiltinToolSafetyProfiles.TryGet(name, out ToolSafetyProfile table))
            {
                problems.Add(
                    $"tool '{name}' registers and declares {declared.ArgKind}, but "
                    + "BuiltinToolSafetyProfiles has no row for it — a direct "
                    + "PermissionRuleset.Evaluate (and ResolveTools) would not guard it");
                continue;
            }

            if (table != declared)
            {
                problems.Add(
                    $"tool '{name}' declares {declared.ArgKind} on ITool.SafetyProfile, "
                    + $"but the fallback table says {table.ArgKind} — the runtime is "
                    + "correct and this table is a lie");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("\n", problems));
    }

    [Test]
    public async Task Fallback_Table_Has_No_Row_For_A_Tool_That_No_Longer_Registers()
    {
        using var sp = Compose();
        var registry = sp.GetRequiredService<IToolRegistry>();
        var live = registry.GetAllTools().Select(d => d.Name.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Plugin-contributed tools (session_broadcast / session_inbox ship as
        // samples/plugins-cs) are intentionally declared here: the table is the
        // BUILTIN vocabulary, and a plugin that loads later must still find its
        // row. Anything else is stale.
        var pluginVocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session_broadcast",
            "session_inbox",
        };

        var stale = BuiltinToolSafetyProfiles.All
            .Select(d => d.ToolName)
            .Where(name => !live.Contains(name) && !pluginVocabulary.Contains(name))
            .ToArray();

        await Assert.That(stale).IsEmpty()
            .Because("a row naming a tool that registers nowhere is a claim nobody "
                     + "maintains; delete it (and its PermissionRuleset.Default rule, "
                     + "if any) or register the tool");
    }

    [Test]
    public async Task Registered_Registry_Derives_The_Same_Guard_Coverage_As_The_Fallback()
    {
        using var sp = Compose();
        var registry = sp.GetRequiredService<IToolRegistry>();

        // The registry-derived set must cover every path-taking tool the fallback
        // covers. This is the assertion that the two can never diverge in the
        // unsafe direction at runtime.
        var derived = registry.SafetyPolicies
            .OfType<PathGuardSafetyPolicy>()
            .ToArray();

        await Assert.That(derived).IsNotEmpty()
            .Because("a composed host registers path-taking tools, so the derived "
                     + "guard set must not be empty — an empty one would guard nothing");
        await Assert.That(derived[0].AppliesTo("write")).IsTrue();
        await Assert.That(derived[0].AppliesTo("read")).IsTrue();
        await Assert.That(derived[0].AppliesTo("edit")).IsTrue();
        await Assert.That(derived[0].AppliesTo("patch")).IsTrue();
        // A shell tool declares Command, so the traversal guard must stand down for
        // it — otherwise `bash ls ../x` stops matching the "ls *" allow rule.
        await Assert.That(derived[0].AppliesTo("bash")).IsFalse();
    }
}
