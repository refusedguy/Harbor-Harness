using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>One issue row as returned by <c>gh issue list --json</c>.</summary>
internal sealed record IssueSummary(int Number, string Title, string State, string Url);

/// <summary>One comment on an issue.</summary>
internal sealed record IssueComment(string Author, string Body);

/// <summary>The projection of <c>gh issue view --json</c> that the user sees.</summary>
internal sealed record IssueDetail(
    int Number,
    string Title,
    string State,
    string Url,
    string Body,
    IReadOnlyList<IssueComment> Comments);

/// <summary>
///     Pure projection of the two <c>gh</c> JSON shapes H8 consumes. Parsing is
///     fail-closed: a malformed or unexpected payload is an error, never a
///     half-rendered issue. Text is capped so one issue cannot flood stdout.
/// </summary>
internal static class IssueProjection
{
    internal const int MaxItems = 100;
    internal const int MaxComments = 50;
    internal const int MaxTextChars = 8000;

    /// <summary>Decode the <c>gh issue list --json</c> array.</summary>
    internal static Result<IReadOnlyList<IssueSummary>> ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Failure<IReadOnlyList<IssueSummary>>("issue list: gh returned no output.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<IssueSummary>>("issue list: expected a JSON array.");

            var items = new List<IssueSummary>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (items.Count >= MaxItems)
                    break;
                if (element.ValueKind != JsonValueKind.Object)
                    return Result.Failure<IReadOnlyList<IssueSummary>>("issue list: expected an object per issue.");

                JsonElement number = Required(element, "number");
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int issueNumber) || issueNumber <= 0)
                    return Result.Failure<IReadOnlyList<IssueSummary>>("issue list: invalid issue number.");

                items.Add(new IssueSummary(
                    issueNumber,
                    Truncate(RequiredText(element, "title")),
                    NormalizeState(RequiredText(element, "state")),
                    Truncate(RequiredText(element, "url"))));
            }

            return Result.Success<IReadOnlyList<IssueSummary>>(items);
        }
        catch (JsonException ex)
        {
            return Result.Failure<IReadOnlyList<IssueSummary>>($"issue list: invalid JSON from gh: {ex.Message}");
        }
    }

    /// <summary>Decode the <c>gh issue view --json</c> object.</summary>
    internal static Result<IssueDetail> ParseDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Failure<IssueDetail>("issue read: gh returned no output.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result.Failure<IssueDetail>("issue read: expected a JSON object.");

            JsonElement number = Required(root, "number");
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int issueNumber) || issueNumber <= 0)
                return Result.Failure<IssueDetail>("issue read: invalid issue number.");

            var comments = new List<IssueComment>();
            if (root.TryGetProperty("comments", out JsonElement commentsElement)
                && commentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement comment in commentsElement.EnumerateArray())
                {
                    if (comments.Count >= MaxComments || comment.ValueKind != JsonValueKind.Object)
                        continue;
                    comments.Add(new IssueComment(
                        Truncate(Author(comment)),
                        Truncate(OptionalText(comment, "body"))));
                }
            }

            return Result.Success(new IssueDetail(
                issueNumber,
                Truncate(RequiredText(root, "title")),
                NormalizeState(RequiredText(root, "state")),
                Truncate(RequiredText(root, "url")),
                Truncate(OptionalText(root, "body")),
                comments));
        }
        catch (JsonException ex)
        {
            return Result.Failure<IssueDetail>($"issue read: invalid JSON from gh: {ex.Message}");
        }
    }

    /// <summary>Compact one-line-per-issue rendering for a terminal.</summary>
    internal static string FormatList(IReadOnlyList<IssueSummary> issues)
    {
        var text = new StringBuilder();
        foreach (IssueSummary issue in issues)
        {
            text.Append('#').Append(issue.Number.ToString(CultureInfo.InvariantCulture))
                .Append("  [").Append(issue.State).Append("]  ")
                .Append(issue.Title)
                .Append("  ").Append(issue.Url)
                .Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Body-first rendering of one issue and its comments.</summary>
    internal static string FormatDetail(IssueDetail issue)
    {
        var text = new StringBuilder();
        text.Append('#').Append(issue.Number.ToString(CultureInfo.InvariantCulture))
            .Append(" [").Append(issue.State).Append("] ").Append(issue.Title).Append('\n')
            .Append(issue.Url).Append('\n');
        if (issue.Body.Length > 0)
            text.Append('\n').Append(issue.Body).Append('\n');

        if (issue.Comments.Count > 0)
        {
            text.Append("\n-- comments (")
                .Append(issue.Comments.Count.ToString(CultureInfo.InvariantCulture))
                .Append(") --\n");
            foreach (IssueComment comment in issue.Comments)
                text.Append('[').Append(comment.Author).Append("] ").Append(comment.Body).Append('\n');
        }

        return text.ToString();
    }

    private static JsonElement Required(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) ? value : default;

    private static string RequiredText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"missing or non-string '{name}'");
        return value.GetString() ?? string.Empty;
    }

    private static string OptionalText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Author(JsonElement comment)
    {
        if (!comment.TryGetProperty("author", out JsonElement author) || author.ValueKind != JsonValueKind.Object)
            return "unknown";
        return OptionalText(author, "login");
    }

    private static string NormalizeState(string state) =>
        string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase) ? "closed" : "open";

    private static string Truncate(string text) =>
        text.Length <= MaxTextChars ? text : text[..MaxTextChars] + "…";
}
