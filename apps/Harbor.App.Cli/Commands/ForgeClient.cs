using System.Globalization;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>One pull request as returned by a forge client (#2686 J02).</summary>
internal sealed record ForgePullRequest(int Number, string Title, string State, string Url);

/// <summary>
///     Pure projection of <c>gh pr list/view --json</c> output. The forge
///     adapter owns process I/O; this class only reshapes the JSON into the
///     small record the CLI prints.
/// </summary>
internal static class ForgeProjection
{
    /// <summary>Cap so one repository cannot flood stdout.</summary>
    internal const int MaxPullRequests = 50;

    internal static IReadOnlyList<ForgePullRequest> BuildList(string? json)
    {
        using JsonDocument? document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ForgePullRequest>();
        }

        var pullRequests = new List<ForgePullRequest>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (pullRequests.Count >= MaxPullRequests) break;
            if (item.ValueKind == JsonValueKind.Object) pullRequests.Add(Project(item));
        }

        return pullRequests;
    }

    internal static ForgePullRequest? BuildOne(string? json)
    {
        using JsonDocument? document = Parse(json);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return null;
        return Project(document.RootElement);
    }

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ForgePullRequest Project(JsonElement item) => new(
        NumberOf(item, "number"),
        TextOf(item, "title"),
        TextOf(item, "state"),
        TextOf(item, "url"));

    private static int NumberOf(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) ? parsed : 0;
    }

    private static string TextOf(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }
}

/// <summary>
///     Read-only forge seam used by the CLI's pull-request verbs. Keeping it
///     narrow makes a second forge adapter a separate file rather than a
///     change to every caller.
/// </summary>
internal interface IForgeClient
{
    /// <summary>Stable forge id, for diagnostics and future selection.</summary>
    string Id { get; }

    /// <summary>List open pull requests in the current repository.</summary>
    Task<Result<IReadOnlyList<ForgePullRequest>>> ListOpenPullRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Read one pull request, or null when the forge has no such number.</summary>
    Task<Result<ForgePullRequest?>> GetPullRequestAsync(
        int number,
        CancellationToken cancellationToken = default);
}

/// <summary>The first <see cref="IForgeClient"/> adapter, backed by the GitHub CLI.</summary>
internal sealed class GitHubForgeClient : IForgeClient
{
    public string Id => "github";

    public Task<Result<IReadOnlyList<ForgePullRequest>>> ListOpenPullRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > ForgeProjection.MaxPullRequests)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<ForgePullRequest>>(
                $"limit must be between 1 and {ForgeProjection.MaxPullRequests}."));
        }

        return RunAndProjectAsync(
            ["pr", "list", "--state", "open", "--json", "number,title,state,url", "--limit", limit.ToString(CultureInfo.InvariantCulture)],
            ForgeProjection.BuildList,
            cancellationToken);
    }

    public Task<Result<ForgePullRequest?>> GetPullRequestAsync(
        int number,
        CancellationToken cancellationToken = default)
    {
        if (number <= 0)
        {
            return Task.FromResult(Result.Failure<ForgePullRequest?>("pull-request number must be positive."));
        }

        return RunAndProjectAsync(
            ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "--json", "number,title,state,url"],
            ForgeProjection.BuildOne,
            cancellationToken);
    }

    private static async Task<Result<T>> RunAndProjectAsync<T>(
        IReadOnlyList<string> args,
        Func<string?, T> project,
        CancellationToken cancellationToken)
    {
        Result<string> json = await GhProcessRunner.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return json.Map(project);
    }
}
