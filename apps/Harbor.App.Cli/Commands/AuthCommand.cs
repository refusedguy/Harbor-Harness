using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

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
    public IReadOnlyList<string>? ArgSuggestions => null;

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
                if (listResult.IsFailure)
                {
                    // #603: no failure arm here meant an unreadable key store
                    // printed nothing and reported success, so `/auth list` (and
                    // `harbor auth list`, which maps the Result to an exit code)
                    // both claimed there were no keys. Matches the `set`/`reset`
                    // arms above.
                    _writer($"✗ Failed: {listResult.Error}");
                    return listResult;
                }

                _writer("Configured API keys:");
                foreach (var kv in listResult.Value)
                {
                    string status = kv.Value ? "✓ set" : "✗ empty";
                    _writer($"  {kv.Key,-15} {status}");
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
