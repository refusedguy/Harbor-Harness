using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using ConsoleAppFramework;
using Harbor.Abstractions.Filesystem;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     One session ↔ pull request link (#2686 J03). The four fields named by
///     the report (<c>url</c>, <c>repo</c>, <c>number</c>, <c>state</c>) are
///     joined with the session that owns the link and the worktree it was
///     created in, so the reverse lookup can name both ends.
/// </summary>
internal sealed record SessionPullRequestLink(
    string SessionId,
    string Repo,
    int Number,
    string State,
    string Url,
    string Worktree,
    string LinkedAt);

/// <summary>
///     Pure parsing, filtering and formatting of session ↔ pull request
///     links. No process and no file are touched here, so every rejection is
///     a testable value.
/// </summary>
internal static class PullRequestLinkProjection
{
    /// <summary>Cap so one link file cannot flood stdout.</summary>
    internal const int MaxLinks = 500;

    /// <summary>Cap on a single text field, inherited from the other forge projections.</summary>
    internal const int MaxTextChars = 2000;

    internal const string Usage =
        "usage: harbor pr link --session <id> --pr <number> [--repo owner/repo]\n" +
        "       harbor pr links (--session <id> | --pr <number>) [--repo owner/repo]";

    /// <summary>Build one link from a forge pull request and a session id.</summary>
    internal static Result<SessionPullRequestLink> Build(
        string? sessionId,
        string? repo,
        ForgePullRequest pullRequest,
        string worktree,
        string linkedAt)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<SessionPullRequestLink>("--session must not be empty.");
        if (pullRequest.Number <= 0)
            return Result.Failure<SessionPullRequestLink>("a positive pull-request number is required.");

        string ownerRepo = string.IsNullOrWhiteSpace(repo)
            ? RepoFromUrl(pullRequest.Url)
            : repo.Trim();
        if (!IsValidRepo(ownerRepo))
            return Result.Failure<SessionPullRequestLink>("--repo must be owner/repo, or the PR url must contain one.");

        return Result.Success(new SessionPullRequestLink(
            sessionId.Trim(),
            ownerRepo,
            pullRequest.Number,
            NormalizeState(pullRequest.State),
            Truncate(pullRequest.Url),
            Truncate(worktree),
            linkedAt));
    }

    /// <summary>Decode one JSONL line, failing closed on an unexpected shape.</summary>
    internal static Result<SessionPullRequestLink> ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Result.Failure<SessionPullRequestLink>("link line is empty.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result.Failure<SessionPullRequestLink>("link line is not an object.");

            return ReadText(root, "session")
                .Bind(session => ReadText(root, "repo")
                    .Bind(repo => ValidRepo(repo)
                        .Bind(validRepo => ReadText(root, "state")
                            .Bind(state => ReadText(root, "url")
                                .Bind(url => ReadNumber(root)
                                    .Map(number => new SessionPullRequestLink(
                                        session,
                                        validRepo,
                                        number,
                                        state,
                                        url,
                                        ReadText(root, "worktree").GetValueOrDefault(string.Empty),
                                        ReadText(root, "linkedAt").GetValueOrDefault(string.Empty))))))));
        }
        catch (JsonException ex)
        {
            return Result.Failure<SessionPullRequestLink>($"link line is not valid JSON: {ex.Message}");
        }
    }

    private static Result<string> ValidRepo(string repo) =>
        IsValidRepo(repo)
            ? Result.Success(repo)
            : Result.Failure<string>("link line has an invalid repo.");

    private static Result<int> ReadNumber(JsonElement obj)
    {
        if (!obj.TryGetProperty("number", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int number)
            || number <= 0)
        {
            return Result.Failure<int>("link line has an invalid number.");
        }

        return Result.Success(number);
    }

    /// <summary>Encode one link as a single JSON line with no embedded newline.</summary>
    internal static string FormatLine(SessionPullRequestLink link)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, link);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Keep links that match every supplied criterion.</summary>
    internal static IReadOnlyList<SessionPullRequestLink> Filter(
        IReadOnlyList<SessionPullRequestLink> links,
        string? sessionId,
        string? repo,
        int? number)
    {
        var matches = new List<SessionPullRequestLink>();
        foreach (SessionPullRequestLink link in links)
        {
            if (sessionId is not null && !string.Equals(link.SessionId, sessionId, StringComparison.Ordinal))
                continue;
            if (repo is not null && !string.Equals(link.Repo, repo, StringComparison.OrdinalIgnoreCase))
                continue;
            if (number is not null && link.Number != number.Value)
                continue;
            matches.Add(link);
        }

        return matches;
    }

    /// <summary>Render links as the JSON array the CLI prints.</summary>
    internal static string Format(IReadOnlyList<SessionPullRequestLink> links)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            int written = 0;
            foreach (SessionPullRequestLink link in links)
            {
                if (written >= MaxLinks)
                    break;
                Write(writer, link);
                written++;
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, SessionPullRequestLink link)
    {
        writer.WriteStartObject();
        writer.WriteString("session", link.SessionId);
        writer.WriteString("repo", link.Repo);
        writer.WriteNumber("number", link.Number);
        writer.WriteString("state", link.State);
        writer.WriteString("url", link.Url);
        writer.WriteString("worktree", link.Worktree);
        writer.WriteString("linkedAt", link.LinkedAt);
        writer.WriteEndObject();
    }

    private static Result<string> ReadText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return Result.Failure<string>($"link line has an invalid '{name}'.");
        return Result.Success(value.GetString() ?? string.Empty);
    }

    private static string NormalizeState(string state) =>
        string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase)
            ? "closed"
            : "open";

    private static string Truncate(string text) =>
        text.Length <= MaxTextChars ? text : text[..MaxTextChars] + "…";

    /// <summary>Derive <c>owner/repo</c> from a GitHub pull-request url.</summary>
    internal static string RepoFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // gh returns https://<host>/<owner>/<repo>/pull/<number>. Cut at the
        // 'pull' marker, then walk back two separators to the repository.
        const string marker = "/pull/";
        int start = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        string after = url[(start + marker.Length)..];
        string number = after.TrimEnd('/');
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return string.Empty;

        string head = url[..start];
        int repoSlash = head.LastIndexOf('/');
        if (repoSlash <= 0)
            return string.Empty;
        int ownerSlash = head.LastIndexOf('/', repoSlash - 1);
        if (ownerSlash < 0)
            return string.Empty;

        return head[(ownerSlash + 1)..repoSlash] + "/" + head[(repoSlash + 1)..];
    }

    /// <summary>Bounded <c>owner/repo</c> check shared with the issue verb.</summary>
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
///     Append-only JSONL store for session ↔ pull request links. The path is
///     under the resolved Harbor home and is never built from user input.
/// </summary>
internal sealed class PullRequestLinkStore
{
    private readonly string _path;

    internal PullRequestLinkStore(string? path = null)
        => _path = path ?? Path.Combine(HarborPaths.GetHarborHome(), "session-pr-links.jsonl");

    /// <summary>Read all links, or an empty list when the store has no file yet.</summary>
    internal Result<IReadOnlyList<SessionPullRequestLink>> ReadAll()
    {
        if (!File.Exists(_path))
            return Result.Success<IReadOnlyList<SessionPullRequestLink>>(Array.Empty<SessionPullRequestLink>());

        try
        {
            using IEnumerator<string> lines = File.ReadLines(_path).GetEnumerator();
            return ParseLines(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<IReadOnlyList<SessionPullRequestLink>>($"cannot read link file: {ex.Message}");
        }
    }

    private static Result<IReadOnlyList<SessionPullRequestLink>> ParseLines(IEnumerator<string> lines)
    {
        Result<IReadOnlyList<SessionPullRequestLink>> accumulated =
            Result.Success<IReadOnlyList<SessionPullRequestLink>>(Array.Empty<SessionPullRequestLink>());
        int lineNumber = 0;
        while (lines.MoveNext())
        {
            lineNumber++;
            string line = lines.Current;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (accumulated.IsSuccess && accumulated.Value.Count >= PullRequestLinkProjection.MaxLinks)
                break;

            int current = lineNumber;
            accumulated = accumulated.Bind(list => PullRequestLinkProjection.ParseLine(line)
                .MapError(error => $"link file line {current.ToString(CultureInfo.InvariantCulture)}: {error}")
                .Map(item => (IReadOnlyList<SessionPullRequestLink>)new List<SessionPullRequestLink>(list) { item }));
        }

        return accumulated;
    }

    /// <summary>Append one link unless the same session/repo/number is already stored.</summary>
    internal Result Add(SessionPullRequestLink link) => ReadAll().Bind(items => Append(items, link));

    private Result Append(IReadOnlyList<SessionPullRequestLink> items, SessionPullRequestLink link)
    {
        foreach (SessionPullRequestLink item in items)
        {
            if (string.Equals(item.SessionId, link.SessionId, StringComparison.Ordinal)
                && string.Equals(item.Repo, link.Repo, StringComparison.OrdinalIgnoreCase)
                && item.Number == link.Number)
            {
                return Result.Success();
            }
        }

        if (items.Count >= PullRequestLinkProjection.MaxLinks)
            return Result.Failure($"link file is full ({PullRequestLinkProjection.MaxLinks} links).");

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(_path, PullRequestLinkProjection.FormatLine(link) + "\n", new UTF8Encoding(false));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure($"cannot write link file: {ex.Message}");
        }
    }
}

/// <summary>CAF handlers for the session ↔ pull request link verbs (#2686 J03).</summary>
internal static class CafPrLinkCommands
{
    /// <summary>Record a link between a session and a pull request.</summary>
    /// <param name="args"><c>--session &lt;id&gt; --pr &lt;number&gt; [--repo owner/repo]</c>.</param>
    public static Task<int> Link([Argument] params string[] args) =>
        PrLinkRunner.LinkAsync(args, CancellationToken.None);

    /// <summary>List links by session or by pull request (reverse lookup).</summary>
    /// <param name="args"><c>(--session &lt;id&gt; | --pr &lt;number&gt;) [--repo owner/repo]</c>.</param>
    public static Task<int> Links([Argument] params string[] args) =>
        PrLinkRunner.LinksAsync(args, CancellationToken.None);
}

/// <summary>
///     Turns CLI arguments and a forge pull request into a stored link, and
///     answers reverse lookups. All shaping is done by
///     <see cref="PullRequestLinkProjection" />; this runner only owns IO.
/// </summary>
internal static class PrLinkRunner
{
    internal static async Task<int> LinkAsync(
        string[] args,
        CancellationToken cancellationToken,
        IForgeClient? forge = null,
        PullRequestLinkStore? store = null)
    {
        string? sessionId = null;
        int? number = null;
        string? repo = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length && sessionId is null)
            {
                sessionId = args[++i];
                continue;
            }

            if (args[i] == "--repo" && i + 1 < args.Length && repo is null)
            {
                repo = args[++i];
                continue;
            }

            if (args[i] == "--pr" && i + 1 < args.Length && number is null
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                && parsed > 0)
            {
                number = parsed;
                continue;
            }

            Console.Error.WriteLine(PullRequestLinkProjection.Usage);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || number is null
            || (repo is not null && !PullRequestLinkProjection.IsValidRepo(repo)))
        {
            Console.Error.WriteLine(PullRequestLinkProjection.Usage);
            return 2;
        }

        Result<ForgePullRequest?> read = await (forge ?? new GitHubForgeClient())
            .GetPullRequestAsync(number.Value, cancellationToken)
            .ConfigureAwait(false);

        return read
            .Bind(item => item is null
                ? Result.Failure<ForgePullRequest>($"no readable pull request #{number.Value.ToString(CultureInfo.InvariantCulture)}.")
                : Result.Success(item))
            .Bind(item => PullRequestLinkProjection.Build(
                sessionId,
                repo,
                item,
                Environment.CurrentDirectory,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)))
            .Bind(link => (store ?? new PullRequestLinkStore()).Add(link).Map(() => link))
            .Map(link =>
            {
                Console.Out.Write(PullRequestLinkProjection.Format([link]));
                Console.Out.Write('\n');
                return 0;
            })
            .MapError(error => $"pr link: {error}")
            .Match(
                exitCode => exitCode,
                error =>
                {
                    Console.Error.WriteLine(error);
                    return 1;
                });
    }

    internal static Task<int> LinksAsync(
        string[] args,
        CancellationToken cancellationToken,
        PullRequestLinkStore? store = null)
    {
        string? sessionId = null;
        int? number = null;
        string? repo = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length && sessionId is null)
            {
                sessionId = args[++i];
                continue;
            }

            if (args[i] == "--repo" && i + 1 < args.Length && repo is null)
            {
                repo = args[++i];
                continue;
            }

            if (args[i] == "--pr" && i + 1 < args.Length && number is null
                && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                && parsed > 0)
            {
                number = parsed;
                continue;
            }

            Console.Error.WriteLine(PullRequestLinkProjection.Usage);
            return Task.FromResult(2);
        }

        if (sessionId is null && number is null
            || (repo is not null && !PullRequestLinkProjection.IsValidRepo(repo)))
        {
            Console.Error.WriteLine(PullRequestLinkProjection.Usage);
            return Task.FromResult(2);
        }

        return Task.FromResult((store ?? new PullRequestLinkStore()).ReadAll()
            .Map(links => PullRequestLinkProjection.Filter(links, sessionId, repo, number))
            .Map(matches =>
            {
                Console.Out.Write(PullRequestLinkProjection.Format(matches));
                Console.Out.Write('\n');
                return 0;
            })
            .MapError(error => $"pr links: {error}")
            .Match(
                exitCode => exitCode,
                error =>
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }));
    }
}
