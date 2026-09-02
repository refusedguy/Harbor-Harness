using System.Threading.Channels;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Application.Agents;

/// <summary>
///     Real sub-agent execution: spawns an isolated <see cref="Harbor.Abstractions.Models.Session" />
///     for the requested <see cref="AgentDefinition" />, drives <see cref="IAgentLoop.RunAsync" />
///     to completion with the parent's prompt as the only user turn, and returns the final
///     assistant text as a <see cref="SubAgentRunResult" /> (G4 follow-up: replaces the old
///     "not implemented" stub path in <c>TaskTool</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>Isolation.</b> The sub-run gets its own session record
///         (<c>ParentSessionId</c> points back at the caller's session), own message
///         history, own steering channel, and the sub-agent definition's permission set.
///         Nothing the sub-run writes lands in the parent's history — only the final
///         assistant text crosses back as tool output.
///     </para>
///     <para>
///         <b>Nesting guard.</b> A running sub-agent MUST NOT invoke <c>task</c> again.
///         Enforcement uses an <see cref="AsyncLocal{T}" /> depth counter, which flows down
///         the entire async call tree of the sub-run (loop → dispatcher → tools): any nested
///         <see cref="RunAsync" /> observes a non-zero depth and fails fast instead of
///         recursing (which, without this guard, could recurse unboundedly since the sub
///         registry still exposes the shared <c>task</c> tool).
///     </para>
/// </remarks>
public sealed class SubAgentRunner(
    ISessionStore store,
    IAgentLoop loop,
    ILogger<SubAgentRunner> logger) : ISubAgentRunner
{
    /// <summary>
    ///     Cap on how much of the sub-run's final answer is returned to the parent.
    ///     Everything beyond the cap is cut so one chatty sub-agent cannot blow the
    ///     parent's context window in a single tool result.
    /// </summary>
    internal const int MaxOutputChars = 32_000;

    /// <summary>Title prefix marker stored on spawned sessions.</summary>
    private const string TitlePrefix = "task";

    private static readonly AsyncLocal<int> Depth = new();

    /// <inheritdoc />
    public bool CanSpawn => Depth.Value == 0;

    /// <inheritdoc />
    public async Task<Result<SubAgentRunResult>> RunAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(request);

        if (!agent.IsSubAgent)
            return Result.Failure<SubAgentRunResult>(
                $"Agent '{agent.Name.Value}' is not a sub-agent (IsSubAgent=false).");
        if (!CanSpawn)
            return Result.Failure<SubAgentRunResult>(
                "Nesting limit reached: sub-agents cannot invoke 'task'. Finish your work with the available tools.");

        Depth.Value++;
        try
        {
            return await RunCoreAsync(agent, request, ct).ConfigureAwait(false);
        }
        finally
        {
            Depth.Value--;
        }
    }

    private async Task<Result<SubAgentRunResult>> RunCoreAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct)
    {
        var directory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Environment.CurrentDirectory
            : request.WorkingDirectory!;
        var title = BuildTitle(agent, request.Prompt);

        var created = await store.CreateAsync(directory, agent.Name.Value, agent.ProviderId, agent.Model, ct)
            .ConfigureAwait(false);
        if (created.IsFailure) // §4.6-ok: single rail-step; store error already diagnostic.
            return Result.Failure<SubAgentRunResult>(
                $"Failed to create sub-agent session: {created.Error}");
        var session = created.Value with { ParentSessionId = request.ParentSessionId, Title = title };

        // Best-effort metadata write per the F14 policy: the run itself does not
        // depend on it, divergence only loses parent linkage and title in listings.
        var linked = await store.UpdateAsync(session, ct).ConfigureAwait(false);
        if (linked.IsFailure)
            logger.LogWarning("Failed to persist sub-session metadata {SessionId}: {Error}", session.Id, linked.Error);

        logger.LogInformation(
            "Sub-agent starting: agent={Agent} session={SessionId} parent={ParentSessionId}",
            agent.Name.Value, session.Id, request.ParentSessionId ?? "-");

        var messages = await store.GetMessagesAsync(session.Id, ct).ConfigureAwait(false);
        if (messages.IsFailure) // §4.6-ok: fresh session should always be readable; treat total store breakage as terminal.
            return Result.Failure<SubAgentRunResult>(
                $"Failed to load sub-agent session '{session.Id}': {messages.Error}");

        // Fresh unbounded steering channel mirrors DefaultAgent's shape. Nothing will
        // ever steer a sub-run — no external handle to it is published anywhere.
        var steering = Channel.CreateUnbounded<Harbor.Abstractions.Models.AgentMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var context = new DefaultSessionContext(session, messages.Value, store, steering);

        // Persist the parent's task as the only user turn of this isolated session
        // (mirrors DefaultAgent.PromptAsync, F14: memory + store in one step).
        var userMessage = new UserMessage(
            Guid.NewGuid().ToString("N"),
            session.Id,
            DateTimeOffset.UtcNow,
            request.Prompt,
            agent.Name.Value,
            agent.Model);
        await context.AppendMessageAsync(userMessage, ct).ConfigureAwait(false);

        var run = await loop.RunAsync(context, agent, ct).ConfigureAwait(false);
        if (run.IsFailure)
        {
            logger.LogWarning(
                "Sub-agent run ended abnormally: agent={Agent} session={SessionId} error={Error}",
                agent.Name.Value, session.Id, run.Error);
            return Result.Failure<SubAgentRunResult>(
                $"Sub-agent '{agent.Name.Value}' failed: {run.Error}. Its partial history is preserved in session {session.Id}.");
        }

        var history = await store.GetMessagesAsync(session.Id, ct).ConfigureAwait(false);
        if (history.IsFailure || history.Value.Count == 0) // §4.6-ok: divergent read-back is a storage bug, surfaced verbatim.
            return Result.Failure<SubAgentRunResult>(
                $"Sub-agent '{agent.Name.Value}' finished but produced no readable history (session {session.Id}).");

        var finalOutput = ExtractFinalOutput(history.Value);
        if (string.IsNullOrWhiteSpace(finalOutput))
            return Result.Failure<SubAgentRunResult>(
                $"Sub-agent '{agent.Name.Value}' finished without producing a final assistant message (session {session.Id}).");

        logger.LogInformation(
            "Sub-agent finished: agent={Agent} session={SessionId} messages={Count} outputChars={Length}",
            agent.Name.Value, session.Id, history.Value.Count, finalOutput.Length);

        return new SubAgentRunResult(session.Id, agent.Name.Value, Truncate(finalOutput), history.Value.Count);
    }

    /// <summary>
    ///     Walk the history backwards and concatenate the text parts of the LAST
    ///     assistant message that carries any prose. Thinking blocks and tool-call
    ///     payloads are ignored — parents receive the answer, not the chatter.
    /// </summary>
    private static string ExtractFinalOutput(IReadOnlyList<Harbor.Abstractions.Models.AgentMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not AssistantMessage assistant)
                continue;

            string text = ConcatTextParts(assistant.Parts);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static string ConcatTextParts(IReadOnlyList<ContentPart> parts)
    {
        // Fast path: exactly one text part (the overwhelmingly common case).
        if (parts.Count == 1 && parts[0] is TextPart single)
            return single.Text;

        string? joined = null;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not TextPart part)
                continue;
            joined = joined is null ? part.Text : $"{joined}\n{part.Text}";
        }

        return joined ?? string.Empty;
    }

    private static string BuildTitle(AgentDefinition agent, string prompt)
    {
        int newline = prompt.IndexOf('\n');
        string firstLine = (newline >= 0 ? prompt[..newline] : prompt).Trim();
        if (firstLine.Length > 60)
            firstLine = firstLine[..60] + "…";
        return $"{TitlePrefix}({agent.Name.Value}): {firstLine}";
    }

    private static string Truncate(string output)
    {
        if (output.Length <= MaxOutputChars)
            return output;
        return output[..MaxOutputChars] + $"\n…[truncated {output.Length - MaxOutputChars} chars]";
    }
}

/// <summary>
///     Forwarding holder used by eager host composition. The tool registries are built
///     BEFORE <see cref="ISessionStore" />/<see cref="IAgentLoop" /> singletons exist, so
///     <c>RegistriesModule</c> hands <c>TaskTool</c> this forwarder and attaches the real
///     <see cref="SubAgentRunner" /> into DI right after. Detached state fails honestly
///     instead of NRE-ing (ROP: no silent fake success — G4 contract).
/// </summary>
public sealed class DeferredSubAgentRunner : ISubAgentRunner
{
    private volatile ISubAgentRunner? _inner;

    /// <summary>Wire in the real runner once the container can build it. One-shot, idempotent-after-attach semantics are NOT required (host composes once).</summary>
    public void Attach(ISubAgentRunner inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public bool CanSpawn => _inner?.CanSpawn ?? false;

    /// <inheritdoc />
    public Task<Result<SubAgentRunResult>> RunAsync(
        AgentDefinition agent,
        SubAgentRunRequest request,
        CancellationToken ct = default)
    {
        var current = _inner;
        return current is not null
            ? current.RunAsync(agent, request, ct)
            : Task.FromResult(Result.Failure<SubAgentRunResult>(
                "Sub-agent runtime is not initialized yet (host composition incomplete)."));
    }
}
