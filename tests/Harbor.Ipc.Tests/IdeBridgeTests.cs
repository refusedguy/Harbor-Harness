using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Ide;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Tests for the <c>harbor ide</c> stdio bridge. Every test drives the
///     bridge through real NDJSON JSON-RPC traffic on a blocking in-memory
///     reader/writer pair — the same surface an external editor uses.
/// </summary>
public class IdeBridgeTests
{
    // ── Protocol semantics through the full NDJSON stack ───────────────────

    [Test]
    public async Task ListSessions_Returns_Serialized_Sessions()
    {
        Session session = Session.Create("/tmp/proj", "code", "kilocode", "kilocode/tencent/hy3:free");
        await using var harness = new IdeHarness(sessionId: "s1");
        harness.Client.Sessions = [session];

        JsonElement result = await harness.RequestAsync("list_sessions");

        await Assert.That(result.GetProperty("sessions").GetArrayLength()).IsEqualTo(1);
        JsonElement first = result.GetProperty("sessions")[0];
        await Assert.That(first.GetProperty("id").GetString()).IsEqualTo(session.Id);
        await Assert.That(first.GetProperty("agent").GetString()).IsEqualTo("code");
        await Assert.That(first.GetProperty("provider").GetString()).IsEqualTo("kilocode");
    }

    [Test]
    public async Task InjectPrompt_Accepts_Immediately_And_Runs_In_Background()
    {
        Session bound = Session.Create("/tmp/proj", "code", "anthropic", "claude");
        await using var harness = new IdeHarness(sessionId: bound.Id);

        JsonElement result = await harness.RequestAsync("inject_prompt",
            $$"""{"session_id":"{{bound.Id}}","prompt":"hello"}""");

        await Assert.That(result.GetProperty("accepted").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("session_id").GetString()).IsEqualTo(bound.Id);

        await harness.Client.WaitPromptAsync(TimeSpan.FromSeconds(5));
        await Assert.That(harness.Client.Prompts[0]).IsEqualTo("hello");
    }

    [Test]
    public async Task InjectPrompt_Empty_Prompt_Is_InvalidParams()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        IdeRpcException ex = await Assert.That(async () =>
            await harness.RequestAsync("inject_prompt", """{"prompt":"  "}""")).Throws<IdeRpcException>();
        await Assert.That(ex.Code).IsEqualTo(IdeRpcException.InvalidParams);
    }

    [Test]
    public async Task InjectPrompt_Foreign_Session_Is_Rejected()
    {
        await using var harness = new IdeHarness(sessionId: "mine");

        IdeRpcException ex = await Assert.That(async () =>
            await harness.RequestAsync("inject_prompt", """{"session_id":"other","prompt":"hi"}"""))
            .Throws<IdeRpcException>();
        await Assert.That(ex.Code).IsEqualTo(IdeRpcException.InvalidParams);
        await Assert.That(harness.Client.Prompts).IsEmpty();
    }

    [Test]
    public async Task InjectPrompt_Missing_Params_Object_Is_InvalidParams()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        IdeRpcException ex = await Assert.That(async () =>
            await harness.RequestAsync("inject_prompt")).Throws<IdeRpcException>();
        await Assert.That(ex.Code).IsEqualTo(IdeRpcException.InvalidParams);
    }

    [Test]
    public async Task ReadStream_Pushes_Stream_Notifications()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        JsonElement result = await harness.RequestAsync("read_stream");
        await Assert.That(result.GetProperty("subscribed").GetBoolean()).IsTrue();

        AssistantMessage partial = AssistantMessage.Empty("s1", "m").AppendText("Hel");
        await harness.Client.PublishEventAsync(new HarborEvent.AgentStarted("s1"));
        await harness.Client.PublishEventAsync(new HarborEvent.MessageUpdate(partial, "Hel"));
        await harness.Client.PublishEventAsync(new HarborEvent.ToolStart("tc1", "read"));

        string agentStart = await harness.WaitOutputAsync("agent_start");
        await Assert.That(agentStart).Contains("\"method\":\"stream\"");
        string delta = await harness.WaitOutputAsync("message_delta");
        await Assert.That(delta).Contains("\"delta\":\"Hel\"");
        string toolStart = await harness.WaitOutputAsync("tool_start");
        await Assert.That(toolStart).Contains("\"tool_name\":\"read\"");
        await Assert.That(toolStart).Contains("\"tool_call_id\":\"tc1\"");
    }

    [Test]
    public async Task StopStream_Stops_Pushing()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        await harness.RequestAsync("read_stream");
        JsonElement stopped = await harness.RequestAsync("stop_stream");
        await Assert.That(stopped.GetProperty("subscribed").GetBoolean()).IsFalse();

        await harness.Client.PublishEventAsync(new HarborEvent.AgentStarted("s1"));
        await Assert.That(async () =>
            await harness.WaitOutputAsync("agent_start", TimeSpan.FromMilliseconds(400))).Throws<TimeoutException>();
    }

    [Test]
    public async Task Abort_Forwards_To_Client()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        JsonElement result = await harness.RequestAsync("abort");

        await Assert.That(result.GetProperty("requested").GetBoolean()).IsTrue();
        await Assert.That(harness.Client.Aborted).IsTrue();
    }

    [Test]
    public async Task Unknown_Method_Is_MethodNotFound()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        IdeRpcException ex = await Assert.That(async () =>
            await harness.RequestAsync("does_not_exist")).Throws<IdeRpcException>();
        await Assert.That(ex.Code).IsEqualTo(IdeRpcException.MethodNotFound);
    }

    [Test]
    public async Task Malformed_Json_Responds_With_Null_Id_Error()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        harness.Input.PushLine("this is not json");

        string line = await harness.WaitOutputAsync("\"error\"", timeout: TimeSpan.FromSeconds(5));
        await Assert.That(line).Contains("\"id\":null");
    }

    [Test]
    public async Task Editor_Notifications_Are_Acknowledged_By_Silence()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        harness.Input.PushLine("""{"jsonrpc":"2.0","method":"editor_hello","params":{"x":1}}""");

        await Assert.That(async () =>
            await harness.WaitOutputAsync("editor_hello", TimeSpan.FromMilliseconds(400))).Throws<TimeoutException>();
    }

    [Test]
    public async Task Slow_Request_Does_Not_Block_Next_Request()
    {
        await using var harness = new IdeHarness(sessionId: "s1");
        harness.Client.GateListSessions();

        Task<JsonElement> slow = harness.RequestAsync("list_sessions");
        await harness.Client.WaitListSessionsStartedAsync(TimeSpan.FromSeconds(5));

        JsonElement fast = await harness.RequestAsync("abort");
        await Assert.That(fast.GetProperty("requested").GetBoolean()).IsTrue();

        harness.Client.ReleaseListSessions();
        JsonElement slowResult = await slow;
        await Assert.That(slowResult.GetProperty("sessions").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Hung_Request_Times_Out_With_Error()
    {
        await using var harness = new IdeHarness(
            sessionId: "s1",
            options: new IdeSessionBridgeOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        harness.Client.GateListSessions();

        IdeRpcException ex = await Assert.That(async () => await harness.RequestAsync(
            "list_sessions", timeout: TimeSpan.FromSeconds(10))).Throws<IdeRpcException>();
        await Assert.That(ex.Code).IsEqualTo(-32002);

        harness.Client.ReleaseListSessions();
    }

    [Test]
    public async Task Abort_Cancels_Inflight_Prompt_Run()
    {
        await using var harness = new IdeHarness(sessionId: "s1");
        harness.Client.GatePrompt();

        await harness.RequestAsync("inject_prompt", """{"prompt":"long running"}""");
        await harness.Client.WaitPromptAsync(TimeSpan.FromSeconds(5));

        JsonElement result = await harness.RequestAsync("abort");
        await Assert.That(result.GetProperty("requested").GetBoolean()).IsTrue();

        harness.Client.ReleasePrompt();
        await Assert.That(harness.Client.LastPromptAborted).IsTrue();
    }

    [Test]
    public async Task Closing_Stdio_Ends_The_Bridge()
    {
        await using var harness = new IdeHarness(sessionId: "s1");

        harness.Input.Close();
        await harness.ServeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Harness ────────────────────────────────────────────────────────────

    /// <summary>
    ///     In-memory editor side of the bridge: pushes NDJSON requests into a
    ///     blocking reader and captures response/notification lines written by
    ///     the bridge.
    /// </summary>
    private sealed class BlockingLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        public void PushLine(string line) => _lines.Writer.TryWrite(line);

        public void Close() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await _lines.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null; // EOF semantics
            }
        }

        public override int Read(char[] buffer, int index, int count) => throw new NotSupportedException();
    }

    /// <summary>Full editor-side harness around <see cref="IdeSessionBridge" />.</summary>
    private sealed class IdeHarness : IAsyncDisposable
    {
        private readonly Lock _outputLock = new();
        private readonly StringBuilder _output = new();
        private int _nextId;

        public IdeHarness(string? sessionId, IdeSessionBridgeOptions? options = null)
        {
            Input = new BlockingLineReader();
            Client = new StubHarborClient();
            Bridge = new IdeSessionBridge(Client, Input, new LockingWriter(this), sessionId, NullLogger.Instance, options);
            ServeTask = Task.Run(() => Bridge.RunAsync(CancellationToken.None));
        }

        public BlockingLineReader Input { get; }

        public StubHarborClient Client { get; }

        public IdeSessionBridge Bridge { get; }

        public Task ServeTask { get; }

        /// <summary>Sends a JSON-RPC request and awaits the matching response.</summary>
        public async Task<JsonElement> RequestAsync(string method, string? paramsJson = null, TimeSpan? timeout = null)
        {
            int id = Interlocked.Increment(ref _nextId);
            string paramsPart = paramsJson is null ? string.Empty : $",\"params\":{paramsJson}";
            Input.PushLine($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{paramsPart}}}""");

            string line = await WaitOutputAsync($"\"id\":{id}", timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                throw new IdeRpcException(
                    error.GetProperty("code").GetInt32(),
                    error.GetProperty("message").GetString() ?? "bridge error");
            }

            return doc.RootElement.GetProperty("result").Clone();
        }

        /// <summary>Polls the captured output for the first line containing <paramref name="fragment" />.</summary>
        public async Task<string> WaitOutputAsync(string fragment, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < deadline)
            {
                lock (_outputLock)
                {
                    string[] lines = _output.ToString().Split('\n');
                    string? match = lines.FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal));
                    if (match is not null) return match.TrimEnd('\r');
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            lock (_outputLock)
            {
                throw new TimeoutException($"No output line containing '{fragment}'. Captured: {_output}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Input.Close();
            try
            {
                await ServeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Bridge is torn down by DisposeAsync below.
            }

            await Bridge.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        ///     Writer that records into the harness buffer. The bridge serializes
        ///     all writes under its own semaphore; locking here is belt and braces.
        /// </summary>
        private sealed class LockingWriter(IdeHarness owner) : StringWriter
        {
            public override void Write(char value)
            {
                lock (owner._outputLock) _ = owner._output.Append(value);
            }

            public override void Write(string? value)
            {
                lock (owner._outputLock) _ = owner._output.Append(value);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                lock (owner._outputLock) _ = owner._output.Append(buffer, index, count);
            }

            public override async Task WriteAsync(string? value)
            {
                if (value is null) return;
                lock (owner._outputLock) _ = owner._output.Append(value);
                await Task.CompletedTask.ConfigureAwait(false);
            }

            public override async Task WriteLineAsync(string? value)
            {
                lock (owner._outputLock) _ = owner._output.Append(value).Append('\n');
                await Task.CompletedTask.ConfigureAwait(false);
            }

            public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken ct = default)
            {
                lock (owner._outputLock) _ = owner._output.Append(buffer.Span);
                await Task.CompletedTask.ConfigureAwait(false);
            }

            public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken ct = default)
            {
                lock (owner._outputLock) _ = owner._output.Append(buffer.Span).Append('\n');
                await Task.CompletedTask.ConfigureAwait(false);
            }

            public override async Task FlushAsync(CancellationToken ct = default)
            {
                await Task.CompletedTask.ConfigureAwait(false);
            }

            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
