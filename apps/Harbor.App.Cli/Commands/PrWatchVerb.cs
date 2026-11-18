using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handlers for the PR watch verbs (#2686 J01).</summary>
internal static class CafPrWatchCommands
{
    /// <summary>Print the projected check and merge status of a pull request as JSON.</summary>
    public static Task<int> Checks([Argument] params string[] args) =>
        PrWatchRunner.ChecksAsync(args, CancellationToken.None);

    /// <summary>Project one poll of a pull request into a session wake decision as JSON.</summary>
    /// <param name="wakes">How many times this watch has already woken the session.</param>
    /// <param name="last">Signature returned by the previous poll, for dedupe.</param>
    /// <param name="args">The pull-request number.</param>
    public static Task<int> Watch(string? wakes = null, string? last = null, [Argument] params string[] args)
    {
        var tail = new List<string>(args);
        if (wakes is not null) { tail.Add("--wakes"); tail.Add(wakes); }
        if (last is not null) { tail.Add("--last"); tail.Add(last); }
        return PrWatchRunner.WatchAsync([.. tail], CancellationToken.None);
    }
}

/// <summary>Parsed arguments shared by <c>pr checks</c> and <c>pr watch</c>.</summary>
internal sealed record PrWatchArgs(int Number, int WakeCount, string? LastSignature)
{
    internal static Result<PrWatchArgs> Parse(string[] args)
    {
        int number = 0;
        int wakes = 0;
        string? last = null;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--wakes")
            {
                if (i + 1 >= args.Length
                    || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out wakes)
                    || wakes < 0 || wakes > PullRequestWatchProjection.MaxWakes)
                {
                    return Result.Failure<PrWatchArgs>($"pr watch: --wakes must be 0..{PullRequestWatchProjection.MaxWakes}.");
                }

                continue;
            }

            if (token == "--last")
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[++i]))
                    return Result.Failure<PrWatchArgs>("pr watch: --last needs a signature.");
                last = args[i];
                continue;
            }

            if (token.StartsWith('-'))
                return Result.Failure<PrWatchArgs>($"pr watch: unexpected argument '{token}'.");

            if (number != 0
                || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                || number <= 0)
            {
                return Result.Failure<PrWatchArgs>("pr watch: expected one positive pull-request number.");
            }
        }

        return number == 0
            ? Result.Failure<PrWatchArgs>("pr watch: expected one positive pull-request number.")
            : Result.Success(new PrWatchArgs(number, wakes, last));
    }
}

/// <summary>
///     Reads pull-request status through <see cref="IForgeClient" /> and prints
///     the pure projection. The forge adapter owns process I/O; this runner
///     only validates arguments and shapes JSON for the terminal.
/// </summary>
internal static class PrWatchRunner
{
    internal static async Task<int> ChecksAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null)
    {
        Result<PrWatchArgs> parsed = PrWatchArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        Result<ForgePullRequestStatus> status = await FetchAsync(parsed.Value.Number, forge, cancellationToken)
            .ConfigureAwait(false);
        if (status.IsFailure)
        {
            Console.Error.WriteLine(status.Error);
            return 1;
        }

        PrintStatus(status.Value);
        return 0;
    }

    internal static async Task<int> WatchAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null)
    {
        Result<PrWatchArgs> parsed = PrWatchArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        Result<ForgePullRequestStatus> status = await FetchAsync(parsed.Value.Number, forge, cancellationToken)
            .ConfigureAwait(false);
        if (status.IsFailure)
        {
            Console.Error.WriteLine(status.Error);
            return 1;
        }

        var previous = new WatchState(parsed.Value.WakeCount, parsed.Value.LastSignature);
        WatchDecision decision = PullRequestWatchProjection.Decide(status.Value, previous);
        PrintDecision(decision);
        return 0;
    }

    private static Task<Result<ForgePullRequestStatus>> FetchAsync(
        int number,
        IForgeClient? forge,
        CancellationToken cancellationToken) =>
        (forge ?? new GitHubForgeClient()).GetStatusAsync(number, cancellationToken);

    private static void PrintStatus(ForgePullRequestStatus status)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", status.Number);
            writer.WriteString("mergeable", status.Mergeable);
            writer.WriteString("mergeStateStatus", status.MergeStateStatus);
            writer.WriteBoolean("hasConflict", status.HasConflict);
            writer.WriteStartArray("checks");
            foreach (ForgeCheck check in status.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", check.Name);
                writer.WriteString("state", check.State);
                writer.WriteString("conclusion", check.Conclusion);
                writer.WriteString("bucket", check.Bucket);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stdout.WriteByte((byte)'\n');
    }

    private static void PrintDecision(WatchDecision decision)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("shouldWake", decision.ShouldWake);
            writer.WriteString("reason", decision.Reason);
            writer.WriteString("signature", decision.Signature);
            writer.WriteBoolean("hasConflict", decision.HasConflict);
            writer.WriteStartArray("failedChecks");
            foreach (string name in decision.FailedChecks) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stdout.WriteByte((byte)'\n');
    }
}
