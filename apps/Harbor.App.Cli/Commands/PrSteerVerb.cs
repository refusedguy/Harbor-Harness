using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     The directive a pull-request watch delivers to the linked session when
///     it wakes it (#2666). This is the steer contract: the watch owns the
///     decision, this record is the exact payload a future runtime watcher
///     hands to <c>session_steer</c>.
/// </summary>
internal sealed record WatchSteerDirective(
    string SessionId,
    string Operation,
    string Instruction,
    string Reason,
    string Signature,
    int PrNumber,
    string PrUrl);

/// <summary>
///     Pure contract between the PR watch projection (J01) and the session
///     steer tool. No process, no store and no reflection live here, so the
///     payload can be built and tested without gh or a running agent.
/// </summary>
internal static class PullRequestWatchSteer
{
    /// <summary>Bound on the rendered instruction so one watch cannot flood history.</summary>
    internal const int MaxInstructionChars = 2000;

    /// <summary>Bound on the number of failing check names listed in the instruction.</summary>
    internal const int MaxListedChecks = 10;

    /// <summary>The steer operation. A watch informs the session; it does not replace its task.</summary>
    internal const string OperationMessage = "message";

    internal static Result<WatchSteerDirective> Build(
        SessionPullRequestLink? link,
        WatchDecision decision,
        int prNumber)
    {
        if (link is null)
            return Result.Failure<WatchSteerDirective>("pr steer: the watch has no linked session.");
        if (string.IsNullOrWhiteSpace(link.SessionId))
            return Result.Failure<WatchSteerDirective>("pr steer: the linked session id is empty.");
        if (prNumber <= 0 || prNumber != link.Number)
            return Result.Failure<WatchSteerDirective>("pr steer: the link and the watched pull request disagree.");
        if (!decision.ShouldWake)
            return Result.Failure<WatchSteerDirective>("pr steer: the watch decided not to wake.");

        string instruction = Render(decision, prNumber, link.Url);
        return Result.Success(new WatchSteerDirective(
            link.SessionId.Trim(),
            OperationMessage,
            instruction,
            decision.Reason,
            decision.Signature,
            prNumber,
            link.Url));
    }

    private static string Render(WatchDecision decision, int prNumber, string prUrl)
    {
        var text = new StringBuilder();
        text.Append("Pull request #")
            .Append(prNumber.ToString(CultureInfo.InvariantCulture))
            .Append(" needs attention: ");

        if (decision.HasConflict)
            text.Append("it has a merge conflict, rebase and resolve it. ");

        if (decision.FailedChecks.Count > 0)
        {
            text.Append("Failing checks: ");
            int listed = Math.Min(decision.FailedChecks.Count, MaxListedChecks);
            for (int i = 0; i < listed; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(decision.FailedChecks[i]);
            }

            if (decision.FailedChecks.Count > listed) text.Append(", and more");
            text.Append(". Inspect and fix them, then re-run the checks. ");
        }

        if (!string.IsNullOrWhiteSpace(prUrl))
            text.Append("PR: ").Append(prUrl).Append('.');

        string instruction = text.ToString().Trim();
        return instruction.Length <= MaxInstructionChars
            ? instruction
            : instruction[..MaxInstructionChars];
    }
}

/// <summary>CAF handler for the pull-request steer verb (#2666).</summary>
internal static class CafPrSteerCommands
{
    /// <summary>
    ///     Print the steer directive for the session linked to a pull request.
    ///     The decision is projected from the same forge seam the watch uses;
    ///     no process is started when a caller injects a forge client.
    /// </summary>
    public static Task<int> Steer(string? wakes = null, string? last = null, [Argument] params string[] args)
    {
        var tail = new List<string>(args);
        if (wakes is not null) { tail.Add("--wakes"); tail.Add(wakes); }
        if (last is not null) { tail.Add("--last"); tail.Add(last); }
        return PrSteerRunner.SteerAsync([.. tail], CancellationToken.None);
    }
}

/// <summary>
///     Resolves the session linked to a pull request and renders the steer
///     payload. All shaping lives in the two pure projections; this runner
///     only owns the links, the forge read and the terminal.
/// </summary>
internal static class PrSteerRunner
{
    internal static async Task<int> SteerAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null,
        PullRequestLinkStore? store = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        TextWriter stdout = output ?? Console.Out;
        TextWriter stderr = error ?? Console.Error;

        Result<PrWatchArgs> parsed = PrWatchArgs.Parse(args);
        if (parsed.IsFailure)
        {
            stderr.WriteLine(parsed.Error);
            return 2;
        }

        int number = parsed.Value.Number;
        Result<IReadOnlyList<SessionPullRequestLink>> links = (store ?? new PullRequestLinkStore()).ReadAll()
            .Map(all => PullRequestLinkProjection.Filter(all, sessionId: null, repo: null, number: number));
        if (links.IsFailure)
        {
            stderr.WriteLine($"pr steer: {links.Error}");
            return 1;
        }

        if (links.Value.Count == 0)
        {
            stderr.WriteLine($"pr steer: no session is linked to pull request #{number.ToString(CultureInfo.InvariantCulture)}.");
            return 1;
        }

        SessionPullRequestLink link = links.Value[0];
        Result<ForgePullRequestStatus> status = await (forge ?? new GitHubForgeClient())
            .GetStatusAsync(number, cancellationToken)
            .ConfigureAwait(false);
        if (status.IsFailure)
        {
            stderr.WriteLine($"pr steer: {status.Error}");
            return 1;
        }

        var previous = new WatchState(parsed.Value.WakeCount, parsed.Value.LastSignature);
        WatchDecision decision = PullRequestWatchProjection.Decide(status.Value, previous);
        if (!decision.ShouldWake)
        {
            stdout.WriteLine(FormatQuiet(decision));
            return 0;
        }

        Result<WatchSteerDirective> steer = PullRequestWatchSteer.Build(link, decision, number);
        if (steer.IsFailure)
        {
            stderr.WriteLine($"pr steer: {steer.Error}");
            return 1;
        }

        stdout.WriteLine(Format(steer.Value));
        return 0;
    }

    private static string FormatQuiet(WatchDecision decision)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("shouldWake", false);
            writer.WriteString("reason", decision.Reason);
            writer.WriteString("signature", decision.Signature);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Format(WatchSteerDirective directive)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", directive.SessionId);
            writer.WriteString("operation", directive.Operation);
            writer.WriteString("instruction", directive.Instruction);
            writer.WriteString("reason", directive.Reason);
            writer.WriteString("signature", directive.Signature);
            writer.WriteNumber("prNumber", directive.PrNumber);
            writer.WriteString("prUrl", directive.PrUrl);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
