using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>One check in a pull request's status rollup (#2686 J01).</summary>
internal sealed record ForgeCheck(string Name, string State, string Conclusion)
{
    /// <summary>Normalized outcome used by the wake policy: pass, fail or pending.</summary>
    internal string Bucket
    {
        get
        {
            if (Is(Conclusion, "FAILURE") || Is(Conclusion, "ERROR") || Is(Conclusion, "TIMED_OUT")
                || Is(Conclusion, "ACTION_REQUIRED") || Is(State, "FAILURE") || Is(State, "ERROR"))
            {
                return "fail";
            }

            return Is(Conclusion, "SUCCESS") || Is(State, "SUCCESS") ? "pass" : "pending";
        }
    }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The check and merge state of one pull request, projected from gh.</summary>
internal sealed record ForgePullRequestStatus(
    int Number,
    string Mergeable,
    string MergeStateStatus,
    IReadOnlyList<ForgeCheck> Checks)
{
    /// <summary>A dirty merge state or an explicit conflict blocks the PR.</summary>
    internal bool HasConflict =>
        string.Equals(Mergeable, "CONFLICT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(MergeStateStatus, "DIRTY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The small bit of poll history the wake policy needs to dedupe.</summary>
internal sealed record WatchState(int WakeCount, string? LastSignature)
{
    internal static WatchState Initial => new(0, null);
}

/// <summary>The result of one poll: whether to steer the session and why.</summary>
internal sealed record WatchDecision(
    bool ShouldWake,
    string Reason,
    string Signature,
    IReadOnlyList<string> FailedChecks,
    bool HasConflict);

/// <summary>
///     Pure projection and wake policy for <c>gh pr view --json statusCheckRollup</c>.
///     No process I/O lives here, so the decision can be tested without gh.
/// </summary>
internal static class PullRequestWatchProjection
{
    /// <summary>Cap so one pull request cannot flood the decision output.</summary>
    internal const int MaxChecks = 100;

    /// <summary>A watch steers a session at most this many times.</summary>
    internal const int MaxWakes = 5;

    /// <summary>Decode the merged <c>gh pr view</c> JSON into a status.</summary>
    internal static Result<ForgePullRequestStatus> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Failure<ForgePullRequestStatus>("pr watch: gh returned no output.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result.Failure<ForgePullRequestStatus>("pr watch: expected a JSON object.");

            if (!root.TryGetProperty("number", out JsonElement number)
                || number.ValueKind != JsonValueKind.Number
                || !number.TryGetInt32(out int prNumber)
                || prNumber <= 0)
            {
                return Result.Failure<ForgePullRequestStatus>("pr watch: invalid pull-request number.");
            }

            var checks = new List<ForgeCheck>();
            if (root.TryGetProperty("statusCheckRollup", out JsonElement rollup)
                && rollup.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement check in rollup.EnumerateArray())
                {
                    if (checks.Count >= MaxChecks) break;
                    if (check.ValueKind != JsonValueKind.Object) continue;
                    checks.Add(new ForgeCheck(
                        TextOf(check, "name", "context"),
                        TextOf(check, "state"),
                        TextOf(check, "conclusion")));
                }
            }

            return Result.Success(new ForgePullRequestStatus(
                prNumber,
                TextOf(root, "mergeable"),
                TextOf(root, "mergeStateStatus"),
                checks));
        }
        catch (JsonException ex)
        {
            return Result.Failure<ForgePullRequestStatus>($"pr watch: invalid JSON from gh: {ex.Message}");
        }
    }

    /// <summary>
    ///     Turn a status and the previous poll history into one decision. A
    ///     repeated signature and an exhausted wake budget both suppress the
    ///     wake; a clean PR never wakes on its own.
    /// </summary>
    internal static WatchDecision Decide(ForgePullRequestStatus status, WatchState state)
    {
        string[] failed = status.Checks
            .Where(check => string.Equals(check.Bucket, "fail", StringComparison.Ordinal))
            .Select(check => check.Name)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        bool conflict = status.HasConflict;
        string signature = string.Concat(
            "c=", conflict ? "1" : "0",
            "|f=", string.Join(',', failed));

        string reason = conflict && failed.Length > 0
            ? "conflict+failed-checks"
            : conflict ? "conflict" : "failed-checks";

        bool hasSignal = conflict || failed.Length > 0;
        bool shouldWake = hasSignal
            && state.WakeCount < MaxWakes
            && !string.Equals(state.LastSignature, signature, StringComparison.Ordinal);

        if (!hasSignal) reason = "no-signal";
        else if (state.WakeCount >= MaxWakes) reason = "wake-limit";
        else if (string.Equals(state.LastSignature, signature, StringComparison.Ordinal)) reason = "unchanged";

        return new WatchDecision(shouldWake, reason, signature, failed, conflict);
    }

    private static string TextOf(JsonElement obj, string key, string? fallbackKey = null)
    {
        if (obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;

        if (fallbackKey is not null
            && obj.TryGetProperty(fallbackKey, out JsonElement fallback)
            && fallback.ValueKind == JsonValueKind.String)
        {
            return fallback.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
