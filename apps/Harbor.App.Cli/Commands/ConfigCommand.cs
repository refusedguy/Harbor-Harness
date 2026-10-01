using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Configuration;
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

            // #709: the value used to be assigned through `HarborConfig`'s
            // property setters — `_ = TrySetModel(value)` — which PARSE and, on
            // failure, discard the value and null the field. The `✓` below was
            // then printed anyway, and the store write succeeded, so a bare
            // model id was reported as written while actually replacing the
            // user's own model with the built-in default.
            //
            // The decision now happens BEFORE the store is touched, and it hands
            // back the mutation rather than performing it: `UpdateAsync`'s
            // updater cannot report a failure, and `HarborConfig.TrySet*` mutate
            // before they report — validating through them inside the updater
            // would apply a refused value as a deletion.
            var decision = ConfigValueSetter.Decide(key, value);
            if (decision.IsFailure)
            {
                // The typed value is the reason the user needs, and it is the
                // reason the write below was never attempted — so it is reported
                // in preference to any store error that would follow it.
                _writer($"✗ {decision.Error}");
                return decision.ConvertFailure();
            }

            // Read the mutation HERE, in the block the guard above already left.
            // Inside the `UpdateAsync` lambda the check and the read sit in
            // different scopes, and CFE0001 — the guard that stops `.Value` on a
            // failed `Result` from throwing §ROP-001's crash into production —
            // cannot see across that boundary. `ModelCommand.cs:87-93` reads its
            // `Result` the same way, in the body and not in a closure.
            Action<HarborConfig> apply = decision.Value;

            var updateResult = await _configStore.UpdateAsync(c =>
            {
                apply(c);
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
        // #709: an unknown key is now a refusal rather than a message followed by
        // a `✓`, which makes the accepted keys part of the command's contract
        // instead of a detail. Derived from the one table, so it cannot go stale.
        _writer($"    keys: {string.Join(", ", ConfigValueSetter.Keys)}");
        _writer("  /config path          Show config file path");
        return Result.Success();
    }
}
