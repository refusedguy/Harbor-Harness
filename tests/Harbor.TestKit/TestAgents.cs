using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>Factory methods for common test agent definitions and tool contexts (P.6 canonical fixtures).</summary>
public static class TestAgents
{
    /// <summary>Agent with a wildcard Allow ruleset for unrestricted test execution.</summary>
    public static AgentDefinition AllowAll(string model = "test-model", string provider = "test") => new(
        AgentName.Create("code"),
        "Code",
        "Test agent with allow-all permissions.",
        model,
        provider,
        new PermissionRuleset([new PermissionRule("*", "*", PermissionAction.Allow)]));

    /// <summary>Wraps <see cref="AgentDefinition.CodeDefault" /> for test convenience.</summary>
    public static AgentDefinition CodeDefault(string model, string provider) => AgentDefinition.CodeDefault(model, provider);

    /// <summary>Pre-built <see cref="ToolContext" /> with an allow-all permission callback (replaces per-test CreateContext).</summary>
    public static ToolContext CreateContext() => new(
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
