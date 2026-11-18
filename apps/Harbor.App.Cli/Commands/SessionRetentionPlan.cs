using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.App.Cli.Commands;

/// <summary>Retention thresholds for one <c>sessions maintenance</c> run.</summary>
/// <param name="MaxAgeDays">Delete sessions inactive for longer than this many days.</param>
/// <param name="KeepRecent">Always keep this many most-recently-active sessions.</param>
internal sealed record SessionRetentionOptions(int MaxAgeDays, int KeepRecent);

/// <summary>Parsed arguments of <c>harbor sessions maintenance</c>.</summary>
/// <param name="Options">The retention thresholds to apply.</param>
/// <param name="Apply">Whether to actually delete the planned sessions, instead of only printing them.</param>
internal sealed record SessionMaintenanceArgs(SessionRetentionOptions Options, bool Apply);

/// <summary>One session's fate in a retention plan.</summary>
/// <param name="Session">The session being classified.</param>
/// <param name="Reason">Short, stable explanation of the classification.</param>
internal sealed record SessionRetentionDecision(Session Session, string Reason);

/// <summary>
///     The outcome of a retention pass: which sessions to delete, which to keep
///     and the thresholds that produced that split.
/// </summary>
/// <param name="Delete">Sessions selected for deletion, most recent first.</param>
/// <param name="Keep">Sessions retained, most recent first.</param>
/// <param name="Options">The thresholds that produced this plan.</param>
internal sealed record SessionRetentionPlan(
    IReadOnlyList<SessionRetentionDecision> Delete,
    IReadOnlyList<SessionRetentionDecision> Keep,
    SessionRetentionOptions Options)
{
    /// <summary>Render a bounded, deterministic report of the plan.</summary>
    /// <returns>Plain text with one line per planned deletion and a totals header.</returns>
    internal string Render()
    {
        var text = new StringBuilder();
        text.Append("sessions maintenance: ")
            .Append(Delete.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" to delete, ")
            .Append(Keep.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" kept (older than ")
            .Append(Options.MaxAgeDays.ToString(CultureInfo.InvariantCulture))
            .Append("d, keep ")
            .Append(Options.KeepRecent.ToString(CultureInfo.InvariantCulture))
            .Append(" recent).\n");

        for (int i = 0; i < Delete.Count && i < SessionRetentionPlanner.RenderCap; i++)
        {
            SessionRetentionDecision decision = Delete[i];
            text.Append("  DELETE ")
                .Append(decision.Session.Id)
                .Append("  ")
                .Append(decision.Reason)
                .Append("  ")
                .Append(OneLine(decision.Session.Title))
                .Append('\n');
        }

        if (Delete.Count > SessionRetentionPlanner.RenderCap)
        {
            text.Append("  ... and ")
                .Append((Delete.Count - SessionRetentionPlanner.RenderCap).ToString(CultureInfo.InvariantCulture))
                .Append(" more.\n");
        }

        return text.ToString();
    }

    /// <summary>Keep a session title from injecting extra lines into the report.</summary>
    private static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }
}

/// <summary>
///     #2686 K05. Pure parsing and projection for session retention. No IO and
///     no ambient clock: the caller supplies the session list and "now", so
///     the plan is reproducible and testable without a store.
/// </summary>
internal static class SessionRetentionPlanner
{
    /// <summary>Default inactivity threshold, in days.</summary>
    internal const int DefaultMaxAgeDays = 30;

    /// <summary>Default number of recent sessions that are never deleted.</summary>
    internal const int DefaultKeepRecent = 5;

    /// <summary>Upper bound for <see cref="SessionRetentionOptions.MaxAgeDays"/>.</summary>
    internal const int MaxMaxAgeDays = 3650;

    /// <summary>Upper bound for <see cref="SessionRetentionOptions.KeepRecent"/>.</summary>
    internal const int MaxKeepRecent = 1000;

    /// <summary>Maximum number of deletion lines in a rendered plan.</summary>
    internal const int RenderCap = 50;

    /// <summary>Parse the <c>sessions maintenance</c> flag tail, fail closed.</summary>
    /// <param name="tokens">Flags and their values, without the <c>maintenance</c> token.</param>
    /// <returns>The parsed options and apply flag, or the first validation error.</returns>
    internal static Result<SessionMaintenanceArgs> Parse(IReadOnlyList<string> tokens)
    {
        int maxAgeDays = DefaultMaxAgeDays;
        int keepRecent = DefaultKeepRecent;
        bool apply = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            switch (token)
            {
                case "--apply":
                    apply = true;
                    break;
                case "--older-than-days":
                    if (++i >= tokens.Count
                        || !int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out maxAgeDays))
                    {
                        return Result.Failure<SessionMaintenanceArgs>(
                            "sessions maintenance: --older-than-days needs a positive integer.");
                    }
                    break;
                case "--keep-recent":
                    if (++i >= tokens.Count
                        || !int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out keepRecent))
                    {
                        return Result.Failure<SessionMaintenanceArgs>(
                            "sessions maintenance: --keep-recent needs a non-negative integer.");
                    }
                    break;
                default:
                    return Result.Failure<SessionMaintenanceArgs>(
                        $"sessions maintenance: unknown argument '{token}'.");
            }
        }

        Result<SessionRetentionOptions> options = Validate(maxAgeDays, keepRecent);
        return options.IsFailure
            ? Result.Failure<SessionMaintenanceArgs>(options.Error)
            : Result.Success(new SessionMaintenanceArgs(options.Value, apply));
    }

    /// <summary>
    ///     Build a deterministic retention plan. The N most recently updated
    ///     sessions are always kept; the rest are deleted only when their last
    ///     activity is older than the threshold.
    /// </summary>
    /// <param name="sessions">The sessions to classify.</param>
    /// <param name="options">Validated retention thresholds.</param>
    /// <param name="now">The reference time for the inactivity check.</param>
    /// <returns>The plan, or a validation error for out-of-range thresholds.</returns>
    internal static Result<SessionRetentionPlan> Build(
        IReadOnlyList<Session> sessions,
        SessionRetentionOptions options,
        DateTimeOffset now)
    {
        Result<SessionRetentionOptions> validated = Validate(options.MaxAgeDays, options.KeepRecent);
        if (validated.IsFailure) return Result.Failure<SessionRetentionPlan>(validated.Error);

        Session[] ordered = sessions
            .OrderByDescending(static session => session.UpdatedAt)
            .ThenBy(static session => session.Id, StringComparer.Ordinal)
            .ToArray();

        var delete = new List<SessionRetentionDecision>();
        var keep = new List<SessionRetentionDecision>();
        TimeSpan threshold = TimeSpan.FromDays(validated.Value.MaxAgeDays);

        for (int i = 0; i < ordered.Length; i++)
        {
            Session session = ordered[i];
            if (i < validated.Value.KeepRecent)
            {
                keep.Add(new SessionRetentionDecision(session, "recent"));
                continue;
            }

            if (now - session.UpdatedAt > threshold)
            {
                string reason = "inactive > "
                    + validated.Value.MaxAgeDays.ToString(CultureInfo.InvariantCulture)
                    + "d";
                delete.Add(new SessionRetentionDecision(session, reason));
            }
            else
                keep.Add(new SessionRetentionDecision(session, "within retention window"));
        }

        return Result.Success(new SessionRetentionPlan(delete, keep, validated.Value));
    }

    /// <summary>Validate both thresholds with one set of bounds.</summary>
    private static Result<SessionRetentionOptions> Validate(int maxAgeDays, int keepRecent)
    {
        if (maxAgeDays < 1 || maxAgeDays > MaxMaxAgeDays)
        {
            return Result.Failure<SessionRetentionOptions>(
                $"sessions maintenance: --older-than-days must be 1..{MaxMaxAgeDays.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (keepRecent < 0 || keepRecent > MaxKeepRecent)
        {
            return Result.Failure<SessionRetentionOptions>(
                $"sessions maintenance: --keep-recent must be 0..{MaxKeepRecent.ToString(CultureInfo.InvariantCulture)}.");
        }

        return Result.Success(new SessionRetentionOptions(maxAgeDays, keepRecent));
    }
}
