using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

public static class TestAgents
{
    public static AgentDefinition AllowAll(string model = "test-model", string provider = "test", string name = "code") =>
        new AgentDefinition(
            AgentName.Create(name),
            "Code",
            "Allow-all test agent",
            model,
            provider,
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    public static AgentDefinition CodeDefault(string model, string provider) =>
        AgentDefinition.CodeDefault(model, provider);

    public static AgentDefinition WithPermission(
        string model = "test-model",
        string provider = "test",
        string name = "code",
        PermissionRuleset? ruleset = null)
    {
        ruleset ??= new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) });
        return new AgentDefinition(
            AgentName.Create(name),
            "Code",
            "Test agent",
            model,
            provider,
            ruleset);
    }

    public static ToolContext CreateToolContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}
