using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Sessions;

/// <summary>
///     Creates <see cref="Session"/> objects with the correct provider/model
///     resolved from <see cref="ICommonConfigReader"/> (with HARBOR_MODEL
///     env-var override). Owns the agent-definition resolution + provider/model
///     split logic so the <see cref="SessionManager"/> facade stays slim.
/// </summary>
/// <remarks>
///     <para>
///         <b>Per-session UiStore:</b> the factory no longer touches the
///         UiStore directly — store binding + history replay is done by
///         <see cref="SessionSwitcher.OpenAsync"/> on the per-session
///         UiStore owned by <see cref="SessionContext"/>. The factory just
///         creates the session record (and, for branches, copies messages).
///     </para>
///     <para>
///         Registered as a singleton in <c>AppHost</c> so tests can mock
///         session creation (e.g. assert the wizard's provider selection
///         takes effect) without constructing the full SessionManager +
///         dispatcher graph.
///     </para>
/// </remarks>
public sealed class SessionFactory
{
    private readonly IServiceProvider _services;
    private readonly IAgent _agent;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<SessionFactory> _logger;

    /// <summary>Construct a <see cref="SessionFactory"/>.</summary>
    public SessionFactory(
        IServiceProvider services,
        IAgent agent,
        ISessionStore sessionStore,
        ILogger<SessionFactory> logger)
    {
        _services = services;
        _agent = agent;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <summary>
    ///     Load the fresh common-config from disk and split the
    ///     DefaultProvider/DefaultModel into a (providerId, modelId) pair.
    ///     Returns (null, null) if the config can't be loaded.
    /// </summary>
    public async Task<(string? ProviderId, string? ModelId)> ResolveProviderModelFromConfigAsync()
    {
        var configReader = _services.GetService<ICommonConfigReader>();
        if (configReader is null) return (null, null);

        var pair = await configReader.TryReadProviderModelAsync().ConfigureAwait(false);
        if (pair is null) return (null, null);

        var (provider, model) = pair.Value;
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model))
            return (null, null);

        // The model may already start with the provider prefix (e.g. the user
        // typed "kilocode/tencent/hy3:free"). Strip it so we get the bare model id.
        string prefix = provider + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            model = model[prefix.Length..];
        }
        return (provider, model);
    }

    /// <summary>
    ///     Resolve an <see cref="AgentDefinition"/> from the registry with
    ///     optional name/provider/model overrides. Falls back to the first
    ///     registered agent when the named agent doesn't exist.
    /// </summary>
    /// <param name="agentName">Optional agent name override (defaults to "code").</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <returns>The resolved <see cref="AgentDefinition"/>.</returns>
    public AgentDefinition ResolveAgentDefinition(string? agentName, string? providerId, string? modelId)
    {
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == (agentName ?? "code"))
            ?? agents.GetAllAgents().First();

        var (configProvider, configModel) = ResolveProviderModelFromConfigAsync().GetAwaiter().GetResult();
        string provider = providerId ?? configProvider ?? agentDef.ProviderId;
        string model = modelId ?? configModel ?? agentDef.Model;
        return agentDef.WithModel(model, provider);
    }

    /// <summary>
    ///     Create the default session if none exists yet. Reads the fresh
    ///     <see cref="CommonConfig"/> from disk so the wizard's saved
    ///     provider/model take effect even though the DI singleton was
    ///     loaded before the wizard ran. Does NOT bind the agent or UiStore
    ///     — that's <see cref="SessionSwitcher.OpenAsync"/>'s job, called
    ///     by <see cref="SessionManager.EnsureDefaultSessionAsync"/> /
    ///     <see cref="SessionManager.OpenSessionAsync"/>.
    /// </summary>
    /// <returns>The created session, or null on failure.</returns>
    public async Task<Session?> CreateDefaultAsync()
    {
        var agents = _services.GetRequiredService<Harbor.Abstractions.Agents.IAgentRegistry>();
        var agentDef = agents.GetAllAgents().FirstOrDefault()
            ?? throw new InvalidOperationException("No agents registered.");

        // Override the agent definition with the fresh CommonConfig values.
        var (providerId, modelId) = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))
        {
            agentDef = agentDef.WithModel(modelId, providerId);
        }

        string directory = Environment.CurrentDirectory;
        var createResult = await _sessionStore.CreateAsync(
            directory, agentDef.Name.Value, agentDef.ProviderId, agentDef.Model).ConfigureAwait(false);
        if (createResult.IsFailure)
        {
            _logger.LogError("Failed to create default session: {Error}", createResult.Error);
            return null;
        }

        var session = createResult.Value;
        _logger.LogInformation("Default session created: {Id} ({Title}) dir={Dir} provider={Provider} model={Model}",
            session.Id, session.Title, session.Directory, agentDef.ProviderId, agentDef.Model);
        return session;
    }

    /// <summary>
    ///     Create a new session with the given agent/model overrides. Does
    ///     NOT bind the agent or UiStore — see <see cref="CreateDefaultAsync"/>.
    /// </summary>
    /// <param name="agentName">Optional agent name override.</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <param name="workingDirectory">Optional working directory for the session.</param>
    /// <returns>The new session, or null on failure.</returns>
    public async Task<Session?> CreateNewAsync(
        string? agentName = null,
        string? providerId = null,
        string? modelId = null,
        string? workingDirectory = null)
    {
        var agentDef = ResolveAgentDefinition(agentName, providerId, modelId);
        string provider = agentDef.ProviderId;
        string model = agentDef.Model;
        string directory = workingDirectory ?? Environment.CurrentDirectory;

        var result = await _sessionStore.CreateAsync(
            directory, agentName ?? agentDef.Name.Value, provider, model).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Create session failed: {Error}", result.Error);
            return null;
        }

        var session = result.Value;
        _logger.LogInformation("New session: {Id} ({Title})", session.Id, session.Title);
        return session;
    }

    /// <summary>
    ///     Branch a session — create a new session with the same messages and
    ///     metadata but a new id, then re-parent every message to the new id.
    ///     The caller is responsible for switching to the branch.
    /// </summary>
    /// <param name="source">The session to branch from.</param>
    /// <returns>The branched session, or null on failure.</returns>
    public async Task<Session?> CreateBranchAsync(Session source)
    {
        var branchResult = await _sessionStore.CreateAsync(
            source.Directory, source.Agent, source.ProviderId, source.Model).ConfigureAwait(false);
        if (branchResult.IsFailure)
        {
            _logger.LogError("Branch session {Id} failed: {Error}", source.Id, branchResult.Error);
            return null;
        }

        var branch = branchResult.Value with { Title = source.Title + " (branch)" };
        var messagesResult = await _sessionStore.GetMessagesAsync(source.Id).ConfigureAwait(false);
        if (messagesResult.IsSuccess)
        {
            foreach (var msg in messagesResult.Value)
            {
                // Re-parent the message to the new session id and persist it.
                var reborn = msg with { SessionId = branch.Id, Id = Guid.NewGuid().ToString("N") };
                await _sessionStore.AppendMessageAsync(branch.Id, reborn).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Branched session {Old} → {New}", source.Id, branch.Id);
        return branch;
    }

    /// <summary>Convert an <see cref="AgentMessage"/> into a chat-line role + text for the UI store.</summary>
    public static (Harbor.Ui.Framework.State.ChatRole role, string text) MessageToChatLine(AgentMessage msg)
    {
        return msg switch
        {
            Harbor.Abstractions.Models.UserMessage u => (Harbor.Ui.Framework.State.ChatRole.User, u.Content),
            Harbor.Abstractions.Models.AssistantMessage a => (Harbor.Ui.Framework.State.ChatRole.Assistant,
                string.Join(string.Empty, a.Parts.OfType<Harbor.Abstractions.Models.TextPart>().Select(p => p.Text))),
            Harbor.Abstractions.Models.ToolResultMessage t => (Harbor.Ui.Framework.State.ChatRole.ToolResult,
                string.Join("\n", t.Results.Select(r => $"[{r.ToolName}] {r.Output}"))),
            _ => (Harbor.Ui.Framework.State.ChatRole.System, msg.Role)
        };
    }
}
