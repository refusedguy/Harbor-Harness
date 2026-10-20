using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Application.Tests;

/// <summary>
///     The agent and message definitions the agent-loop suites share.
/// </summary>
/// <remarks>
///     <para>
///         These three factories were written out again in every file that needed
///         them: eight copies of <c>SubAgent</c>, two of <c>AssistantWithUsage</c>
///         and two of <c>UserMsg</c>, all of them byte-identical. They are here
///         because they are used by more than one file of THIS project and by no
///         other project — the shared kit is the wrong home for a helper nothing
///         outside this project wants.
///     </para>
/// </remarks>
internal static class AgentFixtures
{
    internal static AgentDefinition SubAgent(string name = "explore") => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    internal static AssistantMessage AssistantWithUsage(string text, Usage usage) => new(
        Guid.NewGuid().ToString("N"),
        "child",
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        usage,
        "test-model");

    internal static UserMessage UserMsg(string sessionId, int i) => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        $"question-{i}",
        "user",
        "test-model");
}
