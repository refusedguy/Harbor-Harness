using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tui;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     /model — switch model.
/// </summary>
/// <remarks>
///     PROD-UI-0 З.3: after persisting the new model the command REBINDS the
///     active session (desktop SessionManager.RebindFromCommonConfigAsync
///     pattern): fresh session record + AgentDefinition.WithModel +
///     IAgent.Initialize — no REPL restart. The agent loop resolves the LLM
///     client from the bound definition on every turn, so the next prompt
///     goes through the new provider/model.
/// </remarks>
public sealed class ModelCommand : ISlashCommand
{

    private readonly IConfigStore _configStore;
    private readonly IProviderRegistry _providers;
    private readonly Action<string> _writer;
    private readonly IAgent? _agent;
    private readonly Session? _session;

    public ModelCommand(IConfigStore configStore, IProviderRegistry providers, Action<string> writer)
        : this(configStore, providers, writer, null, null)
    {
    }

    public ModelCommand(
        IConfigStore configStore,
        IProviderRegistry providers,
        Action<string> writer,
        IAgent? agent,
        Session? session)
    {
        _configStore = configStore;
        _providers = providers;
        _writer = writer;
        _agent = agent;
        _session = session;
    }
    public string Name => "model";
    public string Description => "Switch LLM model";
    public string Usage => "/model <provider/model> | /model list [provider]";
    public IReadOnlyList<string> Aliases => new[] { "m" };
    public IReadOnlyList<string>? ArgSuggestions => null;

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
                    return pidResult.ConvertFailure();
                }
                var clientResult = _providers.GetClient(pidResult.Value);
                if (clientResult.IsFailure)
                {
                    _writer(clientResult.Error);
                    return clientResult.ConvertFailure();
                }
                var modelsResult = await clientResult.Value.GetModelsAsync(ct).ConfigureAwait(false);
                if (modelsResult.IsFailure)
                {
                    _writer(modelsResult.Error);
                    return modelsResult.ConvertFailure();
                }
                _writer($"Models for {providerId}:");
                foreach (var m in modelsResult.Value)
                {
                    _writer($"  {m.Id,-50} {m.DisplayName}");
                }
            }
            return Result.Success();
        }

        string rawInput = string.Join(' ', args).Trim();
        var registeredProviders = _providers.GetRegisteredProviderIds();

        string resolvedProviderId;
        string modelId;

        int firstSlash = rawInput.IndexOf('/');
        if (firstSlash > 0)
        {
            string candidateProvider = rawInput[..firstSlash];
            if (registeredProviders.Any(p => p.Value.Equals(candidateProvider, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedProviderId = candidateProvider;
                modelId = rawInput[(firstSlash + 1)..];
            }
            else
            {
                // Unregistered prefix: it is either a bare model id containing
                // a slash (e.g. tencent/hy3:free under kilocode) or an explicit
                // provider/model pair. The cached catalog disambiguates without
                // network; otherwise trust the slash (PROD-UI-0 З.3 tests pin it).
                var loadResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
                string effective = loadResult.IsSuccess ? loadResult.Value.EffectiveProvider : IdentityConfig.FallbackProvider;
                if (await IsKnownModelAsync(effective, rawInput, ct).ConfigureAwait(false))
                {
                    resolvedProviderId = effective;
                    modelId = rawInput;
                }
                else
                {
                    resolvedProviderId = candidateProvider;
                    modelId = rawInput[(firstSlash + 1)..];
                }
            }
        }
        else
        {
            var loadResult = await _configStore.LoadAsync(ct).ConfigureAwait(false);
            resolvedProviderId = loadResult.IsSuccess ? loadResult.Value.EffectiveProvider : IdentityConfig.FallbackProvider;
            modelId = rawInput;
        }

        string canonicalModel = $"{resolvedProviderId}/{modelId}";

        var updateResult = await _configStore.UpdateAsync(c =>
        {
            c.Provider = resolvedProviderId;
            c.Model = canonicalModel;
            return c;
        }, ct).ConfigureAwait(false);

        if (updateResult.IsFailure)
        {
            _writer($"✗ Failed: {updateResult.Error}");
            return updateResult;
        }

        _writer($"✓ Switched to model: {canonicalModel}");
        var rebindResult = await RebindActiveSessionAsync(canonicalModel).ConfigureAwait(false);
        return rebindResult;
    }

    /// <summary>
    ///     Rebind the running agent to the freshly selected provider/model
    ///     (PROD-UI-0 З.3). Best-effort: when the command was constructed
    ///     without agent/session context (e.g. non-REPL usage) the config is
    ///     still updated and takes effect on the next REPL start.
    /// </summary>
    private async Task<bool> IsKnownModelAsync(string providerId, string modelId, CancellationToken ct)
    {
        var pid = ProviderId.TryCreate(providerId);
        if (pid.IsFailure)
        {
            return false;
        }

        var cached = await _providers.GetModelsCachedAsync(pid.Value, ct).ConfigureAwait(false);
        if (cached.IsFailure)
        {
            return false;
        }

        foreach (var m in cached.Value)
        {
            if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    private async Task<Result> RebindActiveSessionAsync(string model)
    {
        if (_agent is null || _session is null)
        {
            _writer("  (no active session — new model applies on next start)");
            return Result.Success();
        }

        // Canonical models are always well-formed "provider/model" by
        // construction (see above): split unconditionally. The registry gate
        // used to live here and broke rebinds for unregistered prefixes.
        string providerId;
        string modelId;

        int firstSlash = model.IndexOf('/');
        if (firstSlash > 0)
        {
            providerId = model[..firstSlash];
            modelId = model[(firstSlash + 1)..];
        }
        else
        {
            var loadResult = await _configStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            providerId = loadResult.IsSuccess ? loadResult.Value.EffectiveProvider : IdentityConfig.FallbackProvider;
            modelId = model;
        }

        // #559: absence is Maybe, so "the agent is not initialized" is a shape the
        // compiler keeps us honest about instead of a `?.` that silently yields a
        // null we then have to re-test. HasNoValue, not an empty property pattern:
        // a struct is never "null", so `{ }` would match the Maybe itself.
        Maybe<AgentState> bound = _agent.State;
        if (bound.HasNoValue)
        {
            _writer("⚠ Agent is not initialized — cannot rebind, restart the REPL.");
            return Result.Success();
        }

        AgentDefinition currentDef = bound.Value.Agent;
        var reboundSession = _session with { ProviderId = providerId, Model = modelId };
        _agent.Initialize(reboundSession, currentDef.WithModel(modelId, providerId));
        _writer($"✓ Active session rebound to {providerId}/{modelId} (no restart needed).");
        return Result.Success();
    }
}
