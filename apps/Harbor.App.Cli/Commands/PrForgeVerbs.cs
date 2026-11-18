using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handlers for the forge-backed pull-request verbs (#2686 J02).</summary>
internal static class CafPrForgeCommands
{
    /// <summary>List open pull requests in the current repository as JSON.</summary>
    public static Task<int> List([Argument] params string[] args) =>
        PrForgeRunner.ListAsync(args, CancellationToken.None);

    /// <summary>Show one pull request as JSON.</summary>
    public static Task<int> View([Argument] params string[] args) =>
        PrForgeRunner.ViewAsync(args, CancellationToken.None);
}

/// <summary>
///     Reads pull requests through <see cref="IForgeClient"/> and prints the
///     normalized records. The GitHub adapter is the default; another forge
///     can replace it without touching the verb code.
/// </summary>
internal static class PrForgeRunner
{
    internal static async Task<int> ListAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null)
    {
        const int defaultLimit = 20;
        int limit = defaultLimit;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--limit" && i + 1 < args.Length
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out limit))
            {
                continue;
            }

            Console.Error.WriteLine($"pr list: unexpected argument '{args[i]}'.");
            return 1;
        }

        if (limit <= 0 || limit > ForgeProjection.MaxPullRequests)
        {
            Console.Error.WriteLine($"pr list: --limit must be between 1 and {ForgeProjection.MaxPullRequests}.");
            return 1;
        }

        var result = await (forge ?? new GitHubForgeClient())
            .ListOpenPullRequestsAsync(limit, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"pr list: {result.Error}");
            return 1;
        }

        PrintList(result.Value);
        return 0;
    }

    internal static async Task<int> ViewAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null)
    {
        if (args.Length != 1
            || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number <= 0)
        {
            Console.Error.WriteLine("pr view: expected one positive pull-request number.");
            return 1;
        }

        var result = await (forge ?? new GitHubForgeClient())
            .GetPullRequestAsync(number, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"pr view: {result.Error}");
            return 1;
        }

        if (result.Value is null)
        {
            Console.Error.WriteLine($"pr view: no readable pull request #{number}.");
            return 1;
        }

        PrintOne(result.Value);
        return 0;
    }

    private static void PrintList(IReadOnlyList<ForgePullRequest> pullRequests)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (ForgePullRequest item in pullRequests)
            {
                Write(writer, item);
            }

            writer.WriteEndArray();
        }

        stdout.WriteByte((byte)'\n');
    }

    private static void PrintOne(ForgePullRequest item)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, item);
        }

        stdout.WriteByte((byte)'\n');
    }

    private static void Write(Utf8JsonWriter writer, ForgePullRequest item)
    {
        writer.WriteStartObject();
        writer.WriteNumber("number", item.Number);
        writer.WriteString("title", item.Title);
        writer.WriteString("state", item.State);
        writer.WriteString("url", item.Url);
        writer.WriteEndObject();
    }
}
