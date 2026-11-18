using System.Globalization;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     One parsed <c>harbor issue</c> invocation (#2674 H8). The three actions
///     are the T3 issue toolkit's read/write core; state changes and
///     thread-linking stay separate slices because they need a session contract.
/// </summary>
/// <param name="Action">One of <c>list</c>, <c>read</c> or <c>comment</c>.</param>
/// <param name="Number">Issue number for <c>read</c>/<c>comment</c>, otherwise 0.</param>
/// <param name="Repo">Optional <c>owner/repo</c> override.</param>
/// <param name="Limit">Page size for <c>list</c>.</param>
/// <param name="State">Issue state filter for <c>list</c>.</param>
/// <param name="Body">Inline comment body.</param>
/// <param name="BodyFile">Path to a comment body file.</param>
internal sealed record IssueRequest(
    string Action,
    int Number,
    string? Repo,
    int Limit,
    string? State,
    string? Body,
    string? BodyFile);

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
        "       harbor issue comment <number> (--body TEXT | --body-file PATH) [--repo owner/repo]";

    /// <summary>Parse the raw tail into one request, or a diagnostic string.</summary>
    internal static Result<IssueRequest> Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return Result.Failure<IssueRequest>(Usage);

        string action = args[0].ToLowerInvariant();
        if (action is not ("list" or "read" or "comment"))
            return Result.Failure<IssueRequest>($"issue: unknown subcommand '{args[0]}'. {Usage}");

        int number = 0;
        int limit = DefaultLimit;
        string? repo = null;
        string? state = null;
        string? body = null;
        string? bodyFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            switch (token)
            {
                case "--limit":
                    if (action != "list")
                        return Result.Failure<IssueRequest>("issue: --limit is only valid for 'list'.");
                    if (++i >= args.Length
                        || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                        || limit < 1 || limit > MaxLimit)
                        return Result.Failure<IssueRequest>($"issue: --limit must be 1..{MaxLimit}.");
                    break;

                case "--state":
                    if (action != "list")
                        return Result.Failure<IssueRequest>("issue: --state is only valid for 'list'.");
                    if (++i >= args.Length || args[i] is not ("open" or "closed" or "all"))
                        return Result.Failure<IssueRequest>("issue: --state must be open, closed or all.");
                    state = args[i];
                    break;

                case "--repo":
                    if (++i >= args.Length || !IsValidRepo(args[i]))
                        return Result.Failure<IssueRequest>("issue: --repo must be owner/repo.");
                    repo = args[i];
                    break;

                case "--body":
                    if (action != "comment")
                        return Result.Failure<IssueRequest>("issue: --body is only valid for 'comment'.");
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                        return Result.Failure<IssueRequest>("issue: --body needs a non-empty value.");
                    body = args[i];
                    break;

                case "--body-file":
                    if (action != "comment")
                        return Result.Failure<IssueRequest>("issue: --body-file is only valid for 'comment'.");
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                        return Result.Failure<IssueRequest>("issue: --body-file needs a non-empty path.");
                    bodyFile = args[i];
                    break;

                default:
                    if (token.StartsWith('-'))
                        return Result.Failure<IssueRequest>($"issue: unknown option '{token}'.");
                    if (number != 0)
                        return Result.Failure<IssueRequest>($"issue: unexpected argument '{token}'.");
                    if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
                        return Result.Failure<IssueRequest>($"issue: expected a positive issue number, got '{token}'.");
                    break;
            }
        }

        if ((action is "read" or "comment") && number == 0)
            return Result.Failure<IssueRequest>($"issue: '{action}' needs a positive issue number. {Usage}");
        if (action == "comment" && (body is null) == (bodyFile is null))
            return Result.Failure<IssueRequest>("issue: 'comment' needs exactly one of --body or --body-file.");

        return Result.Success(new IssueRequest(action, number, repo, limit, state, body, bodyFile));
    }

    /// <summary>
    ///     Bounded <c>owner/repo</c> check. It rejects option-looking values and
    ///     path traversal without trying to be a full GitHub name validator.
    /// </summary>
    internal static bool IsValidRepo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0)
            return false;
        if (value.StartsWith('-') || value.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or ':' or '@')
                return false;
        }

        return true;
    }
}

/// <summary>
///     Pure argv construction for the <c>gh</c> calls behind <c>harbor issue</c>.
///     Every value is a separate argument: no shell string is ever assembled.
/// </summary>
internal static class IssueGhCommand
{
    internal static IReadOnlyList<string> Build(IssueRequest request) => request.Action switch
    {
        "list" => BuildList(request),
        "read" => BuildRead(request),
        "comment" => BuildComment(request),
        _ => throw new InvalidOperationException($"Unknown issue action '{request.Action}'."),
    };

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
        if (request.BodyFile is not null)
        {
            argv.Add("--body-file");
            argv.Add(request.BodyFile);
        }
        else
        {
            argv.Add("--body");
            argv.Add(request.Body!);
        }
        AddRepo(argv, request.Repo);
        return argv;
    }

    private static void AddRepo(List<string> argv, string? repo)
    {
        if (repo is null)
            return;
        argv.Add("--repo");
        argv.Add(repo);
    }
}
