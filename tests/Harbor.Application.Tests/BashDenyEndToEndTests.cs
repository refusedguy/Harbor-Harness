using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Tests.Fakes;
using Harbor.Application.Permissions;
using Harbor.Application.Sessions;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     A4 (sprint 5): bash-deny END-TO-END. The BashArgMatcher unit tests
///     prove glob/argv semantics in isolation, and the real execution path
///     (ToolDispatcher → PermissionService.CheckAsync → ruleset.Evaluate →
///     tool.ExecuteAsync) was never exercised against a deny rule. This suite
///     links the two halves: a REAL <see cref="PermissionService" /> with a
///     REAL agent ruleset decides, and a REAL <see cref="BashTool" /> either
///     runs the command or is refused — proven by filesystem side effects.
/// </summary>
[NotInParallel]
public class BashDenyEndToEndTests
{
    private static AgentDefinition AgentWith(params PermissionRule[] extra)
    {
        var rules = new List<PermissionRule> { new("bash", "*", PermissionAction.Allow) };
        rules.AddRange(extra);
        return new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "bash-deny e2e",
            "test-model",
            "test",
            new PermissionRuleset(rules));
    }

    private static (PermissionService Permissions, BashTool Tool) Build(AgentDefinition agent)
    {
        var agents = new FakeAgentRegistry(agent);
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
        return (permissions, new BashTool(NullLogger<BashTool>.Instance));
    }

    private static JsonElement Args(string command) =>
        JsonDocument.Parse($"{{\"command\":\"{command}\"}}").RootElement.Clone();

    /// <summary>Mirror of ToolDispatcher's decision gate: check → refuse-or-execute.</summary>
    private static async Task<ToolResult> DispatchLike(
        PermissionService permissions,
        BashTool tool,
        string command)
    {
        var decision = await permissions.CheckAsync(
            "code", "bash", Args(command)).ConfigureAwait(false);

        if (decision.IsSuccess && decision.Value.Action == PermissionAction.Deny)
        {
            return ToolResult.Error("Permission denied");
        }

        return await tool.ExecuteAsync(Args(command), DefaultCtx()).ConfigureAwait(false);
    }

    private static ToolContext DefaultCtx() => new(
        "e2e-deny-session",
        "m1",
        "call-1",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    [Test]
    public async Task DenyRule_BlocksRealExecution_NoSideEffect()
    {
        string probe = Path.Combine(Path.GetTempPath(), $"deny-probe-{Guid.NewGuid():N}.txt");
        try
        {
            var agent = AgentWith(new PermissionRule("bash", "rm *", PermissionAction.Deny));
            var (permissions, tool) = Build(agent);

            var result = await DispatchLike(
                permissions, tool, $"rm -rf {probe}").ConfigureAwait(false);

            // The matcher-derived deny fired BEFORE any process spawned.
            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Output).Contains("Permission denied");
            await Assert.That(File.Exists(probe)).IsFalse();
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    [Test]
    public async Task DestructiveCommand_IsDenied_EvenWithAllowAllRules()
    {
        string probe = Path.Combine(Path.GetTempPath(), $"destruct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probe);
        try
        {
            var agent = AgentWith(); // allow "bash *"
            var (permissions, tool) = Build(agent);

            var result = await DispatchLike(
                permissions, tool, $"cd /tmp && rm -fr {probe}").ConfigureAwait(false);

            // IsDestructiveCommand catches flag-swapped compound deletions
            // before rule walking — the directory must still exist.
            await Assert.That(result.IsError).IsTrue();
            await Assert.That(Directory.Exists(probe)).IsTrue();
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }

    [Test]
    public async Task MetacharacterCommand_UnderAllowOnly_EscalatesToDeny()
    {
        var agent = AgentWith();
        var (permissions, tool) = Build(agent);

        // `cat f; rm -rf ~` style: allow-globs never match metachar-bearing
        // commands; with no userAsker wired, the Ask escalation fails closed.
        var result = await DispatchLike(
            permissions, tool, "cat notes.txt; rm -rf /tmp/x").ConfigureAwait(false);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("Permission denied");
    }

    [Test]
    public async Task AllowPath_ExecutesRealProcess_WithOutput()
    {
        string probe = Path.Combine(Path.GetTempPath(), $"allow-probe-{Guid.NewGuid():N}.txt");
        try
        {
            var agent = AgentWith();
            var (permissions, tool) = Build(agent);

            var result = await DispatchLike(
                permissions, tool,
                $"touch {probe}").ConfigureAwait(false);

            // Positive control: the allow path REALLY executes a process.
            // (No shell metacharacters here — `>` would escalate to Ask.)
            await Assert.That(result.IsError).IsFalse();
            await Assert.That(File.Exists(probe)).IsTrue();
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }
}
