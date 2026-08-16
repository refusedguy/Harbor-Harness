using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="WebFetchTool" /> — mock HttpMessageHandler returns a fixed
///     HTML payload, and we verify the markdown conversion + metadata extraction. No real
///     network is hit.
/// </summary>
public class WebFetchToolTests
{
    [Test]
    public async Task Name_IsWebFetch()
    {
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, "<html><body><p>hi</p></body></html>", "text/html"));
        await Assert.That(tool.Name.Value).IsEqualTo("webfetch");
    }

    [Test]
    public async Task ExecutionMode_IsParallel()
    {
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, "<p>hi</p>", "text/html"));
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
    }

    [Test]
    public async Task ValidateArguments_MissingUrl_ReturnsFailure()
    {
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, "<p>hi</p>", "text/html"));
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ValidateArguments_NonHttpUrl_ReturnsFailure()
    {
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, "<p>hi</p>", "text/html"));
        var args = JsonDocument.Parse("""{"url":"file:///etc/passwd"}""").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("http");
    }

    [Test]
    public async Task ExecuteAsync_FetchesHtml_AndConvertsToMarkdown()
    {
        string html = """
                      <html><body>
                      <h1>Title</h1>
                      <p>Hello <a href="https://example.com">world</a>.</p>
                      <pre><code>var x = 1;</code></pre>
                      </body></html>
                      """;
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, html, "text/html"));
        var args = JsonDocument.Parse("""{"url":"https://example.com/"}""").RootElement;

        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        // Status code surfaced in summary
        await Assert.That(result.Output).Contains("200");
        // Heading converted to markdown
        await Assert.That(result.Output).Contains("# Title");
        // Link converted to inline markdown
        await Assert.That(result.Output).Contains("[world](https://example.com)");
        // Code block preserved as fenced markdown
        await Assert.That(result.Output).Contains("```");
        await Assert.That(result.Output).Contains("var x = 1;");
    }

    [Test]
    public async Task ExecuteAsync_BinaryContentType_ReturnsError()
    {
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, "image/png"));
        var args = JsonDocument.Parse("""{"url":"https://example.com/img.png"}""").RootElement;

        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("binary");
    }

    [Test]
    public async Task ExecuteAsync_JsonContentType_ReturnedAsIs()
    {
        const string json = """{"hello":"world","n":42}""";
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, json, "application/json"));
        var args = JsonDocument.Parse("""{"url":"https://api.example.com/v1/foo"}""").RootElement;

        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("\"hello\":\"world\"");
        await Assert.That(result.Output).Contains("42");
    }

    [Test]
    public async Task ExecuteAsync_MaxChars_Truncates()
    {
        string body = "<html><body><p>" + new string('x', 5000) + "</p></body></html>";
        var tool = NewTool(_ => NewResponse(HttpStatusCode.OK, body, "text/html"));
        var args = JsonDocument.Parse("""{"url":"https://example.com/","maxChars":100}""").RootElement;

        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("truncated at 100");
    }

    [Test]
    public async Task ExecuteAsync_HttpRequestException_ReturnsError()
    {
        var tool = NewTool(_ => throw new HttpRequestException("connection refused"));
        var args = JsonDocument.Parse("""{"url":"https://example.com/"}""").RootElement;

        var result = await tool.ExecuteAsync(args, CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("HTTP request failed");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static WebFetchTool NewTool(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(NullLogger<WebFetchTool>.Instance, () => new HttpClient(new StubHandler(responder)));

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    private static HttpResponseMessage NewResponse(HttpStatusCode status, string body, string contentType) =>
        new()
        {
            StatusCode = status,
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };

    private static HttpResponseMessage NewResponse(HttpStatusCode status, byte[] bytes, string contentType)
    {
        var msg = new HttpResponseMessage { StatusCode = status };
        msg.Content = new ByteArrayContent(bytes);
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return msg;
    }

    /// <summary>
    ///     Minimal HttpMessageHandler that runs a supplied responder for each request. No
    ///     network I/O — the responder can throw to simulate transport errors.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(_responder(request));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
