using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Fetches a URL and returns its content as markdown (HTML stripped, code kept, links
///     inlined). Uses a shared <see cref="HttpClient" />, follows redirects manually with
///     per-hop safety re-validation, and sets a realistic User-Agent.
/// </summary>
/// <remarks>
///     <para>
///         <b>SSRF protection:</b> every request target — the original URL
///         and every redirect hop — is resolved via DNS and <b>all</b>
///         returned IPs are checked against a non-public-address deny list:
///         loopback (127.0.0.0/8, ::1), link-local (169.254.0.0/16 incl. the
///         cloud-metadata endpoint 169.254.169.254, fe80::/10), RFC1918
///         private ranges (10/8, 172.16/12, 192.168/16), IPv6 unique-local
///         (fc00::/7), 0.0.0.0/8 + ::, and IPv6-mapped IPv4 equivalents
///         (::ffff:a.b.c.d). Blocking is the default; hosts can be opted in
///         deliberately via <see cref="AllowedHosts" /> (e.g. local
///         development against an Ollama-style endpoint).
///     </para>
///     <para>
///         Redirects are followed manually
///         (<c>HttpClientHandler.AllowAutoRedirect = false</c>) so that each
///         hop goes through the same validation with fresh DNS resolution,
///         capped at <see cref="MaxRedirectHops" /> hops.
///     </para>
///     <para>
///         Known limitation: the DNS check happens just before the request,
///         while <see cref="HttpClient" /> performs its own resolution at
///         connect time — a rebinding attacker with very short TTLs can
///         still slip past the pre-check. This raises the bar substantially
///         but is not a hermetic containment boundary.
///     </para>
/// </remarks>
public sealed class WebFetchTool : ITool
{
    private const int DefaultMaxChars = 50_000;
    private const int HardMaxChars = 500_000;
    private const int MaxDownloadBytes = 5 * 1024 * 1024; // 5 MiB hard cap
    private const int HttpTimeoutSeconds = 30;

    /// <summary>Maximum number of redirects followed per fetch.</summary>
    public const int MaxRedirectHops = 5;

    private static readonly HttpClient SharedClient = BuildClient();
    private readonly Func<HttpClient> _clientFactory;

    private readonly ILogger<WebFetchTool> _logger;

    /// <summary>
    ///     Construct a <see cref="WebFetchTool" /> that uses the process-wide shared
    ///     <see cref="HttpClient" />. This is the constructor used by DI.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public WebFetchTool(ILogger<WebFetchTool> logger) : this(logger, () => SharedClient, allowedHosts: null)
    {
    }

    /// <summary>
    ///     Construct a <see cref="WebFetchTool" /> with a custom <see cref="HttpClient" />
    ///     factory and an optional SSRF allowlist. Used in tests to inject a mock HTTP handler.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="clientFactory">Factory returning the <see cref="HttpClient" /> to use.</param>
    /// <param name="allowedHosts">
    ///     Optional seed for <see cref="AllowedHosts" />. Host names listed here are exempt
    ///     from the non-public-address check (deliberate opt-out for local development).
    /// </param>
    public WebFetchTool(
        ILogger<WebFetchTool> logger,
        Func<HttpClient> clientFactory,
        IEnumerable<string>? allowedHosts = null)
    {
        _logger = logger;
        _clientFactory = clientFactory;
        if (allowedHosts is not null)
        {
            foreach (string host in allowedHosts)
            {
                AllowedHosts.Add(host);
            }
        }
    }

    /// <summary>
    ///     Deliberate SSRF opt-out: host names exempt from the non-public
    ///     address check. Default is EMPTY — every private/loopback/link-local
    ///     target is blocked until a host is added here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Matching is case-insensitive against the URL's IDN-normalized
    ///         host (e.g. <c>"localhost"</c>, <c>"127.0.0.1"</c>,
    ///         <c>"host.docker.internal"</c>). The special entry
    ///         <c>"*"</c> disables the address check entirely — use only in
    ///         trusted test environments.
    ///     </para>
    ///     <para>
    ///         Mutate before the first <c>ExecuteAsync</c> call (or pass
    ///         entries via the constructor overload) — instances are not
    ///         thread-safe during mutation.
    ///     </para>
    /// </remarks>
    public ICollection<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        string? selector = JsonArgs.GetString(args, "selector");
        int maxChars = JsonArgs.GetInt(args, "maxChars") is { } chars
            ? Math.Clamp(chars, 1, HardMaxChars)
            : DefaultMaxChars;

        _logger.LogDebug("WebFetch: {Url} (selector={Selector}, maxChars={MaxChars})",
            url, selector ?? "(none)", maxChars);

        var client = _clientFactory();

        // Manual redirect following with per-hop re-validation (SSRF guard):
        // the original URL and every redirect hop are resolved via DNS and
        // all resulting IPs are checked before connecting.
        var fetched = await FetchWithRedirectsAsync(
            client, url, cancellationToken).ConfigureAwait(false);
        if (fetched.IsFailure)
        {
            return ToolResult.Error(fetched.Error);
        }

        HttpResponseMessage response = fetched.Value.Response;
        Uri finalUri = fetched.Value.FinalUri;
        int hops = fetched.Value.Hops;

        using (response)
        {
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            long? declaredLength = response.Content.Headers.ContentLength;

            // Reject obvious binary content types up front.
            if (IsBinaryContentType(contentType))
            {
                return ToolResult.Error(
                    $"Refusing to fetch binary content-type '{contentType}' from {finalUri}. " +
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
                return ToolResult.Error($"Response body from {finalUri} looks binary ({bytes.Length} bytes).");

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
            summary.Append("Fetched ").Append(finalUri).Append(" — ").Append((int)response.StatusCode)
                .Append(' ').Append(response.ReasonPhrase ?? HttpStatusCode.OK.ToString())
                .Append(" (").Append(bytes.Length).Append(" bytes, ").Append(contentType).Append(')');

            return ToolResult.Success(
                summary + "\n\n" + markdown,
                new
                {
                    url = finalUri.ToString(),
                    status = (int)response.StatusCode,
                    contentType,
                    sizeBytes = bytes.Length,
                    hops,
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
            // Redirects are followed MANUALLY in FetchWithRedirectsAsync so
            // every hop can be re-validated against the SSRF deny list with
            // fresh DNS resolution before we connect.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler, true)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    /// <summary>Successful terminal state of a redirect-aware fetch (ROP-A Z1 п.1).</summary>
    /// <param name="FinalUri">The URI the response ultimately came from.</param>
    /// <param name="Response">The final (non-redirect) response.</param>
    /// <param name="Hops">Number of HTTP requests issued (1 = no redirects).</param>
    /// <remarks>
    ///     Replaces the former nullable-quadruple <c>FetchOutcome</c>: an
    ///     invalid "error AND response present" state is now unrepresentable.
    /// </remarks>
    private sealed record FetchOk(Uri FinalUri, HttpResponseMessage Response, int Hops);

    /// <summary>
    ///     GET a URL following up to <see cref="MaxRedirectHops" /> redirects
    ///     manually. Every hop — original URL included — is validated via
    ///     <see cref="GetBlockedReasonAsync" /> (DNS resolve + IP deny list)
    ///     before connecting; DNS is therefore re-resolved whenever the host
    ///     changes (and even when it does not).
    /// </summary>
    private async Task<Result<FetchOk>> FetchWithRedirectsAsync(
        HttpClient client,
        string url,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? current)
            || current.Scheme != "http" && current.Scheme != "https")
        {
            return Result.Failure<FetchOk>($"'url' must be an absolute http(s) URL: {url}");
        }

        // ROP-A Z1 п.2: one exception classifier instead of two hand-copied
        // catch ladders (send loop here, DNS gate below).
        Result<FetchOk> SendFailure(Exception ex) => Result.Failure<FetchOk>(
            ex is OperationCanceledException
                ? CancelOrTimeoutMessage(ct, ex)
                : ex switch
                {
                    HttpRequestException h => $"HTTP request failed: {h.Message}",
                    _ => $"webfetch failed: {ex.Message}"
                });

        for (int hop = 0; hop <= MaxRedirectHops + 1; hop++)
        {
            // Cap total requests at 1 + MaxRedirectHops.
            if (hop > MaxRedirectHops)
            {
                return Result.Failure<FetchOk>(
                    $"Blocked URL '{url}': exceeded {MaxRedirectHops} redirects.");
            }

            string? blocked = await GetBlockedReasonAsync(current, ct).ConfigureAwait(false);
            if (blocked is not null)
            {
                return Result.Failure<FetchOk>(blocked);
            }

            HttpResponseMessage resp;
            using (var req = new HttpRequestMessage(HttpMethod.Get, current))
            {
                req.Headers.UserAgent.ParseAdd("Harbor/0.4 (+https://github.com/harbor)");
                req.Headers.Accept.ParseAdd("text/html, application/json, text/plain, */*;q=0.5");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSeconds));
                try
                {
                    resp = await client.SendAsync(
                        req,
                        HttpCompletionOption.ResponseHeadersRead,
                        cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return SendFailure(ex);
                }
            }

            bool isRedirect = resp.StatusCode
                is HttpStatusCode.MovedPermanently   // 301
                or HttpStatusCode.Found              // 302
                or HttpStatusCode.SeeOther           // 303
                or HttpStatusCode.TemporaryRedirect  // 307
                or HttpStatusCode.PermanentRedirect; // 308

            if (!isRedirect)
            {
                return Result.Success(new FetchOk(current, resp, hop + 1));
            }

            Uri? location = resp.Headers.Location;
            if (location is null)
            {
                // 3xx without Location — no hop to follow; hand the response
                // back like HttpClient's auto-redirect would.
                return Result.Success(new FetchOk(current, resp, hop + 1));
            }

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            resp.Dispose();

            if (next.Scheme != "http" && next.Scheme != "https")
            {
                return Result.Failure<FetchOk>(
                    $"Blocked redirect from '{current}' to '{next}': only http/https schemes are supported.");
            }

            current = next;
        }

        // Not reachable: the final loop iteration returns the redirect-cap
        // outcome above. Kept so the compiler sees every path returns.
        return Result.Failure<FetchOk>(
            $"Blocked URL '{url}': exceeded {MaxRedirectHops} redirects.");
    }

    /// <summary>Shared cancel-vs-timeout wording (ROP-A Z1 п.2): one source.</summary>
    private string CancelOrTimeoutMessage(CancellationToken ct, Exception ex) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => "webfetch cancelled",
        _ => $"Request timed out after {HttpTimeoutSeconds}s."
    };

    /// <summary>
    ///     SSRF gate for one request target. Returns <see langword="null" />
    ///     when fetching <paramref name="uri" /> is allowed, otherwise a
    ///     human-readable reason.
    /// </summary>
    /// <remarks>
    ///     Hosts listed in <see cref="AllowedHosts" /> (or the wildcard
    ///     <c>"*"</c>) bypass the check. Otherwise ALL addresses the host
    ///     resolves to must be public — a single private/loopback/link-local
    ///     address fails closed. DNS is resolved fresh on every call so each
    ///     redirect hop re-checks its (possibly changed) host.
    /// </remarks>
    private async Task<string?> GetBlockedReasonAsync(Uri uri, CancellationToken ct)
    {
        string host = uri.IdnHost;

        if (AllowedHosts.Contains("*") || AllowedHosts.Contains(host))
        {
            _logger.LogDebug("WebFetch: host {Host} is explicitly allowed past the private-address check", host);
            return null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // ROP-A Z1 п.2: same cancel/timeout wording as the send loop.
            return CancelOrTimeoutMessage(ct, ex);
        }
        catch (SocketException ex)
        {
            return $"Blocked URL '{uri}' (fail-closed): host '{host}' could not be resolved ({ex.Message}).";
        }
        catch (ArgumentException ex)
        {
            return $"Blocked URL '{uri}' (fail-closed): invalid host '{host}' ({ex.Message}).";
        }

        if (addresses.Length == 0)
        {
            return $"Blocked URL '{uri}' (fail-closed): host '{host}' resolved to no addresses.";
        }

        foreach (IPAddress address in addresses)
        {
            if (!IsNonPublicAddress(address))
            {
                continue;
            }

            _logger.LogWarning(
                "WebFetch blocked SSRF attempt: {Url} resolves to non-public address {Address}",
                uri, address);
            return $"Blocked URL '{uri}': host '{host}' resolves to non-public address {address}. " +
                   "Fetching loopback/private/link-local targets is disabled by default; " +
                   "add the host to WebFetchTool.AllowedHosts to allow it deliberately.";
        }

        return null;
    }

    /// <summary>
    ///     True when <paramref name="address" /> must never be fetched:
    ///     loopback, link-local (incl. cloud metadata), RFC1918 private,
    ///     IPv6 unique-local, unspecified ("this network"), broadcast, and
    ///     IPv6-mapped IPv4 forms of all of them.
    /// </summary>
    private static bool IsNonPublicAddress(IPAddress address)
    {
        // Normalize IPv6-mapped IPv4 (::ffff:a.b.c.d) down to plain IPv4 so
        // the same v4 rules apply.
        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = normalized.GetAddressBytes();
            return b[0] == 0                             // 0.0.0.0/8 "this network"
                || b[0] == 127                           // loopback 127.0.0.0/8
                || b[0] == 10                            // RFC1918 10.0.0.0/8
                || (b[0] == 172 && (b[1] & 0xF0) == 16)  // RFC1918 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)          // RFC1918 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)          // link-local 169.254.0.0/16 (cloud metadata!)
                || normalized.Equals(IPAddress.Broadcast); // 255.255.255.255
        }

        byte[] v6 = normalized.GetAddressBytes();
        return v6.Length == 16
            && (IPAddress.IPv6Any.Equals(normalized)     // :: unspecified
                || IPAddress.IPv6Loopback.Equals(normalized) // ::1
                || normalized.IsIPv6LinkLocal            // fe80::/10
                || (v6[0] & 0xFE) == 0xFC                // unique-local fc00::/7
                || normalized.IsIPv6Multicast);          // ff00::/8 — never fetchable
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
