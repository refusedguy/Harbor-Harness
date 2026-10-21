using System.Net;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.TestKit;
using Harbor.Tools.Mcp;
using TUnit.Assertions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1932: the MCP OAuth token's expiry is read through an injected clock,
///     so "the token expired" is observable without waiting for it to expire.
/// </summary>
/// <remarks>
///     <para>
///         The fake clock starts in <b>2035</b> — years away from the wall
///         clock — so a partial revert (a reader that compares against
///         <c>DateTimeOffset.UtcNow</c>, or a writer that stamps the expiry
///         from it) cannot pass by accident: the wall clock sits on the other
///         side of the window, and the assertions fail instead of agreeing.
///     </para>
///     <para>
///         Both ends of the token's life are the product's clock now: the
///         flow stamps <c>ExpiresAtUtc</c> when it writes a token, and the
///         handler compares that stamp when it reads one. The last test pins
///         the pair together — an entry written by one path expires for the
///         other reader.
///     </para>
/// </remarks>
public class McpOAuthClockTests : IDisposable
{
    /// <summary>
    ///     The instant the fake clock starts at: far from the wall clock, and
    ///     on a whole second so no rounding can move it.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2035, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("harbor-mcp-oauth-clock").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient StubClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHttpMessageHandler(respond));

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    /// <summary>
    ///     A token inside its lifetime is served; the same handler, with the
    ///     clock moved past the expiry, reports it gone. No waiting, and no
    ///     wall clock involved.
    /// </summary>
    [Test]
    public async Task Handler_TokenExpires_WhenTheClockMovesPastItsExpiry()
    {
        var clock = new ManualTimeProvider(Start);
        var cache = new McpOAuthTokenCache(_root);
        cache.Save("srv", new McpOAuthTokens("at", null, Start.AddHours(1)));
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache, timeProvider: clock);

        Result<Maybe<string>> fresh = await handler.TryGetAccessTokenResultAsync();

        clock.Advance(TimeSpan.FromHours(2));
        Result<Maybe<string>> expired = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(fresh.IsSuccess).IsTrue();
        await Assert.That(fresh.Value.HasValue).IsTrue();
        await Assert.That(fresh.Value.GetValueOrDefault()).IsEqualTo("at");
        await Assert.That(expired.IsSuccess).IsTrue();
        await Assert.That(expired.Value.HasNoValue).IsTrue()
            .Because("a token past its expiry is not served — the expiry is read from the injected clock");
    }

    /// <summary>
    ///     The write path stamps the expiry from the injected clock. A
    ///     reverted write path (one that stamps it from the wall clock) puts
    ///     the expiry decades away from the fake clock, and this assertion
    ///     says so.
    /// </summary>
    [Test]
    public async Task Flow_RefreshAsync_StampsTheExpiryFromTheInjectedClock()
    {
        var clock = new ManualTimeProvider(Start);
        using var http = StubClient(_ => Json(new { access_token = "at2", expires_in = 3600 }));

        var result = await McpOAuthFlow.RefreshAsync(
            http, "https://x/token", "cid", null, "rt", timeProvider: clock);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ExpiresAtUtc).IsEqualTo(Start.AddSeconds(3600))
            .Because("the write path stamps the expiry from the injected clock — the clock the reader uses");
    }

    /// <summary>
    ///     The cross-path case: the token the WRITE path produces is the one
    ///     the READ path expires. It is served while it is inside its
    ///     lifetime (with the one-minute safety margin the record declares)
    ///     and gone once the clock passes that margin.
    /// </summary>
    [Test]
    public async Task Handler_TokenWrittenByTheWritePath_ExpiresForTheReader()
    {
        var clock = new ManualTimeProvider(Start);
        var cache = new McpOAuthTokenCache(_root);

        // The WRITE path: the flow stamps the expiry from the clock.
        using var http = StubClient(_ => Json(new { access_token = "written", expires_in = 3600 }));
        Result<McpOAuthTokens> written = await McpOAuthFlow.RefreshAsync(
            http, "https://x/token", "cid", null, "rt", timeProvider: clock);
        cache.Save("srv", written.Value);

        // The READ path: the same clock decides what is still usable.
        using var handler = new McpOAuthHandler("srv", new Uri("https://mcp.example.com/mcp"),
            new McpOAuthConfig(), cache, timeProvider: clock);

        Result<Maybe<string>> fresh = await handler.TryGetAccessTokenResultAsync();

        // One minute before the expiry the safety margin has already closed
        // the window — the record expires a token early on purpose.
        clock.Advance(TimeSpan.FromSeconds(3600 - 59));
        Result<Maybe<string>> expired = await handler.TryGetAccessTokenResultAsync();

        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.ExpiresAtUtc).IsEqualTo(Start.AddSeconds(3600));
        await Assert.That(fresh.Value.GetValueOrDefault()).IsEqualTo("written");
        await Assert.That(expired.Value.HasNoValue).IsTrue()
            .Because("the entry the write path stamped expires for the reader one lifetime later");
    }
}
