using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     /agent — switch agent (mode).
/// </summary>
public sealed class AgentCommand : ISlashCommand
{
    private readonly IAgentRegistry _agents;

    private readonly IConfigStore _configStore;
    private readonly Action<string> _writer;

    public AgentCommand(IConfigStore configStore, IAgentRegistry agents, Action<string> writer)
    {
        _configStore = configStore;
        _agents = agents;
        _writer = writer;
    }
    public string Name => "agent";
    public string Description => "Switch agent (mode): code, plan, explore";
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
        var updateResult = await _configStore.UpdateAsync(c =>
        {
            c.Agent = name;
            return c;
        }, ct).ConfigureAwait(false);

        if (updateResult.IsSuccess)
            _writer($"✓ Switched to agent: {name}");
        else
            _writer($"✗ Failed: {updateResult.Error}");

        return updateResult;
    }
}
