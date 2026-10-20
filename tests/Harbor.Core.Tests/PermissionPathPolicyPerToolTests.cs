using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.Registries.Agents;

namespace Harbor.Core.Tests;

/// <summary>
///     Per-tool allow/ask/deny pins for the <see cref="IPathExtractionPolicy" />
///     registry (issue #178): every tool moved out of the former tool-name
///     <c>switch</c> keeps identical semantics through polymorphic dispatch.
///     Additive only — existing permission tests are untouched.
/// </summary>
public class PermissionPathPolicyPerToolTests
{
    private static PermissionService CreateService(
        AgentDefinition agent,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? asker = null)
    {
        var registry = new AgentRegistry();
        registry.Register(agent);
        return new PermissionService(registry, NullLogger<PermissionService>.Instance, asker);
    }

    [Arguments("read")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("ls")]
    [Arguments("patch")]
    [Arguments("tree")]
    [Arguments("ripgrep")]
    [Arguments("notebook")]
    [Arguments("mcp")]
    [Test]
    public async Task PathTool_AnchoredAllow_AllowsInsidePath(string tool)
    {
        var agent = AgentFixtures.AgentWithRuleset(
            new PermissionRule(tool, "safe/*", PermissionAction.Allow),
            new PermissionRule(tool, "*", PermissionAction.Deny));
        var svc = CreateService(agent);

        var result = await svc.CheckAsync("code", tool, TestArgs.FromPairs(("path", "safe/file.txt")));

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Arguments("read")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("ls")]
    [Arguments("patch")]
    [Arguments("tree")]
    [Arguments("ripgrep")]
    [Arguments("notebook")]
    [Arguments("mcp")]
    [Test]
    public async Task PathTool_UnmatchedPath_FallsThroughToDeny(string tool)
    {
        var agent = AgentFixtures.AgentWithRuleset(
            new PermissionRule(tool, "safe/*", PermissionAction.Allow),
            new PermissionRule(tool, "*", PermissionAction.Deny));
        var svc = CreateService(agent);

        var result = await svc.CheckAsync("code", tool, TestArgs.FromPairs(("path", "other/file.txt")));

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Arguments("read")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("ls")]
    [Arguments("patch")]
    [Arguments("tree")]
    [Arguments("ripgrep")]
    [Arguments("notebook")]
    [Arguments("mcp")]
    [Test]
    public async Task PathTool_TraversalOutsideWorkspace_DeniesWithoutAsker(string tool)
    {
        var agent = AgentFixtures.AgentWithRuleset(
            new PermissionRule(tool, "safe/*", PermissionAction.Allow),
            new PermissionRule(tool, "*", PermissionAction.Deny));
        var svc = CreateService(agent);

        var result = await svc.CheckAsync("code", tool, TestArgs.FromPairs(("path", "safe/../../etc/passwd")));

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Arguments("read")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("ls")]
    [Arguments("patch")]
    [Arguments("tree")]
    [Arguments("ripgrep")]
    [Arguments("notebook")]
    [Arguments("mcp")]
    [Test]
    public async Task PathTool_AskRule_DelegatesToAsker(string tool)
    {
        var agent = AgentFixtures.AgentWithRuleset(
            new PermissionRule(tool, "*", PermissionAction.Ask));

        PermissionRequest? captured = null;
        Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
        {
            captured = req;
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, true));
        }

        var svc = CreateService(agent, Asker);
        var result = await svc.CheckAsync("code", tool, TestArgs.FromPairs(("path", "safe/file.txt")));

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(result.Value.PersistDecision).IsTrue();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Permission).IsEqualTo(tool);
    }

    [Arguments("read")]
    [Arguments("write")]
    [Arguments("edit")]
    [Arguments("ls")]
    [Arguments("patch")]
    [Arguments("tree")]
    [Arguments("ripgrep")]
    [Arguments("notebook")]
    [Arguments("mcp")]
    [Test]
    public async Task PathTool_MissingPathArg_MatchesWildcard(string tool)
    {
        var agent = AgentFixtures.AgentWithRuleset(
            new PermissionRule(tool, "*", PermissionAction.Allow));
        var svc = CreateService(agent);

        var result = await svc.CheckAsync("code", tool, TestArgs.FromPairs());

        await Assert.That(result).IsSuccessful();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }
}
