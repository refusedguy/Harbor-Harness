using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Ui.Framework.Configuration;
using Harbor.Ui.Framework.Forking;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.Ui.Framework.Sessions;
/// <summary>
///     Creates <see cref="Session" /> objects with the correct provider/model
///     resolved from <see cref="ICommonConfigModelRefReader" /> (with HARBOR_MODEL
///     env-var override). Owns the agent-definition resolution + provider/model
///     split logic so the <see cref="SessionManager" /> facade stays slim.
/// </summary>
/// <remarks>
///     <para>
///         <b>Per-session UiStore:</b> the factory no longer touches the
///         UiStore directly — store binding + history replay is done by
///         <see cref="SessionSwitcher.OpenAsync" /> on the per-session
///         UiStore owned by <see cref="SessionContext" />. The factory just
///         creates the session record — forking is the core's <c>SessionForkService</c>, reached
///         through the <see cref="ISessionForker" /> port (#670), not a second implementation here.
///     </para>
///     <para>
///         <b>No service locator (#470):</b> the optional
///         <see cref="ICommonConfigModelRefReader" /> is a declared constructor
///         parameter resolved once by the composition root — not an
///         <c>IServiceProvider</c> field re-queried on every session
///         creation. See <see cref="ResolveProviderModelFromConfigAsync" />.
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
    private readonly IAgent _agent;
    private readonly IAgentRegistry _agents;
    private readonly ICommonConfigModelRefReader? _configReader;
    private readonly ISessionForker _forker;
    private readonly ILogger<SessionFactory> _logger;
    private readonly ISessionStore _sessionStore;

    /// <summary>Construct a <see cref="SessionFactory" />.</summary>
    /// <param name="agents">Registry the agent definition is resolved from.</param>
    /// <param name="agent">The agent instance new sessions are created around.</param>
    /// <param name="sessionStore">Persistence each created session is written to.</param>
    /// <param name="forker">
    ///     The core fork, reached through the <see cref="ISessionForker" /> port. Required, not
    ///     optional: #670's copy was only reachable because this constructor had no such
    ///     parameter to pass. Making it required means a host that constructs this factory by
    ///     hand must name the one implementation.
    ///     <para>
    ///         #882 narrowed what that buys, and the narrowing is worth stating here rather than
    ///         only in a guard's remarks. A required parameter is checked at <c>new</c> sites, and
    ///         the shipped host does not use one — it registers
    ///         <c>services.AddSingleton&lt;SessionFactory&gt;()</c>, which is a registration, not a
    ///         construction, so no compiler sees it and the container is not validated at build
    ///         time. It also does not stop a second implementer of the port, nor a stub bound in
    ///         its place: both compile, both wire, and both are the silent second fork this
    ///         parameter was introduced to prevent. What actually holds the seam is
    ///         <c>SessionForkPortSeamRules</c> (#882), which counts the port's production
    ///         implementers and requires the composition root to bind it. Keep this parameter
    ///         required — that guard also checks it, so the two agree.
    ///     </para>
    /// </param>
    /// <param name="logger">Diagnostics sink for the create/branch paths.</param>
    /// <param name="configReader">
    ///     Reads the persisted provider/model. Declared rather than looked up, so
    ///     the dependency is visible in the signature and resolved once instead of
    ///     per call. <see langword="null" /> means "this host registered no config
    ///     reader" — sessions then fall back to the agent definition's own
    ///     provider/model. The read-only half of the shared-config contract pair;
    ///     its writable counterpart is
    ///     <c>Harbor.Desktop.Abstractions.ICommonConfigStore</c> (#453).
    /// </param>
    public SessionFactory(
        IAgentRegistry agents,
        IAgent agent,
        ISessionStore sessionStore,
        ISessionForker forker,
        ILogger<SessionFactory> logger,
        ICommonConfigModelRefReader? configReader = null)
    {
        _agents = agents;
        _agent = agent;
        _sessionStore = sessionStore;
        _forker = forker;
        _logger = logger;
        _configReader = configReader;
    }

    /// <summary>
    ///     Load the fresh common-config from disk and read the provider/model
    ///     choice as ONE reference.
    /// </summary>
    /// <returns>
    ///     The reference the config names, or <c>Maybe.None</c> when it names no
    ///     usable pair — no reader registered, no config written yet, a config
    ///     that could not be read, or one naming a provider/model that cannot be
    ///     qualified.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         #598: this used to return <c>(string? ProviderId, string? ModelId)</c>.
    ///         A two-element tuple of nullable strings makes THREE states spellable
    ///         — both present, both absent, and exactly one present — and the type
    ///         could tell none of them apart. The three callers then answered the
    ///         half case differently:
    ///         <see cref="ResolveAgentDefinitionAsync" /> coalesced per element
    ///         (<c>providerId ?? configProvider ?? agentDef.ProviderId</c>), while
    ///         <see cref="CreateDefaultAsync" /> and
    ///         <c>SessionLifecycleService.RebindFromCommonConfigAsync</c> both
    ///         discarded the whole pair — once with <c>&amp;&amp;</c>, once with
    ///         <c>||</c>. Only a hand-written normalisation in this body kept them
    ///         agreeing, and nothing stopped a second producer from forgetting it.
    ///     </para>
    ///     <para>
    ///         #453: there is now nothing to normalise HERE. This method used to
    ///         unpack the pair and re-run <see cref="ModelRef.Qualify" /> itself,
    ///         because the seam's carrier was
    ///         <c>(string? ProviderId, string? ModelId)?</c> — two raw strings that
    ///         this body then had to turn back into a reference. The producing end
    ///         carries <see cref="ModelRef" /> now, so the qualification happens once,
    ///         at the producer, and the two guards about it — the type here and
    ///         <c>CommonConfigContractRules</c> on the seam — are statements about
    ///         the same object rather than about two representations of it.
    ///     </para>
    ///     <para>
    ///         There is no half to represent. <c>CommonConfigReaderAdapter</c>
    ///         answers <c>None</c> when the config names nothing
    ///         <see cref="ModelRef.Qualify" /> accepts, so the domain's single void
    ///         is "no usable pair", and the signature says so instead of leaving the
    ///         caller to re-derive it.
    ///     </para>
    ///     <para>
    ///         Not a <c>Result</c>. "Nothing is configured yet" is the normal state
    ///         of every install before onboarding, with no error message to report.
    ///         The pre-fix body is the proof: it built a <c>Result</c> from
    ///         <see cref="ModelRef.Qualify" /> and threw it away on the next line
    ///         via <c>GetValueOrDefault((null, null))</c>.
    ///     </para>
    /// </remarks>
    public async Task<Maybe<ModelRef>> ResolveProviderModelFromConfigAsync()
    {
        // #63 legitimate: optional dependency — hosts without a common-config
        // reader (tests, minimal embeds) get None instead of a throw.
        // #470: resolved once by the composition root, not looked up per call.
        var configReader = _configReader;
        if (configReader is null) return Maybe<ModelRef>.None;

        // #678: (provider, model) is ONE reference, and ModelRef is the only type
        // in the repo written to read one. Qualify now runs at the producer
        // (CommonConfigReaderAdapter), which covers both shapes the config can
        // hold: a bare model id ("tencent/hy3:free" — what OnboardingViewModel
        // writes) and a redundant prefix for the same provider
        // ("kilocode/tencent/hy3:free" — what HARBOR_MODEL and the settings
        // screen write), and normalizes the provider half. This method used to
        // build `prefix = provider + "/"` and strip it with StartsWith, handing the
        // RAW provider string to a running Session. An unusable config arrives
        // here as None rather than as a half-filled pair.
        return await configReader.ReadModelRefAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolve an <see cref="AgentDefinition" /> from the registry with
    ///     optional name/provider/model overrides. Falls back to
    ///     <see cref="ResolveDefaultAgentDefinition" /> when the named agent doesn't
    ///     exist, and to the same definition when no name was given at all.
    /// </summary>
    /// <param name="agentName">Optional agent name override; <c>null</c> means the fallback agent.</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <returns>The resolved <see cref="AgentDefinition" />.</returns>
    public async Task<AgentDefinition> ResolveAgentDefinitionAsync(string? agentName, string? providerId, string? modelId)
    {
        // #596: this was the same lookup as ResolveAgentForSession, inlined a third time.
        var agentDef = ResolveAgentForSession(agentName);

        // #598: the config contributes BOTH halves or NEITHER, so the override
        // parameters are the only genuinely per-field input here. They used to be
        // coalesced against two independently-nullable config halves, which read
        // as "a half pair is a valid thing to mix with the agent default".
        Maybe<ModelRef> configured = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (configured.HasValue)
        {
            providerId ??= configured.Value.ProviderId.Value;
            modelId ??= configured.Value.ModelId;
        }

        return agentDef.WithModel(modelId ?? agentDef.Model, providerId ?? agentDef.ProviderId);
    }

    /// <summary>
    ///     The one place that answers "which agent is the default?" — the agent named by
    ///     <see cref="AgentName.Fallback" />, or the first registered one if the host
    ///     registers no such agent.
    /// </summary>
    /// <remarks>
    ///     #683: this question was answered in three places, and one of them did not
    ///     answer it — <c>CreateDefaultAsync</c> took <c>GetAllAgents().FirstOrDefault()</c>
    ///     and named nothing. That agreed with the other two only by coincidence, and not
    ///     even a stable one: <c>AgentRegistry</c> is backed by a
    ///     <c>ConcurrentDictionary</c>, so "first" is the bucket layout, not the
    ///     registration order. <c>SessionLifecycleService.RebindFromCommonConfigAsync</c>
    ///     now calls this instead of running its own lookup, so the default session, the
    ///     named-override path, and the config rebind cannot drift apart again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No agents are registered at all. That is a composition-root bug — a host wired
    ///     without an agent registry — not a runtime condition a caller could handle, so
    ///     it is not dressed up as a <c>Result</c> failure.
    /// </exception>
    public AgentDefinition ResolveDefaultAgentDefinition()
        => _agents.GetAllAgents().FirstOrDefault(a => a.Name.Value == AgentName.Fallback)
           ?? _agents.GetAllAgents().FirstOrDefault()
           ?? throw new InvalidOperationException("No agents registered.");

    /// <summary>
    ///     The one place that answers "this session names agent <paramref name="agentName" />;
    ///     what does it open on?" — the named agent when this host registers it, and
    ///     <see cref="ResolveDefaultAgentDefinition" /> when it does not.
    /// </summary>
    /// <param name="agentName">
    ///     The agent name recorded on the session. Blank is treated as absent, not as a
    ///     name to look up: <see cref="AgentName.Create" /> rejects blank, and a session row
    ///     with an empty agent column should open on the default rather than throw.
    /// </param>
    /// <returns>
    ///     The named agent's definition, or the default one. A session recorded against an
    ///     agent this host does not register — renamed, removed, or copied from another
    ///     machine — still opens; it just opens on the agent the policy names rather than
    ///     on whichever entry the registry happened to enumerate first.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     No agents are registered at all. Same composition-root condition, and the same
    ///     message, as <see cref="ResolveDefaultAgentDefinition" />.
    /// </exception>
    /// <remarks>
    ///     #596: this was hand-rolled at each call site, and the copies had already
    ///     diverged — <c>SessionSwitcher.OpenAsync</c> fell back to <c>First()</c>, so it
    ///     disagreed with <c>SessionLifecycleService.OpenSessionAsync</c> (which #683 had
    ///     already moved onto the default) and raised
    ///     <c>"Sequence contains no elements"</c> where the rest of the slice raises
    ///     <c>"No agents registered."</c>. One question, one answer, one diagnostic.
    /// </remarks>
    public AgentDefinition ResolveAgentForSession(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return ResolveDefaultAgentDefinition();
        }

        // An unknown name is not an exceptional condition here — it is precisely the case
        // this method exists to answer — so it is asked as a question, not thrown from.
        // Read through the Result, not through a null.
        Result<AgentDefinition> named = _agents.GetAgent(AgentName.Create(agentName));
        return named.IsSuccess
            ? named.Value
            : ResolveDefaultAgentDefinition();
    }

    /// <summary>
    ///     Create the default session if none exists yet. Reads the fresh
    ///     <see cref="CommonConfig" /> from disk so the wizard's saved
    ///     provider/model take effect even though the DI singleton was
    ///     loaded before the wizard ran. Does NOT bind the agent or UiStore
    ///     — that's <see cref="SessionSwitcher.OpenAsync" />'s job, called
    ///     by <see cref="SessionManager.EnsureDefaultSessionAsync" /> /
    ///     <see cref="SessionManager.OpenSessionAsync" />.
    /// </summary>
    /// <returns>The created session, or a failure carrying the store error.</returns>
    public async Task<Result<Session>> CreateDefaultAsync()
    {
        // #683: this is the site that made the drift visible. It used to take
        // `GetAllAgents().FirstOrDefault()` — no agent named at all — so the default
        // session was built around whichever entry a ConcurrentDictionary yielded,
        // not the agent the policy names. Now it asks the same question everyone else
        // asks, in the one place the question is answered.
        var agentDef = ResolveDefaultAgentDefinition();

        // Override the agent definition with the fresh CommonConfig values.
        // #598: the `&& IsNullOrEmpty` guard was the caller re-deriving what the
        // value now states — a reference is whole, so there is no half to test.
        Maybe<ModelRef> configured = await ResolveProviderModelFromConfigAsync().ConfigureAwait(false);
        if (configured.HasValue)
        {
            agentDef = agentDef.WithModel(configured.Value.ModelId, configured.Value.ProviderId.Value);
        }

        string directory = Environment.CurrentDirectory;
        var createResult = await _sessionStore.CreateAsync(
            directory, agentDef.Name.Value, agentDef.ProviderId, agentDef.Model).ConfigureAwait(false);
        if (createResult.IsFailure)
        {
            _logger.LogError("Failed to create default session: {Error}", createResult.Error);
            return createResult.MapError(static e => $"Failed to create default session: {e}");
        }

        var session = createResult.Value;
        _logger.LogInformation("Default session created: {Id} ({Title}) dir={Dir} provider={Provider} model={Model}",
            session.Id, session.Title, session.Directory, agentDef.ProviderId, agentDef.Model);
        return Result.Success(session);
    }

    /// <summary>
    ///     Create a new session with the given agent/model overrides. Does
    ///     NOT bind the agent or UiStore — see <see cref="CreateDefaultAsync" />.
    /// </summary>
    /// <param name="agentName">Optional agent name override.</param>
    /// <param name="providerId">Optional provider id override.</param>
    /// <param name="modelId">Optional model id override.</param>
    /// <param name="workingDirectory">Optional working directory for the session.</param>
    /// <returns>The new session, or a failure carrying the store error.</returns>
    public async Task<Result<Session>> CreateNewAsync(
        string? agentName = null,
        string? providerId = null,
        string? modelId = null,
        string? workingDirectory = null)
    {
        var agentDef = await ResolveAgentDefinitionAsync(agentName, providerId, modelId).ConfigureAwait(false);
        string provider = agentDef.ProviderId;
        string model = agentDef.Model;
        string directory = workingDirectory ?? Environment.CurrentDirectory;

        var result = await _sessionStore.CreateAsync(
            directory, agentName ?? agentDef.Name.Value, provider, model).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogError("Create session failed: {Error}", result.Error);
            return result.MapError(static e => $"Failed to create session: {e}");
        }

        var session = result.Value;
        _logger.LogInformation("New session: {Id} ({Title})", session.Id, session.Title);
        return Result.Success(session);
    }

    /// <summary>
    ///     Branch a session — the UI framework's entry into the core's one fork, reached through
    ///     the <see cref="ISessionForker" /> port. The caller is responsible for switching to the
    ///     branch.
    /// </summary>
    /// <param name="source">The session to branch from. Its <see cref="Session.Id" /> selects the parent; the store row is authoritative for everything else.</param>
    /// <returns>The branched session, or a failure carrying the store error.</returns>
    /// <remarks>
    ///     <para>
    ///         #670: this body used to fork the session itself — create a child, stamp a title
    ///         with <c>with { Title = … }</c> and copy the transcript — and the copy had drifted
    ///         from <c>SessionForkService</c> until a UI fork was not recognisable as one. It set
    ///         no <see cref="Session.ParentSessionId" />, so the child rendered as an unrelated
    ///         root in the session tree; it applied a title it never wrote, so the store kept the
    ///         <c>Session {timestamp}</c> default while <see cref="SessionLifecycleService.BranchActiveAsync" />
    ///         returned the other one to a success toast; and it regenerated every copied message
    ///         id, so no id named in the parent could be named in the child.
    ///     </para>
    ///     <para>
    ///         The port cannot drift that way — there is one fork left, in the core. What stays
    ///         here is what belongs to the Presentation layer: which session failed, at what
    ///         point, and what the copy had managed before it broke. The store's own text is
    ///         carried through untouched, because "which session" and "why" are both diagnosis.
    ///     </para>
    ///     <para>
    ///         <b>The parent is re-read from the store</b>, where the old body trusted the
    ///         caller's in-memory record for Directory/Agent/ProviderId/Model. Forking a session
    ///         that was never persisted now fails, which is the honest answer: there is no parent
    ///         to branch from.
    ///     </para>
    /// </remarks>
    public async Task<Result<Session>> CreateBranchAsync(Session source)
    {
        Result<SessionForked> forked = await _forker
            .ForkAsync(source.Id, ct: CancellationToken.None)
            .ConfigureAwait(false);

        if (forked.IsFailure)
        {
            _logger.LogError("Branch session {Id} failed: {Error}", source.Id, forked.Error);

            // The re-type is the one the store's Result<IReadOnlyList<…>>/Result<SessionForked>
            // shape forces, and MapError is what makes the context a function of `e` so a later
            // edit cannot drop the cause. The store said which step broke and how far the copy
            // got; the session id is the half only this layer knows.
            return forked
                .ConvertFailure<Session>()
                .MapError(e => $"Failed to branch session '{source.Id}': {e}");
        }

        Session branch = forked.Value.Session;
        _logger.LogInformation(
            "Branched session {Old} → {New} ({Copied} messages)", source.Id, branch.Id, forked.Value.Copied);
        return Result.Success(branch);
    }

    /// <summary>Convert an <see cref="AgentMessage" /> into a chat-line role + text for the UI store.</summary>
    public static (ChatRole role, string text) MessageToChatLine(AgentMessage msg)
    {
        return msg switch
        {
            UserMessage u => (ChatRole.User, u.Content),
            AssistantMessage a => (ChatRole.Assistant,
                string.Join(string.Empty, a.Parts.OfType<TextPart>().Select(p => p.Text))),
            ToolResultMessage t => (ChatRole.ToolResult,
                string.Join("\n", t.Results.Select(r => $"[{r.ToolName}] {r.Output}"))),
            _ => (ChatRole.System, msg.Role)
        };
    }
}
