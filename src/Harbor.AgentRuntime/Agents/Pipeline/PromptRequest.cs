using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     One agent run entering the pipeline (audit v2 §3.5): the session context that
///     owns the history plus the agent definition driving the loop. Immutable —
///     behaviors must not mutate it; state flows through <see cref="ISessionContext" />.
/// </summary>
public sealed record PromptRequest(ISessionContext Session, AgentDefinition Agent);
