using System.Net;
using System.Text;
using Harbor.Providers.OpenAiCompatible;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.Tests;
/// <summary>
///     #1932: the on-disk model catalogue's freshness window is read through
///     an injected clock, so the 24-hour TTL can be aged in a test instead of
///     by waiting a day.
/// </summary>
/// <remarks>
///     <para>
///         The fake clock starts in <b>2035</b> — years away from the wall
///         clock — so a partial revert (a reader that compares against
///         <c>DateTimeOffset.UtcNow</c>) cannot pass by accident. The wall
///         clock sits on the OTHER side of the window from the fake one, so
///         the assertions below fail instead of quietly agreeing.
///     </para>
///     <para>
///         The window has two ends and both are the product's clock now: the
///         fetch path stamps the file's freshness mark, and the cache path
///         compares the clock against that mark. The third test below pins
///         that pair together — the entry the WRITE path produces is the one
///         the READ path expires.
///     </para>
/// </remarks>
public class ProviderCatalogClockTests : IDisposable
{
    /// <summary>
    ///     The instant the fake clock starts at: far from the wall clock, and
    ///     on a whole second so the filesystem's timestamp granularity cannot
    ///     move it.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2035, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _cacheDir = Directory.CreateTempSubdirectory("harbor-catalog-clock").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ProviderConfig Config() => new()
    {
        Id = "clocked",
        DisplayName = "Clocked",
        BaseUrl = "https://api.example.com",
        ModelsUrl = "https://api.example.com/v1/models",
        ModelsRefreshHours = 24,
    };

    private static HttpResponseMessage ModelsResponse(string id) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"data": [{"id": "{{id}}", "name": "{{id}}", "context_length": 8192}]}""",
                Encoding.UTF8,
                "application/json")
        };

    /// <summary>
    ///     An entry inside its window is served from the cache file: no second
    ///     round-trip, and no waiting for a day to pass. Before #1932 this
    ///     half of the contract was unobservable without sleeping 24 hours.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_InsideTheWindow_ServedFromTheCacheFile()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = new StubHttpMessageHandler(_ => ModelsResponse("m1"));
        using var http = new HttpClient(handler);
        var catalog = new DynamicModelCatalog(http, _cacheDir, NullLogger<DynamicModelCatalog>.Instance, clock);

        var first = await catalog.GetModelsAsync(Config());

        // 23 hours into a 24-hour window: still fresh.
        clock.Advance(TimeSpan.FromHours(23));
        var second = await catalog.GetModelsAsync(Config());

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value[0].Id).IsEqualTo("m1");
        await Assert.That(handler.Calls).IsEqualTo(1)
            .Because("a catalogue inside its window is served from the cache file — no second round-trip");
    }

    /// <summary>
    ///     An entry past its window is refetched: the cache must not serve a
    ///     stale catalogue past the 24 hours.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_PastTheWindow_Refetched()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = new StubHttpMessageHandler(_ => ModelsResponse("m1"));
        using var http = new HttpClient(handler);
        var catalog = new DynamicModelCatalog(http, _cacheDir, NullLogger<DynamicModelCatalog>.Instance, clock);

        _ = await catalog.GetModelsAsync(Config());

        // One hour past the 24-hour window: the entry has expired.
        clock.Advance(TimeSpan.FromHours(25));
        var after = await catalog.GetModelsAsync(Config());

        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(2)
            .Because("past the window the catalogue is refetched — the cache must not serve a stale catalogue");
    }

    /// <summary>
    ///     The cross-path half: the entry the FETCH path writes is stamped
    ///     from the same clock the CACHE path compares against, so the two
    ///     ends of the window are one clock's opinion and the entry expires
    ///     for the reader exactly one window after it was written.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_EntryWrittenByTheFetchPath_IsStampedFromTheSameClock()
    {
        var clock = new ManualTimeProvider(Start);
        var handler = new StubHttpMessageHandler(_ => ModelsResponse("m1"));
        using var http = new HttpClient(handler);
        var catalog = new DynamicModelCatalog(http, _cacheDir, NullLogger<DynamicModelCatalog>.Instance, clock);

        _ = await catalog.GetModelsAsync(Config());

        string cacheFile = Path.Combine(_cacheDir, "clocked.json");
        DateTimeOffset stamped = new(File.GetLastWriteTimeUtc(cacheFile), TimeSpan.Zero);

        await Assert.That(stamped).IsEqualTo(Start)
            .Because("the fetch path stamps the freshness mark from the injected clock, not from the filesystem");
    }

    /// <summary>
    ///     A stale catalogue is still a catalogue: when the fetch fails, the
    ///     reader falls back to the file the fetch path wrote — and that
    ///     fallback is decided by the injected clock too.
    /// </summary>
    [Test]
    public async Task GetModelsAsync_PastTheWindow_FetchFailure_FallsBackToTheStaleFile()
    {
        var clock = new ManualTimeProvider(Start);
        int fetches = 0;
        var handler = new StubHttpMessageHandler(_ => fetches++ == 0
            ? ModelsResponse("m1")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error", Encoding.UTF8)
            });
        using var http = new HttpClient(handler);
        var catalog = new DynamicModelCatalog(http, _cacheDir, NullLogger<DynamicModelCatalog>.Instance, clock);

        _ = await catalog.GetModelsAsync(Config());

        clock.Advance(TimeSpan.FromHours(25));
        var after = await catalog.GetModelsAsync(Config());

        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(after.Value[0].Id).IsEqualTo("m1");
        await Assert.That(handler.Calls).IsEqualTo(2)
            .Because("the stale file is served only after the fetch has failed — not before");
    }
}
