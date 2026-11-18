using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>handoff</c> verb (#2686 J06).</summary>
internal static class CafHandoffCommands
{
    /// <summary>Print a handoff packet for another session to pick up.</summary>
    public static Task<int> Handoff([Argument] params string[] args) =>
        HandoffRunner.RunAsync(args, CancellationToken.None);
}

/// <summary>
///     Builds the handoff packet from the current worktree and one pull
///     request read through the forge seam. Packet shaping is a pure
///     projection; only the PR read calls out of the process.
/// </summary>
internal static class HandoffRunner
{
    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null)
    {
        string? sessionId = null;
        int? pullRequestNumber = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length && sessionId is null)
            {
                sessionId = args[++i];
                continue;
            }

            if (args[i] == "--pr" && i + 1 < args.Length && pullRequestNumber is null
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                && parsed > 0)
            {
                pullRequestNumber = parsed;
                continue;
            }

            Console.Error.WriteLine("handoff: expected --session <id> --pr <positive number>.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || pullRequestNumber is null)
        {
            Console.Error.WriteLine("handoff: expected --session <id> --pr <positive number>.");
            return 1;
        }

        var result = await (forge ?? new GitHubForgeClient())
            .GetPullRequestAsync(pullRequestNumber.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"handoff: {result.Error}");
            return 1;
        }

        if (result.Value is null)
        {
            Console.Error.WriteLine($"handoff: no readable pull request #{pullRequestNumber.Value}.");
            return 1;
        }

        Print(HandoffProjection.Build(sessionId, result.Value, Environment.CurrentDirectory));
        return 0;
    }

    private static void Print(HandoffPacket packet)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("session", packet.Session);
            writer.WriteNumber("pullRequestNumber", packet.PullRequestNumber);
            writer.WriteString("pullRequestTitle", packet.PullRequestTitle);
            writer.WriteString("pullRequestState", packet.PullRequestState);
            writer.WriteString("pullRequestUrl", packet.PullRequestUrl);
            writer.WriteString("worktree", packet.Worktree);
            writer.WriteString("prompt", packet.Prompt);
            writer.WriteEndObject();
        }

        stdout.WriteByte((byte)'\n');
    }
}
