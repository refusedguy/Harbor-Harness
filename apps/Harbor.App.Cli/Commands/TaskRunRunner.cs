using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;
namespace Harbor.App.Cli.Commands;
/// <summary>
///     <c>harbor run task agent=&lt;name&gt; &lt;prompt&gt;</c> — execute a sub-agent
///     directly through <see cref="ISubAgentRunner" /> without a parent LLM turn.
///     The same isolation path the <c>task</c> tool uses (own session, own CTS,
///     final assistant text as output), driven from the command line for
///     verification and scripting.
/// </summary>
public static class TaskRunRunner
{
    public static async Task<int> RunAsync(TextWriter stdout, TextWriter stderr, IServiceProvider services, string[] args)
    {
        string? agentName = null;
        List<string> promptTokens = [];
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a.StartsWith("agent=", StringComparison.Ordinal))
            {
                agentName = a["agent=".Length..];
                i++;
                continue;
            }
            if (a is "--agent" or "-a" && i + 1 < args.Length)
            {
                agentName = args[i + 1];
                i += 2;
                continue;
            }
            promptTokens.Add(a);
            i++;
        }

        if (string.IsNullOrWhiteSpace(agentName))
        {
            await stderr.WriteLineAsync("Usage: harbor run task agent=<name> <prompt>").ConfigureAwait(false);
            await stderr.WriteLineAsync("Example: harbor run task agent=explore \"find all .cs files\"").ConfigureAwait(false);
            return 2;
        }

        string prompt = string.Join(' ', promptTokens).Trim();
        if (prompt.Length == 0)
        {
            await stderr.WriteLineAsync("Missing prompt: harbor run task agent=<name> <prompt>").ConfigureAwait(false);
            return 2;
        }

        var agents = services.GetService(typeof(IAgentRegistry)) as IAgentRegistry;
        var runner = services.GetService(typeof(ISubAgentRunner)) as ISubAgentRunner;
        if (agents is null || runner is null)
        {
            await stderr.WriteLineAsync("Sub-agent runtime is not available in this host composition.").ConfigureAwait(false);
            return 1;
        }

        AgentDefinition? definition = agents.GetAllAgents()
            .FirstOrDefault(a => a.Name.Value.Equals(agentName, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            var available = agents.GetAllAgents().Where(a => a.IsSubAgent).Select(a => a.Name.Value);
            await stderr.WriteLineAsync(
                $"Unknown agent '{agentName}'. Available sub-agents: {string.Join(", ", available)}").ConfigureAwait(false);
            return 1;
        }

        if (!definition.IsSubAgent)
        {
            await stderr.WriteLineAsync(
                $"Agent '{agentName}' is not a sub-agent. Only agents with IsSubAgent=true can be run via task.").ConfigureAwait(false);
            return 1;
        }

        var result = await runner.RunAsync(
            definition,
            new SubAgentRunRequest(prompt, ParentSessionId: null),
            CancellationToken.None).ConfigureAwait(false);

        return await result.Match(
            async run =>
            {
                await stdout.WriteLineAsync(
                    $"[sub-agent '{run.AgentName}' finished — session {run.SessionId}, {run.NewMessages} message(s)]")
                    .ConfigureAwait(false);
                await stdout.WriteLineAsync(run.FinalOutput).ConfigureAwait(false);
                return 0;
            },
            async error =>
            {
                await stderr.WriteLineAsync($"sub-agent run failed: {error}").ConfigureAwait(false);
                return 1;
            }).ConfigureAwait(false);
    }
}
