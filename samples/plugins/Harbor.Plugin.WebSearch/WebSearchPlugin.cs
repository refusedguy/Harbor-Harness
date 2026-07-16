using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugin.WebSearch;
/// <summary>
///     Sample plugin that adds web search capability.
///     Uses DuckDuckGo HTML endpoint — no API key required.
///     Demonstrates:
///     - IToolPlugin pattern
///     - Tool registration via IToolRegistryBuilder
///     - Plugin lifecycle (Initialize / Shutdown)
/// </summary>
public sealed class WebSearchPlugin : IToolPlugin
{

    internal static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Harbor/0.2 (https://harbor.sh)" }
        }
    };
    public string Name => "websearch";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 2, 0);
    public string Description => "Web search via DuckDuckGo (no API key needed)";

    public void Initialize(PluginContext context) => context.CreateLogger<WebSearchPlugin>().LogInformation("WebSearch plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<WebSearchTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
///     Web search tool — queries DuckDuckGo HTML endpoint and parses results.
/// </summary>
public sealed class WebSearchTool : ITool
{
    public ToolName Name => ToolName.Create("websearch");
    public string DisplayName => "Web Search";
    public string Description => "Search the web using DuckDuckGo. Returns titles, URLs, and snippets of top results. No API key needed.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "websearch: Search the web (DuckDuckGo, no API key)";
    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `websearch` when you need current information not in your training data",
        "Use `webfetch` to read full page content from a specific URL"
    };

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "query": { "type": "string", "description": "Search query" },
                                                                          "maxResults": { "type": "integer", "description": "Maximum results to return (default: 5, max: 20)" }
                                                                        },
                                                                        "required": ["query"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing 'query' argument.");
        if (string.IsNullOrWhiteSpace(q.GetString()))
            return Result.Failure("'query' cannot be empty.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string query = args.GetProperty("query").GetString()!;
        int maxResults = args.TryGetProperty("maxResults", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Min(Math.Max(m.GetInt32(), 1), 20)
            : 5;

        string url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";

        try
        {
            string html = await WebSearchPlugin.HttpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var results = ParseResults(html, maxResults);

            if (results.Count == 0)
                return ToolResult.Success($"No results found for: {query}");

            var output = new StringBuilder();
            output.AppendLine($"Found {results.Count} results for '{query}':");
            output.AppendLine();
            for (int i = 0; i < results.Count; i++)
            {
                output.AppendLine($"{i + 1}. {results[i].Title}");
                output.AppendLine($"   {results[i].Url}");
                output.AppendLine($"   {results[i].Snippet}");
                output.AppendLine();
            }

            return ToolResult.Success(output.ToString(), new { count = results.Count, query });
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Web search failed: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        // DuckDuckGo HTML uses <a class="result__a" href="...">title</a>
        // and <a class="result__snippet" ...>snippet</a>
        var linkPattern = new Regex(
            @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var snippetPattern = new Regex(
            @"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var tagStrip = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        var linkMatches = linkPattern.Matches(html);
        var snippetMatches = snippetPattern.Matches(html);

        for (int i = 0; i < Math.Min(linkMatches.Count, maxResults); i++)
        {
            string url = linkMatches[i].Groups[1].Value;
            // DuckDuckGo redirects through /l/?uddg=... — extract actual URL
            if (url.Contains("uddg="))
            {
                int uddgIdx = url.IndexOf("uddg=", StringComparison.Ordinal);
                if (uddgIdx >= 0)
                {
                    int end = url.IndexOf('&', uddgIdx);
                    string encoded = end >= 0
                        ? url.Substring(uddgIdx + 5, end - uddgIdx - 5)
                        : url[(uddgIdx + 5)..];
                    url = Uri.UnescapeDataString(encoded);
                }
            }

            string title = tagStrip.Replace(linkMatches[i].Groups[2].Value, "").Trim();
            string snippet = i < snippetMatches.Count
                ? tagStrip.Replace(snippetMatches[i].Groups[1].Value, "").Trim()
                : "";

            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(url))
            {
                results.Add(new SearchResult(title, url, snippet));
            }
        }

        return results;
    }

    private sealed record SearchResult(string Title, string Url, string Snippet);
}
