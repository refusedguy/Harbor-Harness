using System.Buffers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Fetches a URL and returns its content as markdown (HTML stripped, code kept, links
///     inlined). Uses a shared <see cref="HttpClient" />; respects redirects and sets a
///     realistic User-Agent.
/// </summary>
public sealed class WebFetchTool : ITool
{
    private const int DefaultMaxChars = 50_000;
    private const int HardMaxChars = 500_000;
    private const int MaxDownloadBytes = 5 * 1024 * 1024; // 5 MiB hard cap
    private const int HttpTimeoutSeconds = 30;

    private static readonly HttpClient SharedClient = BuildClient();
    private readonly Func<HttpClient> _clientFactory;

    private readonly ILogger<WebFetchTool> _logger;

    /// <summary>
    ///     Construct a <see cref="WebFetchTool" /> that uses the process-wide shared
    ///     <see cref="HttpClient" />. This is the constructor used by DI.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public WebFetchTool(ILogger<WebFetchTool> logger) : this(logger, () => SharedClient)
    {
    }

    /// <summary>
    ///     Construct a <see cref="WebFetchTool" /> with a custom <see cref="HttpClient" />
    ///     factory. Used in tests to inject a mock HTTP handler.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="clientFactory">Factory returning the <see cref="HttpClient" /> to use.</param>
    public WebFetchTool(ILogger<WebFetchTool> logger, Func<HttpClient> clientFactory)
    {
        _logger = logger;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("webfetch");

    /// <inheritdoc />
    public string DisplayName => "WebFetch";

    /// <inheritdoc />
    public string Description =>
        "Fetch a URL and return markdown-converted content. HTML is stripped to text + " +
        "links + code; binary responses are rejected. Use for documentation, search results, " +
        "API endpoints returning text.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "webfetch: Fetch a URL and return markdown";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `webfetch` to read web pages, docs, JSON endpoints",
        "Pass `selector` to extract a single CSS-like element (e.g. 'main', 'article', '#content')",
        "Default maxChars=50000; raise for long pages, lower for snippets",
        "Binary and >5 MiB responses are rejected"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "url":       { "type": "string",  "description": "Absolute URL (http or https)" },
                                                                          "selector":  { "type": "string",  "description": "Optional CSS-like selector to extract a subtree (e.g. 'main', 'article', '#content', '.docs')" },
                                                                          "maxChars":  { "type": "integer", "description": "Max characters of markdown to return (default: 50000, max: 500000)" }
                                                                        },
                                                                        "required": ["url"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlEl.GetString()))
            return Result.Failure("Missing or empty 'url'.");

        string url = urlEl.GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" && uri.Scheme != "https")
        {
            return Result.Failure($"'url' must be an absolute http(s) URL: {url}");
        }

        if (args.TryGetProperty("maxChars", out var mc) && mc.ValueKind == JsonValueKind.Number
                                                        && mc.TryGetInt32(out int max) && max < 1)
            return Result.Failure("'maxChars' must be >= 1.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string url = args.GetProperty("url").GetString()!;
        string? selector = args.TryGetProperty("selector", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        int maxChars = args.TryGetProperty("maxChars", out var m) && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, HardMaxChars)
            : DefaultMaxChars;

        _logger.LogDebug("WebFetch: {Url} (selector={Selector}, maxChars={MaxChars})",
            url, selector ?? "(none)", maxChars);

        var client = _clientFactory();
        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Harbor/0.4 (+https://github.com/harbor)");
            req.Headers.Accept.ParseAdd("text/html, application/json, text/plain, */*;q=0.5");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSeconds));

            response = await client.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("webfetch cancelled");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"Request timed out after {HttpTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"HTTP request failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"webfetch failed: {ex.Message}");
        }

        using (response)
        {
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            long? declaredLength = response.Content.Headers.ContentLength;

            // Reject obvious binary content types up front.
            if (IsBinaryContentType(contentType))
            {
                return ToolResult.Error(
                    $"Refusing to fetch binary content-type '{contentType}' from {url}. " +
                    "Use a dedicated HTTP client if you need raw bytes.");
            }

            if (declaredLength is > MaxDownloadBytes)
            {
                return ToolResult.Error(
                    $"Response too large: declared {declaredLength} bytes (max {MaxDownloadBytes}).");
            }

            byte[] bytes;
            try
            {
                bytes = await ReadCappedAsync(
                    response.Content,
                    MaxDownloadBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ToolResult.Error("webfetch cancelled");
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Failed to read response body: {ex.Message}");
            }

            if (HasNulByte(bytes))
                return ToolResult.Error($"Response body from {url} looks binary ({bytes.Length} bytes).");

            string body = Encoding.UTF8.GetString(bytes);

            string markdown;
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                markdown = body;
            }
            else
            {
                string html = selector is null ? body : ExtractSelector(body, selector);
                markdown = HtmlToMarkdown(html);
            }

            if (markdown.Length > maxChars)
                markdown = markdown[..maxChars] + "\n\n… truncated at " + maxChars + " chars";

            var summary = new StringBuilder(128);
            summary.Append("Fetched ").Append(url).Append(" — ").Append((int)response.StatusCode)
                .Append(' ').Append(response.ReasonPhrase ?? HttpStatusCode.OK.ToString())
                .Append(" (").Append(bytes.Length).Append(" bytes, ").Append(contentType).Append(')');

            return ToolResult.Success(
                summary + "\n\n" + markdown,
                new
                {
                    url,
                    status = (int)response.StatusCode,
                    contentType,
                    sizeBytes = bytes.Length,
                    selector,
                    truncated = markdown.Length >= maxChars,
                    chars = markdown.Length
                });
        }
    }

    private static HttpClient BuildClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler, true)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    private static async Task<byte[]> ReadCappedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, 64 * 1024));
        try
        {
            int total = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                    .ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (total >= maxBytes)
                {
                    return CopyToExact(buffer, maxBytes);
                }
                if (total == buffer.Length)
                {
                    int nextSize = Math.Min(buffer.Length * 2, maxBytes);
                    if (nextSize <= buffer.Length) break;
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(nextSize);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
            }
            return CopyToExact(buffer, total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] CopyToExact(byte[] src, int len)
    {
        byte[] dst = new byte[len];
        Buffer.BlockCopy(src, 0, dst, 0, len);
        return dst;
    }

    private static bool HasNulByte(byte[] bytes)
    {
        int probe = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < probe; i++)
            if (bytes[i] == 0)
                return true;
        return false;
    }

    private static bool IsBinaryContentType(string contentType)
        => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
           || contentType.Contains("font", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/gzip", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/x-tar", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Extremely small "CSS selector" extractor: supports <c>tag</c>, <c>#id</c>,
    ///     <c>.class</c> (first match only).
    /// </summary>
    private static string ExtractSelector(string html, string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0) return html;

        // §ARCH-007: definite-assignment: tag is set in all branches that reach the
        // post-if usage below (the bare #/. case leaves tag empty; we normalize after).
        string tag = string.Empty;
        string? id = null;
        string? cls = null;
        if (selector[0] == '#') id = selector[1..];
        else if (selector[0] == '.') cls = selector[1..];
        else
        {
            int hash = selector.IndexOf('#');
            int dot = selector.IndexOf('.');
            if (hash < 0 && dot < 0) tag = selector;
            else if (hash >= 0 && (dot < 0 || hash < dot))
            {
                tag = selector[..hash];
                id = selector[(hash + 1)..];
            }
            else
            {
                tag = selector[..dot];
                cls = selector[(dot + 1)..];
            }
        }
        tag = (tag.Length == 0 ? "div" : tag).ToLowerInvariant();

        string openPattern = "<" + tag;
        int idx = 0;
        while ((idx = html.IndexOf(openPattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int tagEnd = html.IndexOf('>', idx);
            if (tagEnd < 0) break;
            string attrBlock = html.Substring(idx + openPattern.Length, tagEnd - idx - openPattern.Length);

            if (id is not null)
            {
                if (!attrBlock.Contains("id=\"" + id + "\"", StringComparison.OrdinalIgnoreCase)
                    && !attrBlock.Contains("id='" + id + "'", StringComparison.OrdinalIgnoreCase))
                {
                    idx = tagEnd + 1;
                    continue;
                }
            }
            if (cls is not null)
            {
                if (!attrBlock.Contains("class=\"" + cls + "\"", StringComparison.OrdinalIgnoreCase)
                    && !attrBlock.Contains("class='" + cls + "'", StringComparison.OrdinalIgnoreCase))
                {
                    idx = tagEnd + 1;
                    continue;
                }
            }

            string closePattern = "</" + tag;
            int close = html.IndexOf(closePattern, tagEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return html;
            int closeEnd = html.IndexOf('>', close);
            if (closeEnd < 0) return html;
            return html.Substring(tagEnd + 1, close - tagEnd - 1);
        }
        return html;
    }

    /// <summary>
    ///     Regex-based HTML → markdown converter. No deps. Lossy but agent-friendly.
    /// </summary>
    private static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        string s = html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        var codeBlocks = new List<string>(4);
        s = Regex.Replace(s, @"<pre\b[^>]*>(.*?)</pre>", m =>
        {
            string code = StripTags(m.Groups[1].Value);
            code = WebUtility.HtmlDecode(code).Trim();
            string fenced = "```\n" + code + "\n```";
            codeBlocks.Add(fenced);
            return "\u0000CODE" + (codeBlocks.Count - 1) + "\u0000";
        }, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<code\b[^>]*>(.*?)</code>",
            m => "`" + StripTags(m.Groups[1].Value).Trim() + "`",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int level = 1; level <= 6; level++)
        {
            string h = "h" + level;
            string prefix = new string('#', level) + " ";
            s = Regex.Replace(s, $"<{h}\\b[^>]*>(.*?)</{h}>",
                m => "\n\n" + prefix + StripTags(m.Groups[1].Value).Trim() + "\n\n",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        s = Regex.Replace(s, @"<a\b[^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>",
            m => "[" + StripTags(m.Groups[2].Value).Trim() + "](" + m.Groups[1].Value + ")",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</li>", "", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"</?(p|div|section|article|header|footer|main|nav|aside)\b[^>]*>",
            "\n\n", RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!--.*?-->", "", RegexOptions.Singleline);

        s = StripTags(s);

        for (int i = 0; i < codeBlocks.Count; i++)
            s = s.Replace("\u0000CODE" + i + "\u0000", codeBlocks[i]);

        s = WebUtility.HtmlDecode(s);

        s = Regex.Replace(s, @"\n{3,}", "\n\n");

        return s.Trim();
    }

    private static string StripTags(string s)
        => Regex.Replace(s, @"<[^>]+>", string.Empty);
}
