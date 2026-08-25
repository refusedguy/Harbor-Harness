using System.Collections.Concurrent;
using Harbor.Core.Resources;
using Microsoft.Extensions.Logging;
namespace Harbor.Core.Permissions;
/// <summary>
///     Default permission service. Implements Specification pattern (GOF).
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly IAgentRegistry _agents;
    private readonly ILogger<PermissionService> _logger;
    private readonly Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? _userAsker;
    private readonly string? _workspaceRoot;

    /// <summary>
    ///     Persisted user decisions (A2): agent name → rule key ("toolName:argPath") → the
    ///     rule recorded when the user answered a prompt with "always". Consulted before
    ///     prompting and merged into <see cref="GetRuleset" /> so the decision survives
    ///     across checks. Thread-safe via concurrent dictionaries.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PermissionRule>> _persisted = new();

    /// <summary>
    ///     Construct a <see cref="PermissionService" /> wired to the supplied registry.
    /// </summary>
    /// <param name="agents">The agent registry for ruleset lookup.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="userAsker">Optional callback for prompting the user on <see cref="PermissionAction.Ask" /> decisions.</param>
    /// <param name="workspaceRoot">
    ///     Optional workspace root for path confinement. When set, tool paths are normalized
    ///     against it before rule matching and any Allow decision for a path resolving
    ///     outside the workspace is downgraded to <see cref="PermissionAction.Ask" />.
    ///     When <see langword="null" />, relative paths resolve against the process working directory.
    /// </param>
    public PermissionService(
        IAgentRegistry agents,
        ILogger<PermissionService> logger,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? userAsker = null,
        string? workspaceRoot = null)
    {
        _agents = agents;
        _logger = logger;
        _userAsker = userAsker;
        _workspaceRoot = workspaceRoot;
    }

    /// <inheritdoc />
    public Task<Result<PermissionResponse>> CheckAsync(
        string agentName,
        string toolName,
        JsonElement args,
        CancellationToken ct = default)
    {
        // ROP-B П.12: name parsing → registry lookup → verdict ride one Bind
        // chain. An invalid agent name is an expected failure (e.g. provider
        // routed a request with a malformed header), so it surfaces as
        // Result.Failure without any .Value read ever compiling in.
        return AgentName.TryCreate(agentName)
            .Bind(_agents.GetAgent)
            .Bind(agent => EvaluateActionAsync(agent, agentName, toolName, args, ct));
    }

    private async Task<Result<PermissionResponse>> EvaluateActionAsync(
        AgentDefinition agent,
        string agentName,
        string toolName,
        JsonElement args,
        CancellationToken ct)
    {
        var extraction = NormalizePathExtraction(toolName, args, _workspaceRoot ?? Environment.CurrentDirectory);
        var action = agent.Permission.Evaluate(toolName, extraction.ArgPath);

        // Workspace confinement (A1/A2): a path that resolves outside the workspace root —
        // or the process working directory when no explicit root is configured — must never
        // be silently allowed, whether or not a root was configured. Deny decisions are
        // preserved — they are strictly safer than Ask.
        if (action == PermissionAction.Allow
            && extraction.IsOutsideWorkspace)
        {
            action = PermissionAction.Ask;
        }

        if (action == PermissionAction.Allow)
        {
            return Result.Success(new PermissionResponse(action, false));
        }

        if (action == PermissionAction.Deny)
        {
            return Result.Success(new PermissionResponse(action, false));
        }

        // Ask: a previously persisted user decision for this exact tool + argument (A2)
        // short-circuits without prompting again.
        string ruleKey = toolName + ":" + extraction.ArgPath;
        string persistedAgentKey = agent.Name.Value;
        if (_persisted.TryGetValue(persistedAgentKey, out var byRule)
            && byRule.TryGetValue(ruleKey, out var persistedRule))
        {
            return Result.Success(new PermissionResponse(persistedRule.Action, false));
        }

        // No UI configured — default to deny for safety
        if (_userAsker is null)
        {
            return Result.Success(new PermissionResponse(PermissionAction.Deny, false));
        }

        var askResult = await AskUserAsync(new PermissionRequest(
            toolName,
            extraction.ArgPath,
            args,
            new[] { "allow", "deny" }), ct).ConfigureAwait(false);

        // A2: an explicit "always" answer is recorded as a literal-pattern rule so later
        // checks for the same tool + argument skip the prompt and GetRuleset reflects it.
        if (askResult.IsSuccess
            && askResult.Value.PersistDecision
            && askResult.Value.Action != PermissionAction.Ask)
        {
            var byAgent = _persisted.GetOrAdd(
                persistedAgentKey,
                static _ => new ConcurrentDictionary<string, PermissionRule>(StringComparer.Ordinal));
            byAgent[ruleKey] = new PermissionRule(toolName, extraction.ArgPath, askResult.Value.Action);
        }

        return askResult;
    }

    /// <inheritdoc />
    public async Task<Result<PermissionResponse>> AskUserAsync(
        PermissionRequest request,
        CancellationToken ct = default)
    {
        if (_userAsker is null)
            return Result.Success(new PermissionResponse(PermissionAction.Deny, false));

        try
        {
            var response = await _userAsker(request, ct).ConfigureAwait(false);
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, CoreResources.GetError("PermissionDenied"), request.Permission, request.Pattern);
            return Result.Success(new PermissionResponse(PermissionAction.Deny, false));
        }
    }

    /// <inheritdoc />
    public PermissionRuleset GetRuleset(string agentName)
    {
        // ROP-B П.12 (residual): same railway as CheckAsync — name parsing and
        // registry lookup ride one Bind chain with no .Value read compiling in.
        // GetRuleset's contract is "best-effort lookup", so any failure (bad
        // name, unknown agent) collapses to the empty ruleset at the Match
        // boundary — callers (e.g. /permissions) get a safe default rather
        // than an exception bubbling up to the UI.
        return AgentName.TryCreate(agentName)
            .Bind(_agents.GetAgent)
            .Match(
                agent => MergePersisted(agent.Name.Value, agent.Permission),
                _ => PermissionRuleset.Empty);
    }

    private PermissionRuleset MergePersisted(string agentKey, PermissionRuleset ruleset)
    {
        // A2: merge persisted user decisions on top of the agent's static ruleset so
        // callers (e.g. /permissions) see the effective ruleset.
        if (!_persisted.TryGetValue(agentKey, out var byRule) || byRule.IsEmpty)
            return ruleset;

        var persistedRules = new PermissionRule[byRule.Count];
        int index = 0;
        foreach (var kvp in byRule)
        {
            persistedRules[index++] = kvp.Value;
        }

        return ruleset.Merge(new PermissionRuleset(persistedRules));
    }

    /// <summary>Raw argument extraction (legacy, un-normalized). Kept for compatibility.</summary>
    private static string ExtractArgPath(string toolName, JsonElement args)
    {
        try
        {
            return toolName switch
            {
                "read" or "write" or "edit" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "*" : "*",
                "bash" => args.TryGetProperty("command", out var c) ? c.GetString() ?? "*" : "*",
                "glob" => args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*",
                "grep" => args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*",
                "ls" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "*" : "*",
                _ => "*"
            };
        }
        catch
        {
            return "*";
        }
    }

    private readonly record struct PathExtraction(string ArgPath, bool IsOutsideWorkspace);

    /// <summary>
    ///     Extracts the rule-matching string for a tool call, normalizing file paths before
    ///     rule evaluation so traversal sequences cannot smuggle a path past anchored Allow
    ///     rules (A1). Relative paths resolve against <paramref name="workspaceRoot" />; the
    ///     normalized path is expressed relative to the workspace root when it stays inside
    ///     it, and as an absolute path otherwise.
    /// </summary>
    private static PathExtraction NormalizePathExtraction(string toolName, JsonElement args, string workspaceRoot)
    {
        switch (toolName)
        {
            case "read" or "write" or "edit" or "ls"
                or "patch" or "tree" or "ripgrep" or "notebook" or "mcp":
                try
                {
                    return NormalizePath(
                        args.TryGetProperty("path", out var p) ? p.GetString() : null,
                        workspaceRoot);
                }
                catch
                {
                    return new PathExtraction("*", true);
                }
            default:
                return new PathExtraction(ExtractArgPath(toolName, args), false);
        }
    }

    private static PathExtraction NormalizePath(string? raw, string workspaceRoot)
    {
        // No usable path argument: fall back to the legacy wildcard (rule matching decides).
        if (string.IsNullOrWhiteSpace(raw) || raw == "*")
            return new PathExtraction("*", false);

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(raw)
                ? raw
                : Path.Combine(workspaceRoot, raw));
        }
        catch
        {
            // Unresolvable path: do not match path-anchored rules; force user decision.
            return new PathExtraction("*", true);
        }

        if (IsInsideWorkspace(full, workspaceRoot))
            return new PathExtraction(RelativeToWorkspace(full, workspaceRoot), false);

        return new PathExtraction(full, true);
    }

    private static bool IsInsideWorkspace(string full, string root)
    {
        if (string.Equals(full, root, StringComparison.Ordinal))
            return true;
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string RelativeToWorkspace(string full, string root)
    {
        if (full.Length == root.Length)
            return string.Empty;
        int start = root.Length;
        if (full[start] == Path.DirectorySeparatorChar || full[start] == Path.AltDirectorySeparatorChar)
            start++;
        return full[start..];
    }
}
