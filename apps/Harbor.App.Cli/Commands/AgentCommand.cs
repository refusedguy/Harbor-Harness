using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     /agent — switch agent (mode).
/// </summary>
/// <remarks>
///     PX5 slice 1 (issue #1251): the switch is a real switch, not a config
///     write for the next restart. After persisting the name it rebinds the
///     live agent (the <c>/model</c> pattern: fresh session record +
///     <see cref="IAgent.Initialize" />), validates the name against
///     <see cref="IAgentRegistry" /> instead of persisting a typo, and reports
///     the switch through <c>onAgentSwitched</c> so the host can move the
///     status-bar indicator. The no-args branch still projects the set from
///     the registry (#582) — it names nothing itself.
/// </remarks>
public sealed class AgentCommand : ISlashCommand
{
    private readonly IAgentRegistry _agents;

    private readonly IConfigStore _configStore;
    private readonly Action<string> _writer;
    private readonly IAgent? _agent;
    private readonly Session? _session;
    private readonly Action<string>? _onAgentSwitched;

    public AgentCommand(IConfigStore configStore, IAgentRegistry agents, Action<string> writer)
        : this(configStore, agents, writer, null, null, null)
    {
    }

    public AgentCommand(
        IConfigStore configStore,
        IAgentRegistry agents,
        Action<string> writer,
        IAgent? agent,
        Session? session,
        Action<string>? onAgentSwitched)
    {
        _configStore = configStore;
        _agents = agents;
        _writer = writer;
        _agent = agent;
        _session = session;
        _onAgentSwitched = onAgentSwitched;
    }
    public string Name => "agent";

    /// <summary>
    ///     Deliberately does not enumerate the agent names (#582). This string is
    ///     what <c>/help</c> prints, and it used to spell the set out — a hand-written
    ///     copy of the agent registry that a newly registered builtin agent would miss
    ///     while <c>ExecuteAsync</c>, three lines below, listed the real set from
    ///     <see cref="IAgentRegistry" />. Run <c>/agent</c> with no arguments to see
    ///     the agents; run <c>/help</c> to see that the command exists.
    /// </summary>
    public string Description => "Switch agent (mode) — run with no arguments to list them";
    public string Usage => "/agent <name>";
    public IReadOnlyList<string> Aliases => new[] { "mode", "a" };
    public IReadOnlyList<string>? ArgSuggestions => null;

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            _writer("Available agents:");
            foreach (var a in _agents.GetAllAgents())
            {
                _writer($"  {a.Name.Value,-15} {a.Description}");
            }
            return Result.Success();
        }

        string name = args[0];
        var parsed = AgentName.TryCreate(name);
        if (parsed.IsFailure)
        {
            _writer($"Invalid agent name: {parsed.Error}");
            return parsed.ConvertFailure();
        }

        var definition = _agents.GetAgent(parsed.Value);
        if (definition.IsFailure)
        {
            string known = string.Join(
                ", ",
                _agents.GetAllAgents().Select(a => a.Name.Value).Order(StringComparer.Ordinal));
            _writer($"Unknown agent '{name}'. Known agents: {known}");
            return definition.ConvertFailure();
        }

        AgentDefinition agentDef = definition.Value;
        string resolved = agentDef.Name.Value;
        var updateResult = await _configStore.UpdateAsync(c =>
        {
            c.Agent = resolved;
            return c;
        }, ct).ConfigureAwait(false);

        if (updateResult.IsFailure)
        {
            _writer($"✗ Failed: {updateResult.Error}");
            return updateResult;
        }

        if (_agent is not null && _session is not null)
        {
            _agent.Initialize(_session with { Agent = resolved }, agentDef);
            _onAgentSwitched?.Invoke(resolved);
        }

        _writer($"✓ Switched to agent: {resolved}");
        return Result.Success();
    }
}
