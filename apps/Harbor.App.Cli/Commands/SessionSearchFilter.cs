using System.Globalization;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     One token of a <c>harbor sessions search</c> filter expression.
/// </summary>
/// <param name="Slot">Unique zero-based slot: every token is an independent AND constraint.</param>
/// <param name="Key">Normalised (lower-case) filter key, with "id" for a bare legacy value.</param>
/// <param name="Value">Normalised (lower-case) filter value.</param>
/// <param name="Date">Parsed instant for after:/before: tokens; null for every other key.</param>
public readonly record struct SessionFilterToken(int Slot, string Key, string Value, DateTimeOffset? Date = null);

/// <summary>
///     Tokenizer for <c>harbor sessions search</c> flags (#2675 item 010).
///     A filter is a whitespace-separated list of <c>key:value</c> tokens;
///     different keys are ANDed together, and a key may repeat to require
///     several values. Values may be double-quoted when they contain spaces.
/// </summary>
/// <remarks>
///     This type owns syntax only. It deliberately has no <c>Matches</c> method.
///     Selecting sessions is the job of <c>FacetedHistoryState</c> via
///     <c>SessionFacetedHistoryAdapter</c>. Values are lower-cased here
///     because the primitive compares facet values ordinally while the CLI
///     filter is case-insensitive.
/// </remarks>
public sealed class SessionSearchFilter
{
    private const string SupportedKeys =
        "id, session, title, label, project, repo, dir, parent, agent, model, provider, branch, status, kind, after, before";

    private static readonly string[] KnownKeys =
    [
        "id", "session", "title", "label", "project", "repo", "dir", "parent",
        "agent", "model", "provider", "branch", "status", "kind", "after", "before"
    ];

    private readonly IReadOnlyList<SessionFilterToken> _tokens;

    private SessionSearchFilter(IReadOnlyList<SessionFilterToken> tokens) => _tokens = tokens;

    /// <summary>True when the filter selects every session.</summary>
    public bool IsEmpty => _tokens.Count == 0;

    /// <summary>Validated, normalised tokens in expression order.</summary>
    public IReadOnlyList<SessionFilterToken> Tokens => _tokens;

    /// <summary>
    ///     Parse <paramref name="expression"/>. Null or whitespace means "no
    ///     filter"; a bare value stays a session-id substring for backward
    ///     compatibility with <c>--session</c>.
    /// </summary>
    public static Result<SessionSearchFilter> Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Result.Success(new SessionSearchFilter(Array.Empty<SessionFilterToken>()));

        // One railway from tokenization to the finished filter: no guard site
        // can swallow a failure and report a filter that selects everything.
        return Tokenize(expression)
            .Bind(static tokens => tokens.Aggregate(
                Result.Success(new Accumulator()),
                static (acc, token) => acc.Bind(a => ApplyToken(a, token))))
            .Map(static acc => new SessionSearchFilter(acc.Tokens));
    }

    private static Result<Accumulator> ApplyToken(Accumulator acc, string token)
    {
        int colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            if (acc.HasBareId)
                return Result.Failure<Accumulator>(
                    $"Ambiguous session-id filter: '{acc.BareId}' and '{token}'. Use id: for each value.");

            // Legacy --session id substring, normalised like every other value.
            acc.BareId = token;
            acc.HasBareId = true;
            acc.Tokens.Add(new SessionFilterToken(acc.Tokens.Count, "id", token.ToLowerInvariant()));
            return Result.Success(acc);
        }

        string key = token[..colon].ToLowerInvariant();
        string value = token[(colon + 1)..];
        if (value.Length == 0)
            return Result.Failure<Accumulator>($"Filter '{key}:' needs a value.");

        if (key is "after" or "before")
        {
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset date))
                return Result.Failure<Accumulator>($"Filter '{key}' needs a date (e.g. 2026-11-19), got '{value}'.");

            acc.Tokens.Add(new SessionFilterToken(acc.Tokens.Count, key, value.ToLowerInvariant(), date));
            return Result.Success(acc);
        }

        if (Array.IndexOf(KnownKeys, key) < 0)
            return Result.Failure<Accumulator>($"Unsupported filter key '{key}'. Supported: {SupportedKeys}.");

        acc.Tokens.Add(new SessionFilterToken(acc.Tokens.Count, key, value.ToLowerInvariant()));
        return Result.Success(acc);
    }

    private sealed class Accumulator
    {
        public List<SessionFilterToken> Tokens { get; } = [];
        public bool HasBareId { get; set; }
        public string? BareId { get; set; }
    }

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
}
