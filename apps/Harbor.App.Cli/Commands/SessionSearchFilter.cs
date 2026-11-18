using System.Globalization;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Filter language for <c>harbor sessions search</c> (#2675 item 010).
///     A filter is a whitespace-separated list of <c>key:value</c> tokens;
///     different keys are ANDed together, and a key may repeat to require
///     several values. Values may be double-quoted when they contain spaces.
/// </summary>
/// <remarks>
///     This is a CLI-local parser over the sessions <see cref="ISessionStore"/>
///     already returns. It adds no seam and no storage API: the store keeps
///     deciding <em>what</em> is persisted, while this type decides which of
///     those sessions a human asked to search. A bare value with no colon is
///     the legacy session-id substring filter and keeps its old meaning.
/// </remarks>
public sealed class SessionSearchFilter
{
    private const string SupportedKeys =
        "id, session, title, label, project, repo, dir, parent, agent, model, provider, branch, status, kind, after, before";

    private readonly List<Criterion> _criteria;
    private readonly string? _sessionIdSubstring;

    private SessionSearchFilter(List<Criterion> criteria, string? sessionIdSubstring)
    {
        _criteria = criteria;
        _sessionIdSubstring = sessionIdSubstring;
    }

    /// <summary>True when the filter selects every session.</summary>
    public bool IsEmpty => _criteria.Count == 0 && _sessionIdSubstring is null;

    /// <summary>
    ///     Parse <paramref name="expression"/>. Null or whitespace means "no
    ///     filter"; a bare value stays a session-id substring for backward
    ///     compatibility with <c>--session</c>.
    /// </summary>
    public static Result<SessionSearchFilter> Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Result.Success(new SessionSearchFilter([], null));

        // One railway from tokenization to the finished filter: no guard site
        // can swallow a failure and report a filter that selects everything.
        return Tokenize(expression)
            .Bind(static tokens => tokens.Aggregate(
                Result.Success(new Accumulator()),
                static (acc, token) => acc.Bind(a => ApplyToken(a, token))))
            .Map(static acc => new SessionSearchFilter(acc.Criteria, acc.IdSubstring));
    }

    private static Result<Accumulator> ApplyToken(Accumulator acc, string token)
    {
        int colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            if (acc.IdSubstring is not null)
                return Result.Failure<Accumulator>(
                    $"Ambiguous session-id filter: '{acc.IdSubstring}' and '{token}'. Use id: for each value.");
            acc.IdSubstring = token;
            return Result.Success(acc);
        }

        string key = token[..colon].ToLowerInvariant();
        string value = token[(colon + 1)..];
        if (value.Length == 0)
            return Result.Failure<Accumulator>($"Filter '{key}:' needs a value.");

        return Criterion.Create(key, value)
            .Tap(criterion => acc.Criteria.Add(criterion))
            .Map(_ => acc);
    }

    private sealed class Accumulator
    {
        public List<Criterion> Criteria { get; } = [];
        public string? IdSubstring { get; set; }
    }

    /// <summary>True when <paramref name="session"/> satisfies every criterion.</summary>
    public bool Matches(Session session)
    {
        if (_sessionIdSubstring is not null
            && !Contains(session.Id, _sessionIdSubstring))
            return false;

        for (int i = 0; i < _criteria.Count; i++)
        {
            if (!_criteria[i].Matches(session))
                return false;
        }

        return true;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Split on whitespace, keeping double-quoted values together. An
    ///     unterminated quote is a parse error instead of a silently greedy
    ///     value that would match nothing.
    /// </summary>
    private static Result<List<string>> Tokenize(string expression)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        bool started = false;

        foreach (char c in expression)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                    tokens.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }

            current.Append(c);
            started = true;
        }

        if (quoted)
            return Result.Failure<List<string>>("Unterminated quote in search filter.");
        if (started)
            tokens.Add(current.ToString());
        if (tokens.Count == 0)
            return Result.Failure<List<string>>("Search filter has no tokens.");

        return Result.Success(tokens);
    }

    private sealed class Criterion
    {
        private readonly Func<Session, string, bool> _predicate;
        private readonly string _value;

        private Criterion(Func<Session, string, bool> predicate, string value)
        {
            _predicate = predicate;
            _value = value;
        }

        public static Result<Criterion> Create(string key, string value)
        {
            Result<Criterion> Ok(Func<Session, string, bool> predicate) =>
                Result.Success(new Criterion(predicate, value));

            return key switch
            {
                "id" or "session" => Ok((s, v) => Contains(s.Id, v)),
                "title" or "label" => Ok((s, v) => Contains(s.Title, v)),
                "project" => Ok((s, v) => Contains(s.ProjectId, v)),
                "repo" or "dir" => Ok((s, v) => Contains(s.Directory, v)),
                "parent" => Ok((s, v) => s.ParentSessionId is not null && Contains(s.ParentSessionId, v)),
                "agent" => Ok((s, v) => Contains(s.Agent, v)),
                "model" => Ok((s, v) => Contains(s.Model, v)),
                "provider" => Ok((s, v) => Contains(s.ProviderId, v)),
                "branch" => Ok((s, v) => s.GitBranch is not null && Contains(s.GitBranch, v)),
                "status" => Ok((s, v) => string.Equals(s.Status.ToString(), v, StringComparison.OrdinalIgnoreCase)),
                "kind" => Ok((s, v) => string.Equals(s.Kind.ToString(), v, StringComparison.OrdinalIgnoreCase)),
                "after" or "before" => DateCriterion(key, value),
                _ => Result.Failure<Criterion>($"Unsupported filter key '{key}'. Supported: {SupportedKeys}."),
            };
        }

        public bool Matches(Session session) => _predicate(session, _value);

        private static Result<Criterion> DateCriterion(string key, string value)
        {
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset date))
                return Result.Failure<Criterion>($"Filter '{key}' needs a date (e.g. 2026-11-19), got '{value}'.");

            return key == "after"
                ? Result.Success(new Criterion((s, _) => s.CreatedAt >= date, value))
                : Result.Success(new Criterion((s, _) => s.CreatedAt <= date, value));
        }
    }
}
