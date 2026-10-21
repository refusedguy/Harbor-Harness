using System.Collections.Frozen;
using System.Text;
using Harbor.Abstractions.Plugins;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The agent's window onto the plugin inventory: it reads what is
///     installed and PROPOSES a change — and the change happens only after
///     the user approves that one call.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gate.</b> Every action except <c>list</c> asks the user
///         through the same approval channel the permission service uses
///         (<see cref="ToolContext.Ask" />), with the concrete effect in the
///         prompt: which file, which scope, what will happen to it. A denial,
///         a cancellation or an unavailable approval channel ends the call
///         with the change NOT applied — the tool fails closed, exactly like
///         the permission service does.
///     </para>
///     <para>
///         <b>An approval covers that one call.</b> The response's
///         <c>PersistDecision</c> flag is deliberately ignored: an approval
///         never becomes a rule, and the session's permission mode is never
///         changed by it. (The plain renderer offers an "always" key; for this
///         tool it means "yes, this time" and nothing more.)
///     </para>
///     <para>
///         <b>Why the tool owns the gate rather than the ruleset.</b> The
///         ruleset decides whether the tool may run at all — a user who does
///         not want an agent touching plugins writes
///         <c>new("plugin_manager", "*", Deny)</c> and the tool never starts.
///         The ruleset cannot say "allow the listing, ask about the
///         install": it matches one argument, and this tool's argument is not
///         a path. The per-ACTION question therefore belongs here, where the
///         action is known, and it is asked once per change.
///     </para>
///     <para>
///         <b>Why the safety profile is Opaque.</b> The arguments are an
///         action name, a plugin FILE NAME and — for an install — the path of
///         a reviewed source that is READ. Permission rules match this tool
///         against <c>"*"</c>, so no glob pattern can match a path and no
///         pattern can be tricked into authorising one. The traversal the path
///         guard protects against is closed here by construction instead: the
///         inventory accepts a plugin only by its plain file name, resolves
///         that name inside the two plugin roots, and never writes outside
///         them.
///     </para>
///     <para>
///         <b>What "install" means here.</b> CS plugins are served by the
///         separate <c>harbor-plugins-host</c> process, so installing a plugin
///         means putting its reviewed source (and the manifest it ships
///         next to it) into a plugin scope. The host picks it up on its next
///         start; nothing is compiled or registered in this process.
///     </para>
/// </remarks>
[ToolInfo(
    "plugin_manager",
    "Plugin Manager",
    "List the installed plugins, and propose installing, removing, enabling or disabling one. Every change is applied only after the user approves that call.",
    Glyph = "🔌",
    ExecutionMode = ExecutionMode.Sequential,
    PromptSnippet = "plugin_manager: list installed plugins, or propose a change (the user approves each change)",
    PromptGuidelines = new[]
    {
        "Call plugin_manager with action='list' first — it reports the exact plugin file names the other actions take",
        "Every action except 'list' asks the user to approve that one change; the change happens only if they approve it",
        "An approval covers that call only — it does not change the session's permission mode",
        "Plugins run in the separate harbor-plugins-host process: a change takes effect after that host restarts",
    })]
[ToolSchema]
[ToolParam("action", "string", Description = "What to do: 'list', 'install', 'uninstall', 'enable' or 'disable'", Required = true)]
[ToolParam("name", "string", Description = "The plugin file name as 'list' reports it (e.g. 'TodoWrite.cs') — required for uninstall, enable and disable")]
[ToolParam("source", "string", Description = "Path of the reviewed '.cs' plugin source to install — required for install")]
[ToolParam("scope", "string", Description = "Where to install: 'global' (~/.harbor/plugins) or 'project' (<cwd>/.harbor/plugins) — install only, default 'global'")]
public sealed partial class PluginManagerTool : ITool
{
    private readonly IPluginInventory _inventory;
    private readonly ILogger<PluginManagerTool> _logger;

    /// <summary>
    ///     Construct the tool over a plugin inventory.
    /// </summary>
    /// <param name="inventory">The inventory the tool reads and, after approval, changes.</param>
    /// <param name="logger">Logger for the proposal / approval / outcome trail.</param>
    public PluginManagerTool(IPluginInventory inventory, ILogger<PluginManagerTool> logger)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> action = JsonArgValidator.RequiredString(args, "action", "Missing 'action'.");
        if (action.IsFailure)
            return action;

        string value = action.Value;
        if (value.Equals(ListAction, StringComparison.OrdinalIgnoreCase))
            return Result.Success();

        if (!Actions.TryGetValue(value, out ActionSpec spec))
        {
            return Result.Failure(
                $"Unknown action '{value}'. It must be one of: {ListAction}, {string.Join(", ", Actions.Keys)}.");
        }

        Result<string> required = JsonArgValidator.RequiredNonEmptyString(
            args,
            spec.RequiredArgument,
            $"'{value}' needs '{spec.RequiredArgument}': "
            + (spec.MentionsSource
                ? "the path of the '.cs' plugin source to install."
                : "the plugin file name, as 'list' reports it."));

        if (required.IsFailure)
            return required;

        if (spec.MentionsSource)
        {
            return JsonArgValidator.OptionalEnum(
                args, "scope", ["global", "project"],
                "'scope' must be 'global' or 'project'.",
                StringComparison.OrdinalIgnoreCase);
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        string action = args.GetProperty("action").GetString()!;

        if (action.Equals(ListAction, StringComparison.OrdinalIgnoreCase))
            return List();

        PluginScope scope = PluginScope.Global;
        if (args.TryGetProperty("scope", out JsonElement scopeElement)
            && scopeElement.ValueKind == JsonValueKind.String
            && scopeElement.GetString()!.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            scope = PluginScope.Project;
        }

        string name = args.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        string? source = args.TryGetProperty("source", out JsonElement sourceElement)
            ? sourceElement.GetString()
            : null;

        Result<Proposal> proposal = Propose(action, name, source, scope);
        if (proposal.IsFailure)
        {
            PluginManagerToolLog.ProposalRefused(_logger, action, name, proposal.Error);
            return ToolResult.Error(proposal.Error, new { action, name, scope = ScopeText(scope) });
        }

        Proposal plan = proposal.Value;
        PluginManagerToolLog.Proposed(_logger, action, plan.Name, plan.Text);

        PermissionRequest request = PermissionRequest.Create(
            Name.Value,
            plan.Text,
            args,
            ["allow", "deny"]);

        PermissionResponse answer = await context.Ask(request, cancellationToken).ConfigureAwait(false);
        if (answer.Action != PermissionAction.Allow)
        {
            // The approval covers THIS call and nothing else: the decision is
            // not written back to any ruleset, so the session's permission mode
            // is the same before and after.
            PluginManagerToolLog.NotApproved(_logger, action, plan.Name);
            return ToolResult.Error(
                $"The user did not approve: {plan.Text} Nothing was changed. Next step: report the outcome "
                + "to the user, or call list to see what is installed.",
                new { action, name = plan.Name, approved = false });
        }

        PluginManagerToolLog.Approved(_logger, action, plan.Name);

        Result<PluginInventoryChangeResult> applied = await _inventory
            .ApplyAsync(plan.Change, cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            PluginManagerToolLog.ApplyFailed(_logger, action, plan.Name, applied.Error);
            return ToolResult.Error(applied.Error, new { action, name = plan.Name, approved = true });
        }

        PluginManagerToolLog.Applied(_logger, action, applied.Value.Name, applied.Value.FullPath);
        return ToolResult.Success(
            applied.Value.Summary
            + " Next step: restart harbor-plugins-host to pick the change up; the running host still serves "
            + "the previous plugin set.",
            new { action, name = applied.Value.Name, path = applied.Value.FullPath, approved = true });
    }

    /// <summary>
    ///     Render the inventory.
    /// </summary>
    private ToolResult List()
    {
        Result<IReadOnlyList<PluginInventoryEntry>> listed = _inventory.List();
        if (listed.IsFailure)
        {
            PluginManagerToolLog.ListFailed(_logger, listed.Error);
            return ToolResult.Error(
                listed.Error + " Next step: report the diagnostic to the user.",
                new { action = "list" });
        }

        IReadOnlyList<PluginInventoryEntry> entries = listed.Value;
        PluginManagerToolLog.Listed(_logger, entries.Count);

        if (entries.Count == 0)
        {
            return ToolResult.Success(
                "No plugins are installed. Next step: to add one, call plugin_manager with action='install' "
                + "and the path of a reviewed '.cs' plugin source.",
                new { count = 0 });
        }

        var text = new StringBuilder();
        text.Append(entries.Count).Append(entries.Count == 1 ? " plugin is installed." : " plugins are installed.")
            .Append('\n');
        for (int i = 0; i < entries.Count; i++)
        {
            text.Append('\n').Append(Describe(entries[i]));
        }

        text.Append("\n\nNext step: to change one, call plugin_manager with its 'name' — the user approves "
                    + "each change.");
        return ToolResult.Success(text.ToString(), new { count = entries.Count });
    }

    /// <summary>
    ///     One line per installed plugin.
    /// </summary>
    private static string Describe(PluginInventoryEntry entry)
    {
        var text = new StringBuilder();
        text.Append("  ").Append(entry.Name);
        text.Append("  [").Append(ScopeText(entry.Scope)).Append(']');
        text.Append(entry.Enabled ? " enabled" : " disabled");
        if (entry.DeclaredVersion is { } version)
            text.Append(", version ").Append(version);
        if (entry.Axes.Count > 0)
            text.Append(", axes ").Append(string.Join(", ", entry.Axes));
        if (entry.RequiredHarborVersion is { } floor)
        {
            text.Append(", harbor >= ").Append(floor);
            text.Append(entry.Compatible ? " (ok)" : " (INCOMPATIBLE with this Harbor)");
        }
        if (entry.ManifestError is { } error)
            text.Append(", manifest unreadable: ").Append(error);
        text.Append(" — ").Append(entry.FullPath);
        return text.ToString();
    }

    /// <summary>
    ///     Turn the call into a concrete proposal: the effect that will
    ///     happen, and the change that carries it out.
    /// </summary>
    private Result<Proposal> Propose(string action, string name, string? source, PluginScope scope)
    {
        if (!Actions.TryGetValue(action, out ActionSpec spec))
        {
            return Result.Failure<Proposal>(
                $"Unknown action '{action}'. Next step: use one of {ListAction}, {string.Join(", ", Actions.Keys)}.");
        }

        if (spec.MentionsSource)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return Result.Failure<Proposal>(
                    "An install needs 'source': the path of the '.cs' plugin source to install. Next step: "
                    + "call list to see what is installed, then pass the path of the source to add.");
            }

            // The install target is the SOURCE's file name: a plugin is one
            // file, and its name is that file's name. A 'name' the model sent
            // along is not what lands on disk — the proposal and the copy have
            // to name the same path.
            return _inventory.DescribeSource(source, scope).Map(entry =>
            {
                string installText = $"{spec.Verb} plugin '{entry.Name}' in the {ScopeText(scope)} scope "
                                     + $"({entry.FullPath}), from {source}. {spec.Effect}";
                return new Proposal(
                    installText,
                    new PluginInventoryChange(spec.Change, entry.Name, source, scope),
                    entry.Name);
            });
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure<Proposal>(
                $"'{action}' needs 'name': the plugin file name, as 'list' reports it. Next step: call "
                + "list and use the exact file name.");
        }

        return _inventory.List()
            .Bind(listed => FindByName(listed, name))
            .Map(found =>
            {
                string text = $"{spec.Verb} plugin '{found.Name}' in the {ScopeText(found.Scope)} scope "
                              + $"({found.FullPath}). {spec.Effect}";
                return new Proposal(text, new PluginInventoryChange(spec.Change, found.Name), found.Name);
            });
    }

    /// <summary>
    ///     The installed plugin with that name, or the failure that says the
    ///     agent has to look at the list first.
    /// </summary>
    private static Result<PluginInventoryEntry> FindByName(
        IReadOnlyList<PluginInventoryEntry> entries,
        string name)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Result.Success(entries[i]);
        }

        return Result.Failure<PluginInventoryEntry>(
            $"No plugin named '{name}' is installed. Next step: call list and use the exact file "
            + "name it reports.");
    }

    /// <summary>
    ///     The action that reads the inventory and changes nothing.
    /// </summary>
    private const string ListAction = "list";

    /// <summary>
    ///     What one action needs, and what it does — the vocabulary of the
    ///     tool, in ONE place.
    /// </summary>
    /// <remarks>
    ///     The table is the answer to the drift the two case ladders used to
    ///     invite: the validation and the proposal used to answer "what does
    ///     this action need?" from two lists of the same strings, and adding an
    ///     action to one of them left the other one behind. Here the required
    ///     argument, the change the inventory is asked for, the verb the user
    ///     reads and the effect they are told about are ONE row.
    /// </remarks>
    /// <param name="RequiredArgument">The argument the action needs.</param>
    /// <param name="Change">The change the action asks the inventory for.</param>
    /// <param name="Verb">The verb the proposal opens with.</param>
    /// <param name="Effect">What the user is told will happen afterwards.</param>
    /// <param name="MentionsSource">
    ///     True when the action names a SOURCE rather than an installed
    ///     plugin — the install is the only one that does.
    /// </param>
    private sealed record ActionSpec(
        string RequiredArgument,
        PluginInventoryAction Change,
        string Verb,
        string Effect,
        bool MentionsSource);

    /// <summary>
    ///     The actions that change something, keyed by the action name.
    /// </summary>
    private static readonly FrozenDictionary<string, ActionSpec> Actions =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, ActionSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["install"] = new(
                    "source",
                    PluginInventoryAction.Install,
                    "Install",
                    "The plugin host compiles it and runs it with full trust.",
                    MentionsSource: true),
                ["uninstall"] = new(
                    "name",
                    PluginInventoryAction.Uninstall,
                    "Remove",
                    "The plugin host stops serving it on its next start.",
                    MentionsSource: false),
                ["enable"] = new(
                    "name",
                    PluginInventoryAction.Enable,
                    "Enable",
                    "The plugin host serves it again on its next start.",
                    MentionsSource: false),
                ["disable"] = new(
                    "name",
                    PluginInventoryAction.Disable,
                    "Disable",
                    "The plugin host stops serving it on its next start.",
                    MentionsSource: false),
            },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     A scope's name as the model reads it.
    /// </summary>
    private static string ScopeText(PluginScope scope) =>
        scope == PluginScope.Global ? "global" : "project";

    /// <summary>
    ///     One proposal: the sentence the user is asked to approve, the change
    ///     it carries, and the plugin's name.
    /// </summary>
    private sealed record Proposal(string Text, PluginInventoryChange Change, string Name);
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="PluginManagerTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class PluginManagerToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "plugin_manager listed {Count} installed plugin(s)")]
    public static partial void Listed(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "plugin_manager could not read the plugin inventory: {Error}")]
    public static partial void ListFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "plugin_manager proposed {Action} on '{Name}': {Proposal}")]
    public static partial void Proposed(ILogger logger, string action, string name, string proposal);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "plugin_manager could not propose {Action} on '{Name}': {Error}")]
    public static partial void ProposalRefused(ILogger logger, string action, string name, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "plugin_manager: {Action} on '{Name}' was approved by the user")]
    public static partial void Approved(ILogger logger, string action, string name);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "plugin_manager: {Action} on '{Name}' was NOT approved; nothing changed")]
    public static partial void NotApproved(ILogger logger, string action, string name);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "plugin_manager applied {Action} on '{Name}' → {Path}")]
    public static partial void Applied(ILogger logger, string action, string name, string path);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "plugin_manager could not apply {Action} on '{Name}': {Error}")]
    public static partial void ApplyFailed(ILogger logger, string action, string name, string error);
}
