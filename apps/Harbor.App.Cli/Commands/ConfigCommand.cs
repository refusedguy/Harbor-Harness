using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

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
    public IReadOnlyList<string>? ArgSuggestions => null;

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

            if (updateResult.IsFailure)
            {
                // #603: the success arm was the only one, so a config file that
                // could not be written produced no output and a bare
                // `return updateResult`. The REPL dropped that Result and
                // `ConfigVerb` only sees the exit code — the user was told
                // nothing about why the value did not change.
                _writer($"✗ Failed: {updateResult.Error}");
                return updateResult;
            }

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
