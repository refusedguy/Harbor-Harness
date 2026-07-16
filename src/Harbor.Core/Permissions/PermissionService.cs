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

    /// <summary>
    ///     Construct a <see cref="PermissionService" /> wired to the supplied registry.
    /// </summary>
    /// <param name="agents">The agent registry for ruleset lookup.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="userAsker">Optional callback for prompting the user on <see cref="PermissionAction.Ask" /> decisions.</param>
    public PermissionService(
        IAgentRegistry agents,
        ILogger<PermissionService> logger,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? userAsker = null)
    {
        _agents = agents;
        _logger = logger;
        _userAsker = userAsker;
    }

    /// <inheritdoc />
    public Task<Result<PermissionResponse>> CheckAsync(
        string agentName,
        string toolName,
        JsonElement args,
        CancellationToken ct = default)
    {
        var agentResult = _agents.GetAgent(AgentName.TryCreate(agentName).Value);
        if (agentResult.IsFailure)
            return Task.FromResult(Result.Failure<PermissionResponse>(agentResult.Error));

        string argPath = ExtractArgPath(toolName, args);
        var action = agentResult.Value.Permission.Evaluate(toolName, argPath);

        if (action == PermissionAction.Allow)
        {
            return Task.FromResult(Result.Success(new PermissionResponse(action, false)));
        }

        if (action == PermissionAction.Deny)
        {
            return Task.FromResult(Result.Success(new PermissionResponse(action, false)));
        }

        // Ask user
        if (_userAsker is null)
        {
            // No UI configured — default to deny for safety
            return Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));
        }

        return AskUserAsync(new PermissionRequest(
            toolName,
            argPath,
            args,
            new[] { "allow", "deny" }), ct);
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
            _logger.LogError(ex, "User ask failed");
            return Result.Success(new PermissionResponse(PermissionAction.Deny, false));
        }
    }

    /// <inheritdoc />
    public PermissionRuleset GetRuleset(string agentName)
    {
        var agentResult = _agents.GetAgent(AgentName.TryCreate(agentName).Value);
        return agentResult.IsSuccess ? agentResult.Value.Permission : PermissionRuleset.Empty;
    }

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
}
