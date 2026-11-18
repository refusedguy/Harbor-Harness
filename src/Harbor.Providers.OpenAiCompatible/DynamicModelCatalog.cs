using Microsoft.Extensions.Logging;
namespace Harbor.Providers.OpenAiCompatible;

/// <summary>
///     Model catalog — fetches and caches models list.
/// </summary>
public interface IModelCatalog
{
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default);
}

public sealed class DynamicModelCatalog : IModelCatalog
{
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly ILogger<DynamicModelCatalog> _logger;

    // #1932: the catalogue's freshness window is read through an injected
    // clock, not through DateTimeOffset.UtcNow. TimeProvider is the BCL clock
    // seam the product already uses (ProviderRegistry #1878, AgentLoop,
    // ToolDispatcher, RetryPolicy, MascotDirector), so the 24-hour TTL can be
    // aged in a test instead of by waiting a day.
    private readonly TimeProvider _time;

    /// <summary>
    ///     Sentinel for a model that did not report <c>context_length</c>. Do
    ///     not invent a positive window: a fake 4096 looks authoritative to
    ///     consumers, so the compaction guard treats it as a real (tiny) window
    ///     and the context-usage UI draws a bogus 4k bar. 0 is the repo-wide
    ///     "window not reported" value that consumers already branch on.
    /// </summary>
    private const int UnknownContextWindow = 0;

    /// <summary>
    ///     Construct the catalogue.
    /// </summary>
    /// <param name="http">Client the model list is fetched over.</param>
    /// <param name="cacheDir">Directory the cached catalogue lives in.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="timeProvider">
    ///     Clock source for the catalogue's freshness window (#1932). Production
    ///     passes nothing and reads <see cref="TimeProvider.System" />; a test
    ///     passes a manual clock so the cache can be aged on demand.
    /// </param>
    public DynamicModelCatalog(
        HttpClient http,
        string cacheDir,
        ILogger<DynamicModelCatalog> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);
    }

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default)
    {
        // Hardcoded models in config.
        //
        // #848: stamped from `config.Id`, NOT returned as the JSON spelled it.
        // This branch used to hand back `config.Models` verbatim, so the
        // `providerId` inside a provider JSON's `models` array was a second,
        // unchecked source of truth for "which provider am I" — while
        // `JsonProviderDiscovery` registered the same file under its `id`. A
        // gateway entry stamped with its upstream (`{ "id": "gateway",
        // "models": [ { ..., "providerId": "anthropic" } ] }`) therefore
        // answered to the registry key `gateway` and told every reader
        // `anthropic`: the prompt rendered `- Model: anthropic/…`,
        // ProviderModelPickerViewModel's `m.ProviderId == group.Id` filter hid
        // the model, and CompactionService resolved the wrong ILlmClient to
        // summarize with.
        //
        // `ParseModel` already stamps `config.Id` for the fetched path; this
        // makes the hardcoded path obey the same invariant. The file's `id` is
        // the one registry key, so the stamp is DERIVED here rather than
        // trusted — and `~/.harbor/providers/*.json` (user-writable, and
        // checked first) can no longer diverge from it.
        if (config.Models is { Count: > 0 })
            return Result.Success(StampWithOwnProvider(config.Models, config.Id));

        if (string.IsNullOrEmpty(config.ModelsUrl))
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Provider '{config.Id}' has no modelsUrl and no hardcoded models.");

        // ROP-A ПР.8 — canonical Compensate chain: fresh cache → network →
        // stale cache. Each fallback fires only when the previous source
        // failed; the headline error stays the FETCH failure, not a cache miss.
        string cachePath = Path.Combine(_cacheDir, $"{config.Id}.json");
        TimeSpan maxAge = TimeSpan.FromHours(config.ModelsRefreshHours);

        return await ReadCacheAsync(cachePath, config, ct, freshOnly: true, maxAge: maxAge)
            .Compensate(_ => FetchAndCacheAsync(config, config.ModelsUrl!, cachePath, ct))
            .Compensate(fetchError => ReadCacheAsync(cachePath, config, ct, freshOnly: false, maxAge: maxAge)
                .MapError(_ => fetchError))
            .ConfigureAwait(false);
    }

    /// <summary>Fetch the live model list and seed the cache.</summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> FetchAndCacheAsync(
        ProviderConfig config, string modelsUrl, string cachePath, CancellationToken ct)
    {
        // Result.Try (CSharpFunctionalExtensions 3.7.0) is the library form of
        // wrapping a throwing call in a Result. The parse step stays OUTSIDE
        // the Try and is chained with Bind: it has its own catch-all (see
        // ParseModelsResponse), so folding it in would double-report, and the
        // two failure texts differ (fetch vs parse).
        return await Result.Try(
                async () =>
                {
                    string response = await _http.GetStringAsync(modelsUrl, ct).ConfigureAwait(false);
                    Directory.CreateDirectory(_cacheDir);
                    await File.WriteAllTextAsync(cachePath, response, ct).ConfigureAwait(false);

                    // #1932: the file's freshness mark is the PRODUCT's clock,
                    // not the filesystem's. The reader below compares the same
                    // clock against that mark, so the entry this path writes
                    // expires for the reader exactly `maxAge` after it was
                    // written — not "24 hours after whatever the filesystem
                    // felt like when it stamped the file".
                    StampFreshness(cachePath);
                    return response;
                },
                ex =>
                {
                    DynamicModelCatalogLog.FetchModelsFailed(_logger, ex, config.Id);
                    return ex.Message;
                })
            .Bind(response => ParseModelsResponse(response, config))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Read the cached model list. <paramref name="freshOnly" /> enforces
    ///     the <paramref name="maxAge" /> window (a missing or aged-out cache is
    ///     a MISS, reported distinctly from a corrupt cache).
    /// </summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> ReadCacheAsync(
        string path, ProviderConfig config, CancellationToken ct, bool freshOnly, TimeSpan maxAge)
    {
        if (freshOnly)
        {
            if (!File.Exists(path))
                return Result.Failure<IReadOnlyList<ModelInfo>>("no cached model catalog");

            // #1932: "now" comes from the injected clock, so the window is
            // testable without waiting a day. "Written at" is the file's own
            // freshness mark — stamped by the fetch path from the same clock,
            // so the two ends of the window are one clock's opinion.
            DateTimeOffset writtenAt = new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (_time.GetUtcNow() - writtenAt >= maxAge)
                return Result.Failure<IReadOnlyList<ModelInfo>>("cached model catalog is stale");
        }

        // Result.Try + Bind, mirroring FetchAndCacheAsync: the read is the
        // fallible I/O step, the parse keeps its own catch-all and its own
        // failure text.
        return await Result.Try(
                () => File.ReadAllTextAsync(path, ct),
                ex =>
                {
                    // A cache read failure is otherwise invisible: the
                    // Compensate chain folds it into a generic fallback and
                    // the caller only sees the FETCH error text.
                    DynamicModelCatalogLog.CacheReadFailed(_logger, ex, config.Id, path);
                    return $"Cache read failed: {ex.Message}";
                })
            .Bind(json => ParseModelsResponse(json, config))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Stamp the cache file's freshness mark from the injected clock
    ///     (#1932).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The catalogue's TTL has two ends: the instant the entry was
    ///         written, and the instant it is read. Before #1932 the first end
    ///         was the FILESYSTEM's clock (the file's own last-write time) and
    ///         the second was the wall clock — two clocks that only ever
    ///         agreed by accident, and a seam that could be pulled from one
    ///         side only. Writing the mark from the same clock the reader uses
    ///         makes the window one clock's, on both ends.
    ///     </para>
    ///     <para>
    ///         BEST-EFFORT: a filesystem that refuses the stamp keeps its own
    ///         write time, which in production is the same instant (both are
    ///         the system clock) — so the TTL still means what it meant. A
    ///         failed stamp must never fail the fetch that produced a good
    ///         catalogue.
    ///     </para>
    /// </remarks>
    private void StampFreshness(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or ArgumentOutOfRangeException)
        {
            DynamicModelCatalogLog.FreshnessStampFailed(_logger, ex, path);
        }
    }

    /// <summary>
    ///     Parse a models-list response through <c>JsonElement</c> traversal.
    ///     The <c>ModelsPath</c> (and per-field mapping paths) are per-provider
    ///     dotted JSON keys, so no static record can serve them — the traversal
    ///     stays dynamic by design. JSON keys are not CLR members (arch-gate
    ///     TRIM-MAY-NOT-RESOLVE-A-CLR-MEMBER-BY-A-STRING-NAME ignores them),
    ///     and no <c>JsonSerializer</c> call is involved here. Cold leg by
    ///     design (one parse per fetch/cache-read, never per-turn/per-frame).
    /// </summary>
    private Result<IReadOnlyList<ModelInfo>> ParseModelsResponse(string json, ProviderConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = root;
            if (!string.IsNullOrEmpty(config.ModelsPath))
            {
                foreach (string part in config.ModelsPath.Split('.'))
                {
                    if (!modelsArray.TryGetProperty(part, out var next))
                        return Result.Failure<IReadOnlyList<ModelInfo>>($"Path '{config.ModelsPath}' not found in response.");
                    modelsArray = next;
                }
            }
            else if (root.TryGetProperty("data", out var data))
            {
                modelsArray = data;
            }
            else if (root.TryGetProperty("models", out var models))
            {
                modelsArray = models;
            }

            if (modelsArray.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<ModelInfo>>("Models response is not an array.");

            var mapping = config.ModelMapping ?? new ModelMapping();
            var result = new List<ModelInfo>();

            // Skip accounting (#1581): a corrupted catalog entry must not
            // silently shorten the returned list. Every entry that fails to
            // parse — either a normal Failure from ParseModel or an unexpected
            // exception — is counted here with its index and reason, and the
            // accounting below decides how the skips surface.
            var skips = new List<string>();
            int total = modelsArray.GetArrayLength();

            for (int i = 0; i < total; i++)
            {
                try
                {
                    var parsed = ParseModel(modelsArray[i], config, mapping);
                    if (parsed.IsSuccess)
                        result.Add(parsed.Value);
                    else
                        skips.Add($"[{i}] {parsed.Error}");
                }
                catch (Exception ex)
                {
                    DynamicModelCatalogLog.ParseModelFailed(_logger, ex, i);
                    skips.Add($"[{i}] {ex.Message}");
                }
            }

            if (skips.Count == 0)
                return Result.Success<IReadOnlyList<ModelInfo>>(result);

            string reasons = string.Join("; ", skips);

            // Every entry was skipped: returning an empty Success would send
            // "Model 'x' not found in provider catalog" downstream and bury the
            // real cause in the log. Keep the true cause on the Result rail.
            if (result.Count == 0)
                return Result.Failure<IReadOnlyList<ModelInfo>>(
                    $"Failed to parse models response: all {total} model entries were skipped — {reasons}");

            DynamicModelCatalogLog.ModelsSkipped(_logger, config.Id, skips.Count, total, reasons);
            return Result.Success<IReadOnlyList<ModelInfo>>(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to parse models response: {ex.Message}");
        }
    }

    /// <summary>
    ///     Re-stamp a hardcoded catalog with the id of the config that carries
    ///     it (#848).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A catalog is fetched under exactly one registry key, so every
    ///         entry it returns must carry that key — see the call site for what
    ///         a divergent stamp costs. The JSON's own <c>providerId</c> is not a
    ///         second source of truth for "which provider am I"; the file's
    ///         <c>id</c> is, because that is what
    ///         <c>JsonProviderDiscovery</c> registers the file under.
    ///     </para>
    ///     <para>
    ///         ALLOCATION: the list is returned as-is when every entry already
    ///         agrees, which is the case for every catalog the bundled provider
    ///         JSONs ship today. Only a diverging entry allocates a replacement
    ///         <see cref="ModelInfo" /> (and the array holding it), so the fix
    ///         costs nothing on the happy path and cannot be defeated by an
    ///         unrelated member being rewritten — <c>with</c> copies the record
    ///         and changes one member.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<ModelInfo> StampWithOwnProvider(IReadOnlyList<ModelInfo> models, string providerId)
    {
        List<ModelInfo>? restamped = null;

        for (int i = 0; i < models.Count; i++)
        {
            ModelInfo model = models[i];

            if (restamped is null)
            {
                // Ordinal, not case-insensitive: a registry lookup is ordinal
                // (`ProviderId.Create`, the `seenIds` HashSet in
                // JsonProviderDiscovery), so "OpenRouter" is NOT the id
                // "openrouter" is registered under, and passing it off as
                // agreeing would bless a provider string nothing resolves.
                if (string.Equals(model.ProviderId, providerId, StringComparison.Ordinal))
                {
                    continue;
                }

                // First divergence: start the replacement, carrying every
                // entry before it verbatim.
                restamped = CopyUpTo(models, i);
            }

            // Past this point every entry is appended, whether it agreed or
            // not — an agreeing entry that FOLLOWS a divergent one still has
            // to reach the caller, or the fix would silently shorten the
            // catalog.
            restamped.Add(
                string.Equals(model.ProviderId, providerId, StringComparison.Ordinal)
                    ? model
                    : model with { ProviderId = providerId });
        }

        return (IReadOnlyList<ModelInfo>?)restamped ?? models;
    }

    /// <summary>
    ///     A fresh list holding entries <c>[0, count)</c> of <paramref name="source" />
    ///     verbatim — used to build the replacement lazily, so a catalog that
    ///     needs no re-stamping allocates nothing.
    /// </summary>
    private static List<ModelInfo> CopyUpTo(IReadOnlyList<ModelInfo> source, int count)
    {
        var copy = new List<ModelInfo>(source.Count);
        for (int i = 0; i < count; i++)
        {
            copy.Add(source[i]);
        }

        return copy;
    }

    /// <summary>
    ///     Parse one models-array entry onto the Result rail (#1581): a record
    ///     without an <c>id</c> is a <see cref="Result" /> failure carrying the
    ///     reason, not a silent <c>null</c>. The caller counts failures and
    ///     reports them, so a corrupted entry surfaces in the catalog
    ///     diagnostics instead of shortening the list by one with no trace.
    /// </summary>
    private static Result<ModelInfo> ParseModel(JsonElement item, ProviderConfig config, ModelMapping mapping)
    {
        string? id = GetStringField(item, mapping.Id ?? "id");
        if (string.IsNullOrEmpty(id))
            return Result.Failure<ModelInfo>("model entry has no 'id' (or it is null)");

        string displayName = GetStringField(item, mapping.DisplayName ?? "name") ?? id;
        int contextWindow = GetIntField(item, mapping.ContextWindow ?? "context_length") ?? UnknownContextWindow;
        int maxOutput = GetIntField(item, mapping.MaxOutputTokens ?? "max_output_tokens") ?? 4096;

        return Result.Success(new ModelInfo(
            id,
            config.Id,
            displayName,
            contextWindow,
            maxOutput,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai"));
    }

    private static Maybe<JsonElement> Navigate(JsonElement item, string path) =>
        path.Split('.').Aggregate(
            Maybe.From(item),
            static (acc, part) => acc.Bind(cur =>
                cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next)
                    ? Maybe.From(next)
                    : Maybe<JsonElement>.None));

    private static string? GetStringField(JsonElement item, string path) =>
        Navigate(item, path).Match(
            static e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(),
            static () => (string?)null);

    private static int? GetIntField(JsonElement item, string path) =>
        Navigate(item, path).Bind(static e => e.ValueKind switch
        {
            JsonValueKind.Number => Maybe.From((int)e.GetInt64()),
            JsonValueKind.String when int.TryParse(e.GetString(), out int i) => Maybe.From(i),
            _ => Maybe<int>.None,
        }).Match(static v => (int?)v, static () => (int?)null);
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="DynamicModelCatalog" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class DynamicModelCatalogLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch models for {Provider}, trying stale cache")]
    public static partial void FetchModelsFailed(ILogger logger, Exception ex, string provider);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to parse model entry {Index} from models response")]
    public static partial void ParseModelFailed(ILogger logger, Exception ex, int index);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Provider {Provider}: skipped {Skipped} of {Total} model entries. Reasons: {Reasons}")]
    public static partial void ModelsSkipped(ILogger logger, string provider, int skipped, int total, string reasons);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Could not stamp the catalogue freshness mark on '{Path}'; the file keeps its own write time")]
    public static partial void FreshnessStampFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Could not read cached model catalog for {Provider} at '{Path}'; falling back")]
    public static partial void CacheReadFailed(ILogger logger, Exception ex, string provider, string path);
}
