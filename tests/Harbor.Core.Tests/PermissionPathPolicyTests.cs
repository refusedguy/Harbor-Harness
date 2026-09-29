using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     Tests for the <see cref="IPathExtractionPolicy" /> registry (issue #178):
///     a new file-based tool is onboarded by registering a policy — the
///     permission core is never edited. Also pins 1-1 behavioral parity for the
///     builtin tools moved out of the former tool-name <c>switch</c>.
/// </summary>
public class PermissionPathPolicyTests
{
    private static AgentDefinition AgentWithRuleset(params PermissionRule[] rules) => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        new PermissionRuleset(rules));

    private static JsonElement Args(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs)
            dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
    }

    private static PermissionService CreateService(
        AgentDefinition agent,
        IEnumerable<IPathExtractionPolicy>? policies = null)
    {
        var registry = new AgentRegistry();
        registry.Register(agent);
        return new PermissionService(
            registry,
            NullLogger<PermissionService>.Instance,
            pathPolicies: policies);
    }

    [Test]
    public async Task NewFileTool_WithPathPolicy_MatchesAnchoredRules_WithoutCoreChange()
    {
        // "myfilesync" never existed in the permission core: it is onboarded
        // purely by composing a policy, no PermissionService edit involved.
        var agent = AgentWithRuleset(
            new PermissionRule("myfilesync", "safe/*", PermissionAction.Allow),
            new PermissionRule("myfilesync", "*", PermissionAction.Deny));
        var svc = CreateService(agent, DefaultPathExtractionPolicies.WithExtra(
            new PathArgExtractionPolicy(new[] { "myfilesync" })));

        var allowedResult = await svc.CheckAsync("code", "myfilesync", Args(("path", "safe/file.txt")));
        await Assert.That(allowedResult.IsSuccess).IsTrue();
        await Assert.That(allowedResult.Value.Action).IsEqualTo(PermissionAction.Allow);

        var deniedResult = await svc.CheckAsync("code", "myfilesync", Args(("path", "unsafe/file.txt")));
        await Assert.That(deniedResult.IsSuccess).IsTrue();
        await Assert.That(deniedResult.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task NewFileTool_OutsideWorkspace_EscalatesToDeny()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("myfilesync", "*", PermissionAction.Allow));
        var svc = CreateService(agent, DefaultPathExtractionPolicies.WithExtra(
            new PathArgExtractionPolicy(new[] { "myfilesync" })));

        // Absolute path outside the workspace root: Allow downgrades to Ask,
        // and with no asker configured Ask falls back to Deny.
        var result = await svc.CheckAsync("code", "myfilesync", Args(("path", "/definitely-outside-workspace-178/probe.txt")));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task BuiltinTools_ParityWithLegacySwitch()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("read", "safe/*", PermissionAction.Allow),
            new PermissionRule("read", "*", PermissionAction.Deny),
            new PermissionRule("bash", "ls *", PermissionAction.Allow),
            new PermissionRule("bash", "*", PermissionAction.Deny),
            new PermissionRule("task", "*", PermissionAction.Allow));
        var svc = CreateService(agent);

        // Normalized path branch (former switch arm): relative inside → Allow.
        var readOk = await svc.CheckAsync("code", "read", Args(("path", "safe/file.txt")));
        await Assert.That(readOk.Value.Action).IsEqualTo(PermissionAction.Allow);

        // Traversal escapes the workspace → downgraded to Ask → Deny.
        var readTraversal = await svc.CheckAsync("code", "read", Args(("path", "safe/../../etc/passwd")));
        await Assert.That(readTraversal.Value.Action).IsEqualTo(PermissionAction.Deny);

        // Legacy raw branch: bash still matches on the raw command string.
        var bashOk = await svc.CheckAsync("code", "bash", Args(("command", "ls -la /tmp")));
        await Assert.That(bashOk.Value.Action).IsEqualTo(PermissionAction.Allow);

        // Legacy default arm: unknown tools extract "*" and match the wildcard.
        var unknownOk = await svc.CheckAsync("code", "task", Args());
        await Assert.That(unknownOk.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task FirstMatchingPolicy_Wins()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("read", "safe/*", PermissionAction.Allow),
            new PermissionRule("read", "*", PermissionAction.Deny));
        var svc = CreateService(agent, DefaultPathExtractionPolicies.WithExtra(
            new FixedArgPolicy("read", "CUSTOM")));

        // The custom policy is consulted before the builtins, so "read" now
        // extracts "CUSTOM" (matching neither rule) instead of the normalized
        // path that would have been allowed.
        var result = await svc.CheckAsync("code", "read", Args(("path", "safe/file.txt")));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    /// <summary>
    ///     Minimal test-double policy returning a fixed extraction for one tool.
    /// </summary>
    private sealed class FixedArgPolicy(string toolName, string argPath) : IPathExtractionPolicy
    {
        public bool Handles(string name) => name == toolName;

        public PathExtraction Extract(string name, JsonElement args, string workspaceRoot) =>
            new(argPath, false);
    }

    // =====================================================================
    // #595 — the drift, pinned.
    // =====================================================================

    /// <summary>
    ///     <c>PathArgExtractionPolicy</c> used to carry a hand-written name set that
    ///     had fallen behind the declarations beside it: <c>glob</c>, <c>grep</c> and
    ///     <c>lsp</c> all declare <see cref="ToolArgKind.Path" /> and all three were
    ///     missing, so they fell through to the legacy policy, whose extraction
    ///     reports <c>IsOutsideWorkspace == false</c>. The A1/A2 downgrade that turns
    ///     an Allow into an Ask for a path outside the workspace therefore never ran
    ///     for them.
    /// </summary>
    [Test]
    public async Task Every_Path_Declaring_Tool_Is_Normalized_Not_Left_To_The_Legacy_Fallback()
    {
        var missing = BuiltinToolSafetyProfiles.All
            .Where(d => d.Profile.ArgKind == ToolArgKind.Path)
            .Select(d => d.ToolName)
            .Where(name => !PathArgExtractionPolicy.Instance.Handles(name))
            .ToArray();

        await Assert.That(missing).IsEmpty()
            .Because("a tool that declares a path argument and is not claimed by "
                   + "PathArgExtractionPolicy is matched RAW by the legacy fallback, which "
                   + "reports IsOutsideWorkspace == false — so the workspace-confinement "
                   + "downgrade in PermissionService silently does not apply to it (#595).");
    }

    /// <summary>
    ///     The behavioural half of the same defect: an <c>Allow</c>-everything rule
    ///     must still be downgraded to <c>Ask</c> for a path that escapes the
    ///     workspace, for the three tools the drift used to exempt. With no asker
    ///     configured, <c>Ask</c> falls back to <c>Deny</c>.
    /// </summary>
    [Test]
    [Arguments("glob")]
    [Arguments("grep")]
    [Arguments("lsp")]
    public async Task Allow_Everything_Rule_Is_Downgraded_For_A_Path_Outside_The_Workspace(string tool)
    {
        var agent = AgentWithRuleset(new PermissionRule(tool, "*", PermissionAction.Allow));
        var svc = CreateService(agent);

        var outside = await svc.CheckAsync(
            "code", tool, Args(("path", "/definitely-outside-workspace-595/probe.txt")));

        await Assert.That(outside.IsSuccess).IsTrue();
        await Assert.That(outside.Value.Action).IsEqualTo(PermissionAction.Deny)
            .Because($"'{tool}' declares a path argument, so an Allow verdict for a path "
                   + "outside the workspace must be downgraded to Ask (and to Deny with no "
                   + "asker). Before #595 these three tools were matched raw and the "
                   + "downgrade never ran.");

        // A path INSIDE the workspace still matches the rule: the guard must not
        // turn every path call into a denial.
        var inside = await svc.CheckAsync("code", tool, Args(("path", "src/inside.txt")));
        await Assert.That(inside.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    /// <summary>
    ///     The legacy fallback's mapping is now derived from
    ///     <see cref="ToolSafetyProfile.ArgumentName" /> rather than switched by hand.
    ///     It previously read <c>glob</c>/<c>grep</c> arguments as <c>pattern</c>,
    ///     while both declare a <c>path</c>. Reached through the public
    ///     <see cref="IPathExtractionPolicy.Extract" />, which is how the composition
    ///     root reaches it.
    /// </summary>
    [Test]
    public async Task Legacy_Extraction_Uses_The_Declared_Argument_Name()
    {
        IPathExtractionPolicy legacy = LegacyArgExtractionPolicy.Instance;

        foreach (ToolSafetyDeclaration declaration in BuiltinToolSafetyProfiles.All)
        {
            if (declaration.Profile.ArgKind != ToolArgKind.Path)
            {
                continue;
            }

            string argumentName = declaration.Profile.ArgumentName!;
            var args = Args((argumentName, "sentinel"));

            await Assert.That(legacy.Extract(declaration.ToolName, args, "/workspace").ArgPath)
                .IsEqualTo("sentinel")
                .Because($"'{declaration.ToolName}' declares its rule-matched argument as "
                       + $"'{argumentName}', so that is the property the extraction must read "
                       + "(#595)");
        }
    }
}
