using System.Globalization;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     One parsed <c>harbor issue</c> invocation (#2674 H8, #2686 J05). The
///     first three actions are the original read/write core; <c>create</c> and
///     <c>state</c> complete it, while <c>link</c>/<c>links</c> are dispatched
///     to <see cref="IssueLinkRunner" /> by <see cref="IssueVerb" />.
/// </summary>
internal sealed record IssueRequest(
    string Action,
    int Number,
    string? Repo,
    int Limit,
    string? State,
    string? Body,
    string? BodyFile,
    string? Title);

/// <summary>
///     Pure argument parsing for <c>harbor issue</c>. No process is started and
///     no file is read here, so every rejection is a testable value.
/// </summary>
internal static class IssueArgs
{
    internal const int DefaultLimit = 30;
    internal const int MaxLimit = 100;

    internal const string Usage =
        "usage: harbor issue list [--limit N] [--state open|closed|all] [--repo owner/repo]\n" +
        "       harbor issue read <number> [--repo owner/repo]\n" +
        "       harbor issue comment <number> (--body TEXT | --body-file PATH) [--repo owner/repo]\n" +
        "       harbor issue create --title TEXT [--body TEXT | --body-file PATH] [--repo owner/repo]\n" +
        "       harbor issue state <number> --state open|closed [--repo owner/repo]";

    /// <summary>Parse the raw tail into one request, or a diagnostic string.</summary>
    internal static Result<IssueRequest> Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return Result.Failure<IssueRequest>(Usage);

        string action = args[0].ToLowerInvariant();
        if (action is not ("list" or "read" or "comment" or "create" or "state"))
            return Result.Failure<IssueRequest>($"issue: unknown subcommand '{args[0]}'. {Usage}");

        int number = 0;
        int limit = DefaultLimit;
        string? repo = null;
        string? state = null;
        string? body = null;
        string? bodyFile = null;
        string? title = null;

        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--limit")
            {
                if (action != "list")
                    return Result.Failure<IssueRequest>("issue: --limit is only valid for 'list'.");
                if (++i >= args.Length
                    || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                    || limit < 1 || limit > MaxLimit)
                    return Result.Failure<IssueRequest>($"issue: --limit must be 1..{MaxLimit}.");
            }
            else if (token == "--state")
            {
                if (action is not ("list" or "state"))
                    return Result.Failure<IssueRequest>("issue: --state is only valid for 'list' or 'state'.");
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    return Result.Failure<IssueRequest>("issue: --state needs a non-empty value.");
                bool valid = action == "list"
                    ? args[i] is "open" or "closed" or "all"
                    : args[i] is "open" or "closed";
                if (!valid)
                    return Result.Failure<IssueRequest>(action == "list"
                        ? "issue: --state must be open, closed or all."
                        : "issue: --state must be open or closed.");
                state = args[i];
            }
            else if (token == "--repo")
            {
                if (++i >= args.Length || !IsValidRepo(args[i]))
                    return Result.Failure<IssueRequest>("issue: --repo must be owner/repo.");
                repo = args[i];
            }
            else if (token == "--body")
            {
                if (action is not ("comment" or "create"))
                    return Result.Failure<IssueRequest>("issue: --body is only valid for 'comment' or 'create'.");
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    return Result.Failure<IssueRequest>("issue: --body needs a non-empty value.");
                body = args[i];
            }
            else if (token == "--body-file")
            {
                if (action is not ("comment" or "create"))
                    return Result.Failure<IssueRequest>("issue: --body-file is only valid for 'comment' or 'create'.");
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    return Result.Failure<IssueRequest>("issue: --body-file needs a non-empty path.");
                bodyFile = args[i];
            }
            else if (token == "--title")
            {
                if (action != "create")
                    return Result.Failure<IssueRequest>("issue: --title is only valid for 'create'.");
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    return Result.Failure<IssueRequest>("issue: --title needs a non-empty value.");
                title = args[i];
            }
            else
            {
                if (token.StartsWith('-'))
                    return Result.Failure<IssueRequest>($"issue: unknown option '{token}'.");
                if (action is "list" or "create" || number != 0)
                    return Result.Failure<IssueRequest>($"issue: unexpected argument '{token}'.");
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
                    return Result.Failure<IssueRequest>($"issue: expected a positive issue number, got '{token}'.");
            }
        }

        if ((action is "read" or "comment" or "state") && number == 0)
            return Result.Failure<IssueRequest>($"issue: '{action}' needs a positive issue number. {Usage}");
        if (action == "comment" && (body is null) == (bodyFile is null))
            return Result.Failure<IssueRequest>("issue: 'comment' needs exactly one of --body or --body-file.");
        if (action == "create")
        {
            if (string.IsNullOrWhiteSpace(title))
                return Result.Failure<IssueRequest>("issue: 'create' needs --title.");
            if (body is not null && bodyFile is not null)
                return Result.Failure<IssueRequest>("issue: 'create' accepts at most one of --body or --body-file.");
        }
        if (action == "state" && state is null)
            return Result.Failure<IssueRequest>("issue: 'state' needs --state open|closed.");

        return Result.Success(new IssueRequest(action, number, repo, limit, state, body, bodyFile, title));
    }

    /// <summary>
    ///     Bounded <c>owner/repo</c> check. It rejects option-looking values and
    ///     path traversal without trying to be a full GitHub name validator.
    /// </summary>
    internal static bool IsValidRepo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '/')
                slashes++;
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or ':' or '@')
                return false;
        }

        return slashes == 1
            && value[0] is not ('/' or '-')
            && value[^1] != '/'
            && !value.Contains("..", StringComparison.Ordinal);
    }
}

/// <summary>
///     Pure argv construction for the <c>gh</c> calls behind <c>harbor issue</c>.
///     Every value is a separate argument: no shell string is ever assembled.
/// </summary>
internal static class IssueGhCommand
{
    internal static IReadOnlyList<string> Build(IssueRequest request)
    {
        if (request.Action == "list")
            return BuildList(request);
        if (request.Action == "read")
            return BuildRead(request);
        if (request.Action == "comment")
            return BuildComment(request);
        if (request.Action == "create")
            return BuildCreate(request);
        if (request.Action == "state")
            return BuildState(request);
        throw new InvalidOperationException($"Unknown issue action '{request.Action}'.");
    }

    private static IReadOnlyList<string> BuildList(IssueRequest request)
    {
        var argv = new List<string>
        {
            "issue", "list",
            "--limit", request.Limit.ToString(CultureInfo.InvariantCulture),
            "--state", request.State ?? "open",
            "--json", "number,title,state,url",
        };
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static IReadOnlyList<string> BuildRead(IssueRequest request)
    {
        var argv = new List<string>
        {
            "issue", "view", request.Number.ToString(CultureInfo.InvariantCulture),
            "--json", "number,title,state,url,body,comments",
        };
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static IReadOnlyList<string> BuildComment(IssueRequest request)
    {
        var argv = new List<string> { "issue", "comment", request.Number.ToString(CultureInfo.InvariantCulture) };
        AddBody(argv, request);
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static IReadOnlyList<string> BuildCreate(IssueRequest request)
    {
        var argv = new List<string> { "issue", "create", "--title", request.Title! };
        AddBody(argv, request);
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static IReadOnlyList<string> BuildState(IssueRequest request)
    {
        var argv = new List<string>
        {
            "issue", "edit", request.Number.ToString(CultureInfo.InvariantCulture),
            "--state", request.State!,
        };
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static void AddBody(List<string> argv, IssueRequest request)
    {
        if (request.BodyFile is not null)
        {
            argv.Add("--body-file");
            argv.Add(request.BodyFile);
        }
        else if (request.Body is not null)
        {
            argv.Add("--body");
            argv.Add(request.Body);
        }
    }

    private static void AddRepo(List<string> argv, string? repo)
    {
        if (repo is null)
            return;
        argv.Add("--repo");
        argv.Add(repo);
    }
}
