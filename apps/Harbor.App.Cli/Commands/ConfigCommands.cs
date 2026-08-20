using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tui;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
namespace Harbor.Cli.Commands;
/// <summary>
///     /setup — run onboarding wizard.
/// </summary>
public sealed class SetupCommand(OnboardingWizard wizard, Func<string, Task<string>> reader, Action<string> writer) : ISlashCommand
{
    public string Name => "setup";
    public string Description => "Run setup wizard (provider, API key, model)";
    public string Usage => "/setup";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        var result = await wizard.RunAsync(reader, writer, ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
///     /auth — manage API keys.
/// </summary>
public sealed class AuthCommand : ISlashCommand
{

    private readonly AuthStore _authStore;
    private readonly Action<string> _writer;

    public AuthCommand(AuthStore authStore, Action<string> writer)
    {
        _authStore = authStore;
        _writer = writer;
    }
    public string Name => "auth";
    public string Description => "Manage API keys (set, list, reset)";
    public string Usage => "/auth set <provider> <key> | /auth list | /auth reset <provider>";
    public IReadOnlyList<string> Aliases => new[] { "key", "api-key" };

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        if (args.Count == 0)
        {
            _writer("Usage:");
            _writer("  /auth set <provider> <key>   Set API key for provider");
            _writer("  /auth list                   List configured providers");
            _writer("  /auth reset <provider>       Remove API key");
            _writer("");
            _writer("Available provider presets:");
            foreach (var p in ProviderPresets.All)
            {
                string auth = p.RequiresApiKey ? "🔑" : "🔧";
                _writer($"  {auth} {p.Id,-15} {p.DisplayName}");
            }
            return Result.Success();
        }

        string subcommand = args[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "set":
                if (args.Count < 3)
                {
                    _writer("Usage: /auth set <provider> <key>");
                    return Result.Success();
                }
                string providerId = args[1];
                string key = args[2];
                var setResult = await _authStore.SetApiKeyAsync(providerId, key, ct).ConfigureAwait(false);
                if (setResult.IsSuccess)
                    _writer($"✓ API key saved for {providerId}");
                else
                    _writer($"✗ Failed: {setResult.Error}");
                return setResult;

            case "list":
                var listResult = await _authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
                if (listResult.IsSuccess)
                {
                    _writer("Configured API keys:");
                    foreach (var kv in listResult.Value)
                    {
                        string status = kv.Value ? "✓ set" : "✗ empty";
                        _writer($"  {kv.Key,-15} {status}");
                    }
                }
                return Result.Success();

            case "reset" or "remove" or "delete":
                if (args.Count < 2)
                {
                    _writer("Usage: /auth reset <provider>");
                    return Result.Success();
                }
                var resetResult = await _authStore.RemoveApiKeyAsync(args[1], ct).ConfigureAwait(false);
                if (resetResult.IsSuccess)
                    _writer($"✓ API key removed for {args[1]}");
                else
                    _writer($"✗ Failed: {resetResult.Error}");
                return resetResult;

            default:
                _writer($"Unknown subcommand: {subcommand}. Use /auth for help.");
                return Result.Success();
        }
    }
}

/// <summary>
///     /model — switch model.
/// </summary>
public sealed class ModelCommand : ISlashCommand
{

    private readonly IConfigStore _configStore;
    private readonly IProviderRegistry _providers;
    private readonly Action<string> _writer;

    public ModelCommand(IConfigStore configStore, IProviderRegistry providers, Action<string> writer)
    {
        _configStore = configStore;
        _providers = providers;
        _writer = writer;
    }
    public string Name => "model";
    public string Description => "Switch LLM model";
    public string Usage => "/model <provider/model> | /model list [provider]";
    public IReadOnlyList<string> Aliases => new[] { "m" };

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            string? providerId = args.Count > 1 ? args[1] : null;
            if (providerId is null)
            {
                var allResult = await _providers.GetAllModelsAsync(ct).ConfigureAwait(false);
                if (allResult.IsFailure)
                {
                    _writer($"Error: {allResult.Error}");
                    return allResult;
                }
                _writer($"All available models ({allResult.Value.Count}):");
                foreach (var g in allResult.Value.GroupBy(m => m.ProviderId))
                {
                    _writer("");
                    _writer($"{g.Key}:");
                    foreach (var m in g)
                    {
                        _writer($"  {m.Id,-50} {m.DisplayName}");
                    }
                }
            }
            else
            {
                var pidResult = ProviderId.TryCreate(providerId);
                if (pidResult.IsFailure)
                {
                    _writer(pidResult.Error);
                    return Result.Failure(pidResult.Error);
                }
                var clientResult = _providers.GetClient(pidResult.Value);
                if (clientResult.IsFailure)
                {
                    _writer(clientResult.Error);
                    return Result.Failure(clientResult.Error);
                }
                var modelsResult = await clientResult.Value.GetModelsAsync(ct).ConfigureAwait(false);
                if (modelsResult.IsFailure)
                {
                    _writer(modelsResult.Error);
                    return Result.Failure(modelsResult.Error);
                }
                _writer($"Models for {providerId}:");
                foreach (var m in modelsResult.Value)
                {
                    _writer($"  {m.Id,-50} {m.DisplayName}");
                }
            }
            return Result.Success();
        }

        // Set model
        string model = string.Join(' ', args);
        var updateResult = await _configStore.UpdateAsync(c =>
        {
            c.Model = model;
            if (model.Contains('/'))
            {
                c.Provider = model.Split('/')[0];
            }
            return c;
        }, ct).ConfigureAwait(false);

        if (updateResult.IsSuccess)
            _writer($"✓ Switched to model: {model}");
        else
            _writer($"✗ Failed: {updateResult.Error}");

        return updateResult;
    }
}

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

/// <summary>
///     /config — show or edit config.
/// </summary>
public sealed class ConfigCommand : ISlashCommand
{

    private readonly IConfigStore _configStore;
    private readonly Action<string> _writer;

    public ConfigCommand(IConfigStore configStore, Action<string> writer)
    {
        _configStore = configStore;
        _writer = writer;
    }
    public string Name => "config";
    public string Description => "Show or edit configuration";
    public string Usage => "/config | /config set <key> <value>";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        var loadResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure)
        {
            _writer($"Error: {loadResult.Error}");
            return loadResult;
        }
        var config = loadResult.Value;

        if (args.Count == 0)
        {
            _writer("Current configuration:");
            _writer($"  Provider:  {config.Provider}");
            _writer($"  Model:     {config.Model}");
            _writer($"  Agent:     {config.Agent}");
            _writer($"  TUI:       {config.Tui}");
            _writer($"  Storage:   {config.Storage}");
            _writer($"  Onboarded: {config.Onboarded}");
            _writer($"  MaxSteps:  {config.MaxSteps}");
            _writer($"  CostLimit: ${config.CostLimit}");
            _writer($"  ApiKeys:   {config.ApiKeys.Count} configured");
            _writer($"  Plugins:   {config.EnabledPlugins.Count} enabled");
            return Result.Success();
        }

        if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase) && args.Count >= 3)
        {
            string key = args[1];
            string value = string.Join(' ', args.Skip(2));

            var updateResult = await _configStore.UpdateAsync(c =>
            {
                switch (key.ToLowerInvariant())
                {
                    case "provider": c.Provider = value; break;
                    case "model": c.Model = value; break;
                    case "agent": c.Agent = value; break;
                    case "tui": c.Tui = value; break;
                    case "storage": c.Storage = value; break;
                    case "maxsteps":
                        if (int.TryParse(value, out int ms)) c.MaxSteps = ms;
                        break;
                    case "costlimit":
                        if (decimal.TryParse(value, out decimal cl)) c.CostLimit = cl;
                        break;
                    default: _writer($"Unknown config key: {key}"); break;
                }
                return c;
            }, ct).ConfigureAwait(false);

            if (updateResult.IsSuccess)
                _writer($"✓ {key} = {value}");
            return updateResult;
        }

        if (args[0].Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            _writer($"Config file: {JsonConfigStore.GetDefaultPath()}");
            return Result.Success();
        }

        _writer("Usage:");
        _writer("  /config               Show current config");
        _writer("  /config set <k> <v>   Set config value");
        _writer("  /config path          Show config file path");
        return Result.Success();
    }
}
