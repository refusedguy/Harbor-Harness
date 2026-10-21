using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     A read-only catalog of what THIS runtime offers — the tools it has
///     registered, the agents it can run, the providers it knows, the skills the
///     model may load, the plugin axes and what each route does with them, the
///     versions, and the limits the current agent runs under.
/// </summary>
/// <remarks>
///     <para>
///         The catalog is a PROJECTION, never a second registry: every section
///         reads the registry that owns the fact — <see cref="IToolRegistry" /> for
///         tools, <see cref="IAgentRegistry" /> for agents,
///         <see cref="IProviderRegistry" /> for registered providers,
///         <see cref="ISkillRegistry" /> for skills — so the catalog cannot
///         describe a runtime that is not the one running. The four facts no
///         registry owns (the Harbor version, the plugin manifest schema
///         version, the extension axes and their verdicts, the provider
///         catalogue) arrive as a <see cref="RuntimeApiFacts" /> snapshot the
///         composition root copies out of the product's own declarations.
///     </para>
///     <para>
///         It only reads. Nothing here executes a tool, starts a process, writes
///         a file or mutates a registry: the worst a call can do is spend the
///         tokens of its own answer. The output is sized for a model's context —
///         one line per entry, and the long tail of a section (a full schema, a
///         full reason) only when a single entry is asked for by name.
///     </para>
/// </remarks>
[ToolInfo(
    "harbor_inspect",
    "Inspect",
    "Read-only catalog of what this runtime offers: registered tools, agents, providers, skills, plugin axes, versions and limits. Reads only — it never runs code and never changes anything.",
    Glyph = "🧭",
    PromptSnippet = "harbor_inspect: read-only catalog of the runtime API",
    PromptGuidelines = new[]
    {
        "Call harbor_inspect with no arguments for a one-screen overview of this runtime",
        "Sections: tools, agents, providers, skills, plugins, versions, limits",
        "Add \"name\" for one entry in full, e.g. {\"section\":\"tools\",\"name\":\"read\"}",
        "Use it to find out what exists before guessing — including why a tool is not offered",
    })]
[ToolSchema]
[ToolParam("section", "string", Description = "Which part of the runtime to describe: overview (default), tools, agents, providers, skills, plugins, versions, limits")]
[ToolParam("name", "string", Description = "One entry of that section, by its exact name (tool, agent, provider id, skill, plugin axis). Omit to list the whole section.")]
public sealed partial class InspectTool : ITool
{
    /// <summary>The sections the catalog answers for, in the order the overview lists them.</summary>
    private static readonly string[] SectionNames =
    [
        "overview", "tools", "agents", "providers", "skills", "plugins", "versions", "limits",
    ];

    /// <summary>
    ///     The section vocabulary, as a table rather than a <c>case</c> ladder: the
    ///     section names are read from here by the validator, by the dispatcher and
    ///     by the overview, so there is one list and no second spelling of it.
    /// </summary>
    private static readonly FrozenDictionary<string, Section> SectionByName = FrozenDictionary.ToFrozenDictionary(
        new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = Section.Overview,
            ["tools"] = Section.Tools,
            ["agents"] = Section.Agents,
            ["providers"] = Section.Providers,
            ["skills"] = Section.Skills,
            ["plugins"] = Section.Plugins,
            ["versions"] = Section.Versions,
            ["limits"] = Section.Limits,
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>How many names an "unknown name" answer may list before it truncates.</summary>
    private const int MaxListedNames = 40;

    /// <summary>How long a one-line description may be before it is cut.</summary>
    private const int MaxDescriptionChars = 140;

    private readonly IToolRegistry _tools;
    private readonly IAgentRegistry _agents;
    private readonly IProviderRegistry _providers;
    private readonly ISkillRegistry? _skills;
    private readonly IMcpRegistry? _mcp;
    private readonly RuntimeApiFacts _facts;
    private readonly ILogger<InspectTool> _logger;

    /// <summary>
    ///     Construct the catalog tool.
    /// </summary>
    /// <param name="tools">The live tool registry — the tools it lists are the ones registered right now.</param>
    /// <param name="agents">The live agent registry.</param>
    /// <param name="providers">The live provider registry.</param>
    /// <param name="skills">
    ///     The skill registry; <see langword="null" /> in a host that composes no
    ///     skills, and the skills section then says so instead of pretending to be
    ///     empty.
    /// </param>
    /// <param name="mcp">
    ///     The MCP registry — the place plugin tools arrive in this runtime;
    ///     <see langword="null" /> in a host that composes no MCP bridge.
    /// </param>
    /// <param name="facts">The static facts the registries do not own.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public InspectTool(
        IToolRegistry tools,
        IAgentRegistry agents,
        IProviderRegistry providers,
        ISkillRegistry? skills,
        IMcpRegistry? mcp,
        RuntimeApiFacts facts,
        ILogger<InspectTool> logger)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _skills = skills;
        _mcp = mcp;
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result section = JsonArgValidator.OptionalEnum(
            args,
            "section",
            SectionNames,
            "'section' must be one of: " + string.Join(", ", SectionNames) + ".",
            StringComparison.OrdinalIgnoreCase);
        if (section.IsFailure)
        {
            return section;
        }

        if (args.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            string value = name.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return Result.Failure("'name' must be a non-empty name when given.");
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        string sectionName = "overview";
        if (args.TryGetProperty("section", out var sectionArg) && sectionArg.ValueKind == JsonValueKind.String)
        {
            sectionName = sectionArg.GetString() ?? sectionName;
        }

        string? name = null;
        if (args.TryGetProperty("name", out var nameArg) && nameArg.ValueKind == JsonValueKind.String)
        {
            name = nameArg.GetString();
        }

        if (!SectionByName.TryGetValue(sectionName, out Section section))
        {
            InspectToolLog.UnknownSection(_logger, sectionName);
            return Task.FromResult(ToolResult.Error(
                $"Unknown section '{sectionName}'. Known sections: {string.Join(", ", SectionNames)}."));
        }

        Result<string> catalog = Describe(section, name, context);
        if (catalog.IsFailure)
        {
            InspectToolLog.UnknownEntry(_logger, sectionName, name ?? string.Empty);
            return Task.FromResult(ToolResult.Error(catalog.Error));
        }

        InspectToolLog.Described(_logger, sectionName, name ?? string.Empty);
        return Task.FromResult(ToolResult.Success(catalog.Value));
    }

    /// <summary>
    ///     Build the answer for one section. A failure is the "no such entry"
    ///     answer, and it names what IS there.
    /// </summary>
    private Result<string> Describe(Section section, string? name, ToolContext context) => section switch
    {
        Section.Overview => Result.Success(BuildOverview(context)),
        Section.Tools => BuildTools(name, context),
        Section.Agents => BuildAgents(name),
        Section.Providers => BuildProviders(name),
        Section.Skills => BuildSkills(name, context),
        Section.Plugins => BuildPlugins(name),
        Section.Versions => Result.Success(BuildVersions()),
        Section.Limits => Result.Success(BuildLimits(context)),
        _ => Result.Failure<string>($"Unknown section '{section}'."),
    };

    private string BuildOverview(ToolContext context)
    {
        var sb = new StringBuilder();
        sb.Append("harbor_inspect - runtime catalog\n");
        sb.Append("harbor ").Append(_facts.HarborVersion);
        sb.Append(" | plugin schema ").Append(_facts.PluginSchemaVersion);
        sb.Append(" | agent ").Append(context.Agent);
        AppendModel(sb, context);
        sb.Append('\n');

        IReadOnlyList<ToolDescriptor> tools = _tools.GetAllTools();
        IReadOnlyList<AgentDefinition> agents = _agents.GetAllAgents();
        IReadOnlyList<ProviderId> providers = _providers.GetRegisteredProviderIds();
        IReadOnlyList<SkillDescriptor> skills = ReadSkills(context);

        sb.Append("tools ").Append(tools.Count);
        sb.Append(" | agents ").Append(agents.Count);
        sb.Append(" | providers ").Append(providers.Count).Append(" registered, ").Append(_facts.KnownProviders.Count).Append(" known");
        sb.Append(" | skills ").Append(skills.Count);
        sb.Append(" | plugin axes ").Append(_facts.ExtensionAxes.Count);
        sb.Append('\n');
        sb.Append("sections: ").Append(string.Join(", ", SectionNames)).Append('\n');
        sb.Append("next: {\"section\":\"tools\"} lists a section, {\"section\":\"tools\",\"name\":\"read\"} shows one entry\n");
        return sb.ToString();
    }

    private Result<string> BuildTools(string? name, ToolContext context)
    {
        IReadOnlyList<ToolDescriptor> descriptors = _tools.GetAllTools();
        PermissionRuleset? ruleset = RulesetFor(context.Agent);

        if (name is not null)
        {
            foreach (ToolDescriptor descriptor in descriptors)
            {
                if (string.Equals(descriptor.Name.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildToolDetail(descriptor, ruleset, context.Agent));
                }
            }

            return Result.Failure<string>(
                $"No tool named '{name}'. Registered tools: {Names(descriptors.Select(d => d.Name.Value))}.");
        }

        var sb = new StringBuilder();
        sb.Append("tools: ").Append(descriptors.Count).Append(" registered");
        if (ruleset is not null)
        {
            int callable = 0;
            foreach (ToolDescriptor descriptor in descriptors)
            {
                if (ruleset.Evaluate(descriptor.Name.Value, "*") == PermissionAction.Allow)
                {
                    callable++;
                }
            }

            sb.Append(", ").Append(callable).Append(" callable by agent '").Append(context.Agent).Append('\'');
        }

        sb.Append('\n');
        foreach (ToolDescriptor descriptor in descriptors)
        {
            sb.Append(descriptor.Name.Value);
            sb.Append(" | ").Append(descriptor.DisplayName);
            sb.Append(" | ").Append(Truncate(descriptor.Description, MaxDescriptionChars));
            sb.Append(" | ").Append(descriptor.ExecutionMode == ExecutionMode.Parallel ? "parallel" : "sequential");
            if (ruleset is not null)
            {
                sb.Append(" | ").Append(Verdict(ruleset.Evaluate(descriptor.Name.Value, "*")));
            }

            sb.Append('\n');
        }

        return Result.Success(sb.ToString());
    }

    private string BuildToolDetail(ToolDescriptor descriptor, PermissionRuleset? ruleset, string agent)
    {
        var sb = new StringBuilder();
        sb.Append("tool ").Append(descriptor.Name.Value).Append('\n');
        sb.Append("display: ").Append(descriptor.DisplayName).Append('\n');
        sb.Append("description: ").Append(descriptor.Description).Append('\n');
        sb.Append("execution: ").Append(descriptor.ExecutionMode == ExecutionMode.Parallel ? "parallel" : "sequential").Append('\n');
        if (ruleset is not null)
        {
            sb.Append("permission: ").Append(Verdict(ruleset.Evaluate(descriptor.Name.Value, "*"))).Append(" (agent '").Append(agent).Append("')").Append('\n');
        }

        Result<ITool> resolved = _tools.GetTool(descriptor.Name);
        if (resolved.IsSuccess)
        {
            sb.Append("argument guard: ").Append(GuardName(resolved.Value.SafetyProfile)).Append('\n');
        }

        sb.Append("schema: ").Append(descriptor.Schema.RootElement.GetRawText()).Append('\n');
        if (!string.IsNullOrWhiteSpace(descriptor.PromptSnippet))
        {
            sb.Append("prompt snippet: ").Append(descriptor.PromptSnippet).Append('\n');
        }

        foreach (string guideline in descriptor.PromptGuidelines)
        {
            sb.Append("- ").Append(guideline).Append('\n');
        }

        return sb.ToString();
    }

    private Result<string> BuildAgents(string? name)
    {
        IReadOnlyList<AgentDefinition> agents = _agents.GetAllAgents();

        if (name is not null)
        {
            foreach (AgentDefinition agent in agents)
            {
                if (string.Equals(agent.Name.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildAgentDetail(agent));
                }
            }

            return Result.Failure<string>(
                $"No agent named '{name}'. Registered agents: {Names(agents.Select(a => a.Name.Value))}.");
        }

        var sb = new StringBuilder();
        sb.Append("agents: ").Append(agents.Count).Append(" registered\n");
        foreach (AgentDefinition agent in agents)
        {
            sb.Append(agent.Name.Value);
            sb.Append(" | ").Append(agent.DisplayName);
            sb.Append(" | ").Append(Truncate(agent.Description, MaxDescriptionChars));
            sb.Append(" | model ").Append(agent.ProviderId).Append('/').Append(agent.Model);
            sb.Append(" | steps ").Append(agent.MaxSteps);
            sb.Append(" | sub-agent ").Append(agent.IsSubAgent ? "yes" : "no");
            if (agent.Hidden)
            {
                sb.Append(" | hidden");
            }

            sb.Append('\n');
        }

        return Result.Success(sb.ToString());
    }

    private string BuildAgentDetail(AgentDefinition agent)
    {
        var sb = new StringBuilder();
        sb.Append("agent ").Append(agent.Name.Value).Append('\n');
        sb.Append("display: ").Append(agent.DisplayName).Append('\n');
        sb.Append("description: ").Append(agent.Description).Append('\n');
        sb.Append("model: ").Append(agent.ProviderId).Append('/').Append(agent.Model).Append('\n');
        sb.Append("max steps: ").Append(agent.MaxSteps).Append('\n');
        sb.Append("tool timeout: ").Append(agent.ToolTimeoutSeconds is { } seconds ? seconds.ToString(CultureInfo.InvariantCulture) + "s" : "none").Append('\n');
        sb.Append("budget: ").Append(DescribeBudget(agent.Budget)).Append('\n');
        sb.Append("sub-agent: ").Append(agent.IsSubAgent ? "yes" : "no").Append('\n');
        sb.Append("hidden: ").Append(agent.Hidden ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(agent.SystemPromptAppend))
        {
            sb.Append("prompt append: ").Append(Truncate(agent.SystemPromptAppend, MaxDescriptionChars)).Append('\n');
        }

        return sb.ToString();
    }

    private Result<string> BuildProviders(string? name)
    {
        IReadOnlyList<ProviderId> registered = _providers.GetRegisteredProviderIds();
        IReadOnlyList<RuntimeProviderEntry> known = _facts.KnownProviders;

        if (name is not null)
        {
            foreach (RuntimeProviderEntry entry in known)
            {
                if (string.Equals(entry.Id, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildProviderDetail(entry, IsRegistered(registered, entry.Id)));
                }
            }

            foreach (ProviderId id in registered)
            {
                if (string.Equals(id.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success(BuildProviderDetail(
                        new RuntimeProviderEntry(id.Value, id.Value, string.Empty, false, null),
                        true));
                }
            }

            return Result.Failure<string>(
                $"No provider named '{name}'. Known providers: {Names(known.Select(p => p.Id))}.");
        }

        var sb = new StringBuilder();
        sb.Append("providers: ").Append(known.Count).Append(" known, ").Append(registered.Count).Append(" registered\n");
        foreach (RuntimeProviderEntry entry in known)
        {
            sb.Append(entry.Id);
            sb.Append(" | ").Append(entry.DisplayName);
            if (!string.IsNullOrWhiteSpace(entry.DefaultModel))
            {
                sb.Append(" | default model ").Append(entry.DefaultModel);
            }

            sb.Append(" | ").Append(entry.RequiresApiKey ? "api key required" : "no api key");
            sb.Append(" | ").Append(IsRegistered(registered, entry.Id) ? "registered" : "not registered");
            sb.Append('\n');
        }

        return Result.Success(sb.ToString());
    }

    private string BuildProviderDetail(RuntimeProviderEntry entry, bool registered)
    {
        var sb = new StringBuilder();
        sb.Append("provider ").Append(entry.Id).Append('\n');
        sb.Append("display: ").Append(entry.DisplayName).Append('\n');
        if (!string.IsNullOrWhiteSpace(entry.DefaultModel))
        {
            sb.Append("default model: ").Append(entry.DefaultModel).Append('\n');
        }

        sb.Append("credential: ").Append(entry.RequiresApiKey ? "an api key is required" : "none required").Append('\n');
        sb.Append("registered: ").Append(registered ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(entry.SetupHint))
        {
            sb.Append("setup: ").Append(entry.SetupHint).Append('\n');
        }

        return sb.ToString();
    }

    private Result<string> BuildSkills(string? name, ToolContext context)
    {
        if (_skills is null)
        {
            return Result.Failure<string>("No skill registry is wired into this host; there is no skill catalog to read.");
        }

        IReadOnlyList<SkillDescriptor> skills = ReadSkills(context);

        if (name is not null)
        {
            foreach (SkillDescriptor skill in skills)
            {
                if (string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    var detail = new StringBuilder();
                    detail.Append("skill ").Append(skill.Name).Append('\n');
                    detail.Append("description: ").Append(skill.Description).Append('\n');
                    detail.Append("model may invoke: ").Append(skill.ModelInvocable ? "yes" : "no").Append('\n');
                    detail.Append("human may invoke: ").Append(skill.UserInvocable ? "yes" : "no").Append('\n');
                    return Result.Success(detail.ToString());
                }
            }

            return Result.Failure<string>(
                $"No skill named '{name}'. Available skills: {Names(skills.Select(s => s.Name))}.");
        }

        var sb = new StringBuilder();
        sb.Append("skills: ").Append(skills.Count).Append(" available to the model\n");
        foreach (SkillDescriptor skill in skills)
        {
            sb.Append(skill.Name);
            sb.Append(" | ").Append(Truncate(skill.Description, MaxDescriptionChars));
            sb.Append(" | ").Append(skill.ModelInvocable ? "model may invoke" : "human only");
            sb.Append('\n');
        }

        return Result.Success(sb.ToString());
    }

    private Result<string> BuildPlugins(string? name)
    {
        IReadOnlyList<RuntimeExtensionAxis> axes = _facts.ExtensionAxes;
        IReadOnlyList<string> servers = _mcp?.GetServerNames() ?? Array.Empty<string>();

        if (name is not null)
        {
            foreach (RuntimeExtensionAxis axis in axes)
            {
                if (string.Equals(axis.Marker, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(axis.Door, name, StringComparison.OrdinalIgnoreCase))
                {
                    var detail = new StringBuilder();
                    detail.Append("axis ").Append(axis.Marker).Append(" -> ").Append(axis.Door).Append('\n');
                    detail.Append("route: ").Append(axis.Route).Append('\n');
                    detail.Append("carriage: ").Append(axis.Carriage).Append('\n');
                    detail.Append("reason: ").Append(axis.Reason).Append('\n');
                    return Result.Success(detail.ToString());
                }
            }

            foreach (string server in servers)
            {
                if (string.Equals(server, name, StringComparison.OrdinalIgnoreCase))
                {
                    var detail = new StringBuilder();
                    detail.Append("mcp server ").Append(server).Append('\n');
                    detail.Append("its tools are reached through the 'mcp' tool, not as first-class tools\n");
                    return Result.Success(detail.ToString());
                }
            }

            return Result.Failure<string>(
                $"No plugin axis or mcp server named '{name}'. Known axes: {Names(axes.Select(a => a.Marker))}.");
        }

        var sb = new StringBuilder();
        sb.Append("plugin axes: ").Append(axes.Count).Append('\n');
        foreach (RuntimeExtensionAxis axis in axes)
        {
            sb.Append(axis.Marker).Append(" -> ").Append(axis.Door);
            sb.Append(" | ").Append(axis.Carriage);
            sb.Append(" | ").Append(axis.Reason);
            sb.Append('\n');
        }

        sb.Append("mcp servers: ").Append(servers.Count == 0 ? "(none)" : string.Join(", ", servers)).Append('\n');
        sb.Append("plugin tools arrive as mcp tools; the 'mcp' tool is the bridge\n");
        return Result.Success(sb.ToString());
    }

    private string BuildVersions()
    {
        var sb = new StringBuilder();
        sb.Append("versions\n");
        sb.Append("harbor: ").Append(_facts.HarborVersion).Append(" (the floor a plugin's required version is checked against)\n");
        sb.Append("plugin manifest schema: ").Append(_facts.PluginSchemaVersion).Append('\n');
        return sb.ToString();
    }

    private string BuildLimits(ToolContext context)
    {
        var sb = new StringBuilder();
        sb.Append("limits for agent '").Append(context.Agent).Append("'\n");

        AgentDefinition? agent = FindAgent(context.Agent);
        if (agent is not null)
        {
            sb.Append("max steps: ").Append(agent.MaxSteps).Append('\n');
            sb.Append("tool timeout: ").Append(agent.ToolTimeoutSeconds is { } seconds ? seconds.ToString(CultureInfo.InvariantCulture) + "s" : "none").Append('\n');
            sb.Append("budget: ").Append(DescribeBudget(agent.Budget)).Append('\n');
        }

        IReadOnlyList<ToolDescriptor> descriptors = _tools.GetAllTools();
        PermissionRuleset? ruleset = RulesetFor(context.Agent);
        if (ruleset is not null)
        {
            int allow = 0, ask = 0, deny = 0;
            foreach (ToolDescriptor descriptor in descriptors)
            {
                switch (ruleset.Evaluate(descriptor.Name.Value, "*"))
                {
                    case PermissionAction.Allow:
                        allow++;
                        break;
                    case PermissionAction.Ask:
                        ask++;
                        break;
                    default:
                        deny++;
                        break;
                }
            }

            sb.Append("tool permissions: ").Append(allow).Append(" allow, ").Append(ask).Append(" ask, ").Append(deny)
                .Append(" deny (of ").Append(descriptors.Count).Append(")\n");
        }

        int path = 0, command = 0, opaque = 0;
        foreach (ToolDescriptor descriptor in descriptors)
        {
            Result<ITool> resolved = _tools.GetTool(descriptor.Name);
            if (!resolved.IsSuccess)
            {
                continue;
            }

            switch (resolved.Value.SafetyProfile.ArgKind)
            {
                case ToolArgKind.Path:
                    path++;
                    break;
                case ToolArgKind.Command:
                    command++;
                    break;
                default:
                    opaque++;
                    break;
            }
        }

        sb.Append("argument guards: ").Append(path).Append(" path, ").Append(command).Append(" command, ").Append(opaque).Append(" unguarded\n");
        return sb.ToString();
    }

    /// <summary>
    ///     The ruleset of the agent the call runs under, or <see langword="null" />
    ///     when the agent is not registered — the tool then reports the registry
    ///     without a verdict rather than inventing one.
    /// </summary>
    private PermissionRuleset? RulesetFor(string agentName)
    {
        AgentDefinition? agent = FindAgent(agentName);
        return agent?.Permission;
    }

    private AgentDefinition? FindAgent(string agentName)
    {
        Result<AgentName> name = AgentName.TryCreate(agentName);
        if (name.IsFailure)
        {
            return null;
        }

        Result<AgentDefinition> agent = _agents.GetAgent(name.Value);
        return agent.IsSuccess ? agent.Value : null;
    }

    /// <summary>
    ///     The skills the model may load right now. A registry that cannot be
    ///     read is an empty list, not an exception: the catalog is a convenience,
    ///     and a broken skills root must not fail the whole call.
    /// </summary>
    private IReadOnlyList<SkillDescriptor> ReadSkills(ToolContext context)
    {
        if (_skills is null)
        {
            return Array.Empty<SkillDescriptor>();
        }

        string workingDirectory = context.WorkingDirectory ?? Environment.CurrentDirectory;
        try
        {
            return _skills.GetSkills(workingDirectory, SkillCaller.Model, context.Agent);
        }
        catch (IOException)
        {
            return Array.Empty<SkillDescriptor>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SkillDescriptor>();
        }
    }

    private void AppendModel(StringBuilder sb, ToolContext context)
    {
        AgentDefinition? agent = FindAgent(context.Agent);
        if (agent is null)
        {
            return;
        }

        sb.Append(" | model ").Append(agent.ProviderId).Append('/').Append(agent.Model);
    }

    private static bool IsRegistered(IReadOnlyList<ProviderId> registered, string id)
    {
        foreach (ProviderId provider in registered)
        {
            if (string.Equals(provider.Value, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeBudget(RunBudgetCaps? budget)
    {
        if (budget is null)
        {
            return "uncapped";
        }

        var parts = new List<string>(3);
        if (budget.MaxTokens is { } tokens)
        {
            parts.Add(tokens.ToString(CultureInfo.InvariantCulture) + " tokens");
        }

        if (budget.MaxCostUsd is { } cost)
        {
            parts.Add(cost.ToString(CultureInfo.InvariantCulture) + " USD");
        }

        if (budget.MaxOutputBytes is { } bytes)
        {
            parts.Add(bytes.ToString(CultureInfo.InvariantCulture) + " output bytes");
        }

        return parts.Count == 0 ? "uncapped" : string.Join(", ", parts);
    }

    private static string Verdict(PermissionAction action) => action switch
    {
        PermissionAction.Allow => "allow",
        PermissionAction.Ask => "ask",
        _ => "deny",
    };

    private static string GuardName(ToolSafetyProfile profile) => profile.ArgKind switch
    {
        ToolArgKind.Path => "path (traversal and rooted paths refused)",
        ToolArgKind.Command => "command (destructive commands denied)",
        _ => "none (the argument is not a path or a command)",
    };

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string collapsed = text.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "...";
    }

    private static string Names(IEnumerable<string> names)
    {
        List<string> all = names.ToList();
        if (all.Count == 0)
        {
            return "(none)";
        }

        if (all.Count <= MaxListedNames)
        {
            return string.Join(", ", all);
        }

        return string.Join(", ", all.Take(MaxListedNames)) + $", ... (+{all.Count - MaxListedNames} more)";
    }

    /// <summary>The sections the catalog answers for.</summary>
    private enum Section
    {
        /// <summary>One screen: what this runtime has and how to ask for the rest.</summary>
        Overview,

        /// <summary>The registered tools and what the current agent may do with each.</summary>
        Tools,

        /// <summary>The registered agents.</summary>
        Agents,

        /// <summary>The provider catalogue and the registered providers.</summary>
        Providers,

        /// <summary>The skills the model may load.</summary>
        Skills,

        /// <summary>The plugin extension axes, their routes and their verdicts.</summary>
        Plugins,

        /// <summary>The versions this build speaks.</summary>
        Versions,

        /// <summary>The limits the current agent runs under.</summary>
        Limits,
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="InspectTool" />.
/// </summary>
internal static partial class InspectToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Catalog described: section={Section} name={Name}")]
    public static partial void Described(ILogger logger, string section, string name);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Unknown section requested: section={Section}")]
    public static partial void UnknownSection(ILogger logger, string section);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Unknown entry requested: section={Section} name={Name}")]
    public static partial void UnknownEntry(ILogger logger, string section, string name);
}
