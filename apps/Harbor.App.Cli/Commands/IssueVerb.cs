using ConsoleAppFramework;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>harbor issue</c> verb (#2674 H8, #2686 J05).</summary>
internal static class CafIssueCommands
{
    /// <summary>List, read, comment, create and state GitHub issues through the gh CLI.</summary>
    /// <param name="limit">Maximum number of issues for 'list' (1..100).</param>
    /// <param name="state">State filter for 'list': open, closed or all.</param>
    /// <param name="repo">Optional owner/repo override.</param>
    /// <param name="body">Inline comment body for 'comment' or 'create'.</param>
    /// <param name="bodyFile">File holding the body for 'comment' or 'create'.</param>
    /// <param name="title">Issue title for 'create'.</param>
    /// <param name="args">Subcommand tail: list, read, comment, create or state.</param>
    public static Task<int> Issue(
        string? limit = null,
        string? state = null,
        string? repo = null,
        string? body = null,
        string? bodyFile = null,
        string? title = null,
        [Argument] params string[] args)
    {
        // Flags are typed for CAF (a dash-led token is never positional); the
        // manual parser is order-insensitive, so re-appending them is enough.
        var tail = new List<string>(args);
        if (limit is not null) { tail.Add("--limit"); tail.Add(limit); }
        if (state is not null) { tail.Add("--state"); tail.Add(state); }
        if (repo is not null) { tail.Add("--repo"); tail.Add(repo); }
        if (body is not null) { tail.Add("--body"); tail.Add(body); }
        if (bodyFile is not null) { tail.Add("--body-file"); tail.Add(bodyFile); }
        if (title is not null) { tail.Add("--title"); tail.Add(title); }
        return IssueVerb.RunAsync(NullLogger.Instance, [.. tail], CancellationToken.None);
    }
}

/// <summary>
///     <c>harbor issue list|read|comment|create|state</c>, the GitHub issue
///     toolkit a terminal agent needs, built on the same <c>gh</c> seam as
///     <c>pr guide</c>.
///     Parsing and projection are pure; only the <c>gh</c> call touches IO.
/// </summary>
internal static class IssueVerb
{
    internal static async Task<int> RunAsync(
        ILogger logger,
        string[] args,
        CancellationToken cancellationToken)
    {
        Result<IssueRequest> parsed = IssueArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        IssueRequest request = parsed.Value;
        Result<string> json = await GhProcessRunner
            .RunAsync(IssueGhCommand.Build(request), cancellationToken)
            .ConfigureAwait(false);
        if (json.IsFailure)
        {
            IssueVerbLog.GhFailed(logger, request.Action, json.Error);
            Console.Error.WriteLine($"issue {request.Action}: {json.Error}");
            return 1;
        }

        if (request.Action == "create")
        {
            Result<IssueProjection.IssueCreated> created = IssueProjection.ParseCreated(json.Value);
            if (created.IsFailure)
            {
                Console.Error.WriteLine($"issue create: {created.Error}");
                return 1;
            }

            IssueVerbLog.Created(logger, created.Value.Number);
            Console.Out.Write(IssueProjection.FormatCreated(created.Value));
            return 0;
        }

        if (request.Action == "state")
        {
            IssueVerbLog.StateChanged(logger, request.Number, request.State!);
            Console.Out.Write(IssueProjection.FormatState(request.Number, request.State!));
            return 0;
        }

        if (request.Action == "list")
        {
            Result<IReadOnlyList<IssueSummary>> issues = IssueProjection.ParseList(json.Value);
            if (issues.IsFailure)
            {
                Console.Error.WriteLine(issues.Error);
                return 1;
            }
            Console.Out.Write(IssueProjection.FormatList(issues.Value));
            return 0;
        }

        if (request.Action == "read")
        {
            Result<IssueDetail> issue = IssueProjection.ParseDetail(json.Value);
            if (issue.IsFailure)
            {
                Console.Error.WriteLine(issue.Error);
                return 1;
            }
            Console.Out.Write(IssueProjection.FormatDetail(issue.Value));
            return 0;
        }

        IssueVerbLog.Commented(logger, request.Number);
        Console.Out.WriteLine($"Commented on #{request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        return 0;
    }
}

/// <summary>Logger-message delegates for <see cref="IssueVerb" />.</summary>
internal static partial class IssueVerbLog
{
    /// <summary>A gh invocation failed.</summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "issue {Action} failed: {Error}")]
    public static partial void GhFailed(ILogger logger, string action, string error);

    /// <summary>A comment was posted.</summary>
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Commented on issue #{Number}")]
    public static partial void Commented(ILogger logger, int number);

    /// <summary>An issue was created.</summary>
    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Created issue #{Number}")]
    public static partial void Created(ILogger logger, int number);

    /// <summary>An issue's state changed.</summary>
    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Set issue #{Number} to {State}")]
    public static partial void StateChanged(ILogger logger, int number, string state);
}
