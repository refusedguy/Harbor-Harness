using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     /permissions — view and edit persisted permission overrides.
/// </summary>
public sealed class PermissionsCommand : ISlashCommand
{
    private readonly IPermissionService _permissions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IConfigStore _configStore;
    private readonly Action<string> _writer;
    private readonly IAgent? _agent;
    private readonly Session? _session;

    public PermissionsCommand(
        IPermissionService permissions,
        IAgentRegistry agentRegistry,
        IConfigStore configStore,
        Action<string> writer)
        : this(permissions, agentRegistry, configStore, writer, null, null)
    {
    }

    public PermissionsCommand(
        IPermissionService permissions,
        IAgentRegistry agentRegistry,
        IConfigStore configStore,
        Action<string> writer,
        IAgent? agent,
        Session? session)
    {
        _permissions = permissions;
        _agentRegistry = agentRegistry;
        _configStore = configStore;
        _writer = writer;
        _agent = agent;
        _session = session;
    }

    public string Name => "permissions";
    public string Description => "View and edit permission overrides";
    public string Usage => "/permissions | /permissions <tool> <pattern> <allow|deny|ask> | /permissions clear";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public IReadOnlyList<string>? ArgSuggestions => null;

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        string agentName = _agent?.State?.Agent.Name.Value ?? context.Session.Session.Agent;

        if (args.Count == 0)
        {
            return await ListRules(agentName).ConfigureAwait(false);
        }

        if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            return await ClearRules(agentName, ct).ConfigureAwait(false);
        }

        if (args.Count >= 3)
        {
            return await SetRule(agentName, args[0], args[1], args[2], ct).ConfigureAwait(false);
        }

        _writer("Usage:");
        _writer("  /permissions                     List current rules");
        _writer("  /permissions <tool> <pattern> <allow|deny|ask>   Add/update rule");
        _writer("  /permissions clear               Clear all persisted rules");
        return Result.Success();
    }

    private async Task<Result> ListRules(string agentName)
    {
        var ruleset = _permissions.GetRuleset(agentName);
        _writer($"Permissions for agent '{agentName}':");
        if (ruleset.Rules.Count == 0)
        {
            _writer("  (no rules — everything asks by default)");
            return Result.Success();
        }
        foreach (var rule in ruleset.Rules)
        {
            _writer($"  {rule.Action,-6} {rule.Permission,-20} {rule.Pattern}");
        }
        return Result.Success();
    }

    private async Task<Result> SetRule(string agentName, string tool, string pattern, string actionStr, CancellationToken ct)
    {
        if (!Enum.TryParse<PermissionAction>(actionStr, true, out var action))
        {
            _writer($"Error: unknown action '{actionStr}'. Use allow, deny, or ask.");
            return Result.Failure($"Unknown action: {actionStr}");
        }

        var rule = new PermissionRule(tool, pattern, action);
        var ruleset = _permissions.GetRuleset(agentName);
        var merged = ruleset.Merge(new PermissionRuleset([rule]));

        _writer($"✓ Set permission: {tool} {pattern} → {action}");
        _writer($"  (effective ruleset for '{agentName}' now has {merged.Rules.Count} rule(s))");

        var saveResult = await _permissions.SaveAsync(ct).ConfigureAwait(false);
        if (saveResult.IsFailure)
        {
            _writer($"⚠ Failed to persist: {saveResult.Error}");
        }
        return saveResult;
    }

    private async Task<Result> ClearRules(string agentName, CancellationToken ct)
    {
        var saveResult = await _permissions.SaveAsync(ct).ConfigureAwait(false);
        if (saveResult.IsSuccess)
        {
            _writer($"✓ Cleared all persisted permission rules for '{agentName}'.");
        }
        return saveResult;
    }
}
