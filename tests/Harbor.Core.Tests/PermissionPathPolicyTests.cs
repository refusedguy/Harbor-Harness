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
}
